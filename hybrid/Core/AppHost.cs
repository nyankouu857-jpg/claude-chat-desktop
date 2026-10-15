using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;

namespace ClaudeChat.Core;

public interface IPlatform
{
    string Version { get; }
    void OpenPath(string path);
    string? PickFolder();
    void MoveToTrash(string path);
}

// 画面 (WebView2 やブラウザ) とのやり取りを一手に引き受ける。通信路には依存しない
public sealed class AppHost : IAsyncDisposable
{
    sealed class EventSink(AppHost host) : IChatEvents
    {
        public void Emit(string name, JsonNode? data) => host.Emit(name, data);
    }

    readonly AppPaths paths;
    readonly IPlatform platform;
    readonly Action<string> send;
    readonly SettingsStore settings;
    readonly SecretStore secrets;
    readonly ConversationStore conversations;
    readonly FolderStore folders;
    readonly UsageLedger ledger;
    readonly Extensions extensions;
    readonly Checkpoints checkpoints;
    readonly Hooks hooks;
    readonly ChatHub hub;
    readonly WorkflowRunner workflows;
    readonly Scheduler scheduler;
    readonly McpHub mcp;
    readonly TerminalToolProvider terminal;
    readonly BrowserToolProvider browser;
    readonly OrgCostClient orgCost;
    readonly HttpClient http = new();

    public AppHost(AppPaths paths, ISecretProtector protector, IPlatform platform, Action<string> send)
    {
        this.paths = paths;
        this.platform = platform;
        this.send = send;
        var events = new EventSink(this);
        settings = new SettingsStore(paths);
        secrets = new SecretStore(paths, protector);
        conversations = new ConversationStore(paths);
        folders = new FolderStore(paths);
        ledger = new UsageLedger(paths);
        extensions = new Extensions(paths);
        checkpoints = new Checkpoints(paths, platform);
        hooks = new Hooks(settings, paths);
        hub = new ChatHub(paths, settings, secrets, conversations, folders, ledger, hooks, extensions, events);
        workflows = new WorkflowRunner(hub, settings, conversations, events);
        scheduler = new Scheduler(paths, hub, conversations, events);
        mcp = new McpHub(paths);
        terminal = new TerminalToolProvider(settings);
        browser = new BrowserToolProvider(settings, paths);
        // 同じ名前があれば先に並べたものが勝つので、MCP は最後
        hub.Tools = new ToolRegistry(
        [
            new FileToolProvider(settings, platform, checkpoints),
            terminal,
            new ProgramToolProvider(settings),
            browser,
            new AgentToolProvider(hub, conversations, folders, settings, workflows),
            new ChatSearchToolProvider(conversations, settings),
            new TodoToolProvider(),
            new ArtifactToolProvider(settings),
            new MemoryToolProvider(paths, settings),
            new PlanToolProvider(),
            new ScheduleToolProvider(scheduler),
            new SkillToolProvider(extensions),
            mcp,
        ]);
        orgCost = new OrgCostClient(paths, http);
        mcp.Changed += () => Emit("mcp.changed", mcp.Describe());
    }

    public async Task StartAsync()
    {
        var current = await settings.GetAsync();
        await ledger.GetPricingAsync();
        await mcp.ApplyAsync(current.McpServers);
        scheduler.Start();
    }

    void Send(JsonObject message) => send(message.ToJsonString(Json.Wire));

    void Emit(string name, JsonNode? data) => Send(new JsonObject { ["event"] = name, ["data"] = data });

    public async Task HandleAsync(string raw)
    {
        JsonObject? message = null;
        try
        {
            message = JsonNode.Parse(raw)?.AsObject();
            var method = (string?)message?["method"] ?? throw new InvalidOperationException("method がありません");
            var result = await DispatchAsync(method, message!["params"] as JsonObject ?? []);
            Send(new JsonObject { ["id"] = message["id"]?.DeepClone(), ["result"] = result });
        }
        catch (Exception e)
        {
            Send(new JsonObject { ["id"] = message?["id"]?.DeepClone(), ["error"] = e.Message });
        }
    }

