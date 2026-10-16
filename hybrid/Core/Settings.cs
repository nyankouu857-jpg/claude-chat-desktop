using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeChat.Core;

public sealed class ProfileSettings
{
    public string FullName { get; set; } = "";
    public string Nickname { get; set; } = "";
    // claude.ai の個人設定と同じく、全会話のシステムプロンプトに入る
    public string Preferences { get; set; } = "";
    // null は未選択、空文字は入力に合わせる。どちらも書かれた言語で答える
    public string? Language { get; set; }
    // claude.ai の「あなたの仕事に最も近いものは?」
    public string Occupation { get; set; } = "";
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
    // 長い会話を要約し始める入力トークン数
    public int CompactThreshold { get; set; } = 150_000;
    public string SystemPrompt { get; set; } = "";
    // オフで日付・呼び名・フォルダ一覧を付けない
    public bool AppendAppContext { get; set; } = true;
    // 1 回のリクエストで検索や取得ができる回数
    public int WebSearchMaxUses { get; set; } = 5;
    public bool WebFetch { get; set; } = true;
    public int WebFetchMaxUses { get; set; } = 10;
    public bool CodeExecution { get; set; }
    // コード実行で Word / Excel / PowerPoint / PDF を作るときの公式スキル
    public bool OfficeSkills { get; set; } = true;
    public string OutputStyle { get; set; } = "default";
}

public sealed class AgentSettings
{
    public bool Enabled { get; set; } = true;
    // 料金と暴走の歯止め
    public int MaxConcurrent { get; set; } = 4;
    public int MaxWorkflowAgents { get; set; } = 10;
    public int MaxMessageHops { get; set; } = 10;
    // 親と話している最中に子の確認待ちが埋もれて止まるのを防ぐ
    public bool AutoApproveChildren { get; set; } = true;
    // 結果は親に渡し終えているので、一覧に残しても散らかるだけ
    public bool DeleteFinishedChildren { get; set; } = true;
}

public sealed class FeatureSettings
{
    public bool Memory { get; set; } = true;
    public bool ConfirmMemoryWrites { get; set; }
    public bool Artifacts { get; set; } = true;
    public bool ChatSearch { get; set; } = true;
    // claude.ai と同じく Skills を 1 つずつ止められる
    public List<string> DisabledSkills { get; set; } = [];
    // 確認なしモードは誤操作が怖いので、ここでオンにしたときだけ選べる
    public bool AllowBypassMode { get; set; }
}

public sealed class HookEntry
{
    // ツール名に対する正規表現。空ならすべて
    public string? Matcher { get; set; }
    public string Command { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
}

public sealed class FolderAccess
{
    public string Path { get; set; } = "";
    public bool ReadOnly { get; set; }
}

public sealed class TerminalSettings
{
    public bool Enabled { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class ProgramSettings
{
    public bool Enabled { get; set; }
}

public sealed class BrowserSettings
{
    public bool Enabled { get; set; }
    // 空なら Chrome、無ければ Edge を探す
    public string? ExecutablePath { get; set; }
    // dedicated: アプリ専用のプロフィール / extension: 拡張機能を通していつもの Chrome
    public string Mode { get; set; } = "dedicated";
}

public sealed class FileAccessSettings
{
    public bool Enabled { get; set; }
    public bool AutoApproveReads { get; set; } = true;
    public List<FolderAccess> Folders { get; set; } = [];
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
    public FileAccessSettings Files { get; set; } = new();
    public TerminalSettings Terminal { get; set; } = new();
    public ProgramSettings Programs { get; set; } = new();
    public BrowserSettings Browser { get; set; } = new();
    public AgentSettings Agents { get; set; } = new();
    public FeatureSettings Features { get; set; } = new();
    // PreToolUse / PostToolUse / UserPromptSubmit / Stop / Notification
    public Dictionary<string, List<HookEntry>> Hooks { get; set; } = [];
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

    // ツール一覧は同期で組み立てるため。起動時の GetAsync 以降は必ず入っている
    public AppSettings? Current => current;

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
