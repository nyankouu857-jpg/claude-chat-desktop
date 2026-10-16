using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using NonBeta = Anthropic.Models.Messages;

namespace ClaudeChat.Core;

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

public sealed class ChatEngine(AnthropicClient client, ToolRegistry tools, UsageLedger ledger, SettingsStore settings, Hooks hooks, AppPaths paths)
{
    // 応答中の料金を画面に出し、サブエージェントの分は起動した会話にも足す。ChatHub が渡す
    public Func<Conversation, decimal, Task>? Charged { get; init; }
    // 大きな PDF を Files API の ID に差し替えて送る
    public Uploads? Uploads { get; init; }

    readonly AutoReviewer reviewer = new(client, ledger);
    const string TitleModel = "claude-haiku-5-5";
    // API の下限
    const int MinCompactThreshold = 50_000;
    // ツールがあると要約中にツールを呼んで要約が空になることがあるため、既定の指示を差し替える
    const string CompactInstructions = "Summarize the transcript inside <summary></summary> tags. Include everything needed to continue the conversation in the next context window: the user's goals and preferences, decisions made, facts and numbers established, file paths and commands used, and anything still pending. Write the summary in the language of the conversation. Do not call any tools while writing this summary; respond with text only.";

    const string CompactOnDemandBeta = "compact-2026-09-04";
    // 直近のやり取りは要約せずにそのまま残す。続きの細部が伝わり、残した分の思考も使い続けられる
    const int KeepRecentTurns = 2;

    static bool IsHaiku(string model) => model.StartsWith("claude-haiku");
    // コード実行のコンテナで Word / Excel / PowerPoint / PDF を作るための公式スキル
    static readonly string[] OfficeSkillIds = ["docx", "xlsx", "pptx", "pdf"];

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

    // claude.ai や Claude Code と同じく、コネクタのツールがこれ以上あると、定義を最初から見せずにツール検索で探させる。
    // 公式の目安では、30 から 50 個を超えると選び間違いが増え、定義だけで会話が埋まる
    const int DeferToolsAbove = 20;

    static BetaToolUnion ToTool(ToolSpec spec, bool deferred)
    {
        if (spec.Native is { } native) return FromJson<BetaToolUnion>(native);
        var json = new JsonObject
        {
            ["type"] = "custom",
            ["name"] = spec.ApiName,
            ["description"] = $"[{spec.Source}] {spec.Description}",
            ["input_schema"] = spec.InputSchema.DeepClone(),
            // 大きな入力も生成と同時に流れてくるようにする
            ["eager_input_streaming"] = true,
        };
        if (deferred) json["defer_loading"] = true;
        return FromJson<BetaToolUnion>(json);
    }

    static T FromJson<T>(JsonObject json) => JsonSerializer.Deserialize<T>(json.ToJsonString())!;

    // 検索結果を地域に合わせる。国コードは対応外だと 400 になるので、確実に分かるタイムゾーンだけ渡す
    static readonly string? LocalTimeZone = TimeZoneInfo.Local.HasIanaId
        ? TimeZoneInfo.Local.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : null;

    // 動的フィルタ版はコード実行の 20260120 以降と一緒に使え、そのときコード実行は無料になる。
    // Haiku はプログラムからのツール呼び出しに対応していないので基本版にする
    static IEnumerable<BetaToolUnion> ServerTools(ChatOptions options)
    {
        var basic = IsHaiku(options.Model);
        if (options.WebSearch)
        {
            var search = new JsonObject { ["type"] = basic ? "web_search_20250305" : "web_search_20260209", ["name"] = "web_search", ["max_uses"] = options.WebSearchMaxUses };
            if (LocalTimeZone is { } zone) search["user_location"] = new JsonObject { ["type"] = "approximate", ["timezone"] = zone };
            yield return FromJson<BetaToolUnion>(search);
        }
        if (options.WebFetch)
        {
            yield return FromJson<BetaToolUnion>(new JsonObject
            {
                ["type"] = basic ? "web_fetch_20250910" : "web_fetch_20260209",
                ["name"] = "web_fetch",
                ["max_uses"] = options.WebFetchMaxUses,
                // 取得したページも検索と同じく出典を示させる。長いページで文脈を埋めないよう上限を置く
                ["citations"] = new JsonObject { ["enabled"] = true },
                ["max_content_tokens"] = 100_000,
            });
        }
        if (options.CodeExecution)
        {
            yield return FromJson<BetaToolUnion>(new JsonObject { ["type"] = "code_execution_20260521", ["name"] = "code_execution" });
        }
    }

    static bool UsesMessageEffort(Conversation conversation, ChatOptions options) =>
        ModelCatalog.SupportsMessageEffort(options.Model) && conversation.TopEffort is not null;

    static bool UsesProgressUpdates(ChatOptions options) =>
        options.ThinkingDisplay == "updates" && ModelCatalog.SupportsProgressUpdates(options.Model);

    // バッチは高速モードに対応していない
    static bool UsesFast(ChatOptions options) => options.Fast && !options.Batch && ModelCatalog.SupportsFast(options.Model);