    Task<JsonNode?> DispatchAsync(string method, JsonObject p) => method switch
    {
        "app.init" => InitAsync(),
        "settings.update" => UpdateSettingsAsync(p),
        "settings.reload" => ReloadSettingsAsync(),
        "secrets.set" => SetSecretAsync(p),
        "api.test" => TestApiKeyAsync(),
        "conversations.list" => ListConversationsAsync(),
        "conversations.get" => GetConversationAsync(Str(p, "id")),
        "conversations.rename" => UpdateConversationAsync(Str(p, "id"), c => c.Title = NonEmpty(Str(p, "title"))),
        "conversations.star" => UpdateConversationAsync(Str(p, "id"), c => c.Starred = (bool?)p["starred"] == true),
        "conversations.move" => MoveConversationAsync(Str(p, "id"), (string?)p["folderId"]),
        "conversations.delete" => DeleteConversationAsync(Str(p, "id")),
        "folders.list" => ListFoldersAsync(),
        "folders.save" => SaveFolderAsync(p),
        "folders.delete" => DeleteFolderAsync(Str(p, "id")),
        "chats.statuses" => StatusesAsync(),
        "chat.send" => SendAsync(p),
        "chat.retry" => RetryAsync(p),
        "chat.edit" => EditAsync(p),
        "chat.stop" => StopAsync(Str(p, "conversationId")),
        "chat.setMode" => SetModeAsync(Str(p, "conversationId"), Str(p, "mode")),
        "chat.compact" => CompactAsync(Str(p, "conversationId"), (string?)p["instructions"]),
        "chat.btw" => BtwAsync(Str(p, "conversationId"), Str(p, "question")),
        "chat.export" => ExportAsync(Str(p, "conversationId")),
        "chat.rewind" => RewindAsync(Str(p, "conversationId"), (int?)p["messageIndex"] ?? throw new InvalidOperationException("messageIndex がありません"), (bool?)p["restoreFiles"] == true),
        "chat.rewindInfo" => RewindInfoAsync(Str(p, "conversationId")),
        "tools.approve" => ApproveAsync(Str(p, "requestId"), Str(p, "decision")),
        "artifacts.export" => ExportArtifactAsync(Str(p, "conversationId"), Str(p, "id")),
        "files.open" => OpenFileAsync(Str(p, "conversationId"), Str(p, "fileId"), (bool?)p["reveal"] == true),
        "workflows.list" => Task.FromResult<JsonNode?>(workflows.ListView()),
        "workflows.stop" => StopWorkflowAsync(Str(p, "id")),
        "extensions.list" => Task.FromResult<JsonNode?>(ExtensionsView()),
        "commands.expand" => ExpandCommandAsync(Str(p, "name"), (string?)p["args"] ?? ""),
        "schedules.list" => ListSchedulesAsync(),
        "schedules.save" => SaveScheduleAsync(p),
        "schedules.delete" => DeleteScheduleAsync(Str(p, "id")),
        "memory.list" => Task.FromResult<JsonNode?>(ListMemory()),
        "memory.read" => Task.FromResult<JsonNode?>(File.ReadAllText(MemoryPath(Str(p, "path")))),
        "memory.write" => WriteMemoryAsync(Str(p, "path"), (string?)p["content"] ?? ""),
        "memory.delete" => DeleteMemoryAsync(Str(p, "path")),
        "mcp.list" => Task.FromResult<JsonNode?>(mcp.Describe()),
        "mcp.save" => SaveMcpAsync(p),
        "mcp.restart" => RestartMcpAsync(Str(p, "name")),
        "mcp.openLog" => OpenAsync(mcp.LogPath(Str(p, "name"))),
        "permissions.revoke" => RevokePermissionAsync(Str(p, "apiName")),
        "usage.local" => LocalUsageAsync(),
        "usage.org" => OrgUsageAsync(Str(p, "range")),
        "pricing.reload" => ReloadPricingAsync(),
        "app.open" => OpenTargetAsync(Str(p, "target")),
        "app.pickFolder" => Task.FromResult<JsonNode?>(platform.PickFolder()),
        "chat.systemPrompt" => PreviewSystemPromptAsync((string?)p["conversationId"]),
        _ => throw new InvalidOperationException($"未知のメソッドです: {method}"),
    };

    // MCP サーバー名がこれと同じだとツール名の接頭辞が重なる
    static readonly string[] BuiltinSources =
    [
        FileToolProvider.Source, TerminalToolProvider.Source, ProgramToolProvider.Source, BrowserToolProvider.Source,
        AgentToolProvider.Source, ChatSearchToolProvider.Source, TodoToolProvider.Source, ArtifactToolProvider.Source,
        MemoryToolProvider.Source, PlanToolProvider.Source, ScheduleToolProvider.Source, SkillToolProvider.Source,
    ];

    static readonly string[] PermissionModes = ["default", "acceptEdits", "plan", "bypass"];

    static string Str(JsonObject p, string key) => (string?)p[key] ?? throw new InvalidOperationException($"{key} がありません");

    static string NonEmpty(string s) => s.Trim() is { Length: > 0 } t ? t : throw new InvalidOperationException("名前を入れてください");

    async Task<Conversation> RequireConversationAsync(string id) =>
        await conversations.GetAsync(id) ?? throw new InvalidOperationException("会話が見つかりません");

