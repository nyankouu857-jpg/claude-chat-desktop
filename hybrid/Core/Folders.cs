namespace ClaudeChat.Core;

// claude.ai のプロジェクトにあたる。会話をまとめ、専用の指示とチャット同士の通信範囲を持つ
public sealed class Folder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新しいフォルダ";
    public string Instructions { get; set; } = "";
    // folder: 同じフォルダの中だけ / all: どのチャットとも / none: 通信しない
    public string MessagingScope { get; set; } = "folder";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class FolderStore(AppPaths paths)
{
    readonly SemaphoreSlim gate = new(1, 1);
    List<Folder>? folders;

    public static readonly string[] Scopes = ["folder", "all", "none"];

    async Task<List<Folder>> LoadAsync() => folders ??= await Json.ReadAsync<List<Folder>>(paths.FoldersFile) ?? [];

    public async Task<List<Folder>> ListAsync()
    {
        await gate.WaitAsync();
        try
        {
            return (await LoadAsync()).Select(Clone).ToList();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Folder?> GetAsync(string? id)
    {
        if (id is null) return null;
        return (await ListAsync()).FirstOrDefault(f => f.Id == id);
    }

    public async Task<Folder> SaveAsync(Folder folder)
    {
        if (string.IsNullOrWhiteSpace(folder.Name)) throw new InvalidOperationException("フォルダ名を入れてください");
        if (!Scopes.Contains(folder.MessagingScope)) throw new InvalidOperationException("通信の範囲が不正です");
        await gate.WaitAsync();
        try
        {
            var list = await LoadAsync();
            var index = list.FindIndex(f => f.Id == folder.Id);
            var saved = Clone(folder);
            saved.Name = saved.Name.Trim();
            if (index >= 0) list[index] = saved;
            else list.Add(saved);
            await Json.WriteAtomicAsync(paths.FoldersFile, list);
            return Clone(saved);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(string id)
    {
        await gate.WaitAsync();
        try
        {
            var list = await LoadAsync();
            list.RemoveAll(f => f.Id == id);
            await Json.WriteAtomicAsync(paths.FoldersFile, list);
        }
        finally
        {
            gate.Release();
        }
    }

    // 呼び出し側に書き換えられても保存内容が変わらないように
    static Folder Clone(Folder f) => new()
    {
        Id = f.Id,
        Name = f.Name,
        Instructions = f.Instructions,
        MessagingScope = f.MessagingScope,
        CreatedAt = f.CreatedAt,
    };
}
