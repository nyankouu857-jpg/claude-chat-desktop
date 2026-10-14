using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ClaudeChat.Core;

public sealed partial class McpHub(AppPaths paths) : IToolProvider, IAsyncDisposable
{
    sealed class ServerState(string name, McpServerConfig config)
    {
        public string Name { get; } = name;
        public McpServerConfig Config { get; } = config;
        public string ConfigKey { get; } = JsonSerializer.Serialize(config);
        public string Status { get; set; } = "stopped";
        public string? Error { get; set; }
        public McpClient? Client { get; set; }
        public List<McpClientTool> Tools { get; set; } = [];
    }

    readonly Dictionary<string, ServerState> servers = [];
    readonly SemaphoreSlim gate = new(1, 1);

    public event Action? Changed;

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex UnsafeNameChars();

    // Claude のツール名は英数字と _ - の 64 文字まで
    static string ApiName(string server, string tool)
    {
        var name = $"{UnsafeNameChars().Replace(server, "_")}__{UnsafeNameChars().Replace(tool, "_")}";
        return name.Length <= 64 ? name : name[..64];
    }

    public async Task ApplyAsync(IReadOnlyDictionary<string, McpServerConfig> configs)
    {
        var toStart = new List<ServerState>();
        await gate.WaitAsync();
        try
        {
            foreach (var name in servers.Keys.ToList())
            {
                if (configs.TryGetValue(name, out var config) && JsonSerializer.Serialize(config) == servers[name].ConfigKey) continue;
                await StopAsync(servers[name]);
                lock (servers) servers.Remove(name);
            }
            foreach (var (name, config) in configs)
            {
                if (servers.ContainsKey(name)) continue;
                var state = new ServerState(name, config) { Status = config.Disabled ? "disabled" : "starting" };
                lock (servers) servers[name] = state;
                if (!config.Disabled) toStart.Add(state);
            }
        }
        finally
        {
            gate.Release();
        }
        Changed?.Invoke();
        // サーバーの起動は数秒かかることがあるので待たずに返す
        foreach (var state in toStart) _ = StartAsync(state);
    }

    public async Task RestartAsync(string name)
    {
        ServerState? state;
        await gate.WaitAsync();
        try
        {
            if (!servers.TryGetValue(name, out state) || state.Config.Disabled) return;
            await StopAsync(state);
            state.Status = "starting";
        }
        finally
        {
            gate.Release();
        }
        Changed?.Invoke();
        await StartAsync(state);
    }

    async Task StartAsync(ServerState state)
    {
        try
        {
            IClientTransport transport = state.Config.IsRemote
                ? new HttpClientTransport(new HttpClientTransportOptions
                {
                    Name = state.Name,
                    Endpoint = new Uri(state.Config.Url!),
                    AdditionalHeaders = state.Config.Headers,
                })
                : new StdioClientTransport(StdioOptions(state));
            // npx が初回にパッケージを落とす時間を見込む
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions { ClientInfo = new Implementation { Name = "ClaudeChat", Version = "0.2.0" } },
                cancellationToken: timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            state.Client = client;
            state.Tools = tools.ToList();
            state.Status = "running";
            state.Error = null;
            Log(state.Name, $"接続しました。ツール {state.Tools.Count} 個");
        }
        catch (Exception e)
        {
            state.Status = "error";
            state.Error = e is OperationCanceledException ? "起動がタイムアウトしました" : e.Message;
            Log(state.Name, $"接続に失敗しました: {e}");
        }
        Changed?.Invoke();
    }

    StdioClientTransportOptions StdioOptions(ServerState state)
    {
        var command = state.Config.Command ?? throw new InvalidOperationException("command が未設定です");
        var args = state.Config.Args ?? [];
        // npx や uvx は .cmd なので、Windows ではそのまま起動できない
        if (OperatingSystem.IsWindows() && !command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            args = ["/c", command, .. args];
            command = "cmd.exe";
        }
        return new StdioClientTransportOptions
        {
            Name = state.Name,
            Command = command,
            Arguments = args,
            WorkingDirectory = state.Config.Cwd,
            EnvironmentVariables = state.Config.Env?.ToDictionary(p => p.Key, p => (string?)p.Value),
            StandardErrorLines = line => Log(state.Name, line),
        };
    }

