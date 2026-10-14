using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Beta.Messages;
using NonBeta = Anthropic.Models.Messages;

namespace ClaudeChat.Core;

public sealed record ChatOptions(string Model, string Effort, bool WebSearch, ISet<string>? EnabledSources, string? SystemPrompt, int MaxToolTurns);

public enum ChatStatus
{
    Done,
    Aborted,
    Failed,
}

public sealed record ChatRunResult(ChatStatus Status, string? Error = null);

// ツール実行の前に人間の判断を挟む。答えは once / always / deny のどれか
public interface IApprovalGate
{
    Task<string> RequestAsync(JsonObject request, CancellationToken cancellationToken);
}

public sealed class ChatEngine(AnthropicClient client, ToolRegistry tools, UsageLedger ledger, SettingsStore settings)
{
    const string TitleModel = "claude-haiku-5-5";

    static bool IsHaiku(string model) => model.StartsWith("claude-haiku");

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

    static JsonObject ToWire<T>(T block) => JsonSerializer.SerializeToNode(block)!.AsObject();

    static BetaToolUnion ToTool(ToolSpec spec)
    {
        var json = new JsonObject
        {
            ["type"] = "custom",
            ["name"] = spec.ApiName,
            ["description"] = $"[{spec.Source}] {spec.Description}",
            ["input_schema"] = spec.InputSchema.DeepClone(),
            // 大きな入力も生成と同時に流れてくるようにする
            ["eager_input_streaming"] = true,
        };
        return JsonSerializer.Deserialize<BetaToolUnion>(json.ToJsonString())!;
    }

