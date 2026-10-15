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

    readonly ConcurrentDictionary<string, CancellationTokenSource> running = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<ChatRunResult>> completions = new();
    readonly ConcurrentDictionary<string, (string ConversationId, JsonObject Request, TaskCompletionSource<string> Decision)> approvals = new();
    readonly ConcurrentDictionary<string, ConcurrentQueue<Inbound>> inboxes = new();
    // 待機中になったら知らせてほしい、という登録。相手 -> 知らせる先
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> idleWatchers = new();
    // 待たずに走らせた子。終わったら親に知らせる
    readonly ConcurrentDictionary<string, byte> backgroundChildren = new();
    readonly ConcurrentDictionary<string, int> runHops = new();
    readonly ConcurrentDictionary<string, int> stopContinuations = new();
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

    public JsonArray RunningView() => new(running.Keys.Select(id => (JsonNode)new JsonObject
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

    public async Task<ChatOptions> OptionsForAsync(Conversation conversation, JsonObject? request = null, int depth = 0)
    {
        var current = await settings.GetAsync();
        var defaults = current.Chat;
        // 画面からの指定が無いとき (ほかのチャットからの通信など) は、その会話の直近の設定で動かす
        var source = request ?? conversation.Options ?? [];
        var enabled = source["enabledSources"] is JsonArray list ? list.Select(n => (string?)n).OfType<string>().ToHashSet() : null;
        // サブエージェントはさらに子を作ったり、ほかのチャットを動かしたりしない
        if (depth > 0) enabled = (enabled ?? Tools.SourceNames().ToHashSet()).Where(s => s != AgentToolProvider.Source).ToHashSet();
        var folder = await folders.GetAsync(conversation.FolderId);
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
            SystemPrompt = SystemPrompt.Build(current, new PromptContext
            {
                FilesEnabled = enabled is null || enabled.Contains(FileToolProvider.Source),
                AgentsEnabled = depth == 0 && current.Agents.Enabled && (enabled is null || enabled.Contains(AgentToolProvider.Source)),
                Folder = folder,
                Conversation = conversation,
                Skills = extensions.ListSkills(),
                OutputStyle = extensions.OutputStyleText(defaults.OutputStyle),
                IsAgent = depth > 0,
            }, DateTime.Now),
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
        }
    }

    // 会話を書き換えてから走らせる。書き換えの前に枠を取るので、連打で 2 本走らない
    public async Task StartAsync(Conversation conversation, Func<Task> prepare, ChatOptions options, int depth = 0, int hops = 0)
    {
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
        events.Emit("chat.event", new JsonObject { ["conversationId"] = conversation.Id, ["event"] = new JsonObject { ["type"] = "started" } });
        EmitStatus(conversation);
        _ = RunAsync(conversation, options, cancellation, completion, depth, hops);
    }

    async Task RunAsync(Conversation conversation, ChatOptions options, CancellationTokenSource cancellation, TaskCompletionSource<ChatRunResult> completion, int depth, int hops)
    {
        var id = conversation.Id;
        ToolContext.Current = new ToolContext { Conversation = conversation, Options = options, Depth = depth, Hops = hops };
        // 途中経過を順番どおりに保存する。落ちてもそこまでの会話は残る
        var saveChain = Task.CompletedTask;
        void EmitChat(JsonObject e)
        {
            if ((string?)e["type"] == "message") saveChain = saveChain.ContinueWith(_ => conversations.SaveAsync(conversation, touch: false)).Unwrap();
            events.Emit("chat.event", new JsonObject { ["conversationId"] = id, ["event"] = e });
        }

        var result = new ChatRunResult(ChatStatus.Failed, "不明なエラー");
        string? stopFeedback = null;
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
                result = await engine.RunAsync(conversation, options, EmitChat, new ApprovalGate(this, conversation), cancellation.Token);
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
            stopFeedback = (await hooks.RunStopAsync(conversation, result)) is { Blocked: true } stop ? stop.Message : null;

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
            lock (agentLock) runningAgents.Remove(id);
            completions.TryRemove(new KeyValuePair<string, TaskCompletionSource<ChatRunResult>>(id, completion));
            completion.TrySetResult(result);
            EmitStatus(conversation);
        }
        await AfterRunAsync(conversation, result, stopFeedback);
    }

    // 終わった会話に溜まったメッセージを流し、待っていた相手や親に知らせる
    async Task AfterRunAsync(Conversation conversation, ChatRunResult result, string? stopFeedback)
    {
        try
        {
            // Stop フックが続けるよう求めたら、理由を渡してもう一度動かす。止まらなくなるのを防ぐため 3 回まで
            if (stopFeedback is not null && result.Status == ChatStatus.Done)
            {
                var count = stopContinuations.AddOrUpdate(conversation.Id, 1, (_, n) => n + 1);
                if (count <= 3) await DeliverAsync(conversation, $"<hook-feedback event=\"Stop\">\n{stopFeedback}\n</hook-feedback>", RunHopsOf(conversation.Id));
            }
            else
            {
                stopContinuations.TryRemove(conversation.Id, out _);
            }
            if (backgroundChildren.TryRemove(conversation.Id, out _) && conversation.ParentId is { } parentId && await conversations.GetAsync(parentId) is { } parent)
            {
                var status = result.Status == ChatStatus.Done ? "completed" : result.Status == ChatStatus.Aborted ? "stopped" : "failed";
                var body = result.Status == ChatStatus.Done ? FinalText(conversation) : result.Error ?? "";
                await DeliverAsync(parent, $"<task-notification agent=\"{Escape(conversation.Title)}\" chat_id=\"{conversation.Id}\" status=\"{status}\">\n{body}\n</task-notification>", hops: 0);
            }
            if (idleWatchers.TryRemove(conversation.Id, out var watchers))
            {
                foreach (var watcherId in watchers.Keys)
                {
                    if (await conversations.GetAsync(watcherId) is not { } watcher) continue;
                    await DeliverAsync(watcher, $"<idle-notification chat=\"{Escape(conversation.Title)}\" chat_id=\"{conversation.Id}\" status=\"{StatusOf(conversation)}\">\n{Shorten(FinalText(conversation), 4000)}\n</idle-notification>", hops: 0);
                }
            }
            await FlushInboxAsync(conversation);
        }
        catch (Exception)
        {
            // 知らせに失敗しても、終わった会話そのものには影響させない
        }
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
        if (!completions.TryGetValue(conversationId, out var completion)) return new ChatRunResult(ChatStatus.Done);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    // Messaging

    // 待機中ならすぐ動かし、応答中なら終わってから届ける
    public async Task DeliverAsync(Conversation target, string text, int hops)
    {
        inboxes.GetOrAdd(target.Id, _ => new()).Enqueue(new Inbound(text, hops));
        if (!running.ContainsKey(target.Id)) await FlushInboxAsync(target);
    }

    async Task FlushInboxAsync(Conversation target)
    {
        if (!inboxes.TryGetValue(target.Id, out var queue) || queue.IsEmpty || running.ContainsKey(target.Id)) return;
        var items = new List<Inbound>();
        while (queue.TryDequeue(out var item)) items.Add(item);
        if (items.Count == 0) return;
        var depth = target.Kind == "chat" ? 0 : 1;
        var options = await OptionsForAsync(target, depth: depth);
        try
        {
            await StartAsync(target, () =>
            {
                var content = new JsonArray(items.Select(i => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = i.Text }).ToArray());
                var message = new JsonObject { ["role"] = "user", ["content"] = content };
                target.Append(message);
                events.Emit("chat.event", new JsonObject { ["conversationId"] = target.Id, ["event"] = new JsonObject { ["type"] = "inbound", ["message"] = message.DeepClone() } });
                return Task.CompletedTask;
            }, options, depth: depth, hops: items.Max(i => i.Hops));
        }
        catch (InvalidOperationException)
        {
            // ちょうど別の応答が始まった。その応答が終わったときにもう一度流す
            foreach (var item in items) queue.Enqueue(item);
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
        var folder = await folders.GetAsync(from.FolderId);
        if (folder is null) return false;
        return folder.MessagingScope switch
        {
            "all" => true,
            "folder" => to.FolderId == from.FolderId,
            _ => false,
        };
    }

    // Agents

    public int RunningAgents()
    {
        lock (agentLock) return runningAgents.Count;
    }

    // ワークフローは空きが出るまで待ち、単発の呼び出しは待たずに断る
    public async Task<Conversation> SpawnAsync(Conversation parent, string prompt, string title, string kind, string? workflowId, Func<ChatOptions, ChatOptions>? customize, bool background, CancellationToken cancellationToken = default)
    {
        var limit = Math.Max(1, (await settings.GetAsync()).Agents.MaxConcurrent);
        var child = conversations.Create();
        while (true)
        {
            lock (agentLock)
            {
                if (runningAgents.Count < limit)
                {
                    runningAgents.Add(child.Id);
                    break;
                }
            }
            if (workflowId is null) throw new ToolException($"同時に動かせるエージェントは {limit} 個までです。終わるのを待ってから呼んでください");
            await Task.Delay(500, cancellationToken);
        }
        child.Title = title;
        child.Kind = kind;
        child.ParentId = parent.Id;
        child.FolderId = parent.FolderId;
        child.WorkflowId = workflowId;
        child.WorktreePath = parent.WorktreePath;
        child.PermissionMode = parent.PermissionMode == "plan" ? "plan" : "default";
        // 親と同じ道具立てで動かす
        child.Options = parent.Options?.DeepClone() as JsonObject;
        try
        {
            var baseOptions = await OptionsForAsync(child, depth: 1);
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
            }, options, depth: 1);
        }
        catch
        {
            lock (agentLock) runningAgents.Remove(child.Id);
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
            await hub.NotifyAsync(conversation, $"{request["title"] ?? request["tool"]} の確認待ち");
            using var registration = cancellationToken.Register(() => decision.TrySetCanceled(cancellationToken));
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

    void EmitChatEvent(string conversationId, JsonObject e) =>
        events.Emit("chat.event", new JsonObject { ["conversationId"] = conversationId, ["event"] = e });

    Task NotifyAsync(Conversation conversation, string message) => hooks.RunNotificationAsync(conversation, message);

    public void Approve(string requestId, string decision)
    {
        if (decision is not ("once" or "always" or "deny")) throw new InvalidOperationException("decision が不正です");
        if (approvals.TryGetValue(requestId, out var pending)) pending.Decision.TrySetResult(decision);
    }

    public int RunHopsOf(string conversationId) => runHops.GetValueOrDefault(conversationId);
}