    static async Task StopAsync(ServerState state)
    {
        if (state.Client is { } client)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception)
            {
                // 既に落ちているプロセスを止めようとして失敗するのは問題ない
            }
        }
        state.Client = null;
        state.Tools = [];
        state.Status = "stopped";
    }

    void Log(string server, string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(paths.LogsDir, $"mcp-{UnsafeNameChars().Replace(server, "_")}.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n", Encoding.UTF8);
        }
        catch (IOException)
        {
            // ログが書けないだけでツールを止めない
        }
    }

    public string LogPath(string server) => Path.Combine(paths.LogsDir, $"mcp-{UnsafeNameChars().Replace(server, "_")}.log");

    List<ServerState> Snapshot()
    {
        lock (servers) return servers.Values.ToList();
    }

    public JsonArray Describe() => new(Snapshot()
        .OrderBy(s => s.Name)
        .Select(s => (JsonNode)new JsonObject
        {
            ["name"] = s.Name,
            ["kind"] = s.Config.IsRemote ? "remote" : "local",
            ["target"] = s.Config.IsRemote ? s.Config.Url : string.Join(" ", new[] { s.Config.Command ?? "" }.Concat(s.Config.Args ?? [])),
            ["status"] = s.Status,
            ["error"] = s.Error,
            ["tools"] = new JsonArray(s.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["apiName"] = ApiName(s.Name, t.Name),
                ["title"] = t.Title,
                ["description"] = t.Description,
            }).ToArray()),
        })
        .ToArray());

    public IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources) => Snapshot()
        .Where(s => s.Status == "running" && (enabledSources is null || enabledSources.Contains(s.Name)))
        .SelectMany(s => s.Tools.Select(t => new ToolSpec(
            ApiName(s.Name, t.Name),
            s.Name,
            t.Name,
            t.Title,
            t.Description ?? "",
            InputSchemaOf(t))))
        .ToList();

    static JsonObject InputSchemaOf(McpClientTool tool)
    {
        var schema = JsonNode.Parse(tool.JsonSchema.GetRawText()) as JsonObject ?? [];
        schema["type"] ??= "object";
        return schema;
    }

    public bool Owns(string apiName) => Snapshot().Any(s => s.Tools.Any(t => ApiName(s.Name, t.Name) == apiName));

    public async Task<ToolCallResult> CallAsync(string apiName, JsonObject input, CancellationToken cancellationToken)
    {
        foreach (var state in Snapshot())
        {
            var tool = state.Tools.FirstOrDefault(t => ApiName(state.Name, t.Name) == apiName);
            if (tool is null || state.Client is null) continue;
            var args = input.ToDictionary(p => p.Key, p => (object?)JsonSerializer.SerializeToElement(p.Value));
            var result = await state.Client.CallToolAsync(tool.Name, args, cancellationToken: cancellationToken);
            return new ToolCallResult(ToClaudeContent(result), result.IsError == true);
        }
        return ToolRegistry.Error($"ツール {apiName} が見つかりません");
    }

    static JsonArray ToClaudeContent(CallToolResult result)
    {
        var content = new JsonArray();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock text:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;
                case ImageContentBlock image:
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = image.MimeType,
                            ["data"] = Convert.ToBase64String(image.DecodedData.ToArray()),
                        },
                    });
                    break;
                default:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = JsonSerializer.Serialize(block) });
                    break;
            }
        }
        if (content.Count == 0 && result.StructuredContent is { } structured)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = structured.ToString() });
        }
        // 空の tool_result は API に弾かれる
        if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = "(出力なし)" });
        return content;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var state in Snapshot()) await StopAsync(state);
    }
}