    async Task<JsonNode?> InitAsync()
    {
        var current = await settings.GetAsync();
        return new JsonObject
        {
            ["settings"] = SettingsView(current),
            ["secrets"] = await SecretsViewAsync(),
            ["conversations"] = Json.ToNode(await conversations.ListAsync()),
            ["folders"] = Json.ToNode(await folders.ListAsync()),
            ["statuses"] = await hub.StatusesAsync(),
            ["workflows"] = workflows.ListView(),
            ["extensions"] = ExtensionsView(),
            ["mcp"] = mcp.Describe(),
            ["paths"] = new JsonObject { ["root"] = paths.Root, ["isFallback"] = paths.IsFallback },
            // 画面を読み直しても、応答中の表示と承認待ちを取り戻せるように
            ["running"] = hub.RunningView(),
            ["version"] = platform.Version,
        };
    }

    // mcpServers は別画面で扱うので、一般設定の更新では触らせない
    static JsonObject SettingsView(AppSettings s) => new()
    {
        ["profile"] = Json.ToNode(s.Profile),
        ["appearance"] = Json.ToNode(s.Appearance),
        ["chat"] = Json.ToNode(s.Chat),
        ["files"] = Json.ToNode(s.Files),
        ["terminal"] = Json.ToNode(s.Terminal),
        ["programs"] = Json.ToNode(s.Programs),
        ["browser"] = Json.ToNode(s.Browser),
        ["agents"] = Json.ToNode(s.Agents),
        ["features"] = Json.ToNode(s.Features),
        ["hooks"] = JsonSerializer.SerializeToNode(s.Hooks, Json.Wire),
        ["mcpServers"] = JsonSerializer.SerializeToNode(s.McpServers, Json.Wire),
        ["toolPermissions"] = JsonSerializer.SerializeToNode(s.ToolPermissions, Json.Wire),
    };

