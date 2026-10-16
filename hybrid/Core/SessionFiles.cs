using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

public sealed class SessionFile
{
    public string Id { get; set; } = "";
    // セッションのフォルダからの相対パス。区切りは /
    public string Path { get; set; } = "";
    public long Size { get; set; }
    // 書いた会話。サブエージェントなら子の ID
    public string? WrittenBy { get; set; }
    public string? WrittenByTitle { get; set; }
    public long UpdatedAt { get; set; }
}

// 1 セッションに 1 フォルダ。サブエージェントやワークフローの子は親のフォルダを使うので、子が消えても作ったファイルは残る。
// フォルダは会話を消したときだけ消す
public sealed class SessionFiles(AppPaths paths, ConversationStore conversations)
{
    const string RegistryName = ".files.json";
    // 1 ファイルの上限。コードや長い文書の受け渡しには足り、会話に貼るには大きすぎる量
    const int MaxFileChars = 2_000_000;
    // 1 セッションのフォルダの上限
    const long MaxFolderBytes = 200L * 1024 * 1024;
    // 読むときに一度に返す上限。会話の文脈を埋めないため
    const int MaxReadChars = 100_000;

    readonly SemaphoreSlim gate = new(1, 1);
    // file_id -> セッション (親の会話) の ID。起動して初めて探すときにフォルダを全部見て作る
    Dictionary<string, string>? owners;

    public static string IncognitoSessionsDir => System.IO.Path.Combine(AppPaths.IncognitoFilesDir, "sessions");

    // 共有したい相手のファイルを読んでよいか。ChatHub のチャット同士の通信の決まりに合わせる
    public Func<Conversation, Conversation, Task<bool>>? CanShare { get; set; }
    public Action<string>? Changed { get; set; }

    public async Task<Conversation> RootOfAsync(Conversation conversation)
    {
        // 子はさらに子を作れないが、壊れたデータで回り続けないよう上限を置く
        for (var depth = 0; depth < 3 && conversation.ParentId is { } parentId && await conversations.GetAsync(parentId) is { } parent; depth++) conversation = parent;
        return conversation;
    }

    public string DirOf(Conversation root) => System.IO.Path.Combine(root.Incognito ? IncognitoSessionsDir : paths.SessionsDir, root.Id);

    static string RegistryPath(string dir) => System.IO.Path.Combine(dir, RegistryName);

    static async Task<List<SessionFile>> LoadAsync(string dir) => await Json.ReadAsync<List<SessionFile>>(RegistryPath(dir)) ?? [];

    async Task<Dictionary<string, string>> OwnersAsync()
    {
        if (owners is not null) return owners;
        owners = [];
        if (Directory.Exists(paths.SessionsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(paths.SessionsDir))
            {
                foreach (var file in await LoadAsync(dir)) owners[file.Id] = System.IO.Path.GetFileName(dir);
            }
        }
        return owners;
    }

