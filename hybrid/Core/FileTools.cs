using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ClaudeChat.Core.ToolJson;

namespace ClaudeChat.Core;

// 許可フォルダ限定の組み込みツール
public sealed class FileToolProvider : IToolProvider
{
    public const string Source = "files";
    const string Prefix = "files__";
    // API に載せる量の上限
    const int MaxReadChars = 200_000;
    const int DefaultReadLines = 2000;
    const long MaxTextFileBytes = 20 * 1024 * 1024;
    // API の画像上限
    const long MaxImageBytes = 5 * 1024 * 1024;
    const int MaxListEntries = 500;
    const int MaxSearchHits = 200;
    // 中身検索で開くファイルの上限
    const long MaxGrepFileBytes = 2 * 1024 * 1024;

    static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", ".vs", "bin", "obj" };
    static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };
    static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    sealed record FileTool(string Name, string Title, string Description, bool ReadOnly, JsonObject Schema, Func<JsonObject, FileAccessSettings, ToolCallResult> Run);

    readonly SettingsStore settings;
    readonly IPlatform platform;
    readonly Checkpoints checkpoints;
    readonly List<FileTool> fileTools;

    static FileToolProvider()
    {
        // Shift_JIS の読み書き
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    // アプリの代表フォルダ。ここの設定や拡張機能のファイルを書き換えられると、確認なしにツールを許可したり、
    // いつもの Chrome を自由に動かす拡張機能に差し替えたりできてしまう。ほかのチャットの中身も読ませない
    static string? appRoot;

    public FileToolProvider(SettingsStore settings, IPlatform platform, Checkpoints checkpoints, AppPaths paths)
    {
        appRoot = Normalize(paths.Root);
        this.settings = settings;
        this.platform = platform;
        this.checkpoints = checkpoints;
        fileTools =
        [
            new("list_allowed_folders", "許可フォルダの確認", "操作を許可されているフォルダの一覧と、読み取り専用かどうかを返す。",
                true, Schema(), (_, access) => ListAllowedFolders(access)),
            new("list_directory", "フォルダの中身を見る", "フォルダ内のファイルとサブフォルダを一覧する。相対パスは最初の許可フォルダが基準。",
                true, Schema(("path", "string", "フォルダのパス", true)), ListDirectory),
            new("read_file", "ファイルを読む", "テキストファイルを行番号付きで読む。画像なら画像として返す。長いファイルは offset と limit で分けて読む。",
                true, Schema(("path", "string", "ファイルのパス", true), ("offset", "integer", "読み始める行番号。1 始まり", false), ("limit", "integer", "読む行数。既定 2000", false)), ReadFile),
            new("search_files", "ファイルを探す", "フォルダ以下を再帰的に探す。pattern でファイル名 (例: *.cs)、contains で中身の文字列を絞り込む。",
                true, Schema(("path", "string", "探し始めるフォルダ", true), ("pattern", "string", "ファイル名のワイルドカード。既定は *", false), ("contains", "string", "中身に含む文字列。大文字小文字は区別しない", false), ("regex", "boolean", "contains を正規表現として扱う", false)), SearchFiles),
            new("get_file_info", "ファイルの情報", "ファイルやフォルダの大きさ、作成日時、更新日時を返す。",
                true, Schema(("path", "string", "対象のパス", true)), GetFileInfo),
            new("write_file", "ファイルに書き込む", "ファイルを新しく作るか、中身を丸ごと置き換える。親フォルダが無ければ作る。既存ファイルは元の文字コードを保つ。",
                false, Schema(("path", "string", "ファイルのパス", true), ("content", "string", "書き込む内容", true)), WriteFile),
            new("edit_file", "ファイルを部分的に直す", "ファイル内の old_text を new_text に置き換える。old_text はファイル内で一意になるよう前後を含めて指定する。",
                false, Schema(("path", "string", "ファイルのパス", true), ("old_text", "string", "置き換える前の文字列", true), ("new_text", "string", "置き換えた後の文字列", true), ("replace_all", "boolean", "一致した箇所をすべて置き換える", false)), EditFile),
            new("create_directory", "フォルダを作る", "フォルダを作る。途中のフォルダもまとめて作る。",
                false, Schema(("path", "string", "作るフォルダのパス", true)), CreateDirectory),
            new("move_path", "移動・名前の変更", "ファイルやフォルダを移動する。名前の変更にも使う。移動先が既にあるときは失敗する。",
                false, Schema(("source", "string", "移動元", true), ("destination", "string", "移動先", true)), MovePath),
            new("copy_file", "ファイルをコピー", "ファイルをコピーする。コピー先が既にあるときは失敗する。",
                false, Schema(("source", "string", "コピー元", true), ("destination", "string", "コピー先", true)), CopyFile),
            new("delete_path", "削除 (ごみ箱へ)", "ファイルやフォルダをごみ箱へ移す。",
                false, Schema(("path", "string", "削除するパス", true)), DeletePath),
            new("create_worktree", "ワークツリーを作る", "最初の許可フォルダが git リポジトリのとき、別のブランチの作業用コピー (git worktree) を .claude/worktrees/<name> に作り、このチャットの作業場所をそこへ切り替える。本体を汚さずに試したいときに使う。",
                false, Schema(("name", "string", "ワークツリーの名前 (英数字と - _)", true)), CreateWorktree),
            new("exit_worktree", "ワークツリーを抜ける", "このチャットの作業場所を元のフォルダに戻す。remove を true にすると、変更が無い場合に限りワークツリーを消す。",
                false, Schema(("remove", "boolean", "ワークツリーを消すか", false)), ExitWorktree),
        ];
    }

    // ワークツリーにいるチャットは、そこを最初の (相対パスの基準の) フォルダとして扱う
    FileAccessSettings? ActiveAccess()
    {
        var access = settings.Current?.Files;
        if (access is not { Enabled: true } || access.Folders.Count == 0) return null;
        return WithWorktree(access, ToolContext.Current?.Conversation.WorktreePath);
    }

    static FileAccessSettings WithWorktree(FileAccessSettings access, string? worktree)
    {
        if (string.IsNullOrEmpty(worktree) || !Directory.Exists(worktree)) return access;
        return new FileAccessSettings
        {
            Enabled = access.Enabled,
            AutoApproveReads = access.AutoApproveReads,
            Folders = [new FolderAccess { Path = worktree }, .. access.Folders],
        };
    }

    public IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources)
    {
        var access = ActiveAccess();
        if (access is null || (enabledSources is not null && !enabledSources.Contains(Source))) return [];
        return fileTools
            .Select(t => new ToolSpec(Prefix + t.Name, Source, t.Name, t.Title, t.Description, t.Schema, t.ReadOnly && access.AutoApproveReads, ReadOnly: t.ReadOnly))
            .ToList();
    }

    public Task<ToolCallResult> CallAsync(string apiName, JsonObject input, CancellationToken cancellationToken)
    {
        var access = ActiveAccess() ?? throw new ToolException("フォルダ操作は設定で無効になっています");
        var tool = fileTools.FirstOrDefault(t => Prefix + t.Name == apiName) ?? throw new ToolException($"ツール {apiName} はありません");
        try
        {
            return Task.FromResult(tool.Run(input, access));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ToolException(e.Message);
        }
        catch (EncoderFallbackException)
        {
            throw new ToolException("このファイルの文字コードでは書けない文字が含まれています。その文字を使わずに書き直してください");
        }
    }

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // ドライブ直下 (D:\) は末尾の区切りが残るので二重にしない
    static bool IsInside(string path, string folder)
    {
        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.Equals(folder, PathComparison) || path.StartsWith(prefix, PathComparison);
    }

    // 一番内側の許可フォルダ。読み取り専用はこれで決まる
    static (string? Root, bool ReadOnly) FolderOf(string full, FileAccessSettings access) => access.Folders
        .Select(f => ((string?)Normalize(f.Path), f.ReadOnly))
        .Where(f => IsInside(full, f.Item1!))
        .OrderByDescending(f => f.Item1!.Length)
        .FirstOrDefault();

    // 許可フォルダ外への脱出防止
    static string Resolve(string path, FileAccessSettings access, bool forWrite)
    {
        var baseFolder = Normalize(access.Folders[0].Path);
        var full = Normalize(Path.IsPathRooted(path) ? path : Path.Combine(baseFolder, path));
        var folder = FolderOf(full, access);
        if (folder.Root is null) throw new ToolException($"{full} は許可されたフォルダの外です。list_allowed_folders で確認してください");
        if (forWrite && folder.ReadOnly) throw new ToolException($"{folder.Root} は読み取り専用です");
        if (appRoot is not null && (IsInside(full, appRoot) || (forWrite && IsInside(appRoot, full)))) throw new ToolException("ClaudeChat 自身のフォルダは読み書きできません");
        // .git のフックや設定を書き換えられると、次の git 操作で確認なしにコードが動く
        if (forWrite && full[folder.Root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolException(".git の中は書き換えられません");
        }

        for (var current = full; current.Length > folder.Root.Length; current = Path.GetDirectoryName(current)!)
        {
            RejectShortName(current);
            // 途中のジャンクションも含めて確認
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists || info.LinkTarget is null) continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            var targetFolder = target is null ? default : FolderOf(Normalize(target), access);
            if (targetFolder.Root is null) throw new ToolException($"{current} は許可フォルダの外を指すリンクです");
            if (appRoot is not null && IsInside(Normalize(target!), appRoot)) throw new ToolException("ClaudeChat 自身のフォルダは読み書きできません");
            if (forWrite && targetFolder.ReadOnly) throw new ToolException($"{current} は読み取り専用フォルダを指すリンクです");
        }
        return full;
    }

    // DOCS~1 のような 8.3 形式の別名だと、文字列の比較で読み取り専用フォルダをすり抜けられる
    static void RejectShortName(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.Contains('~') || (!File.Exists(path) && !Directory.Exists(path))) return;
        // 列挙は本当の名前としか一致しない
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.EnumerateFileSystemEntries(parent, name).Any()) throw new ToolException($"{path} は短い形式の名前です。本来のフォルダ名やファイル名で指定してください");
    }

    static string Display(string full, FileAccessSettings access)
    {
        var baseFolder = Normalize(access.Folders[0].Path);
        return IsInside(full, baseFolder) && full.Length > baseFolder.Length ? Path.GetRelativePath(baseFolder, full) : full;
    }

    static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

    static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;
        try
        {
            new UTF8Encoding(false, true).GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
        }
        // 古い Windows のテキストファイル。書けない文字は ? にせず失敗させる
        var shiftJis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        try
        {
            shiftJis.GetString(bytes);
            return shiftJis;
        }
        catch (DecoderFallbackException)
        {
            // どれでもなければ、バイトをそのまま往復できる Latin-1 で扱う
            return Encoding.Latin1;
        }
    }

    static bool LooksBinary(byte[] bytes, Encoding encoding)
    {
        if (encoding is UnicodeEncoding) return false;
        // 先頭 8KB で判定
        return bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0);
    }

    static (string Text, Encoding Encoding) ReadText(string path)
    {
        if (new FileInfo(path).Length > MaxTextFileBytes) throw new ToolException($"{Size(MaxTextFileBytes)} を超えるファイルは読めません");
        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes);
        if (LooksBinary(bytes, encoding)) throw new ToolException("バイナリファイルのため読めません");
        var preamble = encoding.GetPreamble().Length;
        var hasPreamble = preamble > 0 && bytes.AsSpan().StartsWith(encoding.GetPreamble());
        return (encoding.GetString(bytes, hasPreamble ? preamble : 0, bytes.Length - (hasPreamble ? preamble : 0)), encoding);
    }

    static string EncodingName(Encoding encoding) => encoding switch
    {
        UTF8Encoding utf8 when utf8.GetPreamble().Length > 0 => "UTF-8 (BOM 付き)",
        UTF8Encoding => "UTF-8",
        UnicodeEncoding => "UTF-16",
        _ when encoding.CodePage == 932 => "Shift_JIS",
        _ => encoding.WebName,
    };

    // Tools

    static ToolCallResult ListAllowedFolders(FileAccessSettings access) =>
        Text(string.Join("\n", access.Folders.Select((f, i) =>
            $"{Normalize(f.Path)}{(f.ReadOnly ? " (読み取り専用)" : "")}{(i == 0 ? " (相対パスの基準)" : "")}")));

    static ToolCallResult ListDirectory(JsonObject input, FileAccessSettings access)
    {
        var path = Resolve(Required(input, "path"), access, forWrite: false);
        if (!Directory.Exists(path)) throw new ToolException($"{path} というフォルダはありません");
        var entries = new DirectoryInfo(path).EnumerateFileSystemInfos()
            .OrderBy(e => e is FileInfo)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxListEntries + 1)
            .ToList();
        var lines = entries.Take(MaxListEntries).Select(e => e is FileInfo file
            ? $"[FILE] {file.Name}  {Size(file.Length)}  {file.LastWriteTime:yyyy-MM-dd HH:mm}"
            : $"[DIR]  {e.Name}");
        var footer = entries.Count > MaxListEntries ? $"\n... ほかにもあります。{MaxListEntries} 件までを表示しました" : "";
        return Text($"{path}\n{(entries.Count == 0 ? "(空のフォルダ)" : string.Join("\n", lines))}{footer}");
    }

    static ToolCallResult ReadFile(JsonObject input, FileAccessSettings access)
    {
        var path = Resolve(Required(input, "path"), access, forWrite: false);
        if (!File.Exists(path)) throw new ToolException($"{path} というファイルはありません");

        if (ImageTypes.TryGetValue(Path.GetExtension(path), out var mediaType))
        {
            var info = new FileInfo(path);
            if (info.Length > MaxImageBytes) throw new ToolException($"画像が {Size(MaxImageBytes)} を超えているので読めません");
            return new ToolCallResult(new JsonArray(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = Convert.ToBase64String(File.ReadAllBytes(path)) },
            }), false);
        }

        var (text, encoding) = ReadText(path);
        var lines = text.Split('\n');
        var offset = Math.Max(1, (int?)input["offset"] ?? 1);
        var limit = Math.Max(1, (int?)input["limit"] ?? DefaultReadLines);
        var builder = new StringBuilder();
        var last = Math.Min(lines.Length, offset - 1 + limit);
        for (var i = offset - 1; i < last; i++)
        {
            var room = MaxReadChars - builder.Length;
            if (room <= 0)
            {
                last = i;
                break;
            }
            // 圧縮された JS など 1 行が巨大なファイル
            var line = lines[i].TrimEnd('\r');
            if (line.Length > room) line = line[..room] + " ... (この行は長すぎるので途中まで)";
            builder.Append(i + 1).Append('\t').Append(line).Append('\n');
        }
        var header = $"{Display(path, access)}  {lines.Length} 行  {EncodingName(encoding)}";
        var footer = last < lines.Length ? $"\n... {last} 行目まで表示しました。続きは offset={last + 1} で読んでください" : "";
        return Text($"{header}\n{builder}{footer}");
    }

    static bool IsSkipped(string path, string root) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).SkipLast(1).Any(SkippedDirectories.Contains);

    static ToolCallResult SearchFiles(JsonObject input, FileAccessSettings access)
    {
        var root = Resolve(Required(input, "path"), access, forWrite: false);
        if (!Directory.Exists(root)) throw new ToolException($"{root} というフォルダはありません");
        var pattern = (string?)input["pattern"] is { Length: > 0 } p ? p : "*";
        // パターンに .. やフォルダが入ると、列挙の起点が許可フォルダの外へずれる
        if (pattern != Path.GetFileName(pattern) || pattern.Contains("..")) throw new ToolException("pattern にはファイル名のワイルドカード (例: *.cs) だけを指定してください");
        var contains = (string?)input["contains"];
        Regex? regex = null;
        if ((bool?)input["regex"] == true && !string.IsNullOrEmpty(contains))
        {
            try
            {
                // 書き方しだいで延々と戻り続ける式があるので、1 行あたりの時間を区切る
                regex = new Regex(contains, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException e)
            {
                throw new ToolException($"正規表現が正しくありません: {e.Message}");
            }
        }
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            // リンク経由で許可フォルダ外へ出ない
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, pattern, options))
        {
            if (hits.Count >= MaxSearchHits) break;
            if (!IsInside(Normalize(file), root) || IsSkipped(file, root)) continue;
            if (string.IsNullOrEmpty(contains))
            {
                hits.Add(Display(file, access));
                continue;
            }
            if (new FileInfo(file).Length > MaxGrepFileBytes || ImageTypes.ContainsKey(Path.GetExtension(file))) continue;
            string text;
            try
            {
                text = ReadText(file).Text;
            }
            catch (ToolException)
            {
                continue;
            }
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length && hits.Count < MaxSearchHits; i++)
            {
                bool matched;
                try
                {
                    matched = regex?.IsMatch(lines[i]) ?? lines[i].Contains(contains, StringComparison.OrdinalIgnoreCase);
                }
                catch (RegexMatchTimeoutException)
                {
                    throw new ToolException("正規表現の照合に時間がかかりすぎました。もっと単純な式にしてください");
                }
                if (matched) hits.Add($"{Display(file, access)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        if (hits.Count == 0) return Text("見つかりませんでした");
        var footer = hits.Count >= MaxSearchHits ? $"\n... {MaxSearchHits} 件で打ち切りました。条件を絞ってください" : "";
        return Text(string.Join("\n", hits) + footer);
    }

    static ToolCallResult GetFileInfo(JsonObject input, FileAccessSettings access)
    {
        var path = Resolve(Required(input, "path"), access, forWrite: false);
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists) throw new ToolException($"{path} はありません");
        var kind = info is FileInfo file ? $"ファイル  {Size(file.Length)}" : "フォルダ";
        return Text($"{path}\n種類: {kind}\n作成: {info.CreationTime:yyyy-MM-dd HH:mm:ss}\n更新: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
    }

    ToolCallResult WriteFile(JsonObject input, FileAccessSettings access)
    {
        var path = Resolve(Required(input, "path"), access, forWrite: true);
        var content = (string?)input["content"] ?? throw new ToolException("content を指定してください");
        if (Directory.Exists(path)) throw new ToolException($"{path} はフォルダです");
        var existed = File.Exists(path);
        var encoding = existed ? DetectEncoding(File.ReadAllBytes(path)) : new UTF8Encoding(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        checkpoints.Record(path);
        WriteText(path, content, encoding);
        return Text($"{Display(path, access)} を{(existed ? "上書き" : "作成")}しました ({EncodingName(encoding)}、{content.Split('\n').Length} 行)");
    }

    ToolCallResult EditFile(JsonObject input, FileAccessSettings access)
    {
        var path = Resolve(Required(input, "path"), access, forWrite: true);
        if (!File.Exists(path)) throw new ToolException($"{path} というファイルはありません");
        var oldText = Required(input, "old_text");
        var newText = (string?)input["new_text"] ?? throw new ToolException("new_text を指定してください");
        var replaceAll = (bool?)input["replace_all"] == true;
        var (text, encoding) = ReadText(path);

        // CRLF のファイル対策
        if (!text.Contains(oldText) && text.Contains("\r\n"))
        {
            oldText = oldText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            newText = newText.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
        var count = CountOccurrences(text, oldText);
        if (count == 0) throw new ToolException("old_text がファイル内に見つかりません。read_file で今の中身を確認してください");
        if (count > 1 && !replaceAll) throw new ToolException($"old_text が {count} 箇所に一致しました。前後の行を含めて一意にするか、replace_all を true にしてください");

        var edited = replaceAll ? text.Replace(oldText, newText) : ReplaceFirst(text, oldText, newText);
        checkpoints.Record(path);
        WriteText(path, edited, encoding);
        return Text($"{Display(path, access)} の {count} 箇所を書き換えました");
    }

    // 先にバイト列にしておく。書けない文字で途中で失敗すると、元のファイルが空になる
    static void WriteText(string path, string text, Encoding encoding)
    {
        var bytes = encoding.GetBytes(text);
        using var stream = File.Create(path);
        stream.Write(encoding.GetPreamble());
        stream.Write(bytes);
    }

    static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    static string ReplaceFirst(string text, string oldText, string newText)
    {
        var at = text.IndexOf(oldText, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, at), newText, text.AsSpan(at + oldText.Length));
    }

    ToolCallResult CreateDirectory(JsonObject input, FileAccessSettings access)
    {
        var path = Resolve(Required(input, "path"), access, forWrite: true);
        var existed = Directory.Exists(path);
        if (!existed) checkpoints.Record(path);
        Directory.CreateDirectory(path);
        return Text(existed ? $"{Display(path, access)} は既にあります" : $"{Display(path, access)} を作成しました");
    }

    // 中に許可フォルダ (読み取り専用を含む) を抱えたフォルダも、まとめて消えないように止める
    static void RejectAllowedRoot(string path, FileAccessSettings access)
    {
        if (access.Folders.Any(f => IsInside(Normalize(f.Path), path))) throw new ToolException("許可フォルダそのものや、許可フォルダを含むフォルダは移動や削除ができません");
    }

    ToolCallResult MovePath(JsonObject input, FileAccessSettings access)
    {
        var source = Resolve(Required(input, "source"), access, forWrite: true);
        var destination = Resolve(Required(input, "destination"), access, forWrite: true);
        RejectAllowedRoot(source, access);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new ToolException($"{destination} は既にあります");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        checkpoints.Record(source);
        checkpoints.Record(destination);
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else if (File.Exists(source)) File.Move(source, destination);
        else throw new ToolException($"{source} はありません");
        return Text($"{Display(source, access)} を {Display(destination, access)} に移動しました");
    }

    ToolCallResult CopyFile(JsonObject input, FileAccessSettings access)
    {
        var source = Resolve(Required(input, "source"), access, forWrite: false);
        var destination = Resolve(Required(input, "destination"), access, forWrite: true);
        if (!File.Exists(source)) throw new ToolException($"{source} というファイルはありません");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new ToolException($"{destination} は既にあります");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        checkpoints.Record(destination);
        File.Copy(source, destination);
        return Text($"{Display(source, access)} を {Display(destination, access)} にコピーしました");
    }

    ToolCallResult DeletePath(JsonObject input, FileAccessSettings access)
    {
        var path = Resolve(Required(input, "path"), access, forWrite: true);
        RejectAllowedRoot(path, access);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new ToolException($"{path} はありません");
        checkpoints.Record(path);
        platform.MoveToTrash(path);
        return Text($"{Display(path, access)} をごみ箱へ移しました");
    }

    // ワークツリーの名前に使える文字
    static readonly System.Text.RegularExpressions.Regex WorktreeName = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,40}$");

    ToolCallResult CreateWorktree(JsonObject input, FileAccessSettings access)
    {
        var conversation = ToolContext.Current?.Conversation ?? throw new ToolException("チャットの外からは使えません");
        if (conversation.WorktreePath is not null) throw new ToolException($"すでにワークツリー {conversation.WorktreePath} にいます。exit_worktree で抜けてから作ってください");
        var name = Required(input, "name");
        if (!WorktreeName.IsMatch(name)) throw new ToolException("名前は英数字と - _ だけで、40 文字までにしてください");
        var original = settings.Current?.Files.Folders.FirstOrDefault()?.Path ?? throw new ToolException("許可フォルダがありません");
        var repo = Git(original, "rev-parse", "--show-toplevel").Trim();
        var root = Normalize(repo);
        var repoFolder = FolderOf(root, access);
        if (repoFolder.Root is null) throw new ToolException("リポジトリの場所が許可フォルダの外です");
        if (repoFolder.ReadOnly) throw new ToolException("読み取り専用フォルダではワークツリーを作れません");
        var path = Path.Combine(root, ".claude", "worktrees", name);
        if (Directory.Exists(path)) throw new ToolException($"{path} はすでにあります");
        Git(root, "worktree", "add", "-b", $"claude/{name}", path);
        conversation.WorktreePath = path;
        return Text($"ワークツリーを {path} に作り、ブランチ claude/{name} に切り替えました。このチャットのファイル操作とコマンドは、ここが基準になります");
    }

    ToolCallResult ExitWorktree(JsonObject input, FileAccessSettings access)
    {
        var conversation = ToolContext.Current?.Conversation ?? throw new ToolException("チャットの外からは使えません");
        var path = conversation.WorktreePath ?? throw new ToolException("ワークツリーにはいません");
        var note = "";
        if ((bool?)input["remove"] == true)
        {
            // 変更が残っていると git が断るので、消えてほしくない作業は守られる
            Git(path, "worktree", "remove", path);
            note = "。ワークツリーを消しました (ブランチは残っています)";
        }
        conversation.WorktreePath = null;
        return Text($"元のフォルダに戻りました{note}");
    }

    static string Git(string directory, params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // リポジトリ側のフックや fsmonitor は Claude が仕込めた可能性があるので動かさない
        var noHooks = Path.Combine(Path.GetTempPath(), "claudechat-no-git-hooks");
        Directory.CreateDirectory(noHooks);
        foreach (var arg in new[] { "-c", $"core.hooksPath={noHooks}", "-c", "core.fsmonitor=false" }.Concat(args)) info.ArgumentList.Add(arg);
        try
        {
            using var process = System.Diagnostics.Process.Start(info) ?? throw new ToolException("git を起動できませんでした");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            // 大きなリポジトリでも作業用コピーは 1 分あれば作れる
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                throw new ToolException("git が時間内に終わりませんでした");
            }
            if (process.ExitCode != 0) throw new ToolException($"git が失敗しました: {stderr.Result.Trim()}");
            return stdout.Result;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ToolException("git が見つかりません。Git for Windows を入れてください");
        }
    }

    public static string? DescribeForPrompt(FileAccessSettings access, string? worktree = null)
    {
        if (!access.Enabled || access.Folders.Count == 0) return null;
        access = WithWorktree(access, worktree);
        var folders = string.Join("\n", access.Folders.Select(f => $"- {Normalize(f.Path)}{(f.ReadOnly ? " (read-only)" : "")}"));
        return $"<file_access>\nYou can read and write files on the user's PC with the tools whose names start with files__, but only inside the folders below. Relative paths resolve against the first folder. Deleted files go to the recycle bin.\n{folders}\n</file_access>";
    }
}
