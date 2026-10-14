using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeChat.Core;

public sealed class ProfileSettings
{
    public string FullName { get; set; } = "";
    public string Nickname { get; set; } = "";
    // claude.ai の個人設定と同じく、全会話のシステムプロンプトに入る
    public string Preferences { get; set; } = "";
}

public sealed class AppearanceSettings
{
    public string Theme { get; set; } = "system";
    public string ChatFont { get; set; } = "default";
}

public sealed class ChatDefaults
{
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "medium";
    public bool WebSearch { get; set; }
    public bool ShowThinking { get; set; } = true;
    public int MaxToolTurns { get; set; } = 30;
}

// Claude Desktop の claude_desktop_config.json と同じ書き方を受け付ける
public sealed class McpServerConfig
{
    public string? Type { get; set; }
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public string? Cwd { get; set; }
    public string? Url { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public bool Disabled { get; set; }

    [JsonIgnore]
    public bool IsRemote => !string.IsNullOrWhiteSpace(Url);
}

public sealed class AppSettings
{
    public ProfileSettings Profile { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public ChatDefaults Chat { get; set; } = new();
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = [];
    // ツールの API 名 -> "always"。毎回確認を出さないツールを覚えておく
    public Dictionary<string, string> ToolPermissions { get; set; } = [];

    // 手で書き足した未知の項目を、保存し直したときに消さないため
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SettingsStore(AppPaths paths)
{
    readonly SemaphoreSlim gate = new(1, 1);
    AppSettings? current;

    public async Task<AppSettings> GetAsync()
    {
        if (current is not null) return current;
        await gate.WaitAsync();
        try
        {
            current ??= await Json.ReadAsync<AppSettings>(paths.SettingsFile) ?? new AppSettings();
            if (!File.Exists(paths.SettingsFile)) await Json.WriteAtomicAsync(paths.SettingsFile, current);
            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AppSettings> UpdateAsync(Action<AppSettings> update)
    {
        var settings = await GetAsync();
        await gate.WaitAsync();
        try
        {
            update(settings);
            await Json.WriteAtomicAsync(paths.SettingsFile, settings);
            return settings;
        }
        finally
        {
            gate.Release();
        }
    }

    // エディタで settings.json を直したあとに読み直す
    public async Task<AppSettings> ReloadAsync()
    {
        await gate.WaitAsync();
        try
        {
            current = await Json.ReadAsync<AppSettings>(paths.SettingsFile) ?? new AppSettings();
            return current;
        }
        finally
        {
            gate.Release();
        }
    }
}
