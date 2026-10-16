using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

// Claude Code の巻き戻しにならい、フォルダ操作で書き換える前の中身を発言ごとに取っておく。
// ターミナルやアプリが変えたものは追えない
public sealed class Checkpoints(AppPaths paths, IPlatform platform)
{
    // フォルダごと消すときの控え。これより大きいものは控えを取らず、戻せないと記録する
    const long MaxBackupBytes = 50L * 1024 * 1024;
    readonly Lock sync = new();

    sealed class Entry
    {
        public string Path { get; set; } = "";
        public bool Existed { get; set; }
        public bool IsDirectory { get; set; }
        public string? Backup { get; set; }
        public bool TooLarge { get; set; }
    }

    string TurnDir(string conversationId, int turn) => Path.Combine(paths.CheckpointsDir, conversationId, turn.ToString("D6"));

    // 同じ発言の中で最初に触る前の状態だけを残す
    public void Record(string path)
    {
        // シークレットチャットで触ったファイルの控えや一覧をディスクに残さない
        if (ToolContext.Current?.Conversation is not { Incognito: false } conversation) return;
        var turn = conversation.LastHumanIndex();
        if (turn < 0) return;
        lock (sync)
        {
            var dir = TurnDir(conversation.Id, turn);
            var manifestPath = Path.Combine(dir, "manifest.json");
            var entries = File.Exists(manifestPath) ? Json.ReadAsync<List<Entry>>(manifestPath).GetAwaiter().GetResult() ?? [] : [];
            if (entries.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
            var entry = new Entry { Path = path, Existed = File.Exists(path) || Directory.Exists(path), IsDirectory = Directory.Exists(path) };
            if (entry.Existed)
            {
                var backup = Path.Combine(dir, $"{entries.Count:D4}");
                Directory.CreateDirectory(dir);
                if (entry.IsDirectory)
                {
                    if (DirectorySize(path) > MaxBackupBytes) entry.TooLarge = true;
                    else CopyDirectory(path, backup);
                }
                else if (new FileInfo(path).Length > MaxBackupBytes)
                {
                    entry.TooLarge = true;
                }
                else
                {
                    File.Copy(path, backup);
                }
                if (!entry.TooLarge) entry.Backup = backup;
            }
            entries.Add(entry);
            Json.WriteAtomicAsync(manifestPath, entries).GetAwaiter().GetResult();
        }
    }

    public List<int> Turns(string conversationId)
    {
        var dir = Path.Combine(paths.CheckpointsDir, conversationId);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateDirectories(dir).Select(d => int.TryParse(Path.GetFileName(d), out var n) ? n : -1).Where(n => n >= 0).OrderBy(n => n).ToList();
    }

    // 指定した発言以降に変えたものを、新しい順に元へ戻す
    public JsonObject Restore(string conversationId, int fromTurn)
    {
        var restored = new JsonArray();
        var skipped = new JsonArray();
        lock (sync)
        {
            foreach (var turn in Turns(conversationId).Where(t => t >= fromTurn).OrderByDescending(t => t))
            {
                var dir = TurnDir(conversationId, turn);
                var entries = Json.ReadAsync<List<Entry>>(Path.Combine(dir, "manifest.json")).GetAwaiter().GetResult() ?? [];
                for (var i = entries.Count - 1; i >= 0; i--)
                {
                    var entry = entries[i];
                    try
                    {
                        if (entry.TooLarge)
                        {
                            skipped.Add($"{entry.Path} (大きすぎて控えが無い)");
                            continue;
                        }
                        // 今あるものはごみ箱へ。巻き戻しの巻き戻しもできるように
                        if (File.Exists(entry.Path) || Directory.Exists(entry.Path)) platform.MoveToTrash(entry.Path);
                        if (entry.Existed && entry.Backup is { } backup)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(entry.Path)!);
                            if (entry.IsDirectory) CopyDirectory(backup, entry.Path);
                            else File.Copy(backup, entry.Path, overwrite: true);
                        }
                        restored.Add(entry.Path);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        skipped.Add($"{entry.Path} ({e.Message})");
                    }
                }
                Directory.Delete(dir, recursive: true);
            }
        }
        return new JsonObject { ["restored"] = restored, ["skipped"] = skipped };
    }

    // 巻き戻しや編集で捨てた発言の控え。残すと、後で同じ番号に来た発言の控えとして誤って使われる
    public void ForgetFrom(string conversationId, int fromTurn)
    {
        lock (sync)
        {
            foreach (var turn in Turns(conversationId).Where(t => t >= fromTurn)) Directory.Delete(TurnDir(conversationId, turn), recursive: true);
        }
    }

    public void Forget(string conversationId)
    {
        var dir = Path.Combine(paths.CheckpointsDir, conversationId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static long DirectorySize(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => f.Length);

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var sub in Directory.EnumerateDirectories(source))
        {
            // リンクの先までは控えない
            if (new DirectoryInfo(sub).LinkTarget is not null) continue;
            CopyDirectory(sub, Path.Combine(destination, Path.GetFileName(sub)));
        }
    }
}
