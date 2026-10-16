using System.Text.Json.Nodes;
using static ClaudeChat.Core.ToolJson;

namespace ClaudeChat.Core;

// サブエージェント、ワークフロー、チャット同士の通信。Claude Code の Agent / Workflow / ListAgents / SendMessage にあたる
public sealed class AgentToolProvider(ChatHub hub, ConversationStore conversations, FolderStore folders, SettingsStore settings, WorkflowRunner workflows) : IToolProvider
{
    public const string Source = "agents";
    const string Prefix = "agents__";
    public const string RunWorkflow = Prefix + "run_workflow";
    public const string SpawnAgent = Prefix + "spawn_agent";
    // 子の最後の回答を親に返す量の上限
    const int MaxResultChars = 30_000;
    static readonly string ModelChoice = $"Model id: {string.Join(", ", ModelCatalog.Known)}";
    const string EffortChoice = "low, medium, high, xhigh, or max";

    public IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources)
    {
        if (settings.Current?.Agents.Enabled != true || (enabledSources is not null && !enabledSources.Contains(Source))) return [];
        // 子のエージェントはさらに子を作れない
        if (ToolContext.Current is { Depth: > 0 }) return [];
        return
        [
            new(SpawnAgent, Source, "spawn_agent", "サブエージェントに任せる",
                "Start a sub-agent: a fresh Claude chat that works on one self-contained task with the same tools as you, and reports back. It sees only your prompt, so include all needed context. With background=false (default) you wait and get its final answer as the result; several spawn_agent calls in one message run at the same time, so call them together for independent subtasks. With background=true it runs while you continue, and its result arrives later as a <task-notification> message. The sub-agent appears as a child chat the user can open.",
                Schema(("prompt", "string", "The full task for the sub-agent", true), ("title", "string", "A short name shown to the user", true), ("background", "boolean", "Run without waiting", false),
                    ("model", "string", $"Optional; defaults to this chat's model. Set it only when the task clearly calls for it (a cheaper model for simple bulk work, a stronger one for hard reasoning). {ModelChoice}", false),
                    ("effort", "string", $"Optional; defaults to this chat's effort. {EffortChoice}", false)),
                // 子が作業中に確認なしでツールを使うときは、起動の時点で人に 1 回確かめてもらう
                AutoApprove: settings.Current?.Agents.AutoApproveChildren != true, ReadOnly: true),
            new(RunWorkflow, Source, "run_workflow", "ワークフローを実行",
                WorkflowRunner.ToolDescription,
                Schema(("script", "string", "Workflow script (JavaScript). Omit when using name", false), ("name", "string", "A bundled workflow: research", false), ("args", "object", "Value exposed to the script as args", false), ("title", "string", "A short name shown to the user", true),
                    ("model", "string", $"Optional default model for the workflow's agents; defaults to this chat's model. {ModelChoice}", false),
                    ("effort", "string", $"Optional default effort for the workflow's agents; defaults to this chat's effort. {EffortChoice}", false)),
                AllowAlways: false),
            new(Prefix + "set_model", Source, "set_model", "モデルを切り替える",
                "Change the model and/or effort a chat uses from its next message on: this chat (default) or another chat by id or exact title. Use it only when the user explicitly asks you to switch. Never switch on your own, and don't call it when nothing would change. For sub-agents and workflows, use their model and effort options instead.",
                Schema(("chat", "string", "Chat id or exact title; omit for this chat", false), ("model", "string", ModelChoice, false), ("effort", "string", EffortChoice, false)),
                AutoApprove: true, ReadOnly: true),
            new(Prefix + "list_tasks", Source, "list_tasks", "タスク一覧",
                "List the sub-agents and workflows this chat started, with their status.",
                Schema(), AutoApprove: true, ReadOnly: true),
            new(Prefix + "stop_task", Source, "stop_task", "タスクを止める",
                "Stop a sub-agent or workflow this chat started, by id.",
                Schema(("id", "string", "Chat id of the sub-agent, or workflow id", true)), AutoApprove: true, ReadOnly: true),
            new(Prefix + "list_chats", Source, "list_chats", "チャットの一覧と状態",
                "List the other Claude chats you may contact, with each one's status: running (generating a reply), waiting (waiting for the user's approval), idle, failed, or stopped.",
                Schema(), AutoApprove: true, ReadOnly: true),
            new(Prefix + "send_message", Source, "send_message", "ほかのチャットに送る",
                "Send a message to another Claude chat by id or exact title. An idle chat starts a new turn to answer; a running chat receives it when its current turn ends. Set notify_when_idle to get an <idle-notification> with its latest reply when it next goes idle.",
                Schema(("to", "string", "Chat id or exact title", true), ("message", "string", "The message", true), ("notify_when_idle", "boolean", "Notify me when that chat goes idle", false)),
                // 相手に動いてもらうものなので、計画モードでは使わない
                AutoApprove: true),
        ];
    }

    public async Task<ToolCallResult> CallAsync(string apiName, JsonObject input, CancellationToken cancellationToken)
    {
        var context = ToolContext.Current ?? throw new ToolException("チャットの外からは使えません");
        var me = context.Conversation;
        return apiName[Prefix.Length..] switch
        {
            "spawn_agent" => await SpawnAsync(me, input, cancellationToken),
            "run_workflow" => await RunWorkflowAsync(me, input),
            "list_tasks" => await ListTasksAsync(me),
            "stop_task" => await StopTaskAsync(me, Required(input, "id")),
            "list_chats" => await ListChatsAsync(me),
            "send_message" => await SendMessageAsync(me, input, context.Hops),
            "set_model" => await SetModelAsync(me, input),
            _ => throw new ToolException($"ツール {apiName} はありません"),
        };
    }

    static string Shorten(string s) => s.Length > MaxResultChars ? s[..MaxResultChars] + "\n... (長いので省略)" : s;

    async Task<ToolCallResult> SpawnAsync(Conversation me, JsonObject input, CancellationToken cancellationToken)
    {
        var prompt = Required(input, "prompt");
        var title = (string?)input["title"] is { Length: > 0 } t ? t : "サブエージェント";
        var background = (bool?)input["background"] == true;
        var overrides = ModelCatalog.Overrides((string?)input["model"], (string?)input["effort"]);
        var child = await hub.SpawnAsync(me, prompt, title, "agent", null, null, background, cancellationToken, overrides);
        if (background) return Text($"サブエージェント「{title}」(chat_id {child.Id}) をバックグラウンドで始めました。終わると <task-notification> が届きます");
        // 親を止めたら子も止める
        using var registration = cancellationToken.Register(() => hub.Stop(child.Id));
        var result = await hub.WaitAsync(child.Id, CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Status == ChatStatus.Done
            ? Text($"サブエージェント「{title}」(chat_id {child.Id}) の結果:\n{Shorten(ChatHub.FinalText(child))}")
            : new ToolCallResult(new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"サブエージェント「{title}」が {result.Status} で終わりました: {result.Error}" }), true);
    }

    async Task<ToolCallResult> RunWorkflowAsync(Conversation me, JsonObject input)
    {
        var title = (string?)input["title"] is { Length: > 0 } t ? t : "ワークフロー";
        var overrides = ModelCatalog.Overrides((string?)input["model"], (string?)input["effort"]);
        var run = workflows.Start(me, (string?)input["script"], (string?)input["name"], input["args"]?.DeepClone(), title, overrides);
        return Text($"ワークフロー「{title}」(id {run.Id}) を始めました。終わると結果が <task-notification> で届きます。待っている間に別の作業を続けてかまいません");
    }

    async Task<List<Conversation>> ChildrenAsync(Conversation me)
    {
        var result = new List<Conversation>();
        foreach (var summary in (await conversations.ListAsync()).Where(s => s.ParentId == me.Id))
        {
            if (await conversations.GetAsync(summary.Id) is { } c) result.Add(c);
        }
        return result;
    }

    async Task<ToolCallResult> ListTasksAsync(Conversation me)
    {
        var lines = (await ChildrenAsync(me)).Where(c => c.WorkflowId is null).Select(c => $"agent {c.Id}  {hub.StatusOf(c)}  {c.Title}").ToList();
        lines.AddRange(workflows.ForParent(me.Id).Select(w => $"workflow {w.Id}  {w.Status}  {w.Title}  (エージェント {w.AgentCount} 個)"));
        return Text(lines.Count == 0 ? "このチャットが始めたタスクはありません" : string.Join("\n", lines));
    }

    async Task<ToolCallResult> StopTaskAsync(Conversation me, string id)
    {
        if (workflows.Get(id) is { } run && run.ParentId == me.Id)
        {
            workflows.Stop(id);
            return Text($"ワークフロー {id} を止めました");
        }
        var child = await conversations.GetAsync(id);
        if (child is null || child.ParentId != me.Id) throw new ToolException("このチャットが始めたタスクではありません");
        hub.Stop(id);
        return Text($"サブエージェント {child.Title} を止めました");
    }

    async Task<List<Conversation>> ReachableAsync(Conversation me)
    {
        var result = new List<Conversation>();
        foreach (var summary in await conversations.ListAsync())
        {
            if (summary.Id == me.Id || await conversations.GetAsync(summary.Id) is not { } other) continue;
            if (await hub.CanMessageAsync(me, other)) result.Add(other);
        }
        return result;
    }

    async Task<ToolCallResult> ListChatsAsync(Conversation me)
    {
        var folder = await folders.GetAsync(me.FolderId);
        var scope = folder is null ? "このチャットはプロジェクトに入っていないため、自分が始めたサブエージェントとしか通信できません" : folder.MessagingScope switch
        {
            "all" => "通信できる範囲: すべてのチャット",
            "folder" => $"通信できる範囲: プロジェクト「{folder.Name}」の中",
            _ => $"プロジェクト「{folder.Name}」は通信しない設定です。自分が始めたサブエージェントとだけ通信できます",
        };
        var chats = await ReachableAsync(me);
        var lines = chats.Select(c => $"{c.Id}  [{hub.StatusOf(c)}]  {c.Title}{(c.Kind != "chat" ? $"  ({c.Kind})" : "")}");
        return Text($"{scope}\n{(chats.Count == 0 ? "通信できるチャットはありません" : string.Join("\n", lines))}");
    }

    async Task<ToolCallResult> SetModelAsync(Conversation me, JsonObject input)
    {
        var overrides = ModelCatalog.Overrides((string?)input["model"], (string?)input["effort"]) ?? throw new ToolException("model か effort を指定してください");
        var target = me;
        if ((string?)input["chat"] is { Length: > 0 } chat && chat != me.Id && !chat.Equals(me.Title, StringComparison.OrdinalIgnoreCase))
        {
            var reachable = await ReachableAsync(me);
            target = reachable.FirstOrDefault(c => c.Id == chat)
                ?? reachable.FirstOrDefault(c => c.Title.Equals(chat, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException($"{chat} というチャットは見つかりません。list_chats で確認してください");
        }
        var before = $"{ModelCatalog.DisplayName((string?)target.Options?["model"] ?? target.Model ?? "")} / {(string?)target.Options?["effort"] ?? "既定"}";
        target.Options ??= [];
        foreach (var (key, value) in overrides) target.Options[key] = value?.DeepClone();
        if (overrides["model"] is { } model) target.Model = (string?)model;
        await conversations.SaveAsync(target, touch: false);
        hub.EmitOptions(target, overrides);
        var after = $"{ModelCatalog.DisplayName((string?)target.Options["model"] ?? target.Model ?? "")} / {(string?)target.Options["effort"] ?? "既定"}";
        var who = target == me ? "このチャット" : $"チャット「{target.Title}」";
        return Text($"{who}を {before} から {after} に切り替えました。次のメッセージから使われます");
    }

    async Task<ToolCallResult> SendMessageAsync(Conversation me, JsonObject input, int hops)
    {
        var to = Required(input, "to");
        var message = Required(input, "message");
        var maxHops = Math.Max(1, (await settings.GetAsync()).Agents.MaxMessageHops);
        if (hops + 1 > maxHops) throw new ToolException($"チャット同士のやり取りが {maxHops} 往復に達したので送れません。ユーザーに確認してください");
        var reachable = await ReachableAsync(me);
        var target = reachable.FirstOrDefault(c => c.Id == to)
            ?? reachable.FirstOrDefault(c => c.Title.Equals(to, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException($"{to} というチャットには送れません。list_chats で送れる相手を確認してください");
        var status = hub.StatusOf(target);
        if ((bool?)input["notify_when_idle"] == true) hub.WatchIdle(target.Id, me.Id);
        await hub.DeliverAsync(target, $"<message from_chat=\"{me.Title.Replace("\"", "'")}\" chat_id=\"{me.Id}\">\n{message}\n</message>", hops + 1);
        var timing = status is ChatHub.Running or ChatHub.Waiting ? "相手は応答中なので、今の応答が終わったら届きます" : "相手が受け取って応答を始めました";
        return Text($"「{target.Title}」に送りました。{timing}");
    }
}

// claude.ai の過去のチャット検索にあたる
public sealed class ChatSearchToolProvider(ConversationStore conversations, SettingsStore settings) : IToolProvider
{
    public const string Source = "chats";
    const string Prefix = "chats__";
    const int MaxHits = 20;
    const int MaxReadChars = 40_000;

    public IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources)
    {
        if (settings.Current?.Features.ChatSearch != true || (enabledSources is not null && !enabledSources.Contains(Source))) return [];
        return
        [
            new(Prefix + "search", Source, "search", "過去のチャットを探す",
                "Search the user's past chats by keywords. Only chats in the same folder as this chat are searched (chats outside any folder see only other chats outside folders). Use it when the user refers to something discussed before. Returns matching chats with snippets.",
                Schema(("query", "string", "Keywords. Several words must all appear", true)), AutoApprove: true, ReadOnly: true),
            new(Prefix + "recent", Source, "recent", "最近のチャット",
                "List the user's most recent past chats, newest first, with a short excerpt of how each began. Use it when the user refers to a recent conversation without keywords (\"what did we talk about yesterday?\"). before limits it to chats last updated before a date. Same visibility rule as search.",
                Schema(("limit", "integer", "How many, 1-20 (default 10)", false), ("before", "string", "Only chats updated before this date, yyyy-mm-dd", false)), AutoApprove: true, ReadOnly: true),
            new(Prefix + "read", Source, "read", "過去のチャットを読む",
                "Read the text of a past chat by id, from search or recent results.",
                Schema(("id", "string", "Chat id", true), ("offset", "integer", "Character offset to start from", false)), AutoApprove: true, ReadOnly: true),
        ];
    }

    // claude.ai の recent_chats にあたる
    async Task<string> RecentAsync(Conversation? me, JsonObject input, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp((int?)input["limit"] ?? 10, 1, MaxHits);
        long? before = (string?)input["before"] is { Length: > 0 } date && DateTime.TryParse(date, out var parsed)
            ? new DateTimeOffset(parsed.Date).ToUnixTimeMilliseconds()
            : null;
        var lines = new List<string>();
        foreach (var summary in await conversations.ListAsync())
        {
            if (lines.Count >= limit) break;
            if (summary.FolderId != me?.FolderId || (before is { } b && summary.UpdatedAt >= b)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (await conversations.GetAsync(summary.Id) is not { } c || !Visible(c, me)) continue;
            var first = c.CopyMessages().Where(Conversation.IsHumanTurn).Select(m => WireText.Of(m["content"]).Trim()).FirstOrDefault(t => t.Length > 0) ?? "";
            var excerpt = first.Replace('\n', ' ');
            lines.Add($"{c.Id}  {c.Title}  ({DateTimeOffset.FromUnixTimeMilliseconds(c.UpdatedAt).LocalDateTime:yyyy-MM-dd HH:mm})\n  {(excerpt.Length > 160 ? excerpt[..160] + "..." : excerpt)}");
        }
        return lines.Count == 0 ? "見つかりませんでした" : string.Join("\n", lines);
    }

    static string TextOf(Conversation c) => string.Join("\n\n", c.CopyMessages()
        .Where(m => Conversation.IsHumanTurn(m) || (string?)m["role"] == "assistant")
        .Select(m => $"[{((string?)m["role"] == "user" ? "user" : "assistant")}] {WireText.Of(m["content"])}")
        .Where(t => t.Length > 13));

    // claude.ai と同じく、プロジェクト (フォルダ) の中の会話はその中からだけ探せる。ほかの会話の子は対象にしない
    static bool Visible(Conversation c, Conversation? me) =>
        c.Id != me?.Id && c.FolderId == me?.FolderId && (c.Kind == "chat" || c.ParentId == me?.Id);

    public async Task<ToolCallResult> CallAsync(string apiName, JsonObject input, CancellationToken cancellationToken)
    {
        var me = ToolContext.Current?.Conversation;
        if (apiName == Prefix + "read")
        {
            var c = await conversations.GetAsync(Required(input, "id"));
            if (c is null || !Visible(c, me)) throw new ToolException("そのチャットはありません");
            var text = TextOf(c);
            var offset = Math.Clamp((int?)input["offset"] ?? 0, 0, text.Length);
            var chunk = text[offset..Math.Min(text.Length, offset + MaxReadChars)];
            var more = offset + chunk.Length < text.Length ? $"\n... 続きは offset={offset + chunk.Length}" : "";
            return Text($"# {c.Title} ({DateTimeOffset.FromUnixTimeMilliseconds(c.UpdatedAt).LocalDateTime:yyyy-MM-dd})\n{chunk}{more}");
        }
        if (apiName == Prefix + "recent") return Text(await RecentAsync(me, input, cancellationToken));
        var words = Required(input, "query").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<string>();
        foreach (var summary in await conversations.ListAsync())
        {
            if (summary.FolderId != me?.FolderId || hits.Count >= MaxHits) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (await conversations.GetAsync(summary.Id) is not { } c || !Visible(c, me)) continue;
            var text = $"{c.Title}\n{TextOf(c)}";
            if (!words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            var at = text.IndexOf(words[0], StringComparison.OrdinalIgnoreCase);
            var start = Math.Max(0, at - 80);
            var snippet = text.Substring(start, Math.Min(240, text.Length - start)).Replace('\n', ' ');
            hits.Add($"{c.Id}  {c.Title}  ({DateTimeOffset.FromUnixTimeMilliseconds(c.UpdatedAt).LocalDateTime:yyyy-MM-dd})\n  ...{snippet}...");
        }
        return Text(hits.Count == 0 ? "見つかりませんでした" : string.Join("\n", hits));
    }
}
