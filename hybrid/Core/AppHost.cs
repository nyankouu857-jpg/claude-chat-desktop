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
    readonly AppPaths paths;
    readonly IPlatform platform;
    readonly Action<string> send;
    readonly SettingsStore settings;
    readonly SecretStore secrets;
    readonly ConversationStore conversations;
    readonly UsageLedger ledger;
    readonly McpHub mcp;
    readonly ToolRegistry tools;
    readonly OrgCostClient orgCost;
    readonly HttpClient http = new();
    readonly ConcurrentDictionary<string, CancellationTokenSource> runningChats = new();
    readonly ConcurrentDictionary<string, (string ConversationId, JsonObject Request, TaskCompletionSource<string> Decision)> pendingApprovals = new();
    AnthropicClient? client;
    string? clientKey;

    public AppHost(AppPaths paths, ISecretProtector protector, IPlatform platform, Action<string> send)
    {
        this.paths = paths;
        this.platform = platform;
        this.send = send;
        settings = new SettingsStore(paths);
        secrets = new SecretStore(paths, protector);
        conversations = new ConversationStore(paths);
        ledger = new UsageLedger(paths);
        mcp = new McpHub(paths);
        tools = new ToolRegistry([new FileToolProvider(settings, platform), mcp]);
        orgCost = new OrgCostClient(paths, http);
        mcp.Changed += () => Emit("mcp.changed", mcp.Describe());
    }

    public async Task StartAsync()
    {
        var current = await settings.GetAsync();
        await ledger.GetPricingAsync();
        await mcp.ApplyAsync(current.McpServers);
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
        "conversations.rename" => UpdateConversationAsync(Str(p, "id"), c => c.Title = Str(p, "title").Trim()),
        "conversations.star" => UpdateConversationAsync(Str(p, "id"), c => c.Starred = (bool?)p["starred"] == true),
        "conversations.delete" => DeleteConversationAsync(Str(p, "id")),
        "chat.send" => SendAsync(p),
        "chat.retry" => RetryAsync(p),
        "chat.edit" => EditAsync(p),
        "chat.stop" => StopAsync(Str(p, "conversationId")),
        "tools.approve" => ApproveAsync(Str(p, "requestId"), Str(p, "decision")),
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
        "chat.systemPrompt" => PreviewSystemPromptAsync(),
        _ => throw new InvalidOperationException($"未知のメソッドです: {method}"),
    };

    static string Str(JsonObject p, string key) => (string?)p[key] ?? throw new InvalidOperationException($"{key} がありません");

    async Task<JsonNode?> InitAsync()
    {
        var current = await settings.GetAsync();
        return new JsonObject
        {
            ["settings"] = SettingsView(current),
            ["secrets"] = await SecretsViewAsync(),
            ["conversations"] = Json.ToNode(await conversations.ListAsync()),
            ["mcp"] = mcp.Describe(),
            ["paths"] = new JsonObject { ["root"] = paths.Root, ["isFallback"] = paths.IsFallback },
            // 画面を読み直しても、応答中の表示と承認待ちを取り戻せるように
            ["running"] = new JsonArray(runningChats.Keys.Select(id => (JsonNode)new JsonObject
            {
                ["conversationId"] = id,
                ["approvals"] = new JsonArray(pendingApprovals.Values
                    .Where(p => p.ConversationId == id)
                    .Select(p => (JsonNode)p.Request.DeepClone())
                    .ToArray()),
            }).ToArray()),
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
        ["mcpServers"] = JsonSerializer.SerializeToNode(s.McpServers, Json.Wire),
        ["toolPermissions"] = JsonSerializer.SerializeToNode(s.ToolPermissions, Json.Wire),
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

    async Task<JsonNode?> UpdateSettingsAsync(JsonObject patch)
    {
        if (patch["files"]?["folders"] is JsonArray folders)
        {
            foreach (var path in folders.Select(f => (string?)f?["path"]))
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !Directory.Exists(path)) throw new InvalidOperationException($"フォルダが見つかりません: {path}");
            }
        }
        var updated = await settings.UpdateAsync(s =>
        {
            if (patch["profile"] is JsonObject profile) s.Profile = Merge(s.Profile, profile);
            if (patch["appearance"] is JsonObject appearance) s.Appearance = Merge(s.Appearance, appearance);
            if (patch["chat"] is JsonObject chat) s.Chat = Merge(s.Chat, chat);
            if (patch["files"] is JsonObject files) s.Files = Merge(s.Files, files);
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

    AnthropicClient GetClient(string apiKey)
    {
        if (client is null || clientKey != apiKey)
        {
            client = new AnthropicClient { ApiKey = apiKey };
            clientKey = apiKey;
        }
        return client;
    }

    async Task<JsonNode?> TestApiKeyAsync()
    {
        var apiKey = await secrets.GetAsync(SecretStore.ApiKey);
        if (apiKey is null) return new JsonObject { ["ok"] = false, ["message"] = "APIキーが未設定です" };
        try
        {
            var page = await GetClient(apiKey).Models.List();
            return new JsonObject { ["ok"] = true, ["message"] = $"接続できました。使えるモデルは {page.Items.Count} 個です" };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["message"] = ChatEngine.Describe(e) };
        }
    }

    async Task<JsonNode?> ListConversationsAsync() => Json.ToNode(await conversations.ListAsync());

    async Task<JsonNode?> GetConversationAsync(string id) => (await conversations.GetAsync(id))?.ToWire();

    async Task<JsonNode?> UpdateConversationAsync(string id, Action<Conversation> update)
    {
        var conversation = await conversations.GetAsync(id) ?? throw new InvalidOperationException("会話が見つかりません");
        update(conversation);
        await conversations.SaveAsync(conversation, touch: false);
        Emit("conversations.changed", null);
        return conversation.ToWire();
    }

    async Task<JsonNode?> DeleteConversationAsync(string id)
    {
        if (runningChats.TryRemove(id, out var running)) running.Cancel();
        await conversations.DeleteAsync(id);
        Emit("conversations.changed", null);
        return null;
    }

    async Task<string> BuildSystemPromptAsync(ISet<string>? enabledSources)
    {
        var current = await settings.GetAsync();
        var profile = current.Profile;
        // OS の言語設定に関係なく日本語の曜日にする
        var today = DateTime.Now.ToString("yyyy年M月d日 dddd", System.Globalization.CultureInfo.GetCultureInfo("ja-JP"));
        var parts = new List<string>();
        // 公式プロンプトの書き方をそのまま貼れるように
        var custom = current.Chat.SystemPrompt.Trim();
        var hasDate = custom.Contains("{{currentDateTime}}");
        if (custom.Length > 0) parts.Add(custom.Replace("{{currentDateTime}}", today));
        if (current.Chat.AppendAppContext)
        {
            if (!hasDate) parts.Add($"今日の日付は {today} です。");
            var name = profile.Nickname.Length > 0 ? profile.Nickname : profile.FullName;
            if (name.Length > 0) parts.Add($"ユーザーの呼び名は {name} です。");
            if ((enabledSources is null || enabledSources.Contains(FileToolProvider.Source)) && FileToolProvider.DescribeForPrompt(current.Files) is { } folders) parts.Add(folders);
        }
        if (profile.Preferences.Trim().Length > 0) parts.Add($"ユーザーが設定した個人設定です。回答に反映してください:\n{profile.Preferences.Trim()}");
        return string.Join("\n\n", parts);
    }

    async Task<JsonNode?> PreviewSystemPromptAsync() => await BuildSystemPromptAsync(null);

    async Task<ChatOptions> OptionsFromAsync(JsonObject p)
    {
        var defaults = (await settings.GetAsync()).Chat;
        var enabled = p["enabledSources"] is JsonArray list ? list.Select(n => (string?)n).OfType<string>().ToHashSet() : null;
        return new ChatOptions(
            (string?)p["model"] ?? defaults.Model,
            (string?)p["effort"] ?? defaults.Effort,
            (bool?)p["webSearch"] ?? defaults.WebSearch,
            enabled,
            await BuildSystemPromptAsync(enabled),
            Math.Max(1, defaults.MaxToolTurns));
    }

    async Task<JsonNode?> SendAsync(JsonObject p)
    {
        var id = (string?)p["conversationId"];
        var conversation = (id is null ? null : await conversations.GetAsync(id)) ?? conversations.Create();
        if (runningChats.ContainsKey(conversation.Id)) throw new InvalidOperationException("この会話は応答中です");
        var content = p["content"]?.DeepClone() ?? throw new InvalidOperationException("content がありません");
        conversation.Append(new JsonObject { ["role"] = "user", ["content"] = content });
        await conversations.SaveAsync(conversation);
        Emit("conversations.changed", null);
        StartChat(conversation, await OptionsFromAsync(p));
        return new JsonObject { ["conversationId"] = conversation.Id };
    }

    static bool IsHumanTurn(JsonObject message) =>
        (string?)message["role"] == "user" &&
        !(message["content"] is JsonArray blocks && blocks.OfType<JsonObject>().All(b => (string?)b["type"] == "tool_result"));

    async Task<JsonNode?> RetryAsync(JsonObject p)
    {
        var conversation = await conversations.GetAsync(Str(p, "conversationId")) ?? throw new InvalidOperationException("会話が見つかりません");
        if (runningChats.ContainsKey(conversation.Id)) throw new InvalidOperationException("この会話は応答中です");
        var messages = conversation.CopyMessages();
        var lastHuman = messages.FindLastIndex(IsHumanTurn);
        if (lastHuman < 0) throw new InvalidOperationException("やり直す発言がありません");
        // 最後の発言より前は一切変えないので、過去の思考ブロックはそのまま有効
        conversation.TruncateFrom(lastHuman + 1);
        await conversations.SaveAsync(conversation);
        StartChat(conversation, await OptionsFromAsync(p));
        return conversation.ToWire();
    }

    async Task<JsonNode?> EditAsync(JsonObject p)
    {
        var conversation = await conversations.GetAsync(Str(p, "conversationId")) ?? throw new InvalidOperationException("会話が見つかりません");
        if (runningChats.ContainsKey(conversation.Id)) throw new InvalidOperationException("この会話は応答中です");
        var index = (int?)p["messageIndex"] ?? throw new InvalidOperationException("messageIndex がありません");
        conversation.TruncateFrom(index);
        conversation.Append(new JsonObject { ["role"] = "user", ["content"] = p["content"]?.DeepClone() });
        await conversations.SaveAsync(conversation);
        StartChat(conversation, await OptionsFromAsync(p));
        return conversation.ToWire();
    }

    void StartChat(Conversation conversation, ChatOptions options)
    {
        var cancellation = new CancellationTokenSource();
        runningChats[conversation.Id] = cancellation;
        Emit("chat.event", new JsonObject { ["conversationId"] = conversation.Id, ["event"] = new JsonObject { ["type"] = "started" } });
        _ = RunChatAsync(conversation, options, cancellation);
    }

    async Task RunChatAsync(Conversation conversation, ChatOptions options, CancellationTokenSource cancellation)
    {
        var id = conversation.Id;
        // 途中経過を順番どおりに保存する。落ちてもそこまでの会話は残る
        var saveChain = Task.CompletedTask;
        void EmitChat(JsonObject e)
        {
            if ((string?)e["type"] == "message") saveChain = saveChain.ContinueWith(_ => conversations.SaveAsync(conversation, touch: false)).Unwrap();
            Emit("chat.event", new JsonObject { ["conversationId"] = id, ["event"] = e });
        }

        try
        {
            var apiKey = await secrets.GetAsync(SecretStore.ApiKey);
            ChatRunResult result;
            ChatEngine? engine = null;
            if (apiKey is null)
            {
                result = new ChatRunResult(ChatStatus.Failed, "APIキーが未設定です。設定の API 画面から登録してください");
            }
            else
            {
                conversation.Model = options.Model;
                engine = new ChatEngine(GetClient(apiKey), tools, ledger, settings);
                result = await engine.RunAsync(conversation, options, EmitChat, new ApprovalGate(this, id), cancellation.Token);
            }

            await saveChain;
            // 応答中に削除された会話を保存し直して復活させない
            if (await conversations.GetAsync(id) is null) return;
            await conversations.SaveAsync(conversation);
            EmitChat(new JsonObject
            {
                ["type"] = "done",
                ["status"] = result.Status.ToString().ToLowerInvariant(),
                ["error"] = result.Error,
                ["conversation"] = conversation.ToWire(),
            });
            Emit("conversations.changed", null);

            if (engine is not null && result.Status == ChatStatus.Done && conversation.Title == Conversation.DefaultTitle)
            {
                try
                {
                    var title = await engine.GenerateTitleAsync(conversation);
                    if (title is not null && await conversations.GetAsync(id) is not null)
                    {
                        conversation.Title = title;
                        await conversations.SaveAsync(conversation, touch: false);
                        Emit("conversations.changed", null);
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
            EmitChat(new JsonObject { ["type"] = "done", ["status"] = "failed", ["error"] = e.Message, ["conversation"] = conversation.ToWire() });
        }
        finally
        {
            runningChats.TryRemove(new KeyValuePair<string, CancellationTokenSource>(id, cancellation));
            cancellation.Dispose();
        }
    }

    Task<JsonNode?> StopAsync(string conversationId)
    {
        if (runningChats.TryGetValue(conversationId, out var cancellation)) cancellation.Cancel();
        return Task.FromResult<JsonNode?>(null);
    }

    sealed class ApprovalGate(AppHost host, string conversationId) : IApprovalGate
    {
        public async Task<string> RequestAsync(JsonObject request, CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var decision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            request["type"] = "approval";
            request["requestId"] = requestId;
            host.pendingApprovals[requestId] = (conversationId, (JsonObject)request.DeepClone(), decision);
            host.Emit("chat.event", new JsonObject { ["conversationId"] = conversationId, ["event"] = request });
            using var registration = cancellationToken.Register(() => decision.TrySetCanceled(cancellationToken));
            try
            {
                return await decision.Task;
            }
            finally
            {
                host.pendingApprovals.TryRemove(requestId, out _);
            }
        }
    }

    Task<JsonNode?> ApproveAsync(string requestId, string decision)
    {
        if (decision is not ("once" or "always" or "deny")) throw new InvalidOperationException("decision が不正です");
        if (pendingApprovals.TryGetValue(requestId, out var pending)) pending.Decision.TrySetResult(decision);
        return Task.FromResult<JsonNode?>(null);
    }

    async Task<JsonNode?> SaveMcpAsync(JsonObject p)
    {
        var servers = p["servers"]?.Deserialize<Dictionary<string, McpServerConfig>>(Json.Wire) ?? [];
        foreach (var (name, config) in servers)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("サーバー名が空です");
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
        _ => throw new InvalidOperationException("不明な場所です"),
    });

    public async ValueTask DisposeAsync()
    {
        foreach (var cancellation in runningChats.Values) cancellation.Cancel();
        await mcp.DisposeAsync();
        http.Dispose();
    }
}
