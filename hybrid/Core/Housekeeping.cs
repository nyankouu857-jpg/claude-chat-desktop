using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeChat.Core;

// 毎日 1 回の片付け。使っていないチャットのアーカイブと、データのバックアップ
public sealed class Housekeeping(AppPaths paths, SettingsStore settings, ConversationStore conversations, ChatHub hub, Action conversationsChanged, Action<string> backupFailed) : IDisposable
{
    // 起動直後は画面の読み込みと重ならないよう少し待つ
    static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
    static readonly TimeSpan BackupInterval = TimeSpan.FromHours(24);
    readonly CancellationTokenSource stopping = new();
    readonly SemaphoreSlim backingUp = new(1, 1);

    // 同じ置き場所を別の代表フォルダと共有しても、互いの zip を古いものとして消し合わないよう、代表フォルダごとに名前を分ける
    string BackupPrefix => "ClaudeChat-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(paths.Root).ToLowerInvariant())))[..8] + "-";

    public DateTime? LastSuccessAt { get; private set; }
    public string? LastError { get; private set; }

    public void Start() => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(FirstRunDelay, stopping.Token);
            using var timer = new PeriodicTimer(CheckInterval);
            do
            {
                await RunOnceAsync();
            }
            while (await timer.WaitForNextTickAsync(stopping.Token));
        }
        catch (OperationCanceledException)
        {
        }
    });

    async Task RunOnceAsync()
    {
        try
        {
            await ArchiveIdleAsync();
        }
        catch (Exception)
        {
            // 片付けに失敗してもアプリは止めない。次の回にまたやる
        }
        var backup = (await settings.GetAsync()).Backup;
        if (!backup.Enabled || (LastBackupAt(FolderOf(backup)) is { } last && DateTime.Now - last < BackupInterval)) return;
        try
        {
            await BackupAsync();
        }
        catch (Exception e)
        {
            // 取れていないまま日が過ぎないよう、続けて失敗しても最初の 1 回は知らせる
            var first = LastError is null;
            LastError = e.Message;
            if (first) backupFailed(e.Message);
        }
    }

    public async Task<int> ArchiveIdleAsync()
    {
        var days = (await settings.GetAsync()).Chat.ArchiveAfterDays;
        if (days <= 0) return 0;
        var before = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
        var count = 0;
        // スターを付けたもの、動いているもの、子のエージェントは残す
        foreach (var summary in (await conversations.ListAsync()).Where(s => !s.Archived && !s.Starred && s.ParentId is null && s.UpdatedAt < before).ToList())
        {
            if (await conversations.GetAsync(summary.Id) is not { } conversation || hub.StatusOf(conversation) != ChatHub.Idle) continue;
            conversation.Archived = true;
            await conversations.SaveAsync(conversation, touch: false);
            count++;
        }
        if (count > 0) conversationsChanged();
        return count;
    }

    static string FolderOf(BackupSettings backup)
    {
        if (backup.Folder.Trim() is { Length: > 0 } folder) return folder;
        // ドキュメントの場所が無い環境 (Linux の開発用ホストなど) ではホームに置く
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents.Length > 0 ? documents : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ClaudeChat-backups");
    }

    DateTime? LastBackupAt(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) return null;
        var times = Directory.EnumerateFiles(folder, BackupPrefix + "*.zip").Select(File.GetLastWriteTime).ToList();
        return times.Count > 0 ? times.Max() : null;
    }

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // 置き場所の条件。相対パスは起動のしかたで場所が変わり、代表フォルダの中はフォルダごと消えたときに一緒に消え、次のバックアップに自分も入る
    public static string? FolderProblem(string folder, string root)
    {
        if (folder.Trim().Length == 0) return null;
        if (!Path.IsPathFullyQualified(folder.Trim())) return "バックアップの置き場所は、ドライブから始まるフルパスで指定してください";
        var target = Normalize(folder.Trim()) + Path.DirectorySeparatorChar;
        return target.StartsWith(Normalize(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? "バックアップの置き場所は、アプリのフォルダの外にしてください" : null;
    }

    // config と data をひとつの zip にする。巻き戻し用の控えは大きく、無くても会話は困らないので入れない
    public async Task<string> BackupAsync()
    {
        var backup = (await settings.GetAsync()).Backup;
        var configured = FolderOf(backup);
        if (FolderProblem(configured, paths.Root) is { } problem) throw new InvalidOperationException(problem);
        var folder = Normalize(configured);
        var root = Normalize(paths.Root);
        await backingUp.WaitAsync();
        try
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, $"{BackupPrefix}{DateTime.Now:yyyy-MM-dd-HHmmss}.zip");
            var partial = target + ".partial";
            try
            {
                await WriteZipAsync(partial, root, folder);
            }
            catch
            {
                File.Delete(partial);
                throw;
            }
            File.Move(partial, target, overwrite: true);
            LastSuccessAt = DateTime.Now;
            LastError = null;
            foreach (var old in Directory.EnumerateFiles(folder, BackupPrefix + "*.zip").OrderByDescending(File.GetLastWriteTime).Skip(Math.Max(1, backup.Keep)))
            {
                File.Delete(old);
            }
            return target;
        }
        finally
        {
            backingUp.Release();
        }
    }

    async Task WriteZipAsync(string target, string root, string backupFolder)
    {
        await using var file = File.Create(target);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var dir in new[] { paths.ConfigDir, paths.DataDir })
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (path.StartsWith(paths.CheckpointsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(backupFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // 書き込み中のファイルも読めるように共有で開く
                    await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    await using var entry = zip.CreateEntry(Path.GetRelativePath(root, path).Replace('\\', '/'), CompressionLevel.Optimal).Open();
                    await source.CopyToAsync(entry);
                }
                catch (IOException)
                {
                    // 途中で消えたファイルは飛ばす
                }
            }
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        stopping.Dispose();
    }
}
