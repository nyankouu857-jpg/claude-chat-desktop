namespace ClaudeChat.Core;

// exe を置いたフォルダを代表フォルダとし、設定もデータもすべてその配下に置く
public sealed class AppPaths
{
    public string Root { get; }
    public bool IsFallback { get; }

    public string ConfigDir => Path.Combine(Root, "config");
    public string DataDir => Path.Combine(Root, "data");
    public string LogsDir => Path.Combine(Root, "logs");
    public string CacheDir => Path.Combine(Root, "cache");

    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public string SecretsFile => Path.Combine(ConfigDir, "secrets.json");
    public string PricingFile => Path.Combine(ConfigDir, "pricing.json");
    public string ConversationsDir => Path.Combine(DataDir, "conversations");
    public string UsageDir => Path.Combine(DataDir, "usage");

    AppPaths(string root, bool isFallback)
    {
        Root = root;
        IsFallback = isFallback;
        foreach (var dir in new[] { ConfigDir, DataDir, LogsDir, CacheDir, ConversationsDir, UsageDir }) Directory.CreateDirectory(dir);
    }

    public static AppPaths Resolve(string? root = null)
    {
        var preferred = root ?? AppContext.BaseDirectory;
        if (CanWrite(preferred)) return new AppPaths(preferred, false);
        // Program Files など書き込めない場所に置かれたときだけユーザー領域へ逃がす
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeChat");
        return new AppPaths(fallback, true);
    }

    static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
