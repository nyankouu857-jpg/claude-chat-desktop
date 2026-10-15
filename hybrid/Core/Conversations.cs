using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

public sealed class ConversationUsage
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public decimal CostUsd { get; set; }
}

public sealed class Artifact
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    // html / markdown / svg / mermaid / code
    public string Type { get; set; } = "markdown";
    public string? Language { get; set; }
    public string Content { get; set; } = "";
    public int Version { get; set; } = 1;
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class Conversation
{
    public const string DefaultTitle = "新しいチャット";

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = DefaultTitle;
    public bool Starred { get; set; }
    public string? Model { get; set; }
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    // 思考ブロックの署名や検索結果の暗号化部分を壊さないよう、API の JSON をそのまま持つ
    public List<JsonObject> Messages { get; set; } = [];
    public ConversationUsage Usage { get; set; } = new();

    public string? FolderId { get; set; }
    // サブエージェントとワークフローの子は、呼び出したチャットを親に持つ
    public string? ParentId { get; set; }
    // chat / agent / workflow
    public string Kind { get; set; } = "chat";
    public string? WorkflowId { get; set; }
    // 直近の送信設定。ほかのチャットからの通信で動き出すときに使う
    public JsonObject? Options { get; set; }
    // default / acceptEdits / plan / bypass
    public string PermissionMode { get; set; } = "default";
    // /compact で要約した後は、ここより前を API に送らない
    public int ApiStart { get; set; }
    public JsonArray? Todos { get; set; }
    public List<Artifact> Artifacts { get; set; } = [];
    // コード実行のコンテナ。続けて使うと前の計算結果やファイルが残る
    public string? ContainerId { get; set; }
    public string? WorktreePath { get; set; }
    // 最後の応答の結果。done / failed / aborted
    public string? LastRunStatus { get; set; }
    // コード実行で作られたファイル。API のファイル ID -> 手元に保存した場所
    public Dictionary<string, string> Files { get; set; } = [];

    // 応答中のスレッドと画面からの読み出しが同時に触るので、触るときは必ずこのロックを通す
    readonly Lock sync = new();

    public void Append(JsonObject message)
    {
        lock (sync) Messages.Add(message);
    }

    public void TruncateFrom(int index)
    {
        lock (sync)
        {
            if (index < Messages.Count) Messages.RemoveRange(index, Messages.Count - index);
            if (ApiStart > Messages.Count) ApiStart = 0;
        }
    }

    public bool HasFile(string fileId)
    {
        lock (sync) return Files.ContainsKey(fileId);
    }

    public void AddFile(string fileId, string path)
    {
        lock (sync) Files[fileId] = path;
    }

    public JsonObject? LastMessage()
    {
        lock (sync) return Messages.Count == 0 ? null : Messages[^1];
    }

    public static bool IsHumanTurn(JsonObject message) =>
        (string?)message["role"] == "user" &&
        !(message["content"] is JsonArray blocks && blocks.OfType<JsonObject>().All(b => (string?)b["type"] == "tool_result"));

    public int LastHumanIndex()
    {
        lock (sync) return Messages.FindLastIndex(m => IsHumanTurn(m));
    }

    public int MessageCount
    {
        get { lock (sync) return Messages.Count; }
    }

    public List<JsonObject> CopyMessages()
    {
        lock (sync) return Messages.Select(m => (JsonObject)m.DeepClone()).ToList();
    }

    // API に送る分。手動の要約より前は送らない
    public List<JsonObject> CopyApiMessages()
    {
        lock (sync) return Messages.Skip(Math.Min(ApiStart, Messages.Count)).Select(m => (JsonObject)m.DeepClone()).ToList();
    }

    // アーティファクトのツールはリストそのものをロックして書き換える
    JsonNode? ArtifactsView()
    {
        lock (Artifacts) return Json.ToNode(Artifacts);
    }

    // 応答の途中でも画面のタスクリストやアーティファクトを更新するための分
    public JsonObject StateView()
    {
        lock (sync)
        {
            return new JsonObject
            {
                ["permissionMode"] = PermissionMode,
                ["todos"] = Todos?.DeepClone(),
                ["artifacts"] = ArtifactsView(),
            };
        }
    }

    public JsonObject ToWire()
    {
        lock (sync)
        {
            return new JsonObject
            {
                ["id"] = Id,
                ["title"] = Title,
                ["starred"] = Starred,
                ["model"] = Model,
                ["createdAt"] = CreatedAt,
                ["updatedAt"] = UpdatedAt,
                ["messages"] = new JsonArray(Messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
                ["usage"] = Json.ToNode(Usage),
                ["folderId"] = FolderId,
                ["parentId"] = ParentId,
                ["kind"] = Kind,
                ["workflowId"] = WorkflowId,
                ["options"] = Options?.DeepClone(),
                ["permissionMode"] = PermissionMode,
                ["apiStart"] = ApiStart,
                ["todos"] = Todos?.DeepClone(),
                ["artifacts"] = ArtifactsView(),
                ["containerId"] = ContainerId,
                ["worktreePath"] = WorktreePath,
                ["lastRunStatus"] = LastRunStatus,
                ["files"] = new JsonObject(Files.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))),
            };
        }
    }
}