    // 相対パスだけを受け付ける。.. や絶対パスでセッションのフォルダの外に出させない
    static string SafeRelative(string dir, string path)
    {
        var relative = path.Replace('\\', '/').Trim().TrimStart('/');
        if (relative.Length == 0) throw new ToolException("ファイル名を指定してください");
        if (relative.Split('/').Any(part => part is "" or "." or ".." || part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new ToolException($"使えないファイル名です: {path}");
        }
        if (relative.Equals(RegistryName, StringComparison.OrdinalIgnoreCase)) throw new ToolException("その名前はアプリが使っています");
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, relative));
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dir)) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ToolException($"セッションのフォルダの外は指定できません: {path}");
        return relative;
    }

    public async Task<(SessionFile File, string FullPath)> WriteAsync(Conversation writer, string path, string content)
    {
        if (content.Length > MaxFileChars) throw new ToolException($"1 ファイル {MaxFileChars:N0} 文字までです");
        var root = await RootOfAsync(writer);
        var dir = DirOf(root);
        var relative = SafeRelative(dir, path);
        var bytes = Encoding.UTF8.GetBytes(content);
        await gate.WaitAsync();
        try
        {
            var files = await LoadAsync(dir);
            var existing = files.FirstOrDefault(f => f.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
            if (files.Where(f => f != existing).Sum(f => f.Size) + bytes.Length > MaxFolderBytes) throw new ToolException("このセッションのフォルダがいっぱいです。要らないファイルを消してください");
            var full = System.IO.Path.Combine(dir, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, bytes);
            var file = existing ?? new SessionFile { Id = "file_" + Guid.NewGuid().ToString("N")[..12], Path = relative };
            file.Size = bytes.Length;
            file.WrittenBy = writer.Incognito ? null : writer.Id;
            file.WrittenByTitle = writer.Title;
            file.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (existing is null) files.Add(file);
            await Json.WriteAtomicAsync(RegistryPath(dir), files);
            if (!root.Incognito) (await OwnersAsync())[file.Id] = root.Id;
            Changed?.Invoke(root.Id);
            return (file, full);
        }
        finally
        {
            gate.Release();
        }
    }

    // ID か、自分のセッションの中のパスで探す。ほかのセッションのファイルは、そのチャットと通信できるときだけ読める
    public async Task<(SessionFile File, string FullPath)> FindAsync(Conversation reader, string? fileId, string? path)
    {
        var myRoot = await RootOfAsync(reader);
        var root = myRoot;
        if (fileId is { Length: > 0 } && !(await LoadAsync(DirOf(myRoot))).Any(f => f.Id == fileId))
        {
            Conversation? owner = null;
            if ((await OwnersAsync()).TryGetValue(fileId, out var ownerId)) owner = await conversations.GetAsync(ownerId);
            if (owner is null || owner.Incognito || myRoot.Incognito) throw new ToolException($"{fileId} というファイルはありません");
            // 分けたチャットは、分ける前の会話で受け渡したファイルを引き続き読める
            var forkedFromOwner = myRoot.ForkedFrom == owner.Id;
            if (!forkedFromOwner && (CanShare is null || !await CanShare(myRoot, owner))) throw new ToolException($"{fileId} は、このチャットからは読めないセッションのファイルです");
            root = owner;
        }
        var dir = DirOf(root);
        var files = await LoadAsync(dir);
        var file = fileId is { Length: > 0 }
            ? files.FirstOrDefault(f => f.Id == fileId)
            : files.FirstOrDefault(f => f.Path.Equals(SafeRelative(dir, path ?? ""), StringComparison.OrdinalIgnoreCase));
        if (file is null) throw new ToolException($"{fileId ?? path} というファイルはありません");
        var full = System.IO.Path.Combine(dir, SafeRelative(dir, file.Path));
        if (!File.Exists(full)) throw new ToolException($"{file.Path} はフォルダから消えています");
        return (file, full);
    }

    public async Task<List<SessionFile>> ListAsync(Conversation conversation) => await LoadAsync(DirOf(await RootOfAsync(conversation)));

    // 会話を消す前の確認用。子はフォルダを持たないので 0
    public async Task<int> CountAsync(Conversation conversation) =>
        conversation.ParentId is null ? (await LoadAsync(DirOf(conversation))).Count : 0;

    public async Task DeleteFolderAsync(Conversation conversation)
    {
        if (conversation.ParentId is not null) return;
        var dir = DirOf(conversation);
        await gate.WaitAsync();
        try
        {
            if (owners is not null)
            {
                foreach (var id in owners.Where(o => o.Value == conversation.Id).Select(o => o.Key).ToList()) owners.Remove(id);
            }
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<string> ReadTextAsync(string fullPath, int offset)
    {
        var text = await File.ReadAllTextAsync(fullPath);
        if (offset < 0 || offset > text.Length) throw new ToolException($"offset は 0 から {text.Length} の間にしてください");
        var part = text.Substring(offset, Math.Min(MaxReadChars, text.Length - offset));
        return offset + part.Length < text.Length
            ? $"{part}\n\n(全 {text.Length:N0} 文字のうち {offset + part.Length:N0} 文字目まで。続きは offset={offset + part.Length} で読めます)"
            : part;
    }

    public static JsonObject ToWire(SessionFile file) => new()
    {
        ["id"] = file.Id,
        ["path"] = file.Path,
        ["size"] = file.Size,
        ["writtenBy"] = file.WrittenBy,
        ["writtenByTitle"] = file.WrittenByTitle,
        ["updatedAt"] = file.UpdatedAt,
    };
}

// エージェント同士で、コードや長い文書を本文に貼らずにファイル ID で受け渡す
public sealed class SessionToolProvider(SessionFiles files) : IToolProvider
{
    public const string Source = "session";
    const string Prefix = "session__";

    const string Sharing = "This chat has its own session folder that lasts until the chat is deleted. Sub-agents and workflow agents you start share your session folder, so their files stay after they finish. When you hand code, data or long text to a sub-agent or another chat, or get it back, save it here and pass the file_id instead of pasting the content.";

    public IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources)
    {
        if (enabledSources is not null && !enabledSources.Contains(Source)) return [];
        // アプリのセッションのフォルダの中だけを読み書きし、利用者のファイルは変えないので確認しない
        return
        [
            new(Prefix + "write_file", Source, "write_file", "セッションにファイルを書く",
                $"Write a text file (code, data, documents) into the session folder and get a file_id. Writing the same path again overwrites it and keeps its file_id. {Sharing} The result also gives the file's full path, so you can run or open it with other tools.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative path in the session folder, e.g. components/Button.jsx" },
                        ["content"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("path", "content"),
                    ["additionalProperties"] = false,
                }, AutoApprove: true, ReadOnly: true),
            new(Prefix + "read_file", Source, "read_file", "セッションのファイルを読む",
                "Read a session file by its file_id (also works for a file_id another chat or a sub-agent gave you) or by its relative path in your session folder. Long files come back in parts; pass offset to continue.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["file_id"] = new JsonObject { ["type"] = "string" },
                        ["path"] = new JsonObject { ["type"] = "string" },
                        ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "Character offset to start from" },
                    },
                    ["additionalProperties"] = false,
                }, AutoApprove: true, ReadOnly: true),
            new(Prefix + "list_files", Source, "list_files", "セッションのファイルの一覧",
                "List the files in your session folder with their file_ids, sizes, and which chat or sub-agent wrote them.",
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
                AutoApprove: true, ReadOnly: true),
        ];
    }

    public async Task<ToolCallResult> CallAsync(string apiName, JsonObject input, CancellationToken cancellationToken)
    {
        var conversation = ToolContext.Current?.Conversation ?? throw new ToolException("チャットの外からは使えません");
        switch (apiName[Prefix.Length..])
        {
            case "write_file":
            {
                var (file, full) = await files.WriteAsync(conversation, (string?)input["path"] ?? "", (string?)input["content"] ?? throw new ToolException("content を指定してください"));
                return ToolJson.Text($"書きました。file_id: {file.Id}\npath: {file.Path}\nfull path: {full}\nsize: {file.Size:N0} bytes");
            }
            case "read_file":
            {
                var fileId = (string?)input["file_id"];
                var path = (string?)input["path"];
                if (string.IsNullOrWhiteSpace(fileId) && string.IsNullOrWhiteSpace(path)) throw new ToolException("file_id か path を指定してください");
                var (file, full) = await files.FindAsync(conversation, fileId, path);
                var text = await SessionFiles.ReadTextAsync(full, (int?)input["offset"] ?? 0);
                return ToolJson.Text($"file_id: {file.Id}\npath: {file.Path}\nfull path: {full}\n\n{text}");
            }
            case "list_files":
            {
                var list = await files.ListAsync(conversation);
                if (list.Count == 0) return ToolJson.Text("セッションのフォルダにはまだファイルがありません");
                return ToolJson.Text(string.Join("\n", list.OrderBy(f => f.Path).Select(f => $"{f.Id}  {f.Path}  {f.Size:N0} bytes  by {f.WrittenByTitle ?? "?"}")));
            }
            default:
                throw new ToolException($"{apiName} というツールはありません");
        }
    }
}
