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
    readonly AutoReviewer reviewer = new(client, ledger);
    const string TitleModel = "claude-haiku-5-5";
    // API の下限
    const int MinCompactThreshold = 50_000;
    // ツールがあると要約中にツールを呼んで要約が空になることがあるため、既定の指示を差し替える
    const string CompactInstructions = "Summarize the transcript inside <summary></summary> tags. Include everything needed to continue the conversation in the next context window: the user's goals and preferences, decisions made, facts and numbers established, file paths and commands used, and anything still pending. Write the summary in the language of the conversation. Do not call any tools while writing this summary; respond with text only.";

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

    static BetaToolUnion ToTool(ToolSpec spec)
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

    static MessageCreateParams BuildRequest(Conversation conversation, ChatOptions options, IReadOnlyList<ToolSpec> toolSpecs, List<JsonObject>? messages = null)
    {
        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            // 上限は付けたくないが必須の値なので、選べるどのモデルも受け付ける最大にする。料金は実際に出た分だけ
            MaxTokens = 128000,
            Messages = (messages ?? conversation.CopyApiMessages()).Select(ToParam).ToList(),
            Thinking = new BetaThinkingConfigAdaptive
            {
                Display = Display.Summarized,
                // 日付入りのシステムプロンプトやツールの増減で前の思考ブロックが無効になっても 400 にしない
                BlockBinding = new BetaThinkingBlockBinding { PrefixMismatchBehavior = BetaThinkingPrefixMismatchBehavior.DropBlock },
            },
            OutputConfig = new BetaOutputConfig
            {
                Effort = ParseEffort(options.Effort),
                Format = options.OutputSchema is { } schema ? FromJson<BetaJsonOutputFormat>(new JsonObject { ["type"] = "json_schema", ["schema"] = schema.DeepClone() }) : null,
            },
            CacheControl = new BetaCacheControlEphemeral(),
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
        if (!string.IsNullOrWhiteSpace(options.SystemPrompt)) parameters = parameters with { System = options.SystemPrompt };

        var toolList = ServerTools(options).ToList();
        toolList.AddRange(toolSpecs.Select(ToTool));
        if (toolList.Count > 0) parameters = parameters with { Tools = toolList };

        List<ApiEnum<string, AnthropicBeta>> betas = ["compact-2026-01-12", "thinking-binding-controls-2026-08-01"];
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
        // Haiku 5.5 にはサーバー側フォールバックが無い
        if (!IsHaiku(options.Model))
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
        // 止めたときに、実行済みのツールの結果を捨てないため
        var finishedResults = new JsonArray();
        // 途中で止まったリクエストも課金されるので、届いた分の使用量を覚えておく
        JsonObject? streamUsage = null;
        // コンテナは時間が経つと消えるので、断られたら 1 度だけ新しく作り直す
        var containerRetried = false;
        try
        {
            var mode = conversation.PermissionMode;
            for (var turn = 0; turn < options.MaxToolTurns; turn++)
            {
                // 計画モードを抜けたのに「計画モード中」と言い続けないよう、指示を作り直す
                if (conversation.PermissionMode != mode && options.RebuildSystemPrompt is { } rebuild)
                {
                    mode = conversation.PermissionMode;
                    options = options with { SystemPrompt = rebuild() };
                }
                var toolSpecs = tools.GetTools(options.EnabledSources);
                var aggregator = new BetaMessageContentAggregator();
                var toolInputJson = new Dictionary<int, string>();
                partialText.Clear();
                emit(new JsonObject { ["type"] = "message-start" });

                var stream = client.Beta.Messages.CreateStreaming(BuildRequest(conversation, options, toolSpecs), cancellationToken);
                IAsyncEnumerator<BetaRawMessageStreamEvent> events;
                try
                {
                    events = stream.CollectAsync(aggregator).GetAsyncEnumerator(cancellationToken);
                    if (!await events.MoveNextAsync()) throw new InvalidOperationException("API から空の応答が返りました");
                }
                catch (AnthropicBadRequestException e) when (!containerRetried && conversation.ContainerId is not null && e.Message.Contains("container", StringComparison.OrdinalIgnoreCase))
                {
                    containerRetried = true;
                    conversation.ContainerId = null;
                    turn--;
                    continue;
                }
                await using var _ = events;
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

                var message = aggregator.Message();
                var content = new JsonArray(message.Content.Select(b => (JsonNode)ToWire(b)).ToArray());
                // 画面の「↓ 1.2k tokens」を、流れてきた文字からの見積もりから本当の数に置き換える
                emit(new JsonObject { ["type"] = "usage", ["output"] = message.Usage.OutputTokens });
                // 途中で断られた応答は完成した回答として扱わず捨てる。別モデルが書き直した (fallback) ときは残す
                if (message.StopReason?.Raw() == "refusal" && !content.OfType<JsonObject>().Any(b => (string?)b["type"] == "fallback"))
                {
                    partialText.Clear();
                    streamUsage = null;
                    await RecordUsageAsync(conversation, message.Model.Raw() ?? options.Model, "chat", ToWire(message.Usage));
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
                await RecordUsageAsync(conversation, message.Model.Raw() ?? options.Model, "chat", ToWire(message.Usage));
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
                    if (stopReason == "tool_use") continue;
                }

                if (stopReason == "max_tokens") Notice(emit, "出力上限に達したため途中で終わっています");
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
    static bool RepairDanglingToolUse(Conversation conversation, string reason, JsonArray finished)
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

    async Task<JsonArray> RunToolsAsync(
        Conversation conversation,
        List<JsonObject> toolUses,
        ChatOptions options,
        Action<JsonObject> emit,
        IApprovalGate gate,
        JsonArray results,
        CancellationToken cancellationToken)
    {
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
            else if (await hooks.RunPreToolUseAsync(conversation, apiName, input) is { Blocked: true } blocked)
            {
                outcome = ToolRegistry.Error($"PreToolUse フックが止めました: {blocked.Message}");
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
            results.Add(result);
        }
        return results;
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
        if (spec.AutoApprove || readOnlyCall || (spec.AllowAlways && current.ToolPermissions.GetValueOrDefault(spec.ApiName) == "always")) return null;

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
    async Task RecordUsageAsync(Conversation conversation, string model, string kind, JsonObject usage)
    {
        var iterations = (usage["iterations"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (iterations.Count == 0) iterations = [usage];
        var webSearches = (long?)usage["server_tool_use"]?["web_search_requests"] ?? 0;
        foreach (var iteration in iterations)
        {
            var record = await ledger.RecordAsync(new UsageRecord
            {
                At = DateTimeOffset.UtcNow,
                ConversationId = conversation.Incognito ? null : conversation.Id,
                Kind = kind,
                Model = (string?)iteration["model"] ?? model,
                Input = (long?)iteration["input_tokens"] ?? 0,
                Output = (long?)iteration["output_tokens"] ?? 0,
                CacheWrite = (long?)iteration["cache_creation_input_tokens"] ?? 0,
                CacheRead = (long?)iteration["cache_read_input_tokens"] ?? 0,
                WebSearches = webSearches,
            });
            webSearches = 0;
            conversation.Usage.Input += record.Input + record.CacheWrite;
            conversation.Usage.Output += record.Output;
            conversation.Usage.CacheRead += record.CacheRead;
            conversation.Usage.CacheWrite += record.CacheWrite;
            conversation.Usage.CostUsd += record.CostUsd ?? 0;
        }
    }

    // /btw と /compact 用。履歴には何も足さず、答えだけ返す
    public async Task<string> AskAsideAsync(Conversation conversation, ChatOptions options, string prompt, string kind, CancellationToken cancellationToken)
    {
        var messages = conversation.CopyApiMessages();
        // 応答中だと結果の届いていない tool_use が末尾にあり、そのままでは 400 になる
        while (messages.Count > 0 && (string?)messages[^1]["role"] == "assistant" && (messages[^1]["content"] as JsonArray ?? []).OfType<JsonObject>().Any(b => (string?)b["type"] == "tool_use"))
        {
            messages.RemoveAt(messages.Count - 1);
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt }) });
        var request = BuildRequest(conversation, options, tools.GetTools(options.EnabledSources), messages) with
        {
            // 要約や脇道の質問は長くならない
            MaxTokens = 16000,
            ToolChoice = FromJson<BetaToolChoice>(new JsonObject { ["type"] = "none" }),
        };
        var message = await client.Beta.Messages.Create(request, cancellationToken);
        await RecordUsageAsync(conversation, message.Model.Raw() ?? options.Model, kind, ToWire(message.Usage));
        var text = string.Concat(message.Content.Select(b => ToWire(b)).Where(b => (string?)b["type"] == "text").Select(b => (string?)b["text"])).Trim();
        return text.Length > 0 ? text : throw new InvalidOperationException("答えが空でした");
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