    static MessageCreateParams BuildRequest(Conversation conversation, ChatOptions options, IReadOnlyList<ToolSpec> toolSpecs)
    {
        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = 64000,
            Messages = conversation.CopyMessages().Select(ToParam).ToList(),
            Thinking = new BetaThinkingConfigAdaptive { Display = Display.Summarized },
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(options.Effort) },
            CacheControl = new BetaCacheControlEphemeral(),
        };
        if (!string.IsNullOrWhiteSpace(options.SystemPrompt)) parameters = parameters with { System = options.SystemPrompt };

        var toolList = new List<BetaToolUnion>();
        if (options.WebSearch)
        {
            // Haiku は動的フィルタ版の web_search に非対応
            toolList.Add(IsHaiku(options.Model)
                ? new BetaWebSearchTool20250305 { MaxUses = 5 }
                : new BetaWebSearchTool20260209 { MaxUses = 5 });
        }
        toolList.AddRange(toolSpecs.Select(ToTool));
        if (toolList.Count > 0) parameters = parameters with { Tools = toolList };

        // Haiku 5.5 にはサーバー側フォールバックが無い
        if (!IsHaiku(options.Model))
        {
            parameters = parameters with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
        }
        return parameters;
    }

    public static string Describe(Exception error) => error switch
    {
        AnthropicUnauthorizedException => "APIキーが無効です。設定を確認してください",
        AnthropicForbiddenException => "このモデルを使う権限がありません",
        AnthropicRateLimitException => "レート制限に達しました。少し待ってから再送してください",
        AnthropicBadRequestException e => $"リクエストが拒否されました: {e.Message}",
        AnthropicIOException => "API に接続できません。ネットワークを確認してください",
        AnthropicApiException e => $"API エラー: {e.Message}",
        _ => error.Message,
    };

    public async Task<ChatRunResult> RunAsync(
        Conversation conversation,
        ChatOptions options,
        Action<JsonObject> emit,
        IApprovalGate gate,
        CancellationToken cancellationToken)
    {
        var partialText = new SortedDictionary<int, string>();
        try
        {
            for (var turn = 0; turn < options.MaxToolTurns; turn++)
            {
                var toolSpecs = tools.GetTools(options.EnabledSources);
                var aggregator = new BetaMessageContentAggregator();
                var toolInputJson = new Dictionary<int, string>();
                partialText.Clear();
                emit(new JsonObject { ["type"] = "message-start" });

                var stream = client.Beta.Messages.CreateStreaming(BuildRequest(conversation, options, toolSpecs), cancellationToken);
                await foreach (var streamEvent in stream.CollectAsync(aggregator).WithCancellation(cancellationToken))
                {
                    if (streamEvent.TryPickContentBlockStart(out var start))
                    {
                        var index = (int)start.Index;
                        var block = ToWire(start.ContentBlock);
                        if ((string?)block["type"] == "text") partialText[index] = "";
                        emit(new JsonObject { ["type"] = "block-start", ["index"] = index, ["block"] = block });
                    }
                    else if (streamEvent.TryPickContentBlockDelta(out var delta))
                    {
                        var index = (int)delta.Index;
                        if (delta.Delta.TryPickText(out var text))
                        {
                            partialText[index] = partialText.GetValueOrDefault(index, "") + text.Text;
                            emit(new JsonObject { ["type"] = "delta", ["index"] = index, ["text"] = text.Text });
                        }
                        else if (delta.Delta.TryPickThinking(out var thinking))
                        {
                            emit(new JsonObject { ["type"] = "delta", ["index"] = index, ["text"] = thinking.Thinking });
                        }
                        else if (delta.Delta.TryPickInputJson(out var inputJson))
                        {
                            toolInputJson[index] = toolInputJson.GetValueOrDefault(index, "") + inputJson.PartialJson;
                        }
                    }
                    else if (streamEvent.TryPickContentBlockStop(out var stop) && toolInputJson.TryGetValue((int)stop.Index, out var json))
                    {
                        emit(new JsonObject { ["type"] = "block-stop", ["index"] = (int)stop.Index, ["input"] = TryParse(json) });
                    }
                }

                var message = aggregator.Message();
                var content = new JsonArray(message.Content.Select(b => (JsonNode)ToWire(b)).ToArray());
                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = content };
                conversation.Append(assistant);
                partialText.Clear();
                emit(new JsonObject { ["type"] = "message", ["message"] = assistant.DeepClone() });
                await RecordUsageAsync(conversation, message.Model.Raw() ?? options.Model, "chat", message.Usage);

                var stopReason = message.StopReason?.Raw();
                if (stopReason == "pause_turn") continue;

                var toolUses = content.OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_use").ToList();
                if (toolUses.Count > 0)
                {
                    var results = stopReason == "tool_use"
                        ? await RunToolsAsync(toolUses, options, emit, gate, cancellationToken)
                        // 出力上限で切れたツール入力は信用できないので実行しない
                        : ErrorResults(toolUses, "出力上限で入力が途中までしか届かなかったため、実行しませんでした");
                    var user = new JsonObject { ["role"] = "user", ["content"] = results };
                    conversation.Append(user);
                    emit(new JsonObject { ["type"] = "message", ["message"] = user.DeepClone() });
                    if (stopReason == "tool_use") continue;
                }

                if (stopReason == "refusal") Notice(emit, "Claude がこのリクエストへの応答を断りました");
                else if (stopReason == "max_tokens") Notice(emit, "出力上限に達したため途中で終わっています");
                return new ChatRunResult(ChatStatus.Done);
            }
            Notice(emit, $"ツールの呼び出しが {options.MaxToolTurns} 回に達したので止めました。続けるには続きをお願いしてください");
            return new ChatRunResult(ChatStatus.Done);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // 思考ブロックは署名が揃わないと送り返せないので、出せた本文だけ残す
            var text = string.Concat(partialText.Values);
            if (!RepairDanglingToolUse(conversation, "ユーザーが中断しました") && text.Length > 0)
            {
                conversation.Append(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                });
            }
            return new ChatRunResult(ChatStatus.Aborted);
        }
        catch (Exception error)
        {
            RepairDanglingToolUse(conversation, "エラーで中断しました");
            return new ChatRunResult(ChatStatus.Failed, Describe(error));
        }
    }

    // tool_use の直後に tool_result が無い履歴は次の送信で 400 になるので、結果を補っておく
    static bool RepairDanglingToolUse(Conversation conversation, string reason)
    {
        var last = conversation.LastMessage();
        if (last is null || (string?)last["role"] != "assistant") return false;
        var toolUses = (last["content"] as JsonArray ?? []).OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_use").ToList();
        if (toolUses.Count == 0) return false;
        conversation.Append(new JsonObject { ["role"] = "user", ["content"] = ErrorResults(toolUses, reason) });
        return true;
    }

    static JsonArray ErrorResults(IEnumerable<JsonObject> toolUses, string reason) => new(toolUses
        .Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = (string?)t["id"],
            ["is_error"] = true,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reason }),
        })
        .ToArray());

    static JsonNode? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static void Notice(Action<JsonObject> emit, string text) =>
        emit(new JsonObject { ["type"] = "notice", ["text"] = text });

    async Task<JsonArray> RunToolsAsync(
        List<JsonObject> toolUses,
        ChatOptions options,
        Action<JsonObject> emit,
        IApprovalGate gate,
        CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        foreach (var toolUse in toolUses)
        {
            var id = (string?)toolUse["id"] ?? "";
            var apiName = (string?)toolUse["name"] ?? "";
            var input = toolUse["input"] as JsonObject ?? [];
            var spec = tools.Find(apiName, options.EnabledSources);

            ToolCallResult outcome;
            if (spec is null)
            {
                outcome = ToolRegistry.Error($"ツール {apiName} は現在使えません");
            }
            else if (await IsAllowedAsync(spec, id, input, emit, gate, cancellationToken))
            {
                emit(new JsonObject { ["type"] = "tool-running", ["toolUseId"] = id });
                outcome = await tools.CallAsync(apiName, input, cancellationToken);
            }
            else
            {
                outcome = ToolRegistry.Error("ユーザーがこのツールの実行を許可しませんでした。別の方法を考えるか、ユーザーに確認してください");
            }

            var result = new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = id,
                ["content"] = outcome.Content,
            };
            if (outcome.IsError) result["is_error"] = true;
            emit(new JsonObject { ["type"] = "tool-result", ["toolUseId"] = id, ["result"] = result.DeepClone() });
            results.Add(result);
        }
        return results;
    }

    async Task<bool> IsAllowedAsync(ToolSpec spec, string toolUseId, JsonObject input, Action<JsonObject> emit, IApprovalGate gate, CancellationToken cancellationToken)
    {
        var current = await settings.GetAsync();
        if (current.ToolPermissions.GetValueOrDefault(spec.ApiName) == "always") return true;

        var decision = await gate.RequestAsync(new JsonObject
        {
            ["toolUseId"] = toolUseId,
            ["apiName"] = spec.ApiName,
            ["source"] = spec.Source,
            ["tool"] = spec.Name,
            ["title"] = spec.Title,
            ["description"] = spec.Description,
            ["input"] = input.DeepClone(),
        }, cancellationToken);

        if (decision == "always") await settings.UpdateAsync(s => s.ToolPermissions[spec.ApiName] = "always");
        return decision is "once" or "always";
    }

    async Task RecordUsageAsync(Conversation conversation, string model, string kind, BetaUsage usage)
    {
        var record = await ledger.RecordAsync(new UsageRecord
        {
            At = DateTimeOffset.UtcNow,
            ConversationId = conversation.Id,
            Kind = kind,
            Model = model,
            Input = usage.InputTokens,
            Output = usage.OutputTokens,
            CacheWrite = usage.CacheCreationInputTokens ?? 0,
            CacheRead = usage.CacheReadInputTokens ?? 0,
            WebSearches = usage.ServerToolUse?.WebSearchRequests ?? 0,
        });
        conversation.Usage.Input += record.Input + record.CacheWrite;
        conversation.Usage.Output += record.Output;
        conversation.Usage.CacheRead += record.CacheRead;
        conversation.Usage.CacheWrite += record.CacheWrite;
        conversation.Usage.CostUsd += record.CostUsd ?? 0;
    }

    public async Task<string?> GenerateTitleAsync(Conversation conversation)
    {
        var messages = conversation.CopyMessages();
        var user = WireText.Of(messages.FirstOrDefault(m => (string?)m["role"] == "user")?["content"]);
        var assistant = string.Join("\n", messages.Where(m => (string?)m["role"] == "assistant").Select(m => WireText.Of(m["content"])));
        if (user.Length == 0 && assistant.Length == 0) return null;
        // タイトル用途なので長文は頭だけで十分
        static string Excerpt(string s) => s.Length > 2000 ? s[..2000] : s;

        var response = await client.Messages.Create(new NonBeta.MessageCreateParams
        {
            Model = TitleModel,
            MaxTokens = 1024,
            OutputConfig = new NonBeta.OutputConfig { Effort = NonBeta.Effort.Low },
            System = "会話の内容を表す短い日本語タイトルを20文字以内で1つだけ出力してください。記号や引用符、前置きは付けないでください。",
            Messages = [new() { Role = NonBeta.Role.User, Content = $"ユーザー:\n{Excerpt(user)}\n\nアシスタント:\n{Excerpt(assistant)}" }],
        });
        await ledger.RecordAsync(new UsageRecord
        {
            At = DateTimeOffset.UtcNow,
            ConversationId = conversation.Id,
            Kind = "title",
            Model = TitleModel,
            Input = response.Usage.InputTokens,
            Output = response.Usage.OutputTokens,
            CacheWrite = response.Usage.CacheCreationInputTokens ?? 0,
            CacheRead = response.Usage.CacheReadInputTokens ?? 0,
        });
        if (response.StopReason?.Raw() == "refusal") return null;
        var title = string.Concat(response.Content.Select(b => b.Value).OfType<NonBeta.TextBlock>().Select(t => t.Text)).Trim();
        if (title.Length == 0 || title == Conversation.DefaultTitle) return null;
        return title.Length > 40 ? title[..40] : title;
    }
}
