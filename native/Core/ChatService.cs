using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Beta.Messages;
using NonBeta = Anthropic.Models.Messages;

namespace ClaudeChat.Native.Core;

public abstract record StreamEvent;
public sealed record BlockStarted(int Index, ViewBlock Block) : StreamEvent;
public sealed record DeltaReceived(int Index, string Text) : StreamEvent;
public sealed record SearchQueryResolved(int Index, string Query) : StreamEvent;

public enum ChatOutcome
{
    Done,
    Aborted,
    Failed,
}

public sealed record ChatResult(ChatOutcome Outcome, string? Error = null);

public sealed class ChatService(AnthropicClient client)
{
    const string TitleModel = "claude-haiku-5-5";
    // pause_turn が延々続いたときの保険
    const int MaxContinuations = 5;

    static bool IsHaiku(string model) => model.StartsWith("claude-haiku");

    static MessageCreateParams BuildRequest(ChatSettings settings, Conversation conversation)
    {
        var parameters = new MessageCreateParams
        {
            Model = settings.Model,
            MaxTokens = 64000,
            Messages = conversation.Messages.Select(ToParam).ToList(),
            Thinking = new BetaThinkingConfigAdaptive { Display = Display.Summarized },
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(settings.Effort) },
            CacheControl = new BetaCacheControlEphemeral(),
        };
        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt)) parameters = parameters with { System = settings.SystemPrompt };
        if (settings.WebSearch)
        {
            // Haiku は動的フィルタ版の web_search に非対応
            BetaToolUnion tool = IsHaiku(settings.Model)
                ? new BetaWebSearchTool20250305 { MaxUses = 5 }
                : new BetaWebSearchTool20260209 { MaxUses = 5 };
            parameters = parameters with { Tools = [tool] };
        }
        // Haiku 5.5 にはサーバー側フォールバックが無い
        if (!IsHaiku(settings.Model))
        {
            parameters = parameters with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
        }
        return parameters;
    }

    static Effort ParseEffort(string effort) => effort switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };

    static BetaMessageParam ToParam(JsonObject message) =>
        JsonSerializer.Deserialize<BetaMessageParam>(message.ToJsonString())!;

    static JsonObject ToWire(BetaContentBlock block) =>
        JsonSerializer.SerializeToNode(block)!.AsObject();

    static string Describe(Exception error) => error switch
    {
        AnthropicUnauthorizedException => "APIキーが無効です。設定を確認してください",
        AnthropicForbiddenException => "このモデルを使う権限がありません",
        AnthropicRateLimitException => "レート制限に達しました。少し待ってから再送してください",
        AnthropicBadRequestException e => $"リクエストが拒否されました: {e.Message}",
        AnthropicIOException => "API に接続できません。ネットワークを確認してください",
        AnthropicApiException e => $"API エラー: {e.Message}",
        _ => error.Message,
    };

    public async Task<ChatResult> StreamReplyAsync(
        ChatSettings settings,
        Conversation conversation,
        Action<StreamEvent> emit,
        CancellationToken cancellationToken)
    {
        // pause_turn で続きを取りに行くと index が 0 から振り直されるのでずらす
        var indexBase = 0;
        var partialText = new SortedDictionary<int, string>();

        try
        {
            for (var turn = 0; turn <= MaxContinuations; turn++)
            {
                var aggregator = new BetaMessageContentAggregator();
                var toolInputJson = new Dictionary<int, string>();
                partialText.Clear();

                var stream = client.Beta.Messages.CreateStreaming(BuildRequest(settings, conversation), cancellationToken);
                await foreach (var streamEvent in stream.CollectAsync(aggregator).WithCancellation(cancellationToken))
                {
                    if (streamEvent.TryPickContentBlockStart(out var start))
                    {
                        var index = indexBase + (int)start.Index;
                        var wire = JsonSerializer.SerializeToNode(start.ContentBlock)!.AsObject();
                        if (ViewBlocks.FromWire(wire) is { } view) emit(new BlockStarted(index, view));
                        if ((string?)wire["type"] == "text") partialText[index] = "";
                    }
                    else if (streamEvent.TryPickContentBlockDelta(out var delta))
                    {
                        var index = indexBase + (int)delta.Index;
                        if (delta.Delta.TryPickText(out var text))
                        {
                            partialText[index] = partialText.GetValueOrDefault(index, "") + text.Text;
                            emit(new DeltaReceived(index, text.Text));
                        }
                        else if (delta.Delta.TryPickThinking(out var thinking))
                        {
                            emit(new DeltaReceived(index, thinking.Thinking));
                        }
                        else if (delta.Delta.TryPickInputJson(out var inputJson))
                        {
                            toolInputJson[index] = toolInputJson.GetValueOrDefault(index, "") + inputJson.PartialJson;
                        }
                    }
                    else if (streamEvent.TryPickContentBlockStop(out var stop))
                    {
                        var index = indexBase + (int)stop.Index;
                        if (toolInputJson.TryGetValue(index, out var json) && TryReadQuery(json) is { } query)
                        {
                            emit(new SearchQueryResolved(index, query));
                        }
                    }
                }

                var message = aggregator.Message();
                indexBase += message.Content.Count;
                // 思考ブロックは同じモデルに無加工で返す必要があるので content を丸ごと積む
                conversation.Messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(message.Content.Select(b => (JsonNode)ToWire(b)).ToArray()),
                });
                conversation.Usage.Input += message.Usage.InputTokens + (message.Usage.CacheCreationInputTokens ?? 0);
                conversation.Usage.Output += message.Usage.OutputTokens;
                conversation.Usage.CacheRead += message.Usage.CacheReadInputTokens ?? 0;

                var stopReason = message.StopReason?.Raw();
                if (stopReason == "pause_turn") continue;
                if (stopReason == "refusal")
                {
                    emit(new BlockStarted(indexBase, new NoticeView { Text = "Claude がこのリクエストへの応答を断りました" }));
                }
                else if (stopReason == "max_tokens")
                {
                    emit(new BlockStarted(indexBase, new NoticeView { Text = "出力上限に達したため途中で終わっています" }));
                }
                return new ChatResult(ChatOutcome.Done);
            }
            return new ChatResult(ChatOutcome.Done);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // 中断時は思考ブロックの署名が揃わないので、出せた本文だけ残す
            var text = string.Concat(partialText.Values);
            if (text.Length > 0)
            {
                conversation.Messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                });
            }
            return new ChatResult(ChatOutcome.Aborted);
        }
        catch (Exception error)
        {
            return new ChatResult(ChatOutcome.Failed, Describe(error));
        }
    }

    static string? TryReadQuery(string json)
    {
        try
        {
            return (string?)JsonNode.Parse(json)?["query"];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string FirstText(JsonNode? content) => content switch
    {
        JsonValue value => (string?)value ?? "",
        JsonArray blocks => string.Join("\n", blocks.OfType<JsonObject>()
            .Where(b => (string?)b["type"] == "text")
            .Select(b => (string?)b["text"])),
        _ => "",
    };

    public async Task<string?> GenerateTitleAsync(Conversation conversation)
    {
        if (conversation.Messages.Count < 2) return null;
        // タイトル用途なので長文は頭だけで十分
        static string Excerpt(string s) => s.Length > 2000 ? s[..2000] : s;
        var user = Excerpt(FirstText(conversation.Messages[0]["content"]));
        var assistant = Excerpt(FirstText(conversation.Messages[1]["content"]));

        var response = await client.Messages.Create(new NonBeta.MessageCreateParams
        {
            Model = TitleModel,
            MaxTokens = 1024,
            OutputConfig = new NonBeta.OutputConfig { Effort = NonBeta.Effort.Low },
            System = "会話の内容を表す短い日本語タイトルを20文字以内で1つだけ出力してください。記号や引用符、前置きは付けないでください。",
            Messages = [new() { Role = NonBeta.Role.User, Content = $"ユーザー:\n{user}\n\nアシスタント:\n{assistant}" }],
        });
        if (response.StopReason?.Raw() == "refusal") return null;
        var title = string.Concat(response.Content.Select(b => b.Value).OfType<NonBeta.TextBlock>().Select(t => t.Text)).Trim();
        if (title.Length == 0 || title == Conversation.DefaultTitle) return null;
        return title.Length > 40 ? title[..40] : title;
    }
}
