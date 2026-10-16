using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;

namespace ClaudeChat.Core;

public interface IPlatform
{
    string Version { get; }
    void OpenPath(string path);
    string? PickFolder();
    // 保存先を人に選んでもらう。やめたら null
    string? PickSaveFile(string suggestedName, string? initialFolder);
    void MoveToTrash(string path);
    // 画面上の本物のマウスでブラウザを押せるか。Windows のアプリだけ
    bool CanRealClick { get; }
    // 題名に titleMarker を含むブラウザの窓の、ページ表示領域の中の割合 (0 から 1) の位置を本物のマウスで押す。できなかったら理由を返す
    string? RealClick(string titleMarker, double fx, double fy);
    // デスクトップとスタートメニューにショートカットを作る。できなかったら理由を返す
    string? CreateShortcuts();
    // デスクトップの通知を出す。押されたら onClick を呼ぶ。できなかったら理由を返す
    string? Notify(string title, string message, Action? onClick = null);
    // 窓が前に出ていて、人が見ているはずか
    bool IsActive { get; }
    void ShowWindow();
    // ログオンしたときに裏で起動するか。できなかったら理由を返す
    string? SetStartWithWindows(bool enabled);
    // この exe を作ったコミット。手元でのビルドなど分からないときは null
    string? Commit { get; }
    // 取り出した新しい exe と今の exe を入れ替えて再起動する。できなかったら理由を返す
    string? InstallUpdate(string newExePath);
    // 画面の範囲を選んで撮り、PNG を base64 で返す。撮らずにやめたら null。できない環境では理由を投げる
    Task<string?> CaptureScreenAsync(CancellationToken cancellationToken);
    // 会話を別の窓で開く。できなかったら理由を返す
    string? OpenChatWindow(string conversationId);
    // 入力の小窓を出すキーを登録し直す。空なら外す。ほかのアプリが使っているなどで登録できなかったら理由を返す
    string? SetQuickEntryHotkey(string hotkey);
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
    readonly BrowserToolProvider internalBrowser;
    readonly BrowserBridge bridge;
    readonly OrgCostClient orgCost;
    readonly HttpClient http = new();
    readonly Updater updater;
    readonly Housekeeping housekeeping;
    readonly SessionFiles sessionFiles;
    readonly Uploads uploads;
    // 画面を読み直すたびに確かめ直さない
    int updateChecked;

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
        extensions = new Extensions(paths, settings);
        checkpoints = new Checkpoints(paths, platform);
        hooks = new Hooks(settings, paths);
        hub = new ChatHub(paths, settings, secrets, conversations, folders, ledger, hooks, extensions, events);
        hub.DeleteConversation = async id => await DeleteConversationAsync(id);
        updater = new Updater(platform, http);
        hub.Alert = Alert;
        uploads = new Uploads(paths);
        hub.Uploads = uploads;
        sessionFiles = new SessionFiles(paths, conversations)
        {
            CanShare = hub.CanMessageAsync,
            Changed = rootId => Emit("session.changed", new JsonObject { ["conversationId"] = rootId }),
        };
        housekeeping = new Housekeeping(paths, settings, conversations, hub, () => Emit("conversations.changed", null),
            problem => platform.Notify("バックアップに失敗しました", problem));
        workflows = new WorkflowRunner(hub, settings, conversations, events);
        hub.WorkflowCharged = workflows.Charge;
        scheduler = new Scheduler(paths, hub, conversations, events);
        mcp = new McpHub(paths);
        terminal = new TerminalToolProvider(settings);
        bridge = new BrowserBridge(settings, secrets);
        browser = new BrowserToolProvider(settings, paths, bridge, platform);
        internalBrowser = new BrowserToolProvider(settings, paths, bridge, platform, hidden: true);
        // 同じ名前があれば先に並べたものが勝つので、MCP は最後
        hub.Tools = new ToolRegistry(
        [
            new FileToolProvider(settings, platform, checkpoints, paths),
            terminal,
            new ProgramToolProvider(settings),
            browser,
            internalBrowser,
            new AgentToolProvider(hub, conversations, folders, settings, workflows),
            new ChatSearchToolProvider(conversations, settings),
            new TodoToolProvider(),
            new SessionToolProvider(sessionFiles),
            new ArtifactToolProvider(settings, conversations, async conversation =>
            {
                await conversations.SaveAsync(conversation, touch: false);
                hub.EmitState(conversation);
            }),
            new MemoryToolProvider(paths, settings),
            new PlanToolProvider(),
            new AskToolProvider(),
            new ScheduleToolProvider(scheduler),
            new SkillToolProvider(extensions),
            new McpResourceToolProvider(mcp),
            new NotifyToolProvider(platform),
            new ComposeToolProvider(),
            mcp,
        ]);
        orgCost = new OrgCostClient(paths, http);
        mcp.Changed += () => Emit("mcp.changed", mcp.Describe());
    }

    public async Task StartAsync()
    {
        // 前回の終了が片付けの途中で打ち切られていたときの取りこぼし
        DeleteIncognitoFiles();
        var current = await settings.GetAsync();
        await ledger.GetPricingAsync();
        await hub.MarkInterruptedAsync();
        await mcp.ApplyAsync(current.McpServers);
        scheduler.Start();
        housekeeping.Start();
        // settings.json を手で直したときや、exe を移したときも、ログオン時の起動をそろえる。できない環境では何もしない
        platform.SetStartWithWindows(current.Desktop.StartWithWindows);
        platform.SetQuickEntryHotkey(current.Desktop.QuickEntryHotkey);
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
        "conversations.archive" => UpdateConversationAsync(Str(p, "id"), c => c.Archived = (bool?)p["archived"] == true),
        "conversations.setTags" => UpdateConversationAsync(Str(p, "id"), c => c.Tags = CleanTags(p["tags"] as JsonArray)),
        "usage.exportCsv" => ExportUsageCsvAsync(),
        "app.backup" => BackupAsync(),
        "app.backupStatus" => BackupStatusAsync(),
        "conversations.star" => UpdateConversationAsync(Str(p, "id"), c => c.Starred = (bool?)p["starred"] == true),
        "conversations.move" => MoveConversationAsync(Str(p, "id"), (string?)p["folderId"]),
        "conversations.delete" => DeleteConversationAsync(Str(p, "id")),
        "session.list" => SessionListAsync(Str(p, "conversationId")),
        "conversations.fork" => ForkConversationAsync(Str(p, "id"), (int?)p["upTo"] ?? throw new InvalidOperationException("upTo がありません")),
        "chat.changedFiles" => ChangedFilesViewAsync(Str(p, "conversationId")),
        "chat.undoFiles" => UndoFilesAsync(Str(p, "conversationId"), (int?)p["humanIndex"] ?? throw new InvalidOperationException("humanIndex がありません")),
        "screen.capture" => CaptureScreenAsync(),
        "app.openChatWindow" => OpenChatWindowAsync(Str(p, "conversationId")),
        "app.showChat" => ShowChatAsync(Str(p, "conversationId")),
        "usage.budget" => BudgetViewAsync(),
        "session.open" => SessionOpenAsync(Str(p, "conversationId"), (string?)p["fileId"]),
        "conversations.search" => SearchConversationsAsync(Str(p, "query")),
        "folders.list" => ListFoldersAsync(),
        "folders.save" => SaveFolderAsync(p),
        "folders.delete" => DeleteFolderAsync(Str(p, "id")),
        "folders.knowledge" => KnowledgeAsync(Str(p, "id"), dir => Task.FromResult(ProjectKnowledge.List(dir))),
        "folders.addKnowledge" => KnowledgeAsync(Str(p, "id"), dir => ProjectKnowledge.AddAsync(dir, Str(p, "name"), Str(p, "text"))),
        "folders.readKnowledge" => ReadKnowledgeAsync(Str(p, "id"), Str(p, "name")),
        "folders.deleteKnowledge" => KnowledgeAsync(Str(p, "id"), dir =>
        {
            var path = ProjectKnowledge.PathOf(dir, Str(p, "name"));
            if (File.Exists(path)) platform.MoveToTrash(path);
            return Task.FromResult(ProjectKnowledge.List(dir));
        }),
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
        "chat.switchBranch" => SwitchBranchAsync(Str(p, "conversationId"), (int?)p["index"] ?? -1, (int?)p["version"] ?? -1),
        "tools.approve" => ApproveAsync(Str(p, "requestId"), Str(p, "decision"), (string?)p["rule"]),
        "tools.answer" => AnswerAsync(Str(p, "requestId"), p["answers"] as JsonArray ?? throw new InvalidOperationException("answers がありません")),
        "artifacts.export" => ExportArtifactAsync(Str(p, "conversationId"), Str(p, "id")),
        "artifacts.list" => ListArtifactsAsync(),
        "files.extractText" => Task.FromResult<JsonNode?>(DocumentText.Extract(Str(p, "name"), Convert.FromBase64String(Str(p, "data")))),
        "files.saveCode" => SaveCodeAsync(Str(p, "name"), Str(p, "text")),
        "files.open" => OpenFileAsync(Str(p, "conversationId"), Str(p, "fileId"), (bool?)p["reveal"] == true),
        "workflows.list" => Task.FromResult<JsonNode?>(workflows.ListView()),
        "workflows.stop" => StopWorkflowAsync(Str(p, "id")),
        "extensions.list" => Task.FromResult<JsonNode?>(ExtensionsView()),
        "skills.import" => ImportSkillAsync(Str(p, "data")),
        "styles.get" => Task.FromResult<JsonNode?>(extensions.GetOutputStyle(Str(p, "id"))),
        "styles.save" => SaveStyleAsync((string?)p["id"], Str(p, "name"), Str(p, "text")),
        "styles.delete" => DeleteStyleAsync(Str(p, "id")),
        "styles.generate" => GenerateStyleAsync(Str(p, "request")),
        "commands.expand" => ExpandCommandAsync(Str(p, "name"), (string?)p["args"] ?? ""),
        "schedules.list" => ListSchedulesAsync(),
        "schedules.save" => SaveScheduleAsync(p),
        "schedules.delete" => DeleteScheduleAsync(Str(p, "id")),
        "schedules.setEnabled" => SetScheduleEnabledAsync(Str(p, "id"), (bool?)p["enabled"] == true),
        "memory.list" => MemoryAsync(p, root => ListMemory(root)),
        "memory.read" => MemoryAsync(p, root => File.ReadAllText(MemoryPath(root, Str(p, "path")))),
        "memory.write" => MemoryAsync(p, root =>
        {
            var path = MemoryPath(root, Str(p, "path"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, (string?)p["content"] ?? "");
            return ListMemory(root);
        }),
        "memory.delete" => MemoryAsync(p, root =>
        {
            var path = MemoryPath(root, Str(p, "path"));
            if (File.Exists(path)) platform.MoveToTrash(path);
            return ListMemory(root);
        }),
        "mcp.list" => Task.FromResult<JsonNode?>(mcp.Describe()),
        "mcp.save" => SaveMcpAsync(p),
        "mcp.restart" => RestartMcpAsync(Str(p, "name")),
        "mcp.openLog" => OpenAsync(mcp.LogPath(Str(p, "name"))),
        "permissions.revoke" => RevokePermissionAsync(Str(p, "apiName")),
        "usage.local" => LocalUsageAsync(),
        "usage.org" => OrgUsageAsync(Str(p, "range")),
        "pricing.reload" => ReloadPricingAsync(),
        "app.open" => OpenTargetAsync(Str(p, "target")),
        "app.checkUpdate" => CheckUpdateAsync(),
        "chat.interject" => InterjectAsync(Str(p, "conversationId"), Str(p, "text")),
        "chat.hurry" => HurryAsync(Str(p, "conversationId")),
        "chat.resume" => ResumeAsync(Str(p, "conversationId")),
        "chat.dismissInterrupted" => DismissInterruptedAsync(Str(p, "conversationId")),
        "app.installUpdate" => InstallUpdateAsync(),
        "app.openMail" => OpenMailAsync((string?)p["to"] ?? "", (string?)p["subject"] ?? "", (string?)p["body"] ?? ""),
        "app.pickFolder" => Task.FromResult<JsonNode?>(platform.PickFolder()),
        "app.createShortcuts" => Task.FromResult<JsonNode?>(platform.CreateShortcuts() is { } failed ? throw new InvalidOperationException(failed) : null),
        "browser.extensionStatus" => BrowserStatusAsync(),
        "browser.writeExtension" => WriteExtensionAsync(),
        "browser.resetToken" => ResetBrowserTokenAsync(),
        "chat.systemPrompt" => PreviewSystemPromptAsync((string?)p["conversationId"]),
        _ => throw new InvalidOperationException($"未知のメソッドです: {method}"),
    };

    // MCP サーバー名がこれと同じだとツール名の接頭辞が重なる
    static readonly string[] BuiltinSources =
    [
        FileToolProvider.Source, TerminalToolProvider.Source, ProgramToolProvider.Source, BrowserToolProvider.Source, BrowserToolProvider.HiddenSource,
        AgentToolProvider.Source, ChatSearchToolProvider.Source, TodoToolProvider.Source, ArtifactToolProvider.Source,
        MemoryToolProvider.Source, PlanToolProvider.Source, ScheduleToolProvider.Source, SkillToolProvider.Source,
        AskToolProvider.Source, McpResourceToolProvider.Source, NotifyToolProvider.Source, ComposeToolProvider.Source,
    ];

    static readonly string[] PermissionModes = ["default", "acceptEdits", "auto", "plan", "bypass"];

    static string Str(JsonObject p, string key) => (string?)p[key] ?? throw new InvalidOperationException($"{key} がありません");

    static string NonEmpty(string s) => s.Trim() is { Length: > 0 } t ? t : throw new InvalidOperationException("名前を入れてください");

    async Task<Conversation> RequireConversationAsync(string id) =>
        await conversations.GetAsync(id) ?? throw new InvalidOperationException("会話が見つかりません");

    // 応答中に送られた文。走っていなければ false を返し、画面はふつうに送り直す
    Task<JsonNode?> HurryAsync(string conversationId)
    {
        if (!hub.Hurry(conversationId)) throw new InvalidOperationException("応答中ではありません");
        return Task.FromResult<JsonNode?>(null);
    }

    async Task<JsonNode?> ResumeAsync(string conversationId)
    {
        var conversation = await conversations.GetAsync(conversationId) ?? throw new InvalidOperationException("会話が見つかりません");
        await hub.ResumeAsync(conversation);
        return null;
    }

    async Task<JsonNode?> DismissInterruptedAsync(string conversationId)
    {
        var conversation = await conversations.GetAsync(conversationId) ?? throw new InvalidOperationException("会話が見つかりません");
        if (conversation.LastRunStatus != ChatHub.Interrupted) return null;
        conversation.LastRunStatus = "aborted";
        await conversations.SaveAsync(conversation, touch: false);
        Emit("conversations.changed", null);
        return conversation.ToWire();
    }

    async Task<JsonNode?> InterjectAsync(string conversationId, string text)
    {
        var conversation = await RequireConversationAsync(conversationId);
        if (text.Trim().Length == 0) throw new InvalidOperationException("送る文がありません");
        var outcome = await hooks.RunUserPromptSubmitAsync(conversation, text);
        if (outcome.Blocked) throw new InvalidOperationException($"UserPromptSubmit フックが止めました: {outcome.Message}");
        // ふつうの送信と同じく、フックが返した補足を添える
        if (outcome.Message is { } extra) text += $"\n\n<hook-feedback event=\"UserPromptSubmit\">\n{extra}\n</hook-feedback>";
        return new JsonObject { ["queued"] = hub.Interject(conversationId, text) };
    }

    async Task<JsonNode?> BackupAsync() => new JsonObject { ["path"] = await housekeeping.BackupAsync() };

    Task<JsonNode?> BackupStatusAsync() => Task.FromResult<JsonNode?>(new JsonObject
    {
        ["lastSuccessAt"] = housekeeping.LastSuccessAt?.ToString("O"),
        ["lastError"] = housekeeping.LastError,
    });

    public bool CloseToTray => settings.Current?.Desktop.CloseToTray ?? true;

    // 窓を見ていないときだけ知らせる。押したらその会話を開く
    void Alert(Conversation conversation, string message, bool waiting)
    {
        var desktop = settings.Current?.Desktop;
        if (desktop is null || !(waiting ? desktop.NotifyWhenWaiting : desktop.NotifyWhenDone) || platform.IsActive) return;
        // シークレットチャットは題名も答えも通知の履歴に残さない
        var title = conversation.Incognito ? "シークレットチャット" : conversation.Title;
        if (conversation.Incognito) message = waiting ? "確認や質問で止まっています" : "返答が届きました";
        platform.Notify(message, title, () => Emit("app.navigate", new JsonObject { ["conversationId"] = conversation.Id }));
    }

    async Task<JsonNode?> CheckUpdateAsync()
    {
        using var timeout = new CancellationTokenSource(UpdateCheckTimeout);
        return (await updater.CheckAsync(timeout.Token)).ToWire();
    }

    // 押した人が確認済みなので、応答中でも入れ替える
    async Task<JsonNode?> InstallUpdateAsync()
    {
        if (await updater.InstallAsync(() => null, CancellationToken.None) is { } problem) throw new InvalidOperationException(problem);
        return null;
    }

    string? NothingRunning() => hub.RunningView().Count == 0 ? null : "応答中のチャットがあるので、入れ替えを次の機会にしました";

    static readonly TimeSpan UpdateCheckTimeout = TimeSpan.FromSeconds(20);

    // 起動時の更新の確認。自動で入れる設定なら、まだ何も動いていないうちに入れ替えて再起動する
    async Task CheckUpdateOnStartupAsync()
    {
        var updates = (await settings.GetAsync()).Updates;
        if (!updates.CheckOnStartup || platform.Commit is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(UpdateCheckTimeout);
            var info = await updater.CheckAsync(timeout.Token);
            if (!info.Available) return;
            // 予定の実行などで応答中のチャットがあれば、止めないよう知らせるだけにする
            if (updates.AutoInstall && NothingRunning() is null)
            {
                Emit("update.installing", info.ToWire());
                try
                {
                    var problem = await updater.InstallAsync(NothingRunning, CancellationToken.None);
                    if (problem is null) return;
                    Emit("update.failed", problem);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // 画面が「更新しています」のまま残らないよう、どんな失敗でも知らせる
                    Emit("update.failed", e.Message);
                }
            }
            Emit("update.available", info.ToWire());
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            // ネットにつながっていない起動もある。次の起動でまた確かめる
        }
    }

    async Task<JsonNode?> InitAsync()
    {
        await settings.GetAsync();
        if (Interlocked.Exchange(ref updateChecked, 1) == 0) _ = Task.Run(CheckUpdateOnStartupAsync);
        // 起動直後から拡張機能がつなぎに来られるように
        await bridge.SyncAsync();
        var current = settings.Current ?? await settings.GetAsync();
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
            ["commit"] = platform.Commit,
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
        ["budget"] = Json.ToNode(s.Budget),
        ["updates"] = Json.ToNode(s.Updates),
        ["desktop"] = Json.ToNode(s.Desktop),
        ["backup"] = Json.ToNode(s.Backup),
        ["hooks"] = JsonSerializer.SerializeToNode(s.Hooks, Json.Wire),
        ["mcpServers"] = JsonSerializer.SerializeToNode(s.McpServers, Json.Wire),
        ["toolPermissions"] = JsonSerializer.SerializeToNode(s.ToolPermissions, Json.Wire),
    };

    JsonObject ExtensionsView() => new()
    {
        ["commands"] = new JsonArray(extensions.ListCommands().Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["description"] = c.Description, ["kind"] = c.Kind }).ToArray()),
        ["outputStyles"] = extensions.ListOutputStyles(),
        ["skills"] = new JsonArray(extensions.ListSkills(includeDisabled: true).Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["description"] = s.Description, ["enabled"] = extensions.IsSkillEnabled(s.Name) }).ToArray()),
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
        if ((string?)patch["backup"]?["folder"] is { } backupFolder && Housekeeping.FolderProblem(backupFolder, paths.Root) is { } backupProblem) throw new InvalidOperationException(backupProblem);
        var hookPatch = patch["hooks"]?.Deserialize<Dictionary<string, List<HookEntry>>>(Json.Wire);
        var previousHotkey = settings.Current?.Desktop.QuickEntryHotkey ?? "";
        var updated = await settings.UpdateAsync(s =>
        {
            if (patch["profile"] is JsonObject profile) s.Profile = Merge(s.Profile, profile);
            if (patch["appearance"] is JsonObject appearance)
            {
                s.Appearance = Merge(s.Appearance, appearance);
                // 小さすぎても大きすぎても、設定画面まで戻れなくなる
                s.Appearance.Zoom = Math.Clamp(s.Appearance.Zoom, 70, 200);
                if (s.Appearance.ChatWidth is not ("standard" or "wide" or "full")) s.Appearance.ChatWidth = "standard";
            }
            if (patch["chat"] is JsonObject chat)
            {
                s.Chat = Merge(s.Chat, chat);
                if (s.Chat.ThinkingDisplay is not ("summarized" or "updates")) s.Chat.ThinkingDisplay = "summarized";
                // 「自動」は送るときに選ぶもの。予定やほかのチャットから動き出したときに API へそのまま渡ってしまう
                if (s.Chat.Model == ModelCatalog.AutoModel) s.Chat.Model = "claude-opus-5-5";
            }
            if (patch["files"] is JsonObject files) s.Files = Merge(s.Files, files);
            if (patch["terminal"] is JsonObject terminalPatch) s.Terminal = Merge(s.Terminal, terminalPatch);
            if (patch["programs"] is JsonObject programs) s.Programs = Merge(s.Programs, programs);
            if (patch["browser"] is JsonObject browserPatch)
            {
                s.Browser = Merge(s.Browser, browserPatch);
                if (s.Browser.Mode is not ("dedicated" or "extension")) s.Browser.Mode = "dedicated";
            }
            if (patch["agents"] is JsonObject agents) s.Agents = Merge(s.Agents, agents);
            if (patch["features"] is JsonObject features) s.Features = Merge(s.Features, features);
            if (patch["budget"] is JsonObject budget) s.Budget = Merge(s.Budget, budget);
            if (patch["updates"] is JsonObject updates) s.Updates = Merge(s.Updates, updates);
            if (patch["desktop"] is JsonObject desktop) s.Desktop = Merge(s.Desktop, desktop);
            if (patch["backup"] is JsonObject backup) s.Backup = Merge(s.Backup, backup);
            if (hookPatch is not null)
            {
                // 画面が扱うのは決まった 5 つだけ。settings.json に手で書いたほかの項目は消さずに残す
                s.Hooks = s.Hooks.Where(h => !HookEvents.Contains(h.Key))
                    .Concat(hookPatch.Where(h => HookEvents.Contains(h.Key)))
                    .ToDictionary(h => h.Key, h => h.Value);
            }
        });
        await bridge.SyncAsync();
        // レジストリと設定がずれないよう、書けなかったら設定も戻す
        if (patch["desktop"]?["startWithWindows"] is JsonValue startup && platform.SetStartWithWindows((bool)startup) is { } problem)
        {
            await settings.UpdateAsync(s => s.Desktop.StartWithWindows = false);
            throw new InvalidOperationException(problem);
        }
        if (patch["desktop"]?["quickEntryHotkey"] is JsonValue && platform.SetQuickEntryHotkey(updated.Desktop.QuickEntryHotkey) is { } taken)
        {
            await settings.UpdateAsync(s => s.Desktop.QuickEntryHotkey = previousHotkey);
            platform.SetQuickEntryHotkey(previousHotkey);
            throw new InvalidOperationException(taken);
        }
        return SettingsView(settings.Current ?? updated);
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
        await bridge.SyncAsync();
        platform.SetStartWithWindows(current.Desktop.StartWithWindows);
        platform.SetQuickEntryHotkey(current.Desktop.QuickEntryHotkey);
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
        if (folderId is not null && await folders.GetAsync(folderId) is null) throw new InvalidOperationException("プロジェクトが見つかりません");
        var conversation = await RequireConversationAsync(id);
        if (conversation.Incognito) throw new InvalidOperationException("シークレットチャットはプロジェクトに入れられません");
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
        // 子やワークフローが、消えた親のために動き続けないように
        foreach (var run in workflows.ForParent(id)) workflows.Stop(run.Id);
        foreach (var child in await DescendantsAsync(id)) hub.Stop(child.Id);
        hub.Stop(id);
        var deleting = await conversations.GetAsync(id);
        await conversations.DeleteAsync(id);
        // 親のフォルダを子と共有しているので、消すのは親を消したときだけ
        if (deleting is not null) await sessionFiles.DeleteFolderAsync(deleting);
        if (await secrets.GetAsync(SecretStore.ApiKey) is { } apiKey) await uploads.ReleaseAsync(hub.GetClient(apiKey), id);
        hub.Forget(id);
        checkpoints.Forget(id);
        Emit("conversations.changed", null);
        return null;
    }

    // 一覧に並べて読める長さと数に抑える
    const int MaxTags = 10;
    const int MaxTagLength = 20;

    static List<string> CleanTags(JsonArray? tags) => (tags ?? [])
        .Select(t => ((string?)t ?? "").Trim())
        .Where(t => t.Length > 0)
        .Select(t => t.Length > MaxTagLength ? t[..MaxTagLength] : t)
        .Distinct()
        .Take(MaxTags)
        .ToList();

    // Excel でそのまま開けるよう、BOM 付きの UTF-8 で書く
    async Task<JsonNode?> ExportUsageCsvAsync()
    {
        var titles = (await conversations.ListAsync()).ToDictionary(c => c.Id, c => c.Title);
        var text = new System.Text.StringBuilder("日時,会話,種類,モデル,入力,出力,キャッシュ書き込み,キャッシュ書き込み (1 時間),キャッシュ読み込み,Web 検索,バッチ,高速,料金 (USD)\r\n");
        foreach (var r in (await ledger.AllAsync()).OrderBy(r => r.At))
        {
            var owner = r.ParentId ?? r.ConversationId;
            // 会話に結びつかない記録は、スタイル作りなど会話の外の呼び出しか、シークレットチャット
            var title = owner is not null ? titles.GetValueOrDefault(owner, "(削除したチャット)") : r.Kind == "style" ? "(スタイルの作成)" : "(シークレット)";
            string[] cells =
            [
                r.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), title, r.Kind, r.Model,
                r.Input.ToString(), r.Output.ToString(), r.CacheWrite.ToString(), r.CacheWrite1h.ToString(), r.CacheRead.ToString(), r.WebSearches.ToString(),
                r.Batch ? "1" : "0", r.Fast ? "1" : "0", r.CostUsd?.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            ];
            text.Append(string.Join(",", cells.Select(CsvCell))).Append("\r\n");
        }
        var path = Path.Combine(paths.ExportsDir, $"usage-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        Directory.CreateDirectory(paths.ExportsDir);
        await File.WriteAllTextAsync(path, text.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return new JsonObject { ["path"] = path };
    }

    // 先頭の = や + は Excel が式として動かすので、文字として扱わせる
    static string CsvCell(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    async Task<JsonNode?> ForkConversationAsync(string id, int upTo)
    {
        var source = await RequireConversationAsync(id);
        if (source.Incognito || source.ParentId is not null) throw new InvalidOperationException("シークレットチャットとサブエージェントは分けられません");
        var fork = source.ForkUpTo(upTo);
        // 保存できなかった分岐が手元の控えだけに残らないよう、保存してから載せる
        await conversations.SaveAsync(fork);
        conversations.Adopt(fork);
        await uploads.ShareAsync(source.Id, fork.Id);
        await sessionFiles.CopyAsync(source, fork);
        Emit("conversations.changed", null);
        return fork.ToWire();
    }

    async Task<JsonNode?> ChangedFilesViewAsync(string conversationId) =>
        new JsonObject(checkpoints.ChangedFiles((await RequireConversationAsync(conversationId)).Id).Select(t => KeyValuePair.Create(t.Key.ToString(), (JsonNode?)new JsonArray(t.Value.Select(p => (JsonNode)p).ToArray()))));

    // 返答で変えたファイルを、会話は残したまま元に戻す。後の返答で変えた分も一緒に戻る
    async Task<JsonNode?> UndoFilesAsync(string conversationId, int humanIndex)
    {
        var conversation = await RequireConversationAsync(conversationId);
        // 子の書き換えは子の控えにあり、ここでは戻せない。動いている間に戻すと、戻した後に書き足される
        if ((await DescendantsAsync(conversation.Id)).Any(c => hub.IsRunning(c.Id))) throw new InvalidOperationException("サブエージェントが動いている間は戻せません。終わってからもう一度お試しください");
        var restored = await hub.ExclusiveAsync(conversation, async _ =>
        {
            var result = checkpoints.Restore(conversation.Id, humanIndex);
            var paths = (result["restored"] as JsonArray ?? []).Select(n => (string?)n).OfType<string>().ToList();
            if (paths.Count > 0)
            {
                // 戻したことを Claude が知らないと、次の返答で書いたはずの中身を前提にしてしまう
                conversation.Append(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"<files-reverted>\nユーザーが次のファイルを、変更する前の状態に戻しました:\n{SystemPrompt.EscapeTags(string.Join("\n", paths.Select(p => new string(p.Where(c => !char.IsControl(c)).ToArray()))))}\n</files-reverted>" }),
                });
                await conversations.SaveAsync(conversation);
            }
            return result;
        });
        Emit("conversations.changed", null);
        return new JsonObject { ["conversation"] = conversation.ToWire(), ["files"] = restored };
    }

    // 回答のコードを、人が選んだ場所にファイルとして保存する。選ぶのは人なので、許可フォルダの外でもよい
    async Task<JsonNode?> SaveCodeAsync(string name, string text)
    {
        var current = await settings.GetAsync();
        var folder = current.Files.Folders.FirstOrDefault(f => !f.ReadOnly && Directory.Exists(f.Path))?.Path;
        var safeName = string.Concat(Path.GetFileName(name).Split(Path.GetInvalidFileNameChars()));
        if (platform.PickSaveFile(safeName.Length > 0 ? safeName : "code.txt", folder) is not { } path) return null;
        // BOM を付けると、シェルスクリプトや一部のツールが先頭の 1 文字目を読み違える
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        return path;
    }

    // 入力の小窓から送ったチャットを、メインの窓で開いて前に出す
    Task<JsonNode?> ShowChatAsync(string conversationId)
    {
        if (!Guid.TryParse(conversationId, out _)) throw new InvalidOperationException("チャットの ID が不正です");
        platform.ShowWindow();
        Emit("app.navigate", new JsonObject { ["conversationId"] = conversationId });
        return Task.FromResult<JsonNode?>(null);
    }

    async Task<JsonNode?> OpenChatWindowAsync(string conversationId)
    {
        await RequireConversationAsync(conversationId);
        if (platform.OpenChatWindow(conversationId) is { } problem) throw new InvalidOperationException(problem);
        return null;
    }

    async Task<JsonNode?> CaptureScreenAsync()
    {
        // 範囲を選ぶのを待つ上限。忘れて放っておいても待ち続けない
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        return await platform.CaptureScreenAsync(timeout.Token);
    }

    async Task<JsonNode?> BudgetViewAsync()
    {
        var budget = (await settings.GetAsync()).Budget;
        var (today, month) = await ledger.SpentAsync();
        return new JsonObject
        {
            ["todayUsd"] = today,
            ["monthUsd"] = month,
            ["dailyLimitUsd"] = budget.DailyLimitUsd,
            ["monthlyLimitUsd"] = budget.MonthlyLimitUsd,
        };
    }

    async Task<JsonNode?> SessionListAsync(string conversationId)
    {
        var conversation = await RequireConversationAsync(conversationId);
        var root = await sessionFiles.RootOfAsync(conversation);
        var list = await sessionFiles.ListAsync(conversation);
        return new JsonObject
        {
            ["rootId"] = root.Id,
            ["path"] = sessionFiles.DirOf(root),
            // 消すときの確認は、この会話が持つフォルダのファイルだけ数える
            ["own"] = conversation.ParentId is null,
            ["files"] = new JsonArray(list.OrderBy(f => f.Path).Select(f => (JsonNode)SessionFiles.ToWire(f)).ToArray()),
        };
    }

    async Task<JsonNode?> SessionOpenAsync(string conversationId, string? fileId)
    {
        var conversation = await RequireConversationAsync(conversationId);
        if (fileId is null)
        {
            var dir = sessionFiles.DirOf(await sessionFiles.RootOfAsync(conversation));
            Directory.CreateDirectory(dir);
            platform.OpenPath(dir);
            return null;
        }
        var (_, full) = await sessionFiles.FindAsync(conversation, fileId, null);
        // Claude が書いたファイルなので、関連付けで動いてしまう種類 (.py や .cmd など) はフォルダを開くだけにする
        platform.OpenPath(SafeToOpen.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) ? full : Path.GetDirectoryName(full)!);
        return null;
    }

    static readonly string[] SafeToOpen = [".txt", ".md", ".json", ".csv", ".log", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf", ".xml", ".yaml", ".yml"];

    // チャット一覧の検索。題名だけでなく本文も探し、見つかった所の前後を返す
    async Task<JsonNode?> SearchConversationsAsync(string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var hits = new JsonArray();
        if (words.Length == 0) return hits;
        foreach (var summary in await conversations.ListAsync())
        {
            if (await conversations.GetAsync(summary.Id) is not { } c) continue;
            var body = string.Join("\n", c.CopyMessages().Where(m => Conversation.IsHumanTurn(m) || (string?)m["role"] == "assistant").Select(m => WireText.Of(m["content"])));
            var text = $"{c.Title}\n{body}";
            if (!words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            var at = body.IndexOf(words[0], StringComparison.OrdinalIgnoreCase);
            var start = Math.Max(0, at - 40);
            hits.Add(new JsonObject
            {
                ["id"] = c.Id,
                ["snippet"] = at < 0 ? "" : body.Substring(start, Math.Min(160, body.Length - start)).Replace('\n', ' '),
            });
        }
        return hits;
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
        if (await folders.GetAsync(FolderStore.RequireId(id)) is null) throw new InvalidOperationException("プロジェクトが見つかりません");
        foreach (var summary in (await conversations.ListAsync()).Where(s => s.FolderId == id))
        {
            if (await conversations.GetAsync(summary.Id) is not { } c) continue;
            c.FolderId = null;
            await conversations.SaveAsync(c, touch: false);
        }
        await folders.DeleteAsync(id);
        // ナレッジとメモリはプロジェクトと一緒に消す。チャットだけは外へ出して残す
        foreach (var dir in new[] { ProjectKnowledge.DirFor(paths, id), MemoryToolProvider.RootFor(paths, id) })
        {
            if (Directory.Exists(dir)) platform.MoveToTrash(dir);
        }
        Emit("folders.changed", null);
        Emit("conversations.changed", null);
        return await ListFoldersAsync();
    }

    async Task<JsonNode?> KnowledgeAsync(string folderId, Func<string, Task<JsonArray>> action)
    {
        if (await folders.GetAsync(folderId) is null) throw new InvalidOperationException("プロジェクトが見つかりません");
        return await action(ProjectKnowledge.DirFor(paths, folderId));
    }

    async Task<JsonNode?> ReadKnowledgeAsync(string folderId, string name)
    {
        if (await folders.GetAsync(folderId) is null) throw new InvalidOperationException("プロジェクトが見つかりません");
        return await File.ReadAllTextAsync(ProjectKnowledge.PathOf(ProjectKnowledge.DirFor(paths, folderId), name));
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
        foreach (var key in new[] { "model", "effort", "webSearch", "webFetch", "codeExecution", "enabledSources", "knownSources", "style", "fast" })
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
        if (id is null && conversation.FolderId is null) conversation.Incognito = (bool?)p["incognito"] == true;
        var content = p["content"]?.DeepClone() ?? throw new InvalidOperationException("content がありません");
        // アーカイブしたチャットでも、話しかけたら一覧に戻す
        conversation.Archived = false;
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
            string? routed = null;
            if ((string?)p["model"] == ModelCatalog.AutoModel)
            {
                routed = await RouteAsync(conversation, WireText.Of(content));
                // 判定に使ったモデル名で指示を作り直す。会話の設定には「自動」を残し、次の発言も振り分ける
                var request = (JsonObject)conversation.Options!.DeepClone();
                request["model"] = routed;
                options = await hub.OptionsForAsync(conversation, request);
            }
            await hub.StartAsync(conversation, () =>
            {
                conversation.Append(message);
                return Task.CompletedTask;
            }, options);
            if (routed is not null) hub.Notice(conversation.Id, $"自動で {ModelCatalog.DisplayName(routed)} を選びました");
        }
        return new JsonObject { ["conversationId"] = conversation.Id };
    }

    // 振り分けに失敗しても送るのは止めず、前回のモデルか標準のモデルで答える
    async Task<string> RouteAsync(Conversation conversation, string request)
    {
        try
        {
            using var timeout = new CancellationTokenSource(RouteTimeout);
            return await (await hub.EngineAsync()).RouteModelAsync(conversation, request, timeout.Token);
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            return conversation.Model ?? "claude-sonnet-5-5";
        }
    }

    // 振り分けで送信が待たされすぎないように
    static readonly TimeSpan RouteTimeout = TimeSpan.FromSeconds(10);

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
        var context = ResearchContext(conversation);
        await hub.ExclusiveAsync(conversation, async _ =>
        {
            conversation.Append(message);
            await conversations.SaveAsync(conversation);
            Emit("conversations.changed", null);
            return 0;
        });
        var title = question.Length > 30 ? question[..30] + "…" : question;
        workflows.Start(conversation, null, "research", new JsonObject { ["question"] = question, ["context"] = context }, $"リサーチ: {title}");
    }

    // 直近のやり取りだけを文字で渡す。長い会話を丸ごと渡すとエージェントごとに料金がかさむ
    static string? ResearchContext(Conversation conversation)
    {
        const int MaxChars = 8000;
        var lines = new List<string>();
        var total = 0;
        foreach (var message in conversation.CopyMessages().AsEnumerable().Reverse())
        {
            var text = WireText.Of(message["content"]).Trim();
            if (text.Length == 0) continue;
            var line = $"{((string?)message["role"] == "user" ? "User" : "Assistant")}: {(text.Length > 2000 ? text[..2000] + "…" : text)}";
            if (total + line.Length > MaxChars) break;
            lines.Insert(0, line);
            total += line.Length;
        }
        return lines.Count > 0 ? string.Join("\n\n", lines) : null;
    }

    async Task<JsonNode?> RetryAsync(JsonObject p)
    {
        var conversation = await RequireConversationAsync(Str(p, "conversationId"));
        var options = await OptionsAsync(conversation, p);
        await hub.StartAsync(conversation, () =>
        {
            // 指定が無ければ最後の発言への返事をやり直す
            var human = (int?)p["messageIndex"] ?? conversation.LastHumanIndex();
            var messages = conversation.CopyMessages();
            if (human < 0 || human >= messages.Count || !Conversation.IsHumanTurn(messages[human])) throw new InvalidOperationException("やり直す発言がありません");
            // その発言より前は一切変えないので、過去の思考ブロックはそのまま有効。元の返事は別の版として残す
            conversation.BranchFrom(human + 1);
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
            // 範囲外の番号でファイルの控えまで消さない
            var messages = conversation.CopyMessages();
            if (index < 0 || index >= messages.Count || !Conversation.IsHumanTurn(messages[index])) throw new InvalidOperationException("編集する発言がありません");
            checkpoints.ForgetFrom(conversation.Id, index);
            conversation.BranchFrom(index);
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

    Task<JsonNode?> ApproveAsync(string requestId, string decision, string? rule)
    {
        hub.Approve(requestId, decision, rule);
        return Task.FromResult<JsonNode?>(null);
    }

    Task<JsonNode?> AnswerAsync(string requestId, JsonArray answers)
    {
        hub.Answer(requestId, answers);
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

    // Claude Code の /compact。直近のやり取りを残して、それより前を API の「その場で要約」で畳む。画面には全部残す。
    // 対応していないモデルや、要約が返らなかったときは、自前で要約を会話に足す
    async Task<JsonNode?> CompactAsync(string conversationId, string? instructions)
    {
        var conversation = await RequireConversationAsync(conversationId);
        if (conversation.MessageCount - conversation.ApiStart < 2) throw new InvalidOperationException("要約するほどの会話がありません");
        var engine = await hub.EngineAsync();
        if (await engine.BudgetLimitAsync() is { } limitReached) throw new InvalidOperationException(limitReached);
        var options = await hub.OptionsForAsync(conversation);
        await hub.ExclusiveAsync(conversation, async token =>
        {
            try
            {
                if (await engine.CompactOnDemandAsync(conversation, options, instructions, midTurn: false, token))
                {
                    await conversations.SaveAsync(conversation);
                    return 0;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // 自前の要約でやり直す
            }
            var prompt = string.IsNullOrWhiteSpace(instructions) ? CompactPrompt : $"{CompactPrompt}\nAdditional instructions from the user: {instructions.Trim()}";
            var summary = await engine.AskAsideAsync(conversation, options, prompt, "compact", token);
            conversation.Append(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"<compact-summary>\nThe earlier part of this conversation was summarized to save context:\n\n{summary}\n</compact-summary>" }),
            });
            conversation.ApplyCompaction(null, conversation.MessageCount - 1);
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
        var outcome = await hooks.RunUserPromptSubmitAsync(conversation, question);
        if (outcome.Blocked) throw new InvalidOperationException($"UserPromptSubmit フックが止めました: {outcome.Message}");
        var engine = await hub.EngineAsync();
        if (await engine.BudgetLimitAsync() is { } limitReached) throw new InvalidOperationException(limitReached);
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
            checkpoints.ForgetFrom(conversation.Id, messageIndex);
            conversation.TruncateFrom(messageIndex);
            await conversations.SaveAsync(conversation);
            return result;
        });
        Emit("conversations.changed", null);
        return new JsonObject { ["conversation"] = conversation.ToWire(), ["files"] = restored };
    }

    async Task<JsonNode?> SwitchBranchAsync(string conversationId, int index, int version)
    {
        var conversation = await RequireConversationAsync(conversationId);
        await hub.ExclusiveAsync(conversation, async _ =>
        {
            conversation.SwitchBranch(index, version);
            await conversations.SaveAsync(conversation, touch: false);
            return 0;
        });
        return conversation.ToWire();
    }

    async Task<JsonNode?> RewindInfoAsync(string conversationId) =>
        new JsonArray(checkpoints.Turns((await RequireConversationAsync(conversationId)).Id).Select(t => (JsonNode)t).ToArray());

    // claude.ai のアーティファクト一覧。すべての会話から集める
    async Task<JsonNode?> ListArtifactsAsync()
    {
        var items = new List<(long UpdatedAt, JsonObject Item)>();
        foreach (var summary in await conversations.ListAsync())
        {
            if (await conversations.GetAsync(summary.Id) is not { } conversation) continue;
            lock (conversation.Artifacts)
            {
                items.AddRange(conversation.Artifacts.Select(a => (a.UpdatedAt, new JsonObject
                {
                    ["conversationId"] = conversation.Id,
                    ["conversationTitle"] = conversation.Title,
                    ["id"] = a.Id,
                    ["title"] = a.Title,
                    ["type"] = a.Type,
                    ["version"] = a.Version,
                    ["updatedAt"] = a.UpdatedAt,
                })));
            }
        }
        return new JsonArray(items.OrderByDescending(i => i.UpdatedAt).Select(i => (JsonNode)i.Item).ToArray());
    }

    async Task<JsonNode?> ExportArtifactAsync(string conversationId, string id)
    {
        var conversation = await RequireConversationAsync(conversationId);
        Artifact artifact;
        lock (conversation.Artifacts) artifact = conversation.Artifacts.FirstOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("アーティファクトが見つかりません");
        var codeExtension = CodeExtension(artifact.Language);
        // 言語は Claude が決める。.bat や .js のように、開くと動くファイルは作らない
        if (ExecutableExtensions.Contains(codeExtension, StringComparer.OrdinalIgnoreCase)) codeExtension += ".txt";
        var extension = artifact.Type switch
        {
            "html" => ".html",
            "react" => ".jsx",
            "svg" => ".svg",
            "mermaid" => ".mmd",
            "code" => codeExtension,
            _ => ".md",
        };
        var path = Path.Combine(paths.ExportsDir, $"{FileNameOf(artifact.Title)}-v{artifact.Version}{extension}");
        Directory.CreateDirectory(paths.ExportsDir);
        await File.WriteAllTextAsync(path, artifact.Content);
        platform.OpenPath(paths.ExportsDir);
        return new JsonObject { ["path"] = path };
    }

    static string CodeExtension(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "python" or "py" => ".py",
        "javascript" or "js" => ".js",
        "typescript" or "ts" => ".ts",
        "tsx" => ".tsx",
        "jsx" => ".jsx",
        "csharp" or "c#" or "cs" => ".cs",
        "java" => ".java",
        "kotlin" => ".kt",
        "go" => ".go",
        "rust" => ".rs",
        "ruby" => ".rb",
        "php" => ".php",
        "swift" => ".swift",
        "c" => ".c",
        "cpp" or "c++" => ".cpp",
        "html" => ".html",
        "css" => ".css",
        "json" => ".json",
        "yaml" or "yml" => ".yaml",
        "xml" => ".xml",
        "sql" => ".sql",
        "bash" or "sh" or "shell" => ".sh",
        "powershell" or "ps1" => ".ps1",
        "batch" or "bat" => ".bat",
        _ => ".txt",
    };

    static readonly string[] ExecutableExtensions =
    [
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".url",
        ".scr", ".msi", ".msp", ".msc", ".cpl", ".jar", ".reg", ".appref-ms", ".application", ".pif", ".scf", ".inf", ".chm",
    ];

    // 会話が覚えている場所だけ開く。画面から任意のパスを開かせない
    async Task<JsonNode?> OpenFileAsync(string conversationId, string fileId, bool reveal)
    {
        var conversation = await RequireConversationAsync(conversationId);
        if (!conversation.Files.TryGetValue(fileId, out var path) || !File.Exists(path)) throw new InvalidOperationException("ファイルが見つかりません");
        // コード実行で作られた実行ファイルを、ボタン 1 つで動かさない
        if (ExecutableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) reveal = true;
        platform.OpenPath(reveal ? Path.GetDirectoryName(path)! : path);
        return null;
    }

    Task<JsonNode?> StopWorkflowAsync(string id)
    {
        workflows.Stop(id);
        return Task.FromResult<JsonNode?>(null);
    }

    // 人から受け取った Skill は、説明が毎回 Claude への指示に入る。中身を確かめてからオンにしてもらう
    async Task<JsonNode?> ImportSkillAsync(string data)
    {
        var name = extensions.ImportSkill(Convert.FromBase64String(data));
        await settings.UpdateAsync(s =>
        {
            if (!s.Features.DisabledSkills.Contains(name, StringComparer.OrdinalIgnoreCase)) s.Features.DisabledSkills.Add(name);
        });
        return name;
    }

    Task<JsonNode?> SaveStyleAsync(string? id, string name, string text)
    {
        var saved = extensions.SaveOutputStyle(id, name, text);
        return Task.FromResult<JsonNode?>(saved);
    }

    Task<JsonNode?> DeleteStyleAsync(string id)
    {
        var file = extensions.CustomStylePath(id);
        if (File.Exists(file)) platform.MoveToTrash(file);
        return Task.FromResult<JsonNode?>(null);
    }

    async Task<JsonNode?> GenerateStyleAsync(string request)
    {
        var engine = await hub.EngineAsync();
        var style = await engine.GenerateStyleAsync(request) ?? throw new InvalidOperationException("スタイルを作れませんでした。説明を変えてもう一度試してください");
        return new JsonObject { ["name"] = style.Name, ["text"] = style.Instructions };
    }

    // 自作コマンドと Skills の本文を、送る文に展開する
    Task<JsonNode?> ExpandCommandAsync(string name, string args)
    {
        var command = extensions.ListCommands().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"/{name} というコマンドはありません");
        var body = command.Body.Contains("$ARGUMENTS")
            ? command.Body.Replace("$ARGUMENTS", args)
            : args.Length > 0 ? $"{command.Body}\n\nARGUMENTS: {args}" : command.Body;
        if (command.Kind == "skill" && extensions.GetSkill(command.Name) is { } skill && SkillToolProvider.FilesOf(skill.Info) is { Count: > 0 } files)
        {
            body += $"\n\nFiles in this skill (read them with skills__read_file, name \"{command.Name}\"):\n{string.Join("\n", files)}";
        }
        return Task.FromResult<JsonNode?>($"<command name=\"/{command.Name}\">\n{body}\n</command>");
    }

    // アーティファクトは iframe で別のページとして出す。アプリの画面の CSP ではインラインのスクリプトが動かないため。
    // 画像の読み込みやポップアップだけでも中身を外へ送り出せるので、外への通信は cdnjs だけにし、新しい窓も開かせない。
    // CSP の sandbox は、このページが直接開かれたときもアプリと別オリジンにするため
    public const string ArtifactCsp = "default-src 'none'; script-src 'unsafe-inline' https://cdnjs.cloudflare.com; style-src 'unsafe-inline' https://cdnjs.cloudflare.com; img-src data: blob: https://cdnjs.cloudflare.com; font-src data: https://cdnjs.cloudflare.com; media-src data: blob:; connect-src 'none'; frame-src 'none'; form-action 'none'; base-uri 'none'; frame-ancestors 'self'; sandbox allow-scripts allow-modals";

    public static bool IsArtifactPath(string path) => path.StartsWith("artifact/", StringComparison.Ordinal);

    // artifact/<会話>/<アーティファクト>/<版>
    public async Task<string?> RenderArtifactAsync(string path)
    {
        var parts = path.Split('/');
        if (parts.Length != 4 || !int.TryParse(parts[3], out var version) || await conversations.GetAsync(parts[1]) is not { } conversation) return null;
        lock (conversation.Artifacts)
        {
            var artifact = conversation.Artifacts.FirstOrDefault(a => a.Id == parts[2]);
            return artifact?.ContentOf(version) is { } content ? ArtifactPages.Render(artifact, content) : null;
        }
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

    async Task<JsonNode?> SetScheduleEnabledAsync(string id, bool enabled)
    {
        try
        {
            await scheduler.SetEnabledAsync(id, enabled);
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

    // folderId があればそのプロジェクトのメモリ。無ければ全体のメモリ
    async Task<JsonNode?> MemoryAsync(JsonObject p, Func<string, JsonNode> action)
    {
        var folderId = (string?)p["folderId"];
        if (folderId is not null && await folders.GetAsync(folderId) is null) throw new InvalidOperationException("プロジェクトが見つかりません");
        var root = Path.GetFullPath(MemoryToolProvider.RootFor(paths, folderId));
        Directory.CreateDirectory(root);
        return action(root);
    }

    static string MemoryPath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.TrimStart('/', '\\')));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("メモリのフォルダの外は扱えません");
        return full;
    }

    static JsonArray ListMemory(string root)
    {
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

    async Task<JsonNode?> LocalUsageAsync()
    {
        var summary = await ledger.SummaryAsync(TimeZoneInfo.Local);
        var titles = (await conversations.ListAsync()).ToDictionary(c => c.Id, c => c.Title);
        foreach (var entry in (summary["topConversationsThisMonth"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = (string?)entry["conversationId"] ?? "";
            entry["title"] = titles.GetValueOrDefault(id);
        }
        return summary;
    }

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

    // 下書きのカードから、いつものメールアプリの新規作成を開く
    Task<JsonNode?> OpenMailAsync(string to, string subject, string body)
    {
        // 宛先に ? や & が入ると、件名や本文を差し替えられてしまう
        var url = $"mailto:{Uri.EscapeDataString(to.Trim())}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
        platform.OpenPath(url);
        return Task.FromResult<JsonNode?>(null);
    }

    Task<JsonNode?> OpenAsync(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) File.WriteAllText(path, "");
        platform.OpenPath(path);
        return Task.FromResult<JsonNode?>(null);
    }

    // 拡張機能は Chrome に「パッケージ化されていない拡張機能」として読み込んでもらう。アプリの版に合わせて毎回書き直す
    Task<JsonNode?> WriteExtensionAsync()
    {
        var dir = Path.Combine(paths.Root, "chrome-extension");
        ChromeExtension.Write(dir);
        platform.OpenPath(dir);
        return Task.FromResult<JsonNode?>(dir);
    }

    async Task<JsonNode?> BrowserStatusAsync() => new JsonObject
    {
        ["connected"] = bridge.Connected,
        ["version"] = bridge.Version,
        ["error"] = bridge.ListenError,
        ["token"] = await bridge.TokenAsync(),
    };

    async Task<JsonNode?> ResetBrowserTokenAsync()
    {
        await bridge.ResetTokenAsync();
        return await BrowserStatusAsync();
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
        housekeeping.Dispose();
        foreach (var run in workflows.ListView().OfType<JsonObject>()) workflows.Stop((string?)run["id"] ?? "");
        hub.StopAll();
        // 画面は片付けを数秒しか待たずに終わるので、残ると困るもの (シークレットのファイル、見えないブラウザ、裏のコマンド) を先に片付ける
        terminal.Dispose();
        await internalBrowser.DisposeAsync();
        DeleteIncognitoFiles();
        await mcp.DisposeAsync();
        await browser.DisposeAsync();
        await bridge.DisposeAsync();
        // MCP サーバーが掴んでいたファイルがあれば、ここで消える
        DeleteIncognitoFiles();
        http.Dispose();
    }

    static void DeleteIncognitoFiles()
    {
        try
        {
            if (Directory.Exists(AppPaths.IncognitoFilesDir)) Directory.Delete(AppPaths.IncognitoFilesDir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 開いたままのファイルがある。次に起動したときに消える
        }
    }
}