public sealed class ConversationSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Starred { get; set; }
    public long UpdatedAt { get; set; }
    public string Preview { get; set; } = "";
    public string? FolderId { get; set; }
    public string? ParentId { get; set; }
    public string Kind { get; set; } = "chat";
    public string? LastRunStatus { get; set; }
}

public sealed class ConversationStore(AppPaths paths)
{
    readonly SemaphoreSlim gate = new(1, 1);
    // Create は gate を通らないので、同時に触っても壊れないものにする
    readonly ConcurrentDictionary<string, Conversation> cache = new();
    // 削除中にファイルを読み直して復活させないため
    readonly HashSet<string> deletedIds = [];
    List<ConversationSummary>? summaries;

    string IndexPath => Path.Combine(paths.ConversationsDir, "index.json");
    string FilePath(string id) => Path.Combine(paths.ConversationsDir, $"{id}.json");

    async Task<List<ConversationSummary>> LoadIndexAsync() =>
        summaries ??= await Json.ReadAsync<List<ConversationSummary>>(IndexPath) ?? [];

    public async Task<List<ConversationSummary>> ListAsync()
    {
        await gate.WaitAsync();
        try
        {
            return (await LoadIndexAsync()).OrderByDescending(s => s.UpdatedAt).ToList();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Conversation?> GetAsync(string id)
    {
        await gate.WaitAsync();
        try
        {
            if (deletedIds.Contains(id)) return null;
            if (cache.TryGetValue(id, out var cached)) return cached;
            var loaded = await Json.ReadAsync<Conversation>(FilePath(id));
            if (loaded is not null) cache[id] = loaded;
            return loaded;
        }
        finally
        {
            gate.Release();
        }
    }

    public Conversation Create()
    {
        var conversation = new Conversation();
        cache[conversation.Id] = conversation;
        return conversation;
    }

    public async Task SaveAsync(Conversation conversation, bool touch = true)
    {
        await gate.WaitAsync();
        try
        {
            if (deletedIds.Contains(conversation.Id)) return;
            if (touch) conversation.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await Json.WriteAtomicAsync(FilePath(conversation.Id), conversation.ToWire(), Json.Wire);
            var index = await LoadIndexAsync();
            index.RemoveAll(s => s.Id == conversation.Id);
            index.Add(new ConversationSummary
            {
                Id = conversation.Id,
                Title = conversation.Title,
                Starred = conversation.Starred,
                UpdatedAt = conversation.UpdatedAt,
                Preview = PreviewOf(conversation),
                FolderId = conversation.FolderId,
                ParentId = conversation.ParentId,
                Kind = conversation.Kind,
                LastRunStatus = conversation.LastRunStatus,
            });
            await Json.WriteAtomicAsync(IndexPath, index);
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
            deletedIds.Add(id);
            cache.TryRemove(id, out _);
            var index = await LoadIndexAsync();
            index.RemoveAll(s => s.Id == id);
            await Json.WriteAtomicAsync(IndexPath, index);
            File.Delete(FilePath(id));
        }
        finally
        {
            gate.Release();
        }
    }

    // チャット一覧の検索に使う。本文まで見ると重いので最初のユーザー発言だけ
    static string PreviewOf(Conversation conversation)
    {
        var first = conversation.CopyMessages().FirstOrDefault(m => (string?)m["role"] == "user");
        var text = WireText.Of(first?["content"]);
        return text.Length > 120 ? text[..120] : text;
    }
}

public static class WireText
{
    public static string Of(JsonNode? content) => content switch
    {
        JsonValue value => (string?)value ?? "",
        JsonArray blocks => string.Join("\n", blocks.OfType<JsonObject>()
            .Where(b => (string?)b["type"] == "text")
            .Select(b => (string?)b["text"])),
        _ => "",
    };
}
