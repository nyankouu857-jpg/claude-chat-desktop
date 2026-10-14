using System.Text;
using System.Text.Json.Nodes;

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
    readonly List<FileTool> fileTools;

    static FileToolProvider()
    {
        // Shift_JIS の読み書き
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public FileToolProvider(SettingsStore settings, IPlatform platform)
    {
        this.settings = settings;
        this.platform = platform;
        fileTools =
        [
            new("list_allowed_folders", "許可フォルダの確認", "操作を許可されているフォルダの一覧と、読み取り専用かどうかを返す。",
                true, Schema(), (_, access) => ListAllowedFolders(access)),
            new("list_directory", "フォルダの中身を見る", "フォルダ内のファイルとサブフォルダを一覧する。相対パスは最初の許可フォルダが基準。",
                true, Schema(("path", "string", "フォルダのパス", true)), ListDirectory),
            new("read_file", "ファイルを読む", "テキストファイルを行番号付きで読む。画像なら画像として返す。長いファイルは offset と limit で分けて読む。",
                true, Schema(("path", "string", "ファイルのパス", true), ("offset", "integer", "読み始める行番号。1 始まり", false), ("limit", "integer", "読む行数。既定 2000", false)), ReadFile),
            new("search_files", "ファイルを探す", "フォルダ以下を再帰的に探す。pattern でファイル名 (例: *.cs)、contains で中身の文字列を絞り込む。",
                true, Schema(("path", "string", "探し始めるフォルダ", true), ("pattern", "string", "ファイル名のワイルドカード。既定は *", false), ("contains", "string", "中身に含む文字列。大文字小文字は区別しない", false)), SearchFiles),
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
        ];
    }

    static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] fields) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(fields.Select(f => KeyValuePair.Create(f.Name, (JsonNode?)new JsonObject { ["type"] = f.Type, ["description"] = f.Description }))),
        ["required"] = new JsonArray(fields.Where(f => f.Required).Select(f => (JsonNode)f.Name).ToArray()),
        ["additionalProperties"] = false,
    };

    FileAccessSettings? ActiveAccess()
    {
        var access = settings.Current?.Files;
        return access is { Enabled: true } && access.Folders.Count > 0 ? access : null;
    }

    public IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources)
    {
        var access = ActiveAccess();
        if (access is null || (enabledSources is not null && !enabledSources.Contains(Source))) return [];
        return fileTools
            .Select(t => new ToolSpec(Prefix + t.Name, Source, t.Name, t.Title, t.Description, t.Schema, t.ReadOnly && access.AutoApproveReads))
            .ToList();
    }

    public bool Owns(string apiName) => apiName.StartsWith(Prefix);

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
    }

    static ToolCallResult Text(string text) => new(new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), false);

    static string Required(JsonObject input, string key) =>
        (string?)input[key] is { Length: > 0 } value ? value : throw new ToolException($"{key} を指定してください");

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    static bool IsInside(string path, string folder) =>
        path.Equals(folder, PathComparison) || path.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison);

    // 許可フォルダ外への脱出防止
    static string Resolve(string path, FileAccessSettings access, bool forWrite)
    {
        var baseFolder = Normalize(access.Folders[0].Path);
        var full = Normalize(Path.IsPathRooted(path) ? path : Path.Combine(baseFolder, path));
        var folder = access.Folders
            .Select(f => (Root: Normalize(f.Path), f.ReadOnly))
            .Where(f => IsInside(full, f.Root))
            .OrderByDescending(f => f.Root.Length)
            .FirstOrDefault();
        if (folder.Root is null) throw new ToolException($"{full} は許可されたフォルダの外です。list_allowed_folders で確認してください");
        if (forWrite && folder.ReadOnly) throw new ToolException($"{folder.Root} は読み取り専用です");

        // 途中のジャンクションも含めて確認
        for (var current = full; current.Length > folder.Root.Length; current = Path.GetDirectoryName(current)!)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists || info.LinkTarget is null) continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is null || !IsInside(Normalize(target), folder.Root)) throw new ToolException($"{current} は許可フォルダの外を指すリンクです");
        }
        return full;
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
            // 古い Windows のテキストファイル
            return Encoding.GetEncoding(932);
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
            if (builder.Length > MaxReadChars)
            {
                last = i;
                break;
            }
            builder.Append(i + 1).Append('\t').Append(lines[i].TrimEnd('\r')).Append('\n');
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
        var contains = (string?)input["contains"];
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
            if (IsSkipped(file, root)) continue;
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
                if (lines[i].Contains(contains, StringComparison.OrdinalIgnoreCase)) hits.Add($"{Display(file, access)}:{i + 1}: {lines[i].Trim()}");
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

    static ToolCallResult WriteFile(JsonObject input, FileAccessSettings access)
    {
        var path = Resolve(Required(input, "path"), access, forWrite: true);
        var content = (string?)input["content"] ?? throw new ToolException("content を指定してください");
        if (Directory.Exists(path)) throw new ToolException($"{path} はフォルダです");
        var existed = File.Exists(path);
        var encoding = existed ? DetectEncoding(File.ReadAllBytes(path)) : new UTF8Encoding(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding);
        return Text($"{Display(path, access)} を{(existed ? "上書き" : "作成")}しました ({EncodingName(encoding)}、{content.Split('\n').Length} 行)");
    }

    static ToolCallResult EditFile(JsonObject input, FileAccessSettings access)
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
        File.WriteAllText(path, edited, encoding);
        return Text($"{Display(path, access)} の {count} 箇所を書き換えました");
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

    static ToolCallResult CreateDirectory(JsonObject input, FileAccessSettings access)
    {
        var path = Resolve(Required(input, "path"), access, forWrite: true);
        var existed = Directory.Exists(path);
        Directory.CreateDirectory(path);
        return Text(existed ? $"{Display(path, access)} は既にあります" : $"{Display(path, access)} を作成しました");
    }

    static void RejectAllowedRoot(string path, FileAccessSettings access)
    {
        if (access.Folders.Any(f => Normalize(f.Path).Equals(path, PathComparison))) throw new ToolException("許可フォルダそのものは移動や削除ができません");
    }

    static ToolCallResult MovePath(JsonObject input, FileAccessSettings access)
    {
        var source = Resolve(Required(input, "source"), access, forWrite: true);
        var destination = Resolve(Required(input, "destination"), access, forWrite: true);
        RejectAllowedRoot(source, access);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new ToolException($"{destination} は既にあります");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else if (File.Exists(source)) File.Move(source, destination);
        else throw new ToolException($"{source} はありません");
        return Text($"{Display(source, access)} を {Display(destination, access)} に移動しました");
    }

    static ToolCallResult CopyFile(JsonObject input, FileAccessSettings access)
    {
        var source = Resolve(Required(input, "source"), access, forWrite: false);
        var destination = Resolve(Required(input, "destination"), access, forWrite: true);
        if (!File.Exists(source)) throw new ToolException($"{source} というファイルはありません");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new ToolException($"{destination} は既にあります");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
        return Text($"{Display(source, access)} を {Display(destination, access)} にコピーしました");
    }

    ToolCallResult DeletePath(JsonObject input, FileAccessSettings access)
    {
        var path = Resolve(Required(input, "path"), access, forWrite: true);
        RejectAllowedRoot(path, access);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new ToolException($"{path} はありません");
        platform.MoveToTrash(path);
        return Text($"{Display(path, access)} をごみ箱へ移しました");
    }

    public static string? DescribeForPrompt(FileAccessSettings access)
    {
        if (!access.Enabled || access.Folders.Count == 0) return null;
        var folders = string.Join("\n", access.Folders.Select(f => $"- {Normalize(f.Path)}{(f.ReadOnly ? " (読み取り専用)" : "")}"));
        return $"ユーザーの PC の次のフォルダを files__ で始まるツールで読み書きできます。相対パスは最初のフォルダが基準です。\n{folders}";
    }
}