    static MessageCreateParams BuildRequest(Conversation conversation, ChatOptions options, IReadOnlyList<ToolSpec> toolSpecs, List<JsonObject>? messages = null, bool compacting = false)
    {
        // その場で要約した塊を持つ会話には、サーバー側の自動要約を一緒に付けられない
        var onDemand = compacting || conversation.CompactionBlock is not null;
        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            // 上限は付けたくないが必須の値なので、そのモデルが受け付ける最大にする。料金は実際に出た分だけ
            MaxTokens = ModelCatalog.MaxOutputTokens(options.Model),
            Messages = (messages ?? conversation.CopyApiMessages(UsesMessageEffort(conversation, options))).Select(ToParam).ToList(),
            // 日付入りのシステムプロンプトやツールの増減で前の思考ブロックが無効になっても 400 にしない
            Thinking = ModelCatalog.UsesThinkingBudget(options.Model)
                ? new BetaThinkingConfigEnabled
                {
                    BudgetTokens = ModelCatalog.ThinkingBudget(options.Effort),
                    BlockBinding = new BetaThinkingBlockBinding { PrefixMismatchBehavior = BetaThinkingPrefixMismatchBehavior.DropBlock },
                }
                : new BetaThinkingConfigAdaptive
                {
                    Display = UsesProgressUpdates(options) ? Display.Updates : Display.Summarized,
                    BlockBinding = new BetaThinkingBlockBinding { PrefixMismatchBehavior = BetaThinkingPrefixMismatchBehavior.DropBlock },
                },
            CacheControl = UsesLongCache(conversation, options) ? new BetaCacheControlEphemeral { Ttl = Ttl.Ttl1h } : new BetaCacheControlEphemeral(),
        };
        var format = options.OutputSchema is { } schema ? FromJson<BetaJsonOutputFormat>(new JsonObject { ["type"] = "json_schema", ["schema"] = schema.DeepClone() }) : null;
        // 思考の量をトークン数で決める世代は effort を受け付けないので、項目ごと送らない
        if (!ModelCatalog.UsesThinkingBudget(options.Model))
        {
            // 途中で変えた effort は履歴の中の印で伝え、上は最初の値のままにしてキャッシュを保つ
            var effort = UsesMessageEffort(conversation, options) ? conversation.TopEffort! : options.Effort;
            parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = ParseEffort(ModelCatalog.EffortFor(options.Model, effort)), Format = format } };
        }
        else if (format is not null)
        {
            parameters = parameters with { OutputConfig = new BetaOutputConfig { Format = format } };
        }
        if (ModelCatalog.SupportsCompaction(options.Model) && !onDemand)
        {
            parameters = parameters with
            {
                ContextManagement = new BetaContextManagementConfig
                {
                    Edits =
                    [
                        new BetaCompact20260112Edit
                        {
                            Trigger = new BetaInputTokensTrigger { ValueValue = Math.Max(MinCompactThreshold, options.CompactThreshold) },
                            Instructions = CompactInstructions,
                        },
                    ],
                },
            };
        }
        if (!string.IsNullOrWhiteSpace(options.SystemPrompt)) parameters = parameters with { System = options.SystemPrompt };

        var toolList = ServerTools(options).ToList();
        var deferExternal = toolSpecs.Count(t => t.External) > DeferToolsAbove;
        if (deferExternal) toolList.Add(FromJson<BetaToolUnion>(new JsonObject { ["type"] = "tool_search_tool_bm25_20251119", ["name"] = "tool_search_tool_bm25" }));
        toolList.AddRange(toolSpecs.Select(t => ToTool(t, deferExternal && t.External)));
        if (toolList.Count > 0) parameters = parameters with { Tools = toolList };

        List<ApiEnum<string, AnthropicBeta>> betas = ["thinking-binding-controls-2026-08-01"];
        if (ModelCatalog.SupportsCompaction(options.Model)) betas.Add("compact-2026-01-12");
        if (onDemand) betas.Add(CompactOnDemandBeta);
        // トークン数で思考する世代は、ツールの合間に考えるのにこの印が要る
        if (ModelCatalog.UsesThinkingBudget(options.Model)) betas.Add("interleaved-thinking-2025-05-14");
        // 印を使わない回も付けておく。ベータの組み合わせが変わると比べられなくなる
        if (ModelCatalog.SupportsMessageEffort(options.Model)) betas.Add("mid-conversation-output-config-2026-07-01");
        if (UsesProgressUpdates(options)) betas.Add("thinking-display-updates-2026-08-18");
        if (UsesFast(options))
        {
            betas.Add("fast-mode-2026-02-01");
            parameters = parameters with { Speed = Speed.Fast };
        }
        if (options.CodeExecution && options.OfficeSkills)
        {
            // コンテナでスキルを使うにはコード実行のベータも要る。スキル側の印は古い API のために残す
            betas.Add("code-execution-2025-08-25");
            betas.Add("skills-2025-10-02");
            parameters = parameters with
            {
                Container = new BetaContainerParams
                {
                    ID = conversation.ContainerId,
                    Skills = OfficeSkillIds.Select(id => new BetaSkillParams { Type = BetaSkillParamsType.Anthropic, SkillID = id, Version = "latest" }).ToList(),
                },
            };
        }
        else if (conversation.ContainerId is { } containerId)
        {
            // 同じコンテナを使い続けると、前に作ったファイルや変数が残っている
            parameters = parameters with { Container = containerId };
        }
        // Haiku や前の世代はサーバー側のフォールバックを受け付けない
        if (ModelCatalog.SupportsDefaultFallback(options.Model))
        {
            betas.Add("server-side-fallback-2026-07-01");
            parameters = parameters with { Fallbacks = new Default() };
        }
        return parameters with { Betas = betas };
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
        // 高くつく送信の確認は 1 回の応答で 1 度だけ。ツールを使うたびに聞かない
        var costConfirmed = conversation.ParentId is not null;
        // 止めたときに、実行済みのツールの結果を捨てないため
        var finishedResults = new JsonArray();
        // 途中で止まったリクエストも課金されるので、届いた分の使用量を覚えておく
        JsonObject? streamUsage = null;
        // コンテナは時間が経つと消えるので、断られたら 1 度だけ新しく作り直す
        var containerRetried = false;
        var transientRetries = 0;
        var uploadsDropped = false;

        async Task<BetaMessage> StreamAsync(MessageCreateParams request, CancellationToken requestToken)
        {
            var aggregator = new BetaMessageContentAggregator();
            var toolInputJson = new Dictionary<int, string>();
            var stream = client.Beta.Messages.CreateStreaming(request, requestToken);
            await using var events = stream.CollectAsync(aggregator).GetAsyncEnumerator(requestToken);
            if (!await events.MoveNextAsync()) throw new InvalidOperationException("API から空の応答が返りました");
            do
            {
                var streamEvent = events.Current;
                if (streamEvent.TryPickStart(out var messageStart))
                {
                    streamUsage = ToWire(messageStart.Message.Usage);
                }
                else if (streamEvent.TryPickDelta(out var messageDelta) && streamUsage is not null)
                {
                    foreach (var (key, value) in ToWire(messageDelta.Usage).Where(kv => kv.Value is not null).ToList()) streamUsage[key] = value!.DeepClone();
                }
                else if (streamEvent.TryPickContentBlockStart(out var start))
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
                    else if (delta.Delta.TryPickCompaction(out var compaction))
                    {
                        // 要約は 1 回でまとめて届く。画面で「要約に失敗」と出さないよう流す
                        if ((string?)ToWire(compaction)["content"] is { } summary) emit(new JsonObject { ["type"] = "delta", ["index"] = index, ["text"] = summary });
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
            while (await events.MoveNextAsync());
            return aggregator.Message();
        }

        try
        {
            // 落ちたり止めたりして結果の無いツール呼び出しが履歴に残っていると、以後ずっと 400 になる
            conversation.RepairToolUses("前の応答が途中で終わったため、結果を受け取れませんでした");
            if (ModelCatalog.SupportsMessageEffort(options.Model)) conversation.SetEffort(options.Effort);
            var mode = conversation.PermissionMode;
            for (var turn = 0; turn < options.MaxToolTurns; turn++)
            {
                // 計画モードを抜けたのに「計画モード中」と言い続けないよう、指示を作り直す
                if (conversation.PermissionMode != mode && options.RebuildSystemPrompt is { } rebuild)
                {
                    mode = conversation.PermissionMode;
                    options = options with { SystemPrompt = rebuild() };
                }
                // 上限に届いていたら、要約も含めて何も送らない
                if (await BudgetLimitAsync() is { } limitReached) return new ChatRunResult(ChatStatus.Failed, limitReached);
                await CompactIfLargeAsync(conversation, options, emit, cancellationToken);
                if (!costConfirmed && await ConfirmCostAsync(conversation, options, gate, cancellationToken) is { } approved)
                {
                    if (!approved)
                    {
                        Notice(emit, "料金が高くなりそうなので、送るのをやめました");
                        return new ChatRunResult(ChatStatus.Done);
                    }
                    costConfirmed = true;
                }
                if (options.Hurry?.Take() == true) options = Hurried(conversation, options, emit);
                var toolSpecs = tools.GetTools(options.EnabledSources);
                partialText.Clear();
                emit(new JsonObject { ["type"] = "message-start" });
                var outgoing = conversation.CopyApiMessages(UsesMessageEffort(conversation, options));
                var uploaded = Uploads is not null && !uploadsDropped && (await settings.GetAsync()).Chat.UploadLargePdfs
                    ? await Uploads.SubstituteAsync(client, conversation, outgoing, cancellationToken)
                    : [];
                if (Uploads is not null && !uploadsDropped && options.CodeExecution) uploaded.AddRange(await Uploads.SubstituteOfficeAsync(client, conversation, outgoing, cancellationToken));
                var request = BuildRequest(conversation, options, toolSpecs, outgoing);
                // 前の応答と比べて、キャッシュが外れた理由を返してもらう
                if (!options.Batch) request = request with { Diagnostics = new BetaDiagnosticsParam { PreviousMessageID = conversation.LastResponseId } };
                BetaMessage message;
                var requestToken = options.Hurry?.Begin(cancellationToken) ?? cancellationToken;
                try
                {
                    message = options.Batch ? await SendBatchAsync(conversation, request, emit, cancellationToken) : await StreamAsync(request, requestToken);
                    transientRetries = 0;
                }
                catch (Exception) when (options.Hurry?.Pending == true && !cancellationToken.IsCancellationRequested)
                {
                    // 考えている途中の分は捨てて、次の周で effort を下げて送り直す
                    await RecordInterruptedUsageAsync(conversation, options.Model, streamUsage);
                    streamUsage = null;
                    partialText.Clear();
                    emit(new JsonObject { ["type"] = "discard" });
                    turn--;
                    continue;
                }
                catch (Exception e) when (e is AnthropicBadRequestException or AnthropicNotFoundException && uploaded.Count > 0 && e.Message.Contains("file", StringComparison.OrdinalIgnoreCase))
                {
                    // 上げたファイルが期限切れや別の組織のキーで見えないときは、この応答では中身ごと送る
                    await Uploads!.ForgetAsync(uploaded);
                    uploadsDropped = true;
                    partialText.Clear();
                    emit(new JsonObject { ["type"] = "discard" });
                    turn--;
                    continue;
                }
                catch (AnthropicRateLimitException) when (UsesFast(options) && !cancellationToken.IsCancellationRequested)
                {
                    // 高速モードには別枠の上限がある。待つより通常の速さで続けたほうが早い
                    await RecordInterruptedUsageAsync(conversation, options.Model, streamUsage);
                    streamUsage = null;
                    partialText.Clear();
                    emit(new JsonObject { ["type"] = "discard" });
                    options = options with { Fast = false };
                    Notice(emit, "高速モードの利用上限に達したので、この応答は通常の速さで続けます");
                    turn--;
                    continue;
                }
                catch (AnthropicBadRequestException e) when (!containerRetried && conversation.ContainerId is not null && e.Message.Contains("container", StringComparison.OrdinalIgnoreCase))
                {
                    containerRetried = true;
                    conversation.ContainerId = null;
                    turn--;
                    continue;
                }
                catch (Exception e) when (IsTransient(e) && transientRetries < TransientRetryDelays.Length && !cancellationToken.IsCancellationRequested)
                {
                    // 途中まで流れた分も課金されている。捨てる前に記録し、画面の途中の文も消す
                    await RecordInterruptedUsageAsync(conversation, options.Model, streamUsage);
                    streamUsage = null;
                    // 待っている間に止められても、捨てた途中の文を答えとして残さない
                    partialText.Clear();
                    emit(new JsonObject { ["type"] = "discard" });
                    var wait = TransientRetryDelays[transientRetries++];
                    Notice(emit, $"API が混み合っているか、接続が切れました。{wait.TotalSeconds:0} 秒待ってやり直します ({transientRetries}/{TransientRetryDelays.Length})");
                    await Task.Delay(wait, cancellationToken);
                    turn--;
                    continue;
                }
                finally
                {
                    options.Hurry?.End();
                }
                conversation.LastResponseId = message.ID;
                NoticeCacheMiss(message.Diagnostics, emit);
                var content = new JsonArray(message.Content.Select(b => (JsonNode)ToWire(b)).ToArray());
                // 画面の「↓ 1.2k tokens」を、流れてきた文字からの見積もりから本当の数に置き換える
                emit(new JsonObject { ["type"] = "usage", ["output"] = message.Usage.OutputTokens });
                // 途中で断られた応答は完成した回答として扱わず捨てる。別モデルが書き直した (fallback) ときは残す
                if (message.StopReason?.Raw() == "refusal" && !content.OfType<JsonObject>().Any(b => (string?)b["type"] == "fallback"))
                {
                    partialText.Clear();
                    streamUsage = null;
                    await RecordUsageAsync(conversation, message.Model.Raw() ?? options.Model, "chat", ToWire(message.Usage), options.Batch);
                    var category = (string?)ToWire(message)["stop_details"]?["category"];
                    Notice(emit, category is null ? "Claude がこのリクエストへの応答を断りました" : $"Claude がこのリクエストへの応答を断りました ({category})");
                    emit(new JsonObject { ["type"] = "discard" });
                    return new ChatRunResult(ChatStatus.Done);
                }
                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = content };
                conversation.Append(assistant);
                partialText.Clear();
                emit(new JsonObject { ["type"] = "message", ["message"] = assistant.DeepClone() });
                streamUsage = null;
                await RecordUsageAsync(conversation, message.Model.Raw() ?? options.Model, "chat", ToWire(message.Usage), options.Batch);
                if (message.Container?.ID is { } container) conversation.ContainerId = container;
                await DownloadFilesAsync(conversation, content, emit, cancellationToken);

                var stopReason = message.StopReason?.Raw();
                if (stopReason == "pause_turn") continue;

                var toolUses = content.OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_use").ToList();
                if (toolUses.Count > 0)
                {
                    finishedResults = new JsonArray();
                    var results = stopReason == "tool_use"
                        ? await RunToolsAsync(conversation, toolUses, options, emit, gate, finishedResults, cancellationToken)
                        // 出力上限で切れたツール入力は信用できないので実行しない
                        : ErrorResults(toolUses, "出力上限で入力が途中までしか届かなかったため、実行しませんでした");
                    var user = new JsonObject { ["role"] = "user", ["content"] = results };
                    conversation.Append(user);
                    emit(new JsonObject { ["type"] = "message", ["message"] = user.DeepClone() });
                    if (stopReason == "tool_use")
                    {
                        // 応答中に送られた指示は、ツールの結果の後ろに続けて渡す。API は続いた user をひとつにまとめて読む
                        foreach (var text in options.TakeInterjections?.Invoke() ?? [])
                        {
                            var note = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
                            conversation.Append(note);
                            emit(new JsonObject { ["type"] = "message", ["message"] = note.DeepClone() });
                        }
                        continue;
                    }
                }

                if (stopReason == "max_tokens") Notice(emit, "出力上限に達したため途中で終わっています");
                if (stopReason == "model_context_window_exceeded")
                {
                    Notice(emit, "会話がモデルの扱える長さの上限に届いたため途中で終わっています。" + (ModelCatalog.SupportsCompaction(options.Model)
                        ? "次の送信で古い部分が要約されます"
                        : "/compact で要約するか、新しいチャットで続けてください"));
                }
                return new ChatRunResult(ChatStatus.Done);
            }
            Notice(emit, $"ツールの呼び出しが {options.MaxToolTurns} 回に達したので止めました。続けるには続きをお願いしてください");
            // 子が途中で止まったのを、親やワークフローが成功と取り違えないように
            return ToolContext.Current is { Depth: > 0 }
                ? new ChatRunResult(ChatStatus.Failed, $"ツールの呼び出しが {options.MaxToolTurns} 回に達し、作業が途中で止まりました")
                : new ChatRunResult(ChatStatus.Done);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            await RecordInterruptedUsageAsync(conversation, options.Model, streamUsage);
            // 思考ブロックは署名が揃わないと送り返せないので、出せた本文だけ残す
            var text = string.Concat(partialText.Values);
            if (!RepairDanglingToolUse(conversation, "ユーザーが中断しました", finishedResults) && text.Length > 0)
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
            await RecordInterruptedUsageAsync(conversation, options.Model, streamUsage);
            RepairDanglingToolUse(conversation, "エラーで中断しました", finishedResults);
            return new ChatRunResult(ChatStatus.Failed, Describe(error));
        }
    }

    async Task RecordInterruptedUsageAsync(Conversation conversation, string model, JsonObject? usage)
    {
        if (usage is null) return;
        try
        {
            await RecordUsageAsync(conversation, model, "chat", usage);
        }
        catch (Exception)
        {
            // 記録できなくても、止めた結果を返すほうを優先する
        }
    }

    // tool_use の直後に tool_result が無い履歴は次の送信で 400 になるので、結果を補っておく。実行済みの結果はそのまま残す
    public static bool RepairDanglingToolUse(Conversation conversation, string reason, JsonArray finished)
    {
        var last = conversation.LastMessage();
        if (last is null || (string?)last["role"] != "assistant") return false;
        var toolUses = (last["content"] as JsonArray ?? []).OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_use").ToList();
        if (toolUses.Count == 0) return false;
        var done = finished.OfType<JsonObject>().ToDictionary(r => (string?)r["tool_use_id"] ?? "");
        var results = new JsonArray(toolUses
            .Select(t => done.TryGetValue((string?)t["id"] ?? "", out var result) ? result.DeepClone() : ErrorResults([t], reason)[0]!.DeepClone())
            .ToArray());
        conversation.Append(new JsonObject { ["role"] = "user", ["content"] = results });
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

    const string HurryNote = "<hurry>ユーザーが「急いで」を押しました。考えすぎずに、今わかっている範囲で手短に進めてください。</hurry>";

    // effort を一段下げ、急ぐよう一言添えて続ける。印は添えた一言の前に入る
    static ChatOptions Hurried(Conversation conversation, ChatOptions options, Action<JsonObject> emit)
    {
        var lower = ModelCatalog.Efforts[Math.Max(0, Array.IndexOf(ModelCatalog.Efforts, options.Effort) - 1)];
        var note = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = HurryNote }) };
        conversation.Append(note);
        emit(new JsonObject { ["type"] = "message", ["message"] = note.DeepClone() });
        if (ModelCatalog.SupportsMessageEffort(options.Model)) conversation.SetEffort(lower);
        Notice(emit, lower == options.Effort ? "急ぐよう伝えました" : $"急ぐよう伝えて、effort を {options.Effort} から {lower} に下げました");
        return options with { Effort = lower };
    }

    // 少しの読み直しなら知らせない
    const long CacheMissNoticeAbove = 10_000;

    // 自分で変えた覚えのない料金の跳ね上がりを、理由つきで知らせる。モデルを替えたときは分かっているので黙る
    static void NoticeCacheMiss(BetaDiagnostics? diagnostics, Action<JsonObject> emit)
    {
        if (diagnostics?.CacheMissReason is null) return;
        var reason = ToWire(diagnostics)["cache_miss_reason"];
        var missed = (long?)reason?["cache_missed_input_tokens"] ?? 0;
        if (missed < CacheMissNoticeAbove) return;
        var why = (string?)reason?["type"] switch
        {
            "system_changed" => "指示 (システムプロンプト) が前回から変わった",
            "tools_changed" => "使えるツールの組み合わせが前回から変わった",
            "messages_changed" => "前の履歴が書き換わった",
            _ => null,
        };
        if (why is not null) Notice(emit, $"{why}ため、キャッシュが効かず約 {missed / 1000.0:0.#}k トークンを読み直しました。この回の料金はいつもより高くなります");
    }

    async Task<JsonArray> RunToolsAsync(
        Conversation conversation,
        List<JsonObject> toolUses,
        ChatOptions options,
        Action<JsonObject> emit,
        IApprovalGate gate,
        JsonArray results,
        CancellationToken cancellationToken)
    {
        // 並べて呼ばれたら同時に動かす。1 つずつ待つと並べた意味がない。結果の並びは呼ばれた順のまま
        var together = new List<Task<JsonObject>>();
        foreach (var toolUse in toolUses)
        {
            if (CanRunTogether(toolUse))
            {
                together.Add(RunToolAsync(toolUse));
                continue;
            }
            await CollectTogetherAsync();
            results.Add(await RunToolAsync(toolUse));
        }
        await CollectTogetherAsync();
        return results;

        async Task CollectTogetherAsync()
        {
            foreach (var result in await Task.WhenAll(together)) results.Add(result);
            together.Clear();
        }

        // 読むだけで確認も出ないものとサブエージェント。確認を挟むものは、カードが同時に何枚も出ないよう順番に動かす。
        // ブラウザは 1 つのタブを取り合うので、読むだけでも順番
        bool CanRunTogether(JsonObject toolUse)
        {
            var name = (string?)toolUse["name"] ?? "";
            if (name == AgentToolProvider.SpawnAgent) return true;
            if (tools.Find(name, options.EnabledSources) is not { } spec || spec.Source is BrowserToolProvider.Source or BrowserToolProvider.HiddenSource) return false;
            return (spec.ReadOnly && spec.AutoApprove) || spec.ReadOnlyCall?.Invoke(toolUse["input"] as JsonObject ?? []) == true;
        }

        async Task<JsonObject> RunToolAsync(JsonObject toolUse)
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
            else if (await hooks.RunPreToolUseAsync(conversation, apiName, input) is { Blocked: true } blocked)
            {
                outcome = ToolRegistry.Error($"PreToolUse フックが止めました: {blocked.Message}");
            }
            else if (spec.ApiName == AskToolProvider.ApiName && conversation.ParentId is not null && (await settings.GetAsync()).Agents.AutoApproveChildren)
            {
                // 子の質問は誰にも見られないまま止まる
                outcome = ToolRegistry.Error("サブエージェントはユーザーに質問できません。妥当な前提を置いて進め、その前提を最後の報告に書いてください");
            }
            else if (spec.ApiName == AskToolProvider.ApiName)
            {
                outcome = await AskAsync(spec, id, input, gate, cancellationToken);
            }
            else if (await DenialAsync(conversation, spec, id, input, options, gate, cancellationToken) is { } denial)
            {
                outcome = ToolRegistry.Error(denial);
            }
            else
            {
                emit(new JsonObject { ["type"] = "tool-running", ["toolUseId"] = id });
                outcome = await tools.CallAsync(apiName, input, options.EnabledSources, cancellationToken);
                var resultView = new JsonObject { ["content"] = outcome.Content.DeepClone(), ["is_error"] = outcome.IsError };
                if (await hooks.RunPostToolUseAsync(conversation, apiName, input, resultView) is { Message: { } feedback })
                {
                    var content = (JsonArray)outcome.Content.DeepClone();
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = $"<hook-feedback event=\"PostToolUse\">\n{feedback}\n</hook-feedback>" });
                    outcome = outcome with { Content = content };
                }
            }

            var result = new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = id,
                ["content"] = outcome.Content,
            };
            if (outcome.IsError) result["is_error"] = true;
            emit(new JsonObject { ["type"] = "tool-result", ["toolUseId"] = id, ["result"] = result.DeepClone() });
            if (spec?.Source is TodoToolProvider.Source or ArtifactToolProvider.Source or PlanToolProvider.Source)
            {
                emit(new JsonObject { ["type"] = "state", ["state"] = conversation.StateView() });
            }
            return result;
        }
    }

    // 承認と同じ待ち方で画面に質問を出す。答えは JSON の文字列、答えずに進めるなら deny が返る
    static async Task<ToolCallResult> AskAsync(ToolSpec spec, string toolUseId, JsonObject input, IApprovalGate gate, CancellationToken cancellationToken)
    {
        if (AskToolProvider.Validate(input) is { } problem) return ToolRegistry.Error(problem);
        var reply = await gate.RequestAsync(new JsonObject
        {
            ["kind"] = "question",
            ["toolUseId"] = toolUseId,
            ["apiName"] = spec.ApiName,
            ["source"] = spec.Source,
            ["tool"] = spec.Name,
            ["title"] = spec.Title,
            ["input"] = input.DeepClone(),
        }, cancellationToken);
        if (reply == "deny") return ToolJson.Text("ユーザーは質問に答えずに進めるよう選びました。分かっている範囲で妥当な判断をして続けてください");
        return ToolJson.Text(AskToolProvider.FormatAnswers(input, JsonNode.Parse(reply) as JsonArray ?? []));
    }

    // ワークフローは料金、予定は後で勝手に動く指示、メモリは今後のシステムプロンプト、ページの JavaScript は中身が読めないので、
    // auto でも本人に決めてもらう。メモリの書き込みがここに来るのは、本人が確認するよう設定したときだけ
    static bool AlwaysAskInAuto(ToolSpec spec) =>
        spec.ApiName is AgentToolProvider.RunWorkflow or ScheduleToolProvider.Create
        || spec.Source == MemoryToolProvider.Source
        || spec.Name == "evaluate" && spec.Source is BrowserToolProvider.Source or BrowserToolProvider.HiddenSource;

    // 実行してよければ null、だめなら Claude に返す理由
    async Task<string?> DenialAsync(Conversation conversation, ToolSpec spec, string toolUseId, JsonObject input, ChatOptions options, IApprovalGate gate, CancellationToken cancellationToken)
    {
        var current = await settings.GetAsync();
        var readOnlyCall = spec.ReadOnlyCall?.Invoke(input) == true;
        switch (conversation.PermissionMode)
        {
            case "plan" when !spec.ReadOnly && !readOnlyCall:
                return $"計画モード中なので {spec.ApiName} は使えません。調べるだけにとどめ、計画ができたら {PlanToolProvider.ExitPlanMode} でユーザーに見せてください";
            case "bypass" when current.Features.AllowBypassMode:
                return null;
            // ワークツリーは git を動かすので、編集の自動許可には含めない
            case "acceptEdits" when spec.Source == FileToolProvider.Source && spec.Name is not ("create_worktree" or "exit_worktree"):
                return null;
        }
        var preApproved = spec.AutoApprove || readOnlyCall || (spec.AllowAlways && current.ToolPermissions.GetValueOrDefault(spec.ApiName) == "always")
            || (spec.ApiName is TerminalToolProvider.RunCommandApiName or TerminalToolProvider.StartBackgroundApiName
                && TerminalToolProvider.MatchesRule((string?)input["command"] ?? "", current.Terminal.AllowedCommands));
        if (conversation.ParentId is not null && current.Agents.AutoApproveChildren)
        {
            // 計画を抜ける許可までは子に出させない。計画は親が受け取ってユーザーに見せる
            if (spec.ApiName == PlanToolProvider.ExitPlanMode) return "サブエージェントは計画モードを抜けられません。計画は最後のメッセージにまとめて親に返してください";
            // 起動するときに人が 1 回確かめている (ワークフローは run_workflow の確認)。作業中は人に聞かない
            if (preApproved || conversation.PermissionMode != "auto") return null;
            // auto の子は確認役に見てもらい、止められたものは人に聞かずに断る
            if (AlwaysAskInAuto(spec)) return $"サブエージェントの「自動で判断」では {spec.ApiName} は使えません。必要なら最後の報告で親に頼んでください";
            var childVerdict = await reviewer.ReviewAsync(conversation, spec, input, await tools.DescribeTargetAsync(spec.ApiName, input, options.EnabledSources, cancellationToken), cancellationToken);
            return childVerdict.Allowed ? null : $"自動の判定で止めました: {childVerdict.Reason}。必要なら最後の報告で親に頼んでください";
        }
        if (preApproved) return null;

        string? autoReason = null;
        if (conversation.PermissionMode == "auto" && !AlwaysAskInAuto(spec))
        {
            var target = await tools.DescribeTargetAsync(spec.ApiName, input, options.EnabledSources, cancellationToken);
            var verdict = await reviewer.ReviewAsync(conversation, spec, input, target, cancellationToken);
            if (verdict.Allowed) return null;
            autoReason = verdict.Reason;
        }

        var decision = await gate.RequestAsync(new JsonObject
        {
            ["toolUseId"] = toolUseId,
            ["apiName"] = spec.ApiName,
            ["source"] = spec.Source,
            ["tool"] = spec.Name,
            ["title"] = spec.Title,
            ["description"] = spec.Description,
            ["allowAlways"] = spec.AllowAlways,
            ["autoReason"] = autoReason,
            ["input"] = input.DeepClone(),
        }, cancellationToken);

        if (decision == "always" && spec.AllowAlways) await settings.UpdateAsync(s => s.ToolPermissions[spec.ApiName] = "always");
        if (decision.StartsWith("rule:"))
        {
            var rule = decision["rule:".Length..];
            await settings.UpdateAsync(s =>
            {
                if (!s.Terminal.AllowedCommands.Contains(rule)) s.Terminal.AllowedCommands.Add(rule);
            });
            return null;
        }
        if (decision is "once" or "always") return null;
        return spec.ApiName == PlanToolProvider.ExitPlanMode
            ? "ユーザーは計画を承認しませんでした。計画モードのまま、どこを変えればよいかユーザーに確認してください"
            : "ユーザーがこのツールの実行を許可しませんでした。別の方法を考えるか、ユーザーに確認してください";
    }

    // コード実行で作られたファイルは API 側にしか無いので、届いたらすぐ手元へ落としておく
    async Task DownloadFilesAsync(Conversation conversation, JsonArray content, Action<JsonObject> emit, CancellationToken cancellationToken)
    {
        var fileIds = content.OfType<JsonObject>()
            .Where(b => ((string?)b["type"])?.EndsWith("_tool_result") == true)
            .SelectMany(b => FileIdsIn(b))
            .Distinct()
            .Where(id => !conversation.HasFile(id))
            .ToList();
        foreach (var fileId in fileIds)
        {
            try
            {
                var metadata = await client.Beta.Files.RetrieveMetadata(fileId, cancellationToken: cancellationToken);
                var dir = Path.Combine(conversation.Incognito ? AppPaths.IncognitoFilesDir : paths.FilesDir, conversation.Id);
                Directory.CreateDirectory(dir);
                var path = UniquePath(dir, SafeFileName(metadata.Filename));
                using (var response = await client.Beta.Files.Download(fileId, cancellationToken: cancellationToken))
                await using (var source = await response.ReadAsStream(cancellationToken))
                await using (var target = File.Create(path))
                {
                    await source.CopyToAsync(target, cancellationToken);
                }
                conversation.AddFile(fileId, path);
                emit(new JsonObject { ["type"] = "file", ["fileId"] = fileId, ["name"] = Path.GetFileName(path), ["path"] = path });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Notice(emit, $"作成されたファイル {fileId} を保存できませんでした: {Describe(e)}");
            }
        }
    }

    static IEnumerable<string> FileIdsIn(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((string?)obj["file_id"] is { Length: > 0 } id) yield return id;
                foreach (var (_, value) in obj)
                {
                    foreach (var nested in FileIdsIn(value)) yield return nested;
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var nested in FileIdsIn(item)) yield return nested;
                }
                break;
        }
    }

    static string SafeFileName(string name)
    {
        var cleaned = string.Concat(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "file" : cleaned;
    }

    static string UniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
        return path;
    }

    // 要約やフォールバックの分は iterations にしか載らないので、あればそちらを使う。
    // 長い入力の割増はリクエストごとに決まるので、まとめずに 1 回ずつ記録する
    async Task RecordUsageAsync(Conversation conversation, string model, string kind, JsonObject usage, bool batch = false)
    {
        var iterations = (usage["iterations"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (iterations.Count == 0) iterations = [usage];
        var webSearches = (long?)usage["server_tool_use"]?["web_search_requests"] ?? 0;
        var charged = 0m;
        foreach (var iteration in iterations)
        {
            var written = (long?)iteration["cache_creation_input_tokens"] ?? 0;
            var written1h = Math.Min(written, (long?)iteration["cache_creation"]?["ephemeral_1h_input_tokens"] ?? 0);
            var record = await ledger.RecordAsync(new UsageRecord
            {
                At = DateTimeOffset.UtcNow,
                ConversationId = conversation.Incognito ? null : conversation.Id,
                Kind = kind,
                Model = (string?)iteration["model"] ?? model,
                Input = (long?)iteration["input_tokens"] ?? 0,
                Output = (long?)iteration["output_tokens"] ?? 0,
                CacheWrite = written - written1h,
                CacheWrite1h = written1h,
                Batch = batch,
                Fast = (string?)usage["speed"] == "fast",
                ParentId = conversation.Incognito ? null : conversation.ParentId,
                CacheRead = (long?)iteration["cache_read_input_tokens"] ?? 0,
                WebSearches = webSearches,
            });
            webSearches = 0;
            conversation.Usage.Add(record.Input + written, record.Output, record.CacheRead, written, record.CostUsd ?? 0);
            charged += record.CostUsd ?? 0;
            // 右の「コンテキスト」用に、最後に送った会話の大きさを覚えておく
            if (kind == "chat") conversation.Usage.Context = record.Input + record.CacheRead + written;
        }
        if (charged > 0 && Charged is not null)
        {
            try
            {
                await Charged(conversation, charged);
            }
            catch (Exception)
            {
                // 画面や親への知らせに失敗しても、払い済みの応答を失敗にしない
            }
        }
    }

    // 要約や脇道の質問は長くならない。トークン数で思考する世代は、思考の分より多くないと受け付けられない
    static int AsideMaxTokens(ChatOptions options) =>
        ModelCatalog.UsesThinkingBudget(options.Model) ? Math.Max(16000, ModelCatalog.ThinkingBudget(options.Effort) + 8000) : 16000;

    static bool IsOnlyCompaction(JsonObject message) =>
        message["content"] is JsonArray blocks && blocks.OfType<JsonObject>().All(b => (string?)b["type"] == "compaction");

    static bool HasCompaction(JsonObject message) =>
        (message["content"] as JsonArray)?.OfType<JsonObject>().Any(b => (string?)b["type"] == "compaction") == true;

    // 要約する範囲の終わり。ツールの呼び出しと結果は同じ側に置く。
    // 応答の途中なら、最後のツールの呼び出しとその結果 (または新しい発言) だけを残す
    static int CompactCut(List<JsonObject> messages, int start, bool midTurn)
    {
        if (midTurn)
        {
            var endsWithResults = messages.Count > 0 && (string?)messages[^1]["role"] == "user" && !Conversation.IsHumanTurn(messages[^1]);
            return Math.Max(start, messages.Count - (endsWithResults ? 2 : 1));
        }
        var humans = Enumerable.Range(start, messages.Count - start).Where(i => Conversation.IsHumanTurn(messages[i])).ToList();
        return humans.Count > KeepRecentTurns ? humans[^KeepRecentTurns] : messages.Count;
    }

    // API の「その場で要約」。直近のやり取りを残して、それより前を要約の塊 1 つに置き換える。要約できなかったら false
    public async Task<bool> CompactOnDemandAsync(Conversation conversation, ChatOptions options, string? instructions, bool midTurn, CancellationToken cancellationToken)
    {
        if (!ModelCatalog.SupportsCompaction(options.Model)) return false;
        var all = conversation.CopyMessages();
        var start = Math.Min(conversation.ApiStart, all.Count);
        var cut = CompactCut(all, start, midTurn);
        // 残す側に自動の要約の塊があると、送る塊が 2 つになって断られる。そのときは全部を畳む
        if (all.Skip(cut).Any(HasCompaction)) cut = all.Count;
        var older = new List<JsonObject>();
        if (conversation.CompactionBlock is { } previous) older.Add(new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(previous.DeepClone()) });
        older.AddRange(all.Skip(start).Take(cut - start));
        // 自動の要約の塊が途中にあれば、API はそれより前を読んでいない。塊を先頭にして要約し直す
        var latest = older.FindLastIndex(HasCompaction);
        if (latest > 0) older.RemoveRange(0, latest);
        if (latest >= 0 && older[0]["content"] is JsonArray head)
        {
            while (head.Count > 0 && (string?)head[0]?["type"] != "compaction") head.RemoveAt(0);
        }
        if (older.All(IsOnlyCompaction)) return false;

        var prompt = string.IsNullOrWhiteSpace(instructions) ? CompactInstructions : $"{CompactInstructions}\nAdditional instructions from the user: {instructions.Trim()}";
        var request = BuildRequest(conversation, options, tools.GetTools(options.EnabledSources), older, compacting: true) with
        {
            MaxTokens = AsideMaxTokens(options),
            Compaction = new BetaCompactionConfig { Instructions = prompt },
        };
        var message = await client.Beta.Messages.Create(request, cancellationToken);
        var usage = ToWire(message.Usage);
        await RecordUsageAsync(conversation, message.Model.Raw() ?? options.Model, "compact", usage);
        var block = message.Content.Select(b => ToWire(b)).FirstOrDefault(b => (string?)b["type"] == "compaction");
        if (message.StopReason?.Raw() != "compaction" || block is null) return false;
        conversation.ApplyCompaction(block, cut);
        // 次の見積もりが要約前の大きさのままだと、すぐまた要約してしまう
        conversation.Usage.Context = (usage["iterations"] as JsonArray)?.OfType<JsonObject>().Sum(i => (long?)i["output_tokens"] ?? 0) ?? 0;
        return true;
    }

    static readonly JsonSerializerOptions Unescaped = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // 英数字はおよそ 4 文字で 1 トークン、日本語などはおよそ 1 文字で 1 トークン
    static long EstimateTokens(string text)
    {
        long ascii = 0, other = 0;
        foreach (var c in text)
        {
            if (c < 128) ascii++;
            else other++;
        }
        return ascii / 4 + other;
    }

    // 次に送る大きさの見積もり。前回送った分と、そのあと増えた分 (前回の返答と、ツールの結果や新しい発言)
    static (long Previous, long Added) PromptSize(Conversation conversation)
    {
        var messages = conversation.CopyApiMessages();
        var lastAssistant = Math.Max(0, messages.FindLastIndex(m => (string?)m["role"] == "assistant"));
        return (conversation.Usage.Context, messages.Skip(lastAssistant).Sum(m => EstimateTokens(m.ToJsonString(Unescaped))));
    }

    // プロンプトのキャッシュ (ephemeral) が残る時間。長い会話は 1 時間持たせる
    static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    static readonly TimeSpan LongCacheLifetime = TimeSpan.FromHours(1);

    // 書き込みは 5 分のものより高いが、休憩を挟んで同じ長い会話を続けても読み直しが安いまま
    static bool UsesLongCache(Conversation conversation, ChatOptions options) =>
        options.LongCacheAbove > 0 && conversation.Usage.Context >= options.LongCacheAbove;

    // SDK 自身も数回やり直すので、それでも駄目だったときにもう少し長く待つ
    static readonly TimeSpan[] TransientRetryDelays = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(180)];

    // 混雑 (529)、サーバーの不調 (5xx)、レート制限、接続切れ、流れている途中の error イベント
    static bool IsTransient(Exception e) =>
        e is Anthropic5xxException or AnthropicRateLimitException or AnthropicIOException or AnthropicSseException
        || (e is AnthropicApiException api && (int)api.StatusCode is 408 or 409 or 529);

    // 急がない送信を Message Batches で送る。料金は半分で、結果が返るまで数分から長いと 24 時間かかる
    static readonly TimeSpan BatchPollInterval = TimeSpan.FromSeconds(30);

    async Task<BetaMessage> SendBatchAsync(Conversation conversation, MessageCreateParams request, Action<JsonObject> emit, CancellationToken cancellationToken)
    {
        // バッチは Fallbacks を受け付けないことがあるので外す。stream は付けない
        var body = request.RawBodyData.Where(kv => kv.Key != "fallbacks").ToDictionary(kv => kv.Key, kv => kv.Value);
        var batch = await client.Beta.Messages.Batches.Create(new Anthropic.Models.Beta.Messages.Batches.BatchCreateParams
        {
            Requests = [new() { CustomID = "chat", Params = Anthropic.Models.Beta.Messages.Batches.Params.FromRawUnchecked(body) }],
            Betas = request.Betas,
        }, cancellationToken);
        Notice(emit, "バッチで送りました (料金は半分)。結果が返るまで待っています");
        try
        {
            while (batch.ProcessingStatus.Raw() != "ended")
            {
                await Task.Delay(BatchPollInterval, cancellationToken);
                try
                {
                    batch = await client.Beta.Messages.Batches.Retrieve(batch.ID, new(), cancellationToken);
                }
                catch (Exception e) when (IsTransient(e))
                {
                    // 様子を聞けなかっただけでバッチは進んでいる。出し直すと二重に課金されるので、同じバッチを待ち続ける
                }
            }
        }
        catch
        {
            // 止めたときや諦めたとき、終わっていないバッチの処理と課金を進めない
            try
            {
                await client.Beta.Messages.Batches.Cancel(batch.ID, new(), CancellationToken.None);
            }
            catch (Exception)
            {
            }
            throw;
        }
        // 結果を受け取りそこねても、出し直すと二重に課金されるので同じバッチから取り直す
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await foreach (var response in client.Beta.Messages.Batches.ResultsStreaming(batch.ID, new(), cancellationToken))
                {
                    if (response.Result.TryPickSucceeded(out var succeeded)) return succeeded.Message;
                    if (response.Result.TryPickErrored(out var errored)) throw new InvalidOperationException($"バッチの送信が失敗しました: {Json.ToNode(errored.Error)?["error"]?["message"] ?? Json.ToNode(errored.Error)}");
                    throw new InvalidOperationException("バッチの送信が期限切れか取り消しで終わりました");
                }
                throw new InvalidOperationException("バッチの結果が空でした");
            }
            catch (Exception e) when (IsTransient(e))
            {
                // 諦めるときも一時的な失敗として外へ出すと、呼び出し元が新しいバッチを出し直してしまう
                if (attempt >= TransientRetryDelays.Length) throw new InvalidOperationException($"バッチの結果を受け取れませんでした: {e.Message}");
                await Task.Delay(TransientRetryDelays[attempt - 1], cancellationToken);
            }
        }
    }

    // 今日や今月の合計が上限に届いていたら、その理由
    public async Task<string?> BudgetLimitAsync()
    {
        var budget = (await settings.GetAsync()).Budget;
        if (budget.DailyLimitUsd <= 0 && budget.MonthlyLimitUsd <= 0) return null;
        var (today, month) = await ledger.SpentAsync();
        if (budget.DailyLimitUsd > 0 && today >= budget.DailyLimitUsd) return $"今日の料金 (${today:0.00}) が上限の ${budget.DailyLimitUsd:0.##} に達したので、送るのを止めました。設定の「使用量と料金」で上限を変えられます";
        if (budget.MonthlyLimitUsd > 0 && month >= budget.MonthlyLimitUsd) return $"今月の料金 (${month:0.00}) が上限の ${budget.MonthlyLimitUsd:0.##} に達したので、送るのを止めました。設定の「使用量と料金」で上限を変えられます";
        return null;
    }

    // 送る量から入力の料金を見積もり、設定の金額を超えそうなら確認する。null は聞かなかった、true は送ってよい
    async Task<bool?> ConfirmCostAsync(Conversation conversation, ChatOptions options, IApprovalGate gate, CancellationToken cancellationToken)
    {
        var limit = (await settings.GetAsync()).Budget.ConfirmRequestUsd;
        if (limit <= 0) return null;
        var (previous, added) = PromptSize(conversation);
        // キャッシュが残っている会話は、前回までの分が安く読まれる。それ以外は書き込みの単価
        var longCache = UsesLongCache(conversation, options);
        var warm = await ledger.LastChatRequestAsync(conversation.Id, options.Model) is { } last && DateTimeOffset.UtcNow - last < (longCache ? LongCacheLifetime : CacheLifetime);
        var cached = warm ? previous : 0;
        var written = previous + added - cached;
        var cost = (await ledger.GetPricingAsync()).Cost(new UsageRecord
        {
            Model = options.Model,
            CacheRead = cached,
            CacheWrite = longCache ? 0 : written,
            CacheWrite1h = longCache ? written : 0,
            Batch = options.Batch,
            Fast = UsesFast(options),
        });
        if (cost is not { } usd || usd < limit) return null;
        var decision = await gate.RequestAsync(new JsonObject
        {
            ["kind"] = "cost",
            ["title"] = "料金",
            ["toolUseId"] = "",
            ["costUsd"] = usd,
            ["tokens"] = previous + added,
            ["cached"] = warm,
            ["limitUsd"] = limit,
        }, cancellationToken);
        return decision is "once" or "always";
    }

    // その場で要約した会話は、サーバー側の自動要約が使えない。送る前に大きさを見積もり、設定の長さを超えていたら要約する
    async Task CompactIfLargeAsync(Conversation conversation, ChatOptions options, Action<JsonObject> emit, CancellationToken cancellationToken)
    {
        if (conversation.CompactionBlock is null || !ModelCatalog.SupportsCompaction(options.Model)) return;
        var (previous, added) = PromptSize(conversation);
        if (previous + added < Math.Max(MinCompactThreshold, options.CompactThreshold)) return;
        try
        {
            if (await CompactOnDemandAsync(conversation, options, null, midTurn: true, cancellationToken))
            {
                emit(new JsonObject { ["type"] = "compacted", ["apiStart"] = conversation.ApiStart, ["compactionBlock"] = conversation.CompactionBlock?.DeepClone() });
                Notice(emit, "会話が長くなったので、古い部分を要約しました");
                return;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Notice(emit, $"会話の要約に失敗しました: {Describe(e)}");
            return;
        }
        Notice(emit, "会話の要約に失敗しました。このまま続けます");
    }

    // /btw と /compact 用。履歴には何も足さず、答えだけ返す
    public async Task<string> AskAsideAsync(Conversation conversation, ChatOptions options, string prompt, string kind, CancellationToken cancellationToken)
    {
        var messages = conversation.CopyApiMessages(UsesMessageEffort(conversation, options));
        // 応答中だと結果の届いていない tool_use が末尾にあり、そのままでは 400 になる
        while (messages.Count > 0 && (string?)messages[^1]["role"] == "assistant" && (messages[^1]["content"] as JsonArray ?? []).OfType<JsonObject>().Any(b => (string?)b["type"] == "tool_use"))
        {
            messages.RemoveAt(messages.Count - 1);
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt }) });
        var request = BuildRequest(conversation, options, tools.GetTools(options.EnabledSources), messages) with
        {
            MaxTokens = AsideMaxTokens(options),
            ToolChoice = FromJson<BetaToolChoice>(new JsonObject { ["type"] = "none" }),
        };
        var message = await client.Beta.Messages.Create(request, cancellationToken);
        await RecordUsageAsync(conversation, message.Model.Raw() ?? options.Model, kind, ToWire(message.Usage));
        var text = string.Concat(message.Content.Select(b => ToWire(b)).Where(b => (string?)b["type"] == "text").Select(b => (string?)b["text"])).Trim();
        return text.Length > 0 ? text : throw new InvalidOperationException("答えが空でした");
    }

    // 入力欄で「自動」を選んだときの振り分け先。難しさの段ごとに 1 つ
    static readonly Dictionary<string, string> RouteTargets = new()
    {
        ["simple"] = "claude-haiku-5-5",
        ["standard"] = "claude-sonnet-5-5",
        ["hard"] = "claude-opus-5-5",
    };

    // 発言の難しさを Haiku に聞いて、答えるモデルを選ぶ。判定できなかったら標準のモデルにする
    public async Task<string> RouteModelAsync(Conversation conversation, string request, CancellationToken cancellationToken)
    {
        var context = conversation.CopyMessages().Where(m => Conversation.IsHumanTurn(m) || (string?)m["role"] == "assistant").TakeLast(4)
            .Select(m => $"{((string?)m["role"] == "user" ? "User" : "Assistant")}: {Clip(WireText.Of(m["content"]))}");
        var response = await client.Messages.Create(new NonBeta.MessageCreateParams
        {
            Model = TitleModel,
            MaxTokens = 16,
            OutputConfig = new NonBeta.OutputConfig { Effort = NonBeta.Effort.Low },
            // 1 語で答えるのに考える時間は要らない
            Thinking = new NonBeta.ThinkingConfigDisabled(),
            System = "You route a chat request to a model. Reply with exactly one word. simple: greetings, small talk, short factual questions, quick rewrites or translations. standard: everyday writing, explanations, ordinary coding and analysis. hard: complex multi-step reasoning, large or tricky code, design, research, math proofs, long agentic tasks with tools.",
            Messages = [new() { Role = NonBeta.Role.User, Content = $"<recent>\n{string.Join("\n", context)}\n</recent>\n<request>\n{Clip(request)}\n</request>" }],
        }, cancellationToken);
        await ledger.RecordAsync(new UsageRecord
        {
            At = DateTimeOffset.UtcNow,
            ConversationId = conversation.Incognito ? null : conversation.Id,
            Kind = "route",
            Model = TitleModel,
            Input = response.Usage.InputTokens,
            Output = response.Usage.OutputTokens,
            CacheWrite = response.Usage.CacheCreationInputTokens ?? 0,
            CacheRead = response.Usage.CacheReadInputTokens ?? 0,
        });
        var answer = string.Concat(response.Content.Select(b => b.Value).OfType<NonBeta.TextBlock>().Select(t => t.Text)).Trim().Trim('.').ToLowerInvariant();
        return RouteTargets.GetValueOrDefault(answer, RouteTargets["standard"]);

        // 難しさの見当には冒頭で足りる。長い貼り付けで判定の料金を増やさない
        static string Clip(string text) => text.Length > 1500 ? text[..1500] : text;
    }

    public async Task<string?> GenerateTitleAsync(Conversation conversation)
    {
        var messages = conversation.CopyMessages();
        // claude.ai と同じく最初のやり取りで名付ける。ツールの途中経過は混ぜない
        var user = WireText.Of(messages.FirstOrDefault(m => Conversation.IsHumanTurn(m))?["content"]);
        var assistant = messages.Where(m => (string?)m["role"] == "assistant").Select(m => WireText.Of(m["content"]).Trim()).FirstOrDefault(t => t.Length > 0) ?? "";
        if (user.Length == 0 && assistant.Length == 0) return null;
        // タイトル用途なので長文は頭だけで十分
        static string Excerpt(string s) => s.Length > 2000 ? s[..2000] : s;

        var response = await client.Messages.Create(new NonBeta.MessageCreateParams
        {
            Model = TitleModel,
            MaxTokens = 1024,
            OutputConfig = new NonBeta.OutputConfig { Effort = NonBeta.Effort.Low },
            // タイトルに考える時間は要らない
            Thinking = new NonBeta.ThinkingConfigDisabled(),
            System = "Write a short title (at most about 20 characters, or 6 words) that captures what this conversation is about, in the same language as the user's message. Output only the title, with no quotes, punctuation at the end, or preamble.",
            Messages = [new() { Role = NonBeta.Role.User, Content = $"ユーザー:\n{Excerpt(user)}\n\nアシスタント:\n{Excerpt(assistant)}" }],
        });
        await ledger.RecordAsync(new UsageRecord
        {
            At = DateTimeOffset.UtcNow,
            ConversationId = conversation.Incognito ? null : conversation.Id,
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

    // claude.ai の「スタイルを作る」。説明か見本の文章から、文体の指示を書き起こす
    public async Task<(string Name, string Instructions)?> GenerateStyleAsync(string request)
    {
        var response = await client.Messages.Create(new NonBeta.MessageCreateParams
        {
            Model = TitleModel,
            MaxTokens = 2048,
            OutputConfig = new NonBeta.OutputConfig { Effort = NonBeta.Effort.Low },
            Thinking = new NonBeta.ThinkingConfigDisabled(),
            System = "The user describes a writing style, or pastes a sample of writing whose style they want. Write instructions that tell an AI assistant how to write in that style: tone, formality, sentence length, structure, formatting, and word choice. On the first line, write only a short name for the style in the user's language. Then a blank line, then the instructions (under 150 words, in English). No preamble.",
            Messages = [new() { Role = NonBeta.Role.User, Content = request.Length > 20_000 ? request[..20_000] : request }],
        });
        await ledger.RecordAsync(new UsageRecord
        {
            At = DateTimeOffset.UtcNow,
            Kind = "style",
            Model = TitleModel,
            Input = response.Usage.InputTokens,
            Output = response.Usage.OutputTokens,
            CacheWrite = response.Usage.CacheCreationInputTokens ?? 0,
            CacheRead = response.Usage.CacheReadInputTokens ?? 0,
        });
        if (response.StopReason?.Raw() == "refusal") return null;
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<NonBeta.TextBlock>().Select(t => t.Text)).Trim().Replace("\r\n", "\n");
        var newline = text.IndexOf('\n');
        if (newline < 0) return null;
        var name = text[..newline].Trim().Trim('#', '*', ' ');
        return (name.Length > 30 ? name[..30] : name, text[(newline + 1)..].Trim());
    }
}