    JsonObject ExtensionsView() => new()
    {
        ["commands"] = new JsonArray(extensions.ListCommands().Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["description"] = c.Description, ["kind"] = c.Kind }).ToArray()),
        ["outputStyles"] = extensions.ListOutputStyles(),
    };

    async Task<JsonObject> SecretsViewAsync()
    {
        var apiKey = await secrets.GetAsync(SecretStore.ApiKey);
        var adminKey = await secrets.GetAsync(SecretStore.AdminKey);
        return new JsonObject
        {
            ["hasApiKey"] = apiKey is not null,
            ["apiKeyMasked"] = SecretStore.Mask(apiKey),
            ["hasAdminKey"] = adminKey is not null,
            ["adminKeyMasked"] = SecretStore.Mask(adminKey),
        };
    }

    static readonly string[] HookEvents = [Hooks.PreToolUse, Hooks.PostToolUse, Hooks.UserPromptSubmit, Hooks.Stop, Hooks.Notification];

    async Task<JsonNode?> UpdateSettingsAsync(JsonObject patch)
    {
        if (patch["files"]?["folders"] is JsonArray folderList)
        {
            foreach (var path in folderList.Select(f => (string?)f?["path"]))
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !Directory.Exists(path)) throw new InvalidOperationException($"フォルダが見つかりません: {path}");
            }
        }
        var hookPatch = patch["hooks"]?.Deserialize<Dictionary<string, List<HookEntry>>>(Json.Wire);
        if (hookPatch?.Keys.FirstOrDefault(k => !HookEvents.Contains(k)) is { } unknown) throw new InvalidOperationException($"{unknown} というフックはありません");
        var updated = await settings.UpdateAsync(s =>
        {
            if (patch["profile"] is JsonObject profile) s.Profile = Merge(s.Profile, profile);
            if (patch["appearance"] is JsonObject appearance) s.Appearance = Merge(s.Appearance, appearance);
            if (patch["chat"] is JsonObject chat) s.Chat = Merge(s.Chat, chat);
            if (patch["files"] is JsonObject files) s.Files = Merge(s.Files, files);
            if (patch["terminal"] is JsonObject terminalPatch) s.Terminal = Merge(s.Terminal, terminalPatch);
            if (patch["programs"] is JsonObject programs) s.Programs = Merge(s.Programs, programs);
            if (patch["browser"] is JsonObject browserPatch) s.Browser = Merge(s.Browser, browserPatch);
            if (patch["agents"] is JsonObject agents) s.Agents = Merge(s.Agents, agents);
            if (patch["features"] is JsonObject features) s.Features = Merge(s.Features, features);
            if (hookPatch is not null) s.Hooks = hookPatch;
        });
        return SettingsView(updated);
    }

    static T Merge<T>(T current, JsonObject patch)
    {
        var node = JsonSerializer.SerializeToNode(current, Json.Wire)!.AsObject();
        foreach (var (key, value) in patch) node[key] = value?.DeepClone();
        return node.Deserialize<T>(Json.Wire)!;
    }

    async Task<JsonNode?> ReloadSettingsAsync()
    {
        var current = await settings.ReloadAsync();
        await mcp.ApplyAsync(current.McpServers);
        return await InitAsync();
    }

    async Task<JsonNode?> SetSecretAsync(JsonObject p)
    {
        var name = Str(p, "name");
        if (name is not (SecretStore.ApiKey or SecretStore.AdminKey)) throw new InvalidOperationException("不明な項目です");
        await secrets.SetAsync(name, (string?)p["value"]);
        return await SecretsViewAsync();
    }

    async Task<JsonNode?> TestApiKeyAsync()
    {
        var apiKey = await secrets.GetAsync(SecretStore.ApiKey);
        if (apiKey is null) return new JsonObject { ["ok"] = false, ["message"] = "APIキーが未設定です" };
        try
        {
            var page = await hub.GetClient(apiKey).Models.List();
            return new JsonObject { ["ok"] = true, ["message"] = $"接続できました。使えるモデルは {page.Items.Count} 個です" };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["message"] = ChatEngine.Describe(e) };
        }
    }

    // Conversations

    async Task<JsonNode?> ListConversationsAsync() => Json.ToNode(await conversations.ListAsync());

    async Task<JsonNode?> GetConversationAsync(string id) => (await conversations.GetAsync(id))?.ToWire();

    async Task<JsonNode?> UpdateConversationAsync(string id, Action<Conversation> update)
    {
        var conversation = await RequireConversationAsync(id);
        update(conversation);
        await conversations.SaveAsync(conversation, touch: false);
        Emit("conversations.changed", null);
        return conversation.ToWire();
    }

    // 子のエージェントも一緒に動かす。親子が別のフォルダにいると一覧で迷子になる
    async Task<JsonNode?> MoveConversationAsync(string id, string? folderId)
    {
        if (folderId is not null && await folders.GetAsync(folderId) is null) throw new InvalidOperationException("フォルダが見つかりません");
        var conversation = await RequireConversationAsync(id);
        foreach (var target in new[] { conversation }.Concat(await DescendantsAsync(id)))
        {
            target.FolderId = folderId;
            await conversations.SaveAsync(target, touch: false);
        }
        Emit("conversations.changed", null);
        return conversation.ToWire();
    }

    async Task<List<Conversation>> DescendantsAsync(string id)
    {
        var all = await conversations.ListAsync();
        var result = new List<Conversation>();
        var queue = new Queue<string>([id]);
        while (queue.TryDequeue(out var parentId))
        {
            foreach (var child in all.Where(s => s.ParentId == parentId))
            {
                if (await conversations.GetAsync(child.Id) is not { } c) continue;
                result.Add(c);
                queue.Enqueue(c.Id);
            }
        }
        return result;
    }

    async Task<JsonNode?> DeleteConversationAsync(string id)
    {
        hub.Stop(id);
        await conversations.DeleteAsync(id);
        checkpoints.Forget(id);
        Emit("conversations.changed", null);
        return null;
    }

    // Folders

    async Task<JsonNode?> ListFoldersAsync() => Json.ToNode(await folders.ListAsync());

    async Task<JsonNode?> SaveFolderAsync(JsonObject p)
    {
        var folder = p["folder"]?.Deserialize<Folder>(Json.Wire) ?? throw new InvalidOperationException("folder がありません");
        await folders.SaveAsync(folder);
        Emit("folders.changed", null);
        return await ListFoldersAsync();
    }

    // 中の会話は消さずにフォルダの外へ出す
    async Task<JsonNode?> DeleteFolderAsync(string id)
    {
        foreach (var summary in (await conversations.ListAsync()).Where(s => s.FolderId == id))
        {
            if (await conversations.GetAsync(summary.Id) is not { } c) continue;
            c.FolderId = null;
            await conversations.SaveAsync(c, touch: false);
        }
        await folders.DeleteAsync(id);
        Emit("folders.changed", null);
        Emit("conversations.changed", null);
        return await ListFoldersAsync();
    }

    async Task<JsonNode?> StatusesAsync() => await hub.StatusesAsync();

    // Chat

    async Task<JsonNode?> PreviewSystemPromptAsync(string? conversationId)
    {
        var conversation = (conversationId is null ? null : await conversations.GetAsync(conversationId)) ?? new Conversation();
        return (await hub.OptionsForAsync(conversation)).SystemPrompt;
    }

    // ほかのチャットから起こされたときも同じ道具立てで動くよう、送信時の設定を会話に残す
    static JsonObject RequestOptions(JsonObject p)
    {
        var options = new JsonObject();
        foreach (var key in new[] { "model", "effort", "webSearch", "webFetch", "codeExecution", "enabledSources" })
        {
            if (p[key] is { } value) options[key] = value.DeepClone();
        }
        return options;
    }

    async Task ApplyModeAsync(Conversation conversation, string? mode)
    {
        if (mode is null) return;
        if (!PermissionModes.Contains(mode)) throw new InvalidOperationException("権限モードが不正です");
        if (mode == "bypass" && !(await settings.GetAsync()).Features.AllowBypassMode) throw new InvalidOperationException("確認なしモードは設定でオンにしたときだけ使えます");
        conversation.PermissionMode = mode;
    }

    // UserPromptSubmit フックが止めたら送らない。返した文字は Claude への補足として添える
    async Task<JsonNode> PrepareContentAsync(Conversation conversation, JsonNode content)
    {
        var outcome = await hooks.RunUserPromptSubmitAsync(conversation, WireText.Of(content));
        if (outcome.Blocked) throw new InvalidOperationException($"UserPromptSubmit フックが送信を止めました: {outcome.Message}");
        if (outcome.Message is not { } extra) return content;
        var blocks = content as JsonArray ?? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = (string?)content ?? "" });
        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = $"<hook-feedback event=\"UserPromptSubmit\">\n{extra}\n</hook-feedback>" });
        return blocks;
    }

    async Task<JsonNode?> SendAsync(JsonObject p)
    {
        var id = (string?)p["conversationId"];
        var conversation = (id is null ? null : await conversations.GetAsync(id)) ?? conversations.Create();
        if (id is null && (string?)p["folderId"] is { } folderId && await folders.GetAsync(folderId) is not null) conversation.FolderId = folderId;
        var content = p["content"]?.DeepClone() ?? throw new InvalidOperationException("content がありません");
        await ApplyModeAsync(conversation, (string?)p["permissionMode"]);
        content = await PrepareContentAsync(conversation, content);
        var message = new JsonObject { ["role"] = "user", ["content"] = content };
        if ((bool?)p["research"] == true)
        {
            await StartResearchAsync(conversation, p, message);
        }
        else
        {
            var options = await OptionsAsync(conversation, p);
            await hub.StartAsync(conversation, () =>
            {
                conversation.Append(message);
                return Task.CompletedTask;
            }, options);
        }
        return new JsonObject { ["conversationId"] = conversation.Id };
    }

    async Task<ChatOptions> OptionsAsync(Conversation conversation, JsonObject p)
    {
        conversation.Options = RequestOptions(p);
        return await hub.OptionsForAsync(conversation, conversation.Options);
    }

    // claude.ai の Research にあたる。同梱のリサーチ用ワークフローを走らせ、結果が届いたら Claude がまとめる
    async Task StartResearchAsync(Conversation conversation, JsonObject p, JsonObject message)
    {
        if (!(await settings.GetAsync()).Agents.Enabled) throw new InvalidOperationException("リサーチにはエージェントが要ります。設定でエージェントをオンにしてください");
        conversation.Options = RequestOptions(p);
        var question = WireText.Of(message["content"]).Trim();
        if (question.Length == 0) throw new InvalidOperationException("調べたいことを書いてください");
        await hub.ExclusiveAsync(conversation, async _ =>
        {
            conversation.Append(message);
            await conversations.SaveAsync(conversation);
            Emit("conversations.changed", null);
            return 0;
        });
        var title = question.Length > 30 ? question[..30] + "…" : question;
        workflows.Start(conversation, null, "research", new JsonObject { ["question"] = question }, $"リサーチ: {title}");
    }

    async Task<JsonNode?> RetryAsync(JsonObject p)
    {
        var conversation = await RequireConversationAsync(Str(p, "conversationId"));
        var options = await OptionsAsync(conversation, p);
        await hub.StartAsync(conversation, () =>
        {
            var lastHuman = conversation.LastHumanIndex();
            if (lastHuman < 0) throw new InvalidOperationException("やり直す発言がありません");
            // 最後の発言より前は一切変えないので、過去の思考ブロックはそのまま有効
            conversation.TruncateFrom(lastHuman + 1);
            return Task.CompletedTask;
        }, options);
        return conversation.ToWire();
    }

    async Task<JsonNode?> EditAsync(JsonObject p)
    {
        var conversation = await RequireConversationAsync(Str(p, "conversationId"));
        var index = (int?)p["messageIndex"] ?? throw new InvalidOperationException("messageIndex がありません");
        var content = await PrepareContentAsync(conversation, p["content"]?.DeepClone() ?? throw new InvalidOperationException("content がありません"));
        var options = await OptionsAsync(conversation, p);
        await hub.StartAsync(conversation, () =>
        {
            conversation.TruncateFrom(index);
            conversation.Append(new JsonObject { ["role"] = "user", ["content"] = content });
            return Task.CompletedTask;
        }, options);
        return conversation.ToWire();
    }

    Task<JsonNode?> StopAsync(string conversationId)
    {
        hub.Stop(conversationId);
        return Task.FromResult<JsonNode?>(null);
    }

    Task<JsonNode?> ApproveAsync(string requestId, string decision)
    {
        hub.Approve(requestId, decision);
        return Task.FromResult<JsonNode?>(null);
    }

    async Task<JsonNode?> SetModeAsync(string conversationId, string mode)
    {
        var conversation = await RequireConversationAsync(conversationId);
        await ApplyModeAsync(conversation, mode);
        await conversations.SaveAsync(conversation, touch: false);
        return conversation.ToWire();
    }

    const string CompactPrompt = "Summarize this conversation so far so it can continue in a fresh context. Include the user's goals and preferences, decisions made, facts and numbers established, file paths and commands used, and anything still pending. Write it in the language of the conversation. Respond with the summary only.";

    // Claude Code の /compact。要約を会話に足し、それより前は API に送らない。画面には全部残す
    async Task<JsonNode?> CompactAsync(string conversationId, string? instructions)
    {
        var conversation = await RequireConversationAsync(conversationId);
        if (conversation.MessageCount - conversation.ApiStart < 2) throw new InvalidOperationException("要約するほどの会話がありません");
        var engine = await hub.EngineAsync();
        var options = await hub.OptionsForAsync(conversation);
        await hub.ExclusiveAsync(conversation, async token =>
        {
            var prompt = string.IsNullOrWhiteSpace(instructions) ? CompactPrompt : $"{CompactPrompt}\nAdditional instructions from the user: {instructions.Trim()}";
            var summary = await engine.AskAsideAsync(conversation, options, prompt, "compact", token);
            conversation.Append(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"<compact-summary>\nThe earlier part of this conversation was summarized to save context:\n\n{summary}\n</compact-summary>" }),
            });
            conversation.ApiStart = conversation.MessageCount - 1;
            await conversations.SaveAsync(conversation);
            return 0;
        });
        Emit("conversations.changed", null);
        return conversation.ToWire();
    }

    // Claude Code の /btw。会話の流れを踏まえて答えるが、履歴には残さない
    async Task<JsonNode?> BtwAsync(string conversationId, string question)
    {
        var conversation = await RequireConversationAsync(conversationId);
        var engine = await hub.EngineAsync();
        var options = await hub.OptionsForAsync(conversation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var answer = await engine.AskAsideAsync(conversation, options, $"<btw>\nA quick side question from the user. Answer briefly from what you already know in this conversation; it will not be added to the conversation.\n{question}\n</btw>", "btw", timeout.Token);
        return new JsonObject { ["answer"] = answer };
    }

    async Task<JsonNode?> ExportAsync(string conversationId)
    {
        var conversation = await RequireConversationAsync(conversationId);
        var text = new System.Text.StringBuilder($"# {conversation.Title}\n\n");
        foreach (var message in conversation.CopyMessages())
        {
            var role = (string?)message["role"];
            if (role == "user" && !Conversation.IsHumanTurn(message)) continue;
            var body = WireText.Of(message["content"]).Trim();
            var tools = (message["content"] as JsonArray ?? []).OfType<JsonObject>().Where(b => ((string?)b["type"])?.EndsWith("tool_use") == true).Select(b => (string?)b["name"]).ToList();
            if (body.Length == 0 && tools.Count == 0) continue;
            text.Append(role == "user" ? "## ユーザー\n\n" : "## Claude\n\n");
            if (body.Length > 0) text.Append(body).Append("\n\n");
            foreach (var tool in tools) text.Append($"> ツール: {tool}\n\n");
        }
        var path = Path.Combine(paths.ExportsDir, $"{FileNameOf(conversation.Title)}-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        Directory.CreateDirectory(paths.ExportsDir);
        await File.WriteAllTextAsync(path, text.ToString());
        return new JsonObject { ["path"] = path };
    }

    static string FileNameOf(string title)
    {
        var cleaned = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        // Windows のパス長に余裕を残す
        if (cleaned.Length > 60) cleaned = cleaned[..60];
        return cleaned.Length == 0 ? "chat" : cleaned;
    }

    // Claude Code の巻き戻し。その発言以降を消し、望めばフォルダ操作で変えたファイルも戻す
    async Task<JsonNode?> RewindAsync(string conversationId, int messageIndex, bool restoreFiles)
    {
        var conversation = await RequireConversationAsync(conversationId);
        var messages = conversation.CopyMessages();
        if (messageIndex < 0 || messageIndex >= messages.Count || !Conversation.IsHumanTurn(messages[messageIndex])) throw new InvalidOperationException("ユーザーの発言を選んでください");
        var restored = await hub.ExclusiveAsync(conversation, async _ =>
        {
            var result = restoreFiles ? checkpoints.Restore(conversation.Id, messageIndex) : null;
            conversation.TruncateFrom(messageIndex);
            await conversations.SaveAsync(conversation);
            return result;
        });
        Emit("conversations.changed", null);
        return new JsonObject { ["conversation"] = conversation.ToWire(), ["files"] = restored };
    }

    async Task<JsonNode?> RewindInfoAsync(string conversationId) =>
        new JsonArray(checkpoints.Turns((await RequireConversationAsync(conversationId)).Id).Select(t => (JsonNode)t).ToArray());

    async Task<JsonNode?> ExportArtifactAsync(string conversationId, string id)
    {
        var conversation = await RequireConversationAsync(conversationId);
        Artifact artifact;
        lock (conversation.Artifacts) artifact = conversation.Artifacts.FirstOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("アーティファクトが見つかりません");
        var extension = artifact.Type switch
        {
            "html" => ".html",
            "svg" => ".svg",
            "mermaid" => ".mmd",
            "code" => ".txt",
            _ => ".md",
        };
        var path = Path.Combine(paths.ExportsDir, $"{FileNameOf(artifact.Title)}-v{artifact.Version}{extension}");
        Directory.CreateDirectory(paths.ExportsDir);
        await File.WriteAllTextAsync(path, artifact.Content);
        platform.OpenPath(paths.ExportsDir);
        return new JsonObject { ["path"] = path };
    }

    // 会話が覚えている場所だけ開く。画面から任意のパスを開かせない
    async Task<JsonNode?> OpenFileAsync(string conversationId, string fileId, bool reveal)
    {
        var conversation = await RequireConversationAsync(conversationId);
        if (!conversation.Files.TryGetValue(fileId, out var path) || !File.Exists(path)) throw new InvalidOperationException("ファイルが見つかりません");
        platform.OpenPath(reveal ? Path.GetDirectoryName(path)! : path);
        return null;
    }

    Task<JsonNode?> StopWorkflowAsync(string id)
    {
        workflows.Stop(id);
        return Task.FromResult<JsonNode?>(null);
    }

    // 自作コマンドと Skills の本文を、送る文に展開する
    Task<JsonNode?> ExpandCommandAsync(string name, string args)
    {
        var command = extensions.ListCommands().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"/{name} というコマンドはありません");
        var body = command.Body.Contains("$ARGUMENTS")
            ? command.Body.Replace("$ARGUMENTS", args)
            : args.Length > 0 ? $"{command.Body}\n\nARGUMENTS: {args}" : command.Body;
        return Task.FromResult<JsonNode?>($"<command name=\"/{command.Name}\">\n{body}\n</command>");
    }

    // アーティファクトは iframe で別のページとして出す。アプリの画面の CSP ではインラインのスクリプトが動かないため。
    // iframe は sandbox で別オリジン扱いにし、ここで外への通信も絞る
    public const string ArtifactCsp = "default-src 'none'; script-src 'unsafe-inline' https://cdnjs.cloudflare.com; style-src 'unsafe-inline' https://cdnjs.cloudflare.com; img-src data: blob: https:; font-src data: https://cdnjs.cloudflare.com; media-src data: blob: https:; connect-src 'none'; frame-src 'none'; form-action 'none'; base-uri 'none'";

    public static bool IsArtifactPath(string path) => path.StartsWith("artifact/", StringComparison.Ordinal);

    // artifact/<会話>/<アーティファクト>
    public async Task<string?> RenderArtifactAsync(string path)
    {
        var parts = path.Split('/');
        if (parts.Length != 3 || await conversations.GetAsync(parts[1]) is not { } conversation) return null;
        Artifact? artifact;
        lock (conversation.Artifacts) artifact = conversation.Artifacts.FirstOrDefault(a => a.Id == parts[2]);
        if (artifact is null) return null;
        return artifact.Type switch
        {
            "html" => artifact.Content,
            "svg" => $"<!doctype html><meta charset=\"utf-8\"><style>body{{margin:0;display:flex;justify-content:center;background:#fff}}svg{{max-width:100%;height:auto}}</style>{artifact.Content}",
            "mermaid" => $$"""
                <!doctype html><meta charset="utf-8">
                <style>body{margin:16px;background:#fff;font-family:sans-serif}</style>
                <pre class="mermaid">{{System.Net.WebUtility.HtmlEncode(artifact.Content)}}</pre>
                <script src="https://cdnjs.cloudflare.com/ajax/libs/mermaid/10.9.1/mermaid.min.js"></script>
                <script>mermaid.initialize({ startOnLoad: true, securityLevel: "strict" })</script>
                """,
            _ => null,
        };
    }

    // Schedules

    async Task<JsonNode?> ListSchedulesAsync() => Json.ToNode(await scheduler.ListAsync());

    async Task<JsonNode?> SaveScheduleAsync(JsonObject p)
    {
        var schedule = p["schedule"]?.Deserialize<Schedule>(Json.Wire) ?? throw new InvalidOperationException("schedule がありません");
        if (schedule.ConversationId is { } id && await conversations.GetAsync(id) is null) throw new InvalidOperationException("送り先のチャットが見つかりません");
        // /loop は Claude Code と同じく 7 日で切れる
        if ((bool?)p["loop"] == true) schedule.ExpiresAt = DateTimeOffset.UtcNow.Add(Scheduler.LoopLifetime).ToUnixTimeMilliseconds();
        try
        {
            await scheduler.SaveAsync(schedule);
        }
        catch (ToolException e)
        {
            throw new InvalidOperationException(e.Message);
        }
        return await ListSchedulesAsync();
    }

    async Task<JsonNode?> DeleteScheduleAsync(string id)
    {
        try
        {
            await scheduler.DeleteAsync(id);
        }
        catch (ToolException e)
        {
            throw new InvalidOperationException(e.Message);
        }
        return await ListSchedulesAsync();
    }

    // Memory

    string MemoryPath(string relative)
    {
        var root = Path.GetFullPath(paths.MemoryDir);
        var full = Path.GetFullPath(Path.Combine(root, relative.TrimStart('/', '\\')));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("メモリのフォルダの外は扱えません");
        return full;
    }

    JsonArray ListMemory()
    {
        var root = Path.GetFullPath(paths.MemoryDir);
        Directory.CreateDirectory(root);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return new JsonArray(Directory.EnumerateFiles(root, "*", options)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => (JsonNode)new JsonObject
            {
                ["path"] = Path.GetRelativePath(root, f).Replace('\\', '/'),
                ["size"] = new FileInfo(f).Length,
                ["updatedAt"] = new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeMilliseconds(),
            })
            .ToArray());
    }

    async Task<JsonNode?> WriteMemoryAsync(string relative, string content)
    {
        var path = MemoryPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return ListMemory();
    }

    Task<JsonNode?> DeleteMemoryAsync(string relative)
    {
        var path = MemoryPath(relative);
        if (File.Exists(path)) platform.MoveToTrash(path);
        return Task.FromResult<JsonNode?>(ListMemory());
    }

    async Task<JsonNode?> SaveMcpAsync(JsonObject p)
    {
        var servers = p["servers"]?.Deserialize<Dictionary<string, McpServerConfig>>(Json.Wire) ?? [];
        foreach (var (name, config) in servers)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("サーバー名が空です");
            // ツール名の接頭辞が組み込みツールと重なる
            if (BuiltinSources.Contains(McpHub.SafeName(name), StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"{name} は組み込みのツールと同じ名前なので使えません。別の名前にしてください");
            if (!config.IsRemote && string.IsNullOrWhiteSpace(config.Command)) throw new InvalidOperationException($"{name} に command か url を指定してください");
        }
        var updated = await settings.UpdateAsync(s => s.McpServers = servers);
        await mcp.ApplyAsync(updated.McpServers);
        return mcp.Describe();
    }

    async Task<JsonNode?> RestartMcpAsync(string name)
    {
        await mcp.RestartAsync(name);
        return mcp.Describe();
    }

    async Task<JsonNode?> RevokePermissionAsync(string apiName)
    {
        var updated = await settings.UpdateAsync(s => s.ToolPermissions.Remove(apiName));
        return SettingsView(updated);
    }

    async Task<JsonNode?> LocalUsageAsync() => await ledger.SummaryAsync(TimeZoneInfo.Local);

    async Task<JsonNode?> OrgUsageAsync(string range)
    {
        // Admin キーがあればそれを、無ければ通常キーで試す。組織に紐づく個人キーなら通ることがある
        var key = await secrets.GetAsync(SecretStore.AdminKey) ?? await secrets.GetAsync(SecretStore.ApiKey)
            ?? throw new InvalidOperationException("APIキーが未設定です");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        return await orgCost.FetchAsync(key, range, timeout.Token);
    }

    Task<JsonNode?> ReloadPricingAsync()
    {
        ledger.ReloadPricing();
        return LocalUsageAsync();
    }

    Task<JsonNode?> OpenAsync(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) File.WriteAllText(path, "");
        platform.OpenPath(path);
        return Task.FromResult<JsonNode?>(null);
    }

    Task<JsonNode?> OpenTargetAsync(string target) => OpenAsync(target switch
    {
        "root" => paths.Root,
        "config" => paths.ConfigDir,
        "data" => paths.DataDir,
        "logs" => paths.LogsDir,
        "settings" => paths.SettingsFile,
        "pricing" => paths.PricingFile,
        "memory" => paths.MemoryDir,
        "skills" => paths.SkillsDir,
        "commands" => paths.CommandsDir,
        "outputStyles" => paths.OutputStylesDir,
        "exports" => paths.ExportsDir,
        _ => throw new InvalidOperationException("不明な場所です"),
    });

    public async ValueTask DisposeAsync()
    {
        scheduler.Dispose();
        foreach (var run in workflows.ListView().OfType<JsonObject>()) workflows.Stop((string?)run["id"] ?? "");
        hub.StopAll();
        await mcp.DisposeAsync();
        terminal.Dispose();
        await browser.DisposeAsync();
        http.Dispose();
    }
}
