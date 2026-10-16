using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Anthropic;

namespace ClaudeChat.Core;

public interface IChatEvents
{
    void Emit(string name, JsonNode? data);
}

// 会話の実行、状態、承認待ち、チャット同士のメッセージをまとめて持つ。
// ユーザーの送信も、ほかのチャットからの通信も、サブエージェントも、すべてここから走らせる
public sealed class ChatHub(
    AppPaths paths,
    SettingsStore settings,
    SecretStore secrets,
    ConversationStore conversations,
    FolderStore folders,
    UsageLedger ledger,
    Hooks hooks,
    Extensions extensions,
    IChatEvents events)
{
    sealed class Inbound(string text, int hops)
    {
        public string Text { get; } = text;
        public int Hops { get; } = hops;
    }

    // Stop フックに続けさせるのは、止まらなくなるのを防ぐため 1 回の送信につき 3 回まで
    const int MaxStopContinuations = 3;

    readonly ConcurrentDictionary<string, CancellationTokenSource> running = new();
    // 終わった後も残しておき、終わった直後に待ち始めた呼び出しにも結果を返す
    readonly ConcurrentDictionary<string, TaskCompletionSource<ChatRunResult>> completions = new();
    readonly ConcurrentDictionary<string, (string ConversationId, JsonObject Request, TaskCompletionSource<string> Decision)> approvals = new();
    // 先頭に戻す操作があるので ConcurrentQueue ではなくロックで守る
    readonly Lock inboxLock = new();
    readonly Dictionary<string, List<Inbound>> inboxes = [];
    // 待機中になったら知らせてほしい、という登録。相手 -> 知らせる先
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> idleWatchers = new();
    // 待たずに走らせた子。終わったら親に知らせる
    readonly ConcurrentDictionary<string, byte> backgroundChildren = new();
    readonly ConcurrentDictionary<string, int> runHops = new();
    // 枠が空くのを待っている子。メッセージが溜まっている
    readonly ConcurrentDictionary<string, byte> waitingForSlot = new();
    // 実行中のエージェントの数。上限の確認と登録を同時にするためロックで守る
    readonly Lock agentLock = new();
    readonly HashSet<string> runningAgents = [];
    AnthropicClient? client;
    string? clientKey;

    public ToolRegistry Tools { get; set; } = new([]);

    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Idle = "idle";
    public const string Failed = "failed";
    public const string Stopped = "stopped";

    // 0 はユーザーのチャット、1 はサブエージェントやワークフローの子
    public static int DepthOf(Conversation conversation) => conversation.Kind == "chat" ? 0 : 1;

    public bool IsRunning(string id) => running.ContainsKey(id);

    public string StatusOf(Conversation conversation) => StatusOf(conversation.Id, conversation.LastRunStatus);

    string StatusOf(string id, string? lastRunStatus)
    {
        if (approvals.Values.Any(a => a.ConversationId == id)) return Waiting;
        if (running.ContainsKey(id)) return Running;
        return lastRunStatus switch
        {
            "failed" => Failed,
            "aborted" => Stopped,
            _ => Idle,
        };
    }

    // 画面を読み直したときに応答中として出す分。要約や巻き戻しの間は done が来ないので含めない
    public JsonArray RunningView() => new(running.Keys.Where(id => completions.TryGetValue(id, out var c) && !c.Task.IsCompleted).Select(id => (JsonNode)new JsonObject
    {
        ["conversationId"] = id,
        ["approvals"] = new JsonArray(approvals.Values
            .Where(p => p.ConversationId == id)
            .Select(p => (JsonNode)p.Request.DeepClone())
            .ToArray()),
    }).ToArray());

    public async Task<JsonObject> StatusesAsync()
    {
        var result = new JsonObject();
        // 全部の会話を読み込むと重いので、一覧に持たせた直近の結果で決める
        foreach (var summary in await conversations.ListAsync()) result[summary.Id] = StatusOf(summary.Id, summary.LastRunStatus);
        return result;
    }

    void EmitStatus(Conversation conversation) =>
        events.Emit("chats.status", new JsonObject { ["id"] = conversation.Id, ["status"] = StatusOf(conversation) });

    public AnthropicClient GetClient(string apiKey)
    {
        if (client is null || clientKey != apiKey)
        {
            client = new AnthropicClient { ApiKey = apiKey };
            clientKey = apiKey;
        }
        return client;
    }

    // Options

    public async Task<ChatOptions> OptionsForAsync(Conversation conversation, JsonObject? request = null)
    {
        var current = await settings.GetAsync();
        var defaults = current.Chat;
        var depth = DepthOf(conversation);
        // 画面からの指定が無いとき (ほかのチャットからの通信など) は、その会話の直近の設定で動かす
        var source = request ?? conversation.Options ?? [];
        var enabled = source["enabledSources"] is JsonArray list ? list.Select(n => (string?)n).OfType<string>().ToHashSet() : null;
        // サブエージェントはさらに子を作ったり、ほかのチャットを動かしたりしない
        if (depth > 0) enabled = (enabled ?? Tools.SourceNames().ToHashSet()).Where(s => s != AgentToolProvider.Source).ToHashSet();
        // 予定は会話の中身を schedules.json に残し、後で保存されるチャットを作るので使わせない
        if (conversation.Incognito) enabled = (enabled ?? Tools.SourceNames().ToHashSet()).Where(s => s is not (MemoryToolProvider.Source or ChatSearchToolProvider.Source or ScheduleToolProvider.Source)).ToHashSet();
        var folder = await folders.GetAsync(conversation.FolderId);
        string BuildPrompt() => SystemPrompt.Build(current, new PromptContext
        {
            FilesEnabled = enabled is null || enabled.Contains(FileToolProvider.Source),
            AgentsEnabled = depth == 0 && current.Agents.Enabled && (enabled is null || enabled.Contains(AgentToolProvider.Source)),
            Folder = folder,
            Conversation = conversation,
            Skills = enabled is null || enabled.Contains(SkillToolProvider.Source) ? extensions.ListSkills() : [],
            OutputStyle = extensions.OutputStyleText((string?)source["style"] ?? defaults.OutputStyle),
            IsAgent = depth > 0,
            Model = (string?)source["model"] ?? conversation.Model ?? defaults.Model,
            MemoryListing = current.Features.Memory && (enabled is null || enabled.Contains(MemoryToolProvider.Source))
                ? MemoryToolProvider.PromptListing(MemoryToolProvider.RootFor(paths, conversation.FolderId))
                : null,
            ProjectKnowledge = folder is null ? null : ProjectKnowledge.PromptBlock(ProjectKnowledge.DirFor(paths, folder.Id)),
        }, DateTime.Now);
        return new ChatOptions
        {
            Model = (string?)source["model"] ?? conversation.Model ?? defaults.Model,
            Effort = (string?)source["effort"] ?? defaults.Effort,
            WebSearch = (bool?)source["webSearch"] ?? defaults.WebSearch,
            WebSearchMaxUses = Math.Clamp(defaults.WebSearchMaxUses, 1, 50),
            WebFetch = (bool?)source["webFetch"] ?? defaults.WebFetch,
            WebFetchMaxUses = Math.Clamp(defaults.WebFetchMaxUses, 1, 50),
            CodeExecution = (bool?)source["codeExecution"] ?? defaults.CodeExecution,
            OfficeSkills = defaults.OfficeSkills,
            EnabledSources = enabled,
            SystemPrompt = BuildPrompt(),
            // 計画モードを抜けたときなど、応答の途中で指示が変わったら作り直す
            RebuildSystemPrompt = BuildPrompt,
            MaxToolTurns = Math.Max(1, defaults.MaxToolTurns),
            CompactThreshold = defaults.CompactThreshold,
        };
    }

    // Runs

    CancellationTokenSource Reserve(string conversationId)
    {
        var cancellation = new CancellationTokenSource();
        if (running.TryAdd(conversationId, cancellation)) return cancellation;
        cancellation.Dispose();
        throw new InvalidOperationException("この会話は応答中です");
    }

    void Release(string conversationId, CancellationTokenSource cancellation)
    {
        if (running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(conversationId, cancellation))) cancellation.Dispose();
    }

    public async Task<ChatEngine> EngineAsync()
    {
        var apiKey = await secrets.GetAsync(SecretStore.ApiKey) ?? throw new InvalidOperationException("APIキーが未設定です。設定の API 画面から登録してください");
        return new ChatEngine(GetClient(apiKey), Tools, ledger, settings, hooks, paths);
    }

    // 要約や巻き戻しの間に送信が割り込まないよう、応答と同じ枠を取って動かす
    public async Task<T> ExclusiveAsync<T>(Conversation conversation, Func<CancellationToken, Task<T>> work)
    {
        var cancellation = Reserve(conversation.Id);
        EmitStatus(conversation);
        try
        {
            return await work(cancellation.Token);
        }
        finally
        {
            Release(conversation.Id, cancellation);
            EmitStatus(conversation);
            // 枠を取っている間に届いたメッセージを流す
            _ = FlushQuietlyAsync(conversation);
        }
    }

    // 会話を書き換えてから走らせる。書き換えの前に枠を取るので、連打で 2 本走らない
    public async Task StartAsync(Conversation conversation, Func<Task> prepare, ChatOptions options, int hops = 0)
    {
        // 削除された会話を、手元に残っていた参照から動かし直さない
        if (await conversations.GetAsync(conversation.Id) is null) throw new InvalidOperationException("この会話は削除されています");
        var cancellation = Reserve(conversation.Id);
        try
        {
            await prepare();
            await conversations.SaveAsync(conversation);
            events.Emit("conversations.changed", null);
        }
        catch
        {
            Release(conversation.Id, cancellation);
            throw;
        }
        var completion = new TaskCompletionSource<ChatRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        completions[conversation.Id] = completion;
        runHops[conversation.Id] = hops;
        EmitChatEvent(conversation.Id, new JsonObject { ["type"] = "started" });
        EmitStatus(conversation);
        _ = RunAsync(conversation, options, cancellation, completion, hops);
    }

    async Task RunAsync(Conversation conversation, ChatOptions options, CancellationTokenSource cancellation, TaskCompletionSource<ChatRunResult> completion, int hops)
    {
        var id = conversation.Id;
        ToolContext.Current = new ToolContext { Conversation = conversation, Options = options, Depth = DepthOf(conversation), Hops = hops };
        // 途中経過を順番どおりに保存する。落ちてもそこまでの会話は残る
        var saveChain = Task.CompletedTask;
        void EmitChat(JsonObject e)
        {
            if ((string?)e["type"] == "message") saveChain = saveChain.ContinueWith(_ => conversations.SaveAsync(conversation, touch: false)).Unwrap();
            EmitChatEvent(id, e);
        }

        var result = new ChatRunResult(ChatStatus.Failed, "不明なエラー");
        try
        {
            var apiKey = await secrets.GetAsync(SecretStore.ApiKey);
            ChatEngine? engine = null;
            if (apiKey is null)
            {
                result = new ChatRunResult(ChatStatus.Failed, "APIキーが未設定です。設定の API 画面から登録してください");
            }
            else
            {
                conversation.Model = options.Model;
                engine = new ChatEngine(GetClient(apiKey), Tools, ledger, settings, hooks, paths);
                var gate = new ApprovalGate(this, conversation);
                result = await engine.RunAsync(conversation, options, EmitChat, gate, cancellation.Token);
                // Stop フックが続けるよう求めたら、同じ枠のまま理由を渡して続けさせる。
                // 枠を手放してからだと、待っている親や知らせの先に、フックが退けた答えが渡ってしまう
                for (var continuation = 0; ; continuation++)
                {
                    var stop = await hooks.RunStopAsync(conversation, result, continuation > 0);
                    if (result.Status != ChatStatus.Done || !stop.Blocked || continuation >= MaxStopContinuations) break;
                    var feedback = new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"<hook-feedback event=\"Stop\">\n{stop.Message}\n</hook-feedback>" }),
                    };
                    conversation.Append(feedback);
                    EmitChat(new JsonObject { ["type"] = "inbound", ["message"] = feedback.DeepClone() });
                    result = await engine.RunAsync(conversation, options, EmitChat, gate, cancellation.Token);
                }
            }
            conversation.LastRunStatus = result.Status.ToString().ToLowerInvariant();

            await saveChain;
            // 応答中に削除された会話を保存し直して復活させない
            if (await conversations.GetAsync(id) is null) return;
            await conversations.SaveAsync(conversation);
            // 画面は done で入力できるようになるので、先に空けてタイトル作りの間も次の送信を受け付ける
            Release(id, cancellation);
            EmitChat(new JsonObject
            {
                ["type"] = "done",
                ["status"] = conversation.LastRunStatus,
                ["error"] = result.Error,
                ["conversation"] = conversation.ToWire(),
            });
            events.Emit("conversations.changed", null);
            EmitStatus(conversation);

            if (engine is not null && result.Status == ChatStatus.Done && conversation.Title == Conversation.DefaultTitle)
            {
                try
                {
                    var title = await engine.GenerateTitleAsync(conversation);
                    // 待っている間にユーザーが名前を付けていたら上書きしない
                    if (title is not null && conversation.Title == Conversation.DefaultTitle && await conversations.GetAsync(id) is not null)
                    {
                        conversation.Title = title;
                        await conversations.SaveAsync(conversation, touch: false);
                        events.Emit("conversations.changed", null);
                    }
                }
                catch (Exception)
                {
                    // タイトルは後回しでいいので失敗しても会話は止めない
                }
            }
        }
        catch (Exception e)
        {
            result = new ChatRunResult(ChatStatus.Failed, e.Message);
            conversation.LastRunStatus = "failed";
            EmitChat(new JsonObject { ["type"] = "done", ["status"] = "failed", ["error"] = e.Message, ["conversation"] = conversation.ToWire() });
        }
        finally
        {
            Release(id, cancellation);
            ReleaseSlot(id);
            completion.TrySetResult(result);
            EmitStatus(conversation);
        }
        await AfterRunAsync(conversation, result);
    }

    // 終わった会話に溜まったメッセージを流し、待っていた相手や親に知らせる
    async Task AfterRunAsync(Conversation conversation, ChatRunResult result)
    {
        try
        {
            // 往復の回数は引き継ぐ。知らせのたびに 0 に戻すと、知らせ合いが止まらなくなる
            var hops = RunHopsOf(conversation.Id);
            if (backgroundChildren.TryRemove(conversation.Id, out _) && conversation.ParentId is { } parentId && await conversations.GetAsync(parentId) is { } parent)
            {
                var status = result.Status == ChatStatus.Done ? "completed" : result.Status == ChatStatus.Aborted ? "stopped" : "failed";
                var body = result.Status == ChatStatus.Done ? FinalText(conversation) : result.Error ?? "";
                await DeliverAsync(parent, $"<task-notification agent=\"{Escape(conversation.Title)}\" chat_id=\"{conversation.Id}\" status=\"{status}\">\n{body}\n</task-notification>", hops);
            }
            if (idleWatchers.TryRemove(conversation.Id, out var watchers))
            {
                foreach (var watcherId in watchers.Keys)
                {
                    if (await conversations.GetAsync(watcherId) is not { } watcher) continue;
                    await DeliverAsync(watcher, $"<idle-notification chat=\"{Escape(conversation.Title)}\" chat_id=\"{conversation.Id}\" status=\"{StatusOf(conversation)}\">\n{Shorten(FinalText(conversation), 4000)}\n</idle-notification>", hops + 1);
                }
            }
            await FlushInboxAsync(conversation);
            await DiscardFinishedChildAsync(conversation, result);
        }
        catch (Exception)
        {
            // 知らせに失敗しても、終わった会話そのものには影響させない
        }
    }

    // 会話を消すときの後片付け (チェックポイントなど) は AppHost が持っている
    public Func<string, Task>? DeleteConversation { get; set; }

    // サブエージェントは使い捨て。失敗や停止は原因を見られるよう残す
    async Task DiscardFinishedChildAsync(Conversation child, ChatRunResult result)
    {
        if (result.Status != ChatStatus.Done || child.ParentId is null || DeleteConversation is not { } delete) return;
        if (!(await settings.GetAsync()).Agents.DeleteFinishedChildren) return;
        // 誰かが話しかけて次の番が始まった、または始まるのを待っている子は消さない
        if (StatusOf(child) != Idle) return;
        lock (inboxLock)
        {
            if (inboxes.TryGetValue(child.Id, out var pending) && pending.Count > 0) return;
        }
        await delete(child.Id);
    }

    static string Escape(string s) => s.Replace("\"", "'");

    static string Shorten(string s, int max) => s.Length > max ? s[..max] + "\n... (長いので省略)" : s;

    public static string FinalText(Conversation conversation)
    {
        var last = conversation.CopyMessages().LastOrDefault(m => (string?)m["role"] == "assistant");
        return WireText.Of(last?["content"]).Trim();
    }

    public void Stop(string conversationId)
    {
        if (running.TryGetValue(conversationId, out var cancellation)) cancellation.Cancel();
    }

    public void StopAll()
    {
        foreach (var cancellation in running.Values) cancellation.Cancel();
    }

    public async Task<ChatRunResult> WaitAsync(string conversationId, CancellationToken cancellationToken)
    {
        if (!completions.TryGetValue(conversationId, out var completion)) return new ChatRunResult(ChatStatus.Failed, "この会話は動いていません");
        return await completion.Task.WaitAsync(cancellationToken);
    }

    // 削除した会話の、残った待ち合わせや知らせの登録を消す
    public void Forget(string conversationId)
    {
        lock (inboxLock) inboxes.Remove(conversationId);
        idleWatchers.TryRemove(conversationId, out _);
        foreach (var watchers in idleWatchers.Values) watchers.TryRemove(conversationId, out _);
        backgroundChildren.TryRemove(conversationId, out _);
        waitingForSlot.TryRemove(conversationId, out _);
        runHops.TryRemove(conversationId, out _);
        completions.TryRemove(conversationId, out _);
    }

    // Messaging

    // 待機中ならすぐ動かし、応答中なら終わってから届ける
    public async Task DeliverAsync(Conversation target, string text, int hops)
    {
        lock (inboxLock)
        {
            if (!inboxes.TryGetValue(target.Id, out var list)) inboxes[target.Id] = list = [];
            list.Add(new Inbound(text, hops));
        }
        if (!running.ContainsKey(target.Id)) await FlushInboxAsync(target);
    }

    async Task FlushQuietlyAsync(Conversation target)
    {
        try
        {
            await FlushInboxAsync(target);
        }
        catch (Exception)
        {
            // 流せなかった分は受け箱に残っていて、次の機会に流れる
        }
    }

    async Task FlushInboxAsync(Conversation target)
    {
        lock (inboxLock)
        {
            if (!inboxes.TryGetValue(target.Id, out var pending) || pending.Count == 0) return;
        }
        if (running.ContainsKey(target.Id)) return;
        if (await conversations.GetAsync(target.Id) is null)
        {
            Forget(target.Id);
            return;
        }
        var options = await OptionsForAsync(target);
        var isAgent = DepthOf(target) > 0;
        // 子は通信で動き出すときもエージェントの数に数える
        if (isAgent && !TryTakeSlot(target.Id, await AgentLimitAsync()))
        {
            waitingForSlot[target.Id] = 0;
            return;
        }
        waitingForSlot.TryRemove(target.Id, out _);
        List<Inbound> items;
        lock (inboxLock)
        {
            items = inboxes.Remove(target.Id, out var taken) ? taken : [];
        }
        if (items.Count == 0)
        {
            if (isAgent) ReleaseSlot(target.Id);
            return;
        }
        try
        {
            await StartAsync(target, () =>
            {
                var content = new JsonArray(items.Select(i => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = i.Text }).ToArray());
                var message = new JsonObject { ["role"] = "user", ["content"] = content };
                target.Append(message);
                EmitChatEvent(target.Id, new JsonObject { ["type"] = "inbound", ["message"] = message.DeepClone() });
                return Task.CompletedTask;
            }, options, hops: items.Max(i => i.Hops));
        }
        catch (Exception e)
        {
            if (isAgent) ReleaseSlot(target.Id);
            // 先に届いたものが先に読まれるよう、受け箱の先頭に戻す
            lock (inboxLock)
            {
                if (inboxes.TryGetValue(target.Id, out var later)) items.AddRange(later);
                inboxes[target.Id] = items;
            }
            // ちょうど別の応答が始まったときは、その応答が終わったときにもう一度流す
            if (e is not InvalidOperationException) throw;
        }
    }

    public void WatchIdle(string targetId, string watcherId) =>
        idleWatchers.GetOrAdd(targetId, _ => new())[watcherId] = 0;

    // 通信できる相手か。フォルダの設定で決まる
    public async Task<bool> CanMessageAsync(Conversation from, Conversation to)
    {
        if (from.Id == to.Id) return false;
        // 親子の間はフォルダに関係なく通じる
        if (to.ParentId == from.Id || from.ParentId == to.Id) return true;
        // ほかの会話の子には、その親を通さずに話しかけない
        if (to.Kind != "chat" || from.Kind != "chat") return false;
        // 送る側と受ける側の両方のフォルダが許していること。片方だけだと「通信しない」フォルダにも届いてしまう
        return Allows(await folders.GetAsync(from.FolderId), to) && Allows(await folders.GetAsync(to.FolderId), from);

        static bool Allows(Folder? folder, Conversation other) => folder?.MessagingScope switch
        {
            "all" => true,
            "folder" => other.FolderId == folder.Id,
            _ => false,
        };
    }

    // Agents

    public int RunningAgents()
    {
        lock (agentLock) return runningAgents.Count;
    }

    async Task<int> AgentLimitAsync() => Math.Max(1, (await settings.GetAsync()).Agents.MaxConcurrent);

    bool TryTakeSlot(string conversationId, int limit)
    {
        lock (agentLock)
        {
            if (runningAgents.Contains(conversationId)) return true;
            if (runningAgents.Count >= limit) return false;
            runningAgents.Add(conversationId);
            return true;
        }
    }

    void ReleaseSlot(string conversationId)
    {
        bool released;
        lock (agentLock) released = runningAgents.Remove(conversationId);
        if (!released) return;
        // 空いた枠で、メッセージを溜めて待っていた子を動かす
        foreach (var waitingId in waitingForSlot.Keys)
        {
            _ = Task.Run(async () =>
            {
                if (await conversations.GetAsync(waitingId) is { } waiting) await FlushQuietlyAsync(waiting);
            });
        }
    }

    // 結果を待つ呼び出しは空きが出るまで待ち、バックグラウンドは待たずに断る
    public async Task<Conversation> SpawnAsync(Conversation parent, string prompt, string title, string kind, string? workflowId, Func<ChatOptions, ChatOptions>? customize, bool background, CancellationToken cancellationToken = default, JsonObject? overrides = null)
    {
        var limit = await AgentLimitAsync();
        var child = conversations.Create();
        while (true)
        {
            // 止めたワークフローから、空きがあるからと新しい子を作らない
            cancellationToken.ThrowIfCancellationRequested();
            if (TryTakeSlot(child.Id, limit)) break;
            if (background) throw new ToolException($"同時に動かせるエージェントは {limit} 個までです。終わるのを待ってから呼んでください");
            await Task.Delay(500, cancellationToken);
        }
        child.Title = title;
        child.Kind = kind;
        child.ParentId = parent.Id;
        child.FolderId = parent.FolderId;
        child.Incognito = parent.Incognito;
        child.WorkflowId = workflowId;
        child.WorktreePath = parent.WorktreePath;
        // 計画中の子は変更せず、auto の子は同じ確認役に見てもらう
        child.PermissionMode = parent.PermissionMode is "plan" or "auto" ? parent.PermissionMode : "default";
        // 親と同じ道具立てで動かす。モデルと effort は、呼んだ Claude が選んだときだけ変える
        child.Options = parent.Options?.DeepClone() as JsonObject;
        if (overrides is not null)
        {
            child.Options ??= [];
            foreach (var (key, value) in overrides) child.Options[key] = value?.DeepClone();
        }
        try
        {
            var baseOptions = await OptionsForAsync(child);
            var options = customize?.Invoke(baseOptions) ?? baseOptions;
            if (background) backgroundChildren[child.Id] = 0;
            await StartAsync(child, () =>
            {
                child.Append(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"<task from_chat=\"{Escape(parent.Title)}\" chat_id=\"{parent.Id}\">\n{prompt}\n</task>" }),
                });
                return Task.CompletedTask;
            }, options, hops: RunHopsOf(parent.Id));
        }
        catch
        {
            ReleaseSlot(child.Id);
            backgroundChildren.TryRemove(child.Id, out _);
            throw;
        }
        return child;
    }

    // Approvals

    sealed class ApprovalGate(ChatHub hub, Conversation conversation) : IApprovalGate
    {
        public async Task<string> RequestAsync(JsonObject request, CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var decision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            request["type"] = "approval";
            request["requestId"] = requestId;
            hub.approvals[requestId] = (conversation.Id, (JsonObject)request.DeepClone(), decision);
            hub.EmitChatEvent(conversation.Id, request);
            hub.EmitStatus(conversation);
            using var registration = cancellationToken.Register(() => decision.TrySetCanceled(cancellationToken));
            // 通知のフックが遅くても、確認と停止は待たせない
            _ = hub.NotifyAsync(conversation, (string?)request["kind"] == "question" ? "Claude からの質問" : $"{request["title"] ?? request["tool"]} の確認待ち");
            try
            {
                return await decision.Task;
            }
            finally
            {
                hub.approvals.TryRemove(requestId, out _);
                hub.EmitStatus(conversation);
            }
        }
    }

    // Claude がモデルや effort を切り替えたとき。開いている画面の選択も合わせる
    public void EmitOptions(Conversation conversation, JsonObject overrides)
    {
        var e = (JsonObject)overrides.DeepClone();
        e["type"] = "options";
        EmitChatEvent(conversation.Id, e);
    }

    // サブエージェントが元のチャットのアーティファクトを作ったり直したりしたとき
    public void EmitState(Conversation conversation) =>
        EmitChatEvent(conversation.Id, new JsonObject { ["type"] = "state", ["state"] = conversation.StateView() });

    void EmitChatEvent(string conversationId, JsonObject e) =>
        events.Emit("chat.event", new JsonObject { ["conversationId"] = conversationId, ["event"] = e });

    Task NotifyAsync(Conversation conversation, string message) => hooks.RunNotificationAsync(conversation, message);

    public void Approve(string requestId, string decision)
    {
        if (decision is not ("once" or "always" or "deny")) throw new InvalidOperationException("decision が不正です");
        if (!approvals.TryGetValue(requestId, out var pending)) return;
        // 質問を「許可」で抜けると、答えの代わりに once が届いてしまう
        if (IsQuestion(pending.Request) && decision != "deny") throw new InvalidOperationException("質問には回答で答えてください");
        pending.Decision.TrySetResult(decision);
    }

    public void Answer(string requestId, JsonArray answers)
    {
        if (!approvals.TryGetValue(requestId, out var pending) || !IsQuestion(pending.Request)) return;
        pending.Decision.TrySetResult(answers.ToJsonString());
    }

    static bool IsQuestion(JsonObject request) => (string?)request["kind"] == "question";

    public int RunHopsOf(string conversationId) => runHops.GetValueOrDefault(conversationId);
}
