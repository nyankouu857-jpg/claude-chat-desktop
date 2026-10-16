using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

public sealed class ConversationUsage
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    // サブエージェントの分も含む。子は終わると消えるので、親の側に足しておく
    public decimal CostUsd { get; set; }
    public decimal AgentsCostUsd { get; set; }
    public long Context { get; set; }

    // 親の応答と子の応答が別のスレッドから同時に足しに来る
    readonly Lock gate = new();

    public void Charge(decimal cost, bool byAgent)
    {
        lock (gate)
        {
            CostUsd += cost;
            if (byAgent) AgentsCostUsd += cost;
        }
    }

    public void Add(long input, long output, long cacheRead, long cacheWrite, decimal cost)
    {
        lock (gate)
        {
            Input += input;
            Output += output;
            CacheRead += cacheRead;
            CacheWrite += cacheWrite;
            CostUsd += cost;
        }
    }

    // 保存や画面に出すときの写し。足している途中の値を書き出さない
    public ConversationUsage Copy()
    {
        lock (gate)
        {
            return new ConversationUsage { Input = Input, Output = Output, CacheRead = CacheRead, CacheWrite = CacheWrite, CostUsd = CostUsd, AgentsCostUsd = AgentsCostUsd, Context = Context };
        }
    }
}

public sealed class Artifact
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    // html / react / markdown / svg / mermaid / code
    public string Type { get; set; } = "markdown";
    public string? Language { get; set; }
    public string Content { get; set; } = "";
    public int Version { get; set; } = 1;
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    // claude.ai と同じく前の版に戻って見られるよう、直す前の中身を残す。History[n] が版 n + 1
    public List<ArtifactRevision> History { get; set; } = [];

    public string? ContentOf(int version) =>
        version == Version ? Content : version >= 1 && version <= History.Count ? History[version - 1].Content : null;
}

public sealed class ArtifactRevision
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public long UpdatedAt { get; set; }
}

// 会話の途中で effort を変えた位置。Index の発言の直前に、effort だけの system メッセージとして API に送る
public sealed class EffortMark
{
    public int Index { get; set; }
    public string Effort { get; set; } = "";
}

// 編集ややり直しで分かれた先の 1 本。その時点より後の履歴と、その中でさらに分かれた先を持つ
public sealed class BranchVersion
{
    public List<JsonObject> Messages { get; set; } = [];
    public Dictionary<int, Fork> Forks { get; set; } = [];
    public List<EffortMark> EffortMarks { get; set; } = [];
}

// 同じ位置から分かれた版の束。表示中の版は会話の本体にあるので、その枠は null にしておく
public sealed class Fork
{
    public List<BranchVersion?> Versions { get; set; } = [];
    public int Active { get; set; }
}

public sealed class Conversation
{
    public const string DefaultTitle = "新しいチャット";

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = DefaultTitle;
    public bool Starred { get; set; }
    // 一覧から外しただけで、消してはいない
    public bool Archived { get; set; }
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
    // API の「その場で要約」が返した塊。画面の履歴には入れず、API に送るときだけ先頭に付ける
    public JsonObject? CompactionBlock { get; set; }
    public JsonArray? Todos { get; set; }
    public List<Artifact> Artifacts { get; set; } = [];
    // コード実行のコンテナ。続けて使うと前の計算結果やファイルが残る
    public string? ContainerId { get; set; }
    public string? WorktreePath { get; set; }
    // 最後の応答の結果。done / failed / aborted
    public string? LastRunStatus { get; set; }
    // コード実行で作られたファイル。API のファイル ID -> 手元に保存した場所
    public Dictionary<string, string> Files { get; set; } = [];
    // claude.ai と同じく、編集ややり直しの前の版を消さずに残す。キーは分かれた位置
    public Dictionary<int, Fork> Forks { get; set; } = [];
    // claude.ai のシークレットチャット。ディスクにも一覧にも残さず、メモリにも書かせない
    public bool Incognito { get; set; }
    // 最初に送った effort。上の effort を変えるとキャッシュが切れるので、後の変更は EffortMarks で伝える
    public string? TopEffort { get; set; }
    // 一覧で絞り込むための目印。利用者が付ける
    public List<string> Tags { get; set; } = [];
    // 「ここから別のチャット」で分けた元。元のセッションのファイルを読めるようにする
    public string? ForkedFrom { get; set; }
    public long ForkedAt { get; set; }
    public List<EffortMark> EffortMarks { get; set; } = [];
    // キャッシュが外れた理由を API に聞くための前の応答の ID。保存しない
    public string? LastResponseId { get; set; }

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
            EffortMarks.RemoveAll(m => m.Index >= index);
            LastResponseId = null;
            if (ApiStart > Messages.Count) (ApiStart, CompactionBlock) = (0, null);
            // 消した範囲で分かれていた版も一緒に捨てる
            foreach (var key in Forks.Keys.Where(k => k >= index).ToList()) Forks.Remove(key);
        }
    }

    // index から後を今の版として取っておき、新しい空の版に切り替える。後に何も無ければ分けない
    public void BranchFrom(int index)
    {
        lock (sync)
        {
            if (index >= Messages.Count)
            {
                foreach (var key in Forks.Keys.Where(k => k >= index).ToList()) Forks.Remove(key);
                return;
            }
            var current = TakeTail(index);
            LastResponseId = null;
            if (!Forks.TryGetValue(index, out var fork)) Forks[index] = fork = new Fork { Versions = [null] };
            fork.Versions[fork.Active] = current;
            fork.Versions.Add(null);
            fork.Active = fork.Versions.Count - 1;
        }
    }

    public void SwitchBranch(int index, int version)
    {
        lock (sync)
        {
            if (!Forks.TryGetValue(index, out var fork) || version < 0 || version >= fork.Versions.Count) throw new InvalidOperationException("その版はありません");
            if (version == fork.Active) return;
            var target = fork.Versions[version] ?? throw new InvalidOperationException("その版はありません");
            fork.Versions[fork.Active] = TakeTail(index);
            Messages.AddRange(target.Messages);
            EffortMarks.AddRange(target.EffortMarks);
            LastResponseId = null;
            foreach (var (key, nested) in target.Forks) Forks[key] = nested;
            fork.Versions[version] = null;
            fork.Active = version;
        }
    }

    // index から後の履歴と、その中で分かれていた版を外して返す
    BranchVersion TakeTail(int index)
    {
        // 境目の人の発言を差し替えるときだけ塊を使い続けられる。境目の返答をやり直すと、塊のあとに人の発言が無くなって送れない
        var keepBlock = CompactionBlock is not null && ApiStart == index && index < Messages.Count && IsHumanTurn(Messages[index]);
        var tail = new BranchVersion
        {
            Messages = Messages.GetRange(index, Messages.Count - index),
            EffortMarks = EffortMarks.Where(m => m.Index >= index).ToList(),
        };
        Messages.RemoveRange(index, Messages.Count - index);
        EffortMarks.RemoveAll(m => m.Index >= index);
        foreach (var key in Forks.Keys.Where(k => k > index).ToList())
        {
            tail.Forks[key] = Forks[key];
            Forks.Remove(key);
        }
        // 要約がその先にあったら、要約前から送り直す
        if (ApiStart > index || (ApiStart == index && !keepBlock)) (ApiStart, CompactionBlock) = (0, null);
        return tail;
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

    // API に送る分。手動の要約より前は送らない。withEffort なら effort を変えた印も挟む
    public List<JsonObject> CopyApiMessages(bool withEffort = false)
    {
        lock (sync)
        {
            var messages = new List<JsonObject>();
            if (CompactionBlock is { } block) messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(block.DeepClone()) });
            var start = Math.Min(ApiStart, Messages.Count);
            // 要約の塊より前は API が読まないので、そこで効いていた effort を塊の直後で言い直す
            var restart = Messages.FindLastIndex(m => (m["content"] as JsonArray)?.OfType<JsonObject>().Any(b => (string?)b["type"] == "compaction") == true) + 1;
            if (restart <= start) restart = CompactionBlock is null ? -1 : start;
            for (var i = start; i < Messages.Count; i++)
            {
                if (withEffort)
                {
                    var marks = EffortMarks.Where(m => m.Index == i).ToList();
                    if (i == restart && marks.Count == 0 && EffortAt(i) is { } carried && carried != TopEffort) marks.Add(new EffortMark { Index = i, Effort = carried });
                    foreach (var mark in marks)
                    {
                        messages.Add(new JsonObject { ["role"] = "system", ["content"] = new JsonArray(), ["output_config"] = new JsonObject { ["effort"] = mark.Effort } });
                    }
                }
                messages.Add((JsonObject)Messages[i].DeepClone());
            }
            return messages;
        }
    }

    // 結果の無い tool_use の直後に結果を補う。人の発言が続いていれば、その先頭に結果を入れる
    public void RepairToolUses(string reason)
    {
        lock (sync)
        {
            for (var i = Math.Min(ApiStart, Messages.Count); i < Messages.Count; i++)
            {
                if ((string?)Messages[i]["role"] != "assistant") continue;
                var ids = (Messages[i]["content"] as JsonArray ?? []).OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_use").Select(b => (string?)b["id"]).OfType<string>().ToList();
                if (ids.Count == 0) continue;
                var next = i + 1 < Messages.Count ? Messages[i + 1] : null;
                var answered = (next?["content"] as JsonArray ?? []).OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_result").Select(b => (string?)b["tool_use_id"]).ToHashSet();
                var missing = ids.Where(id => !answered.Contains(id)).ToList();
                if (missing.Count == 0) continue;
                var results = missing.Select(id => (JsonNode)new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = id,
                    ["is_error"] = true,
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reason }),
                }).ToList();
                if (next is not null && (string?)next["role"] == "user")
                {
                    var blocks = next["content"] as JsonArray ?? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = (string?)next["content"] ?? "" });
                    for (var r = results.Count - 1; r >= 0; r--) blocks.Insert(0, results[r]);
                    next["content"] = blocks;
                }
                else
                {
                    Messages.Insert(i + 1, new JsonObject { ["role"] = "user", ["content"] = new JsonArray(results.ToArray()) });
                    foreach (var mark in EffortMarks.Where(m => m.Index > i)) mark.Index++;
                }
            }
        }
    }

    // index の発言の手前で効いている effort
    string? EffortAt(int index) => EffortMarks.Where(m => m.Index < index).LastOrDefault()?.Effort ?? TopEffort;

    // effort を変えたら、まだ送っていない末尾の発言の前に印を置く。初めて送るときは上の effort にする
    public void SetEffort(string effort)
    {
        lock (sync)
        {
            if (TopEffort is null)
            {
                TopEffort = effort;
                return;
            }
            // 要約の後も CopyApiMessages が言い直すので、印は会話の頭から数える
            var current = EffortMarks.Where(m => m.Index < Messages.Count).LastOrDefault()?.Effort ?? TopEffort;
            if (current == effort) return;
            var index = Messages.Count > 0 && (string?)Messages[^1]["role"] == "user" ? Messages.Count - 1 : Messages.Count;
            EffortMarks.RemoveAll(m => m.Index >= index);
            EffortMarks.Add(new EffortMark { Index = index, Effort = effort });
        }
    }

    // 先頭から upTo 件目の手前までを、新しい会話として写す。コンテナや作業中の状態は元に残す
    public Conversation ForkUpTo(int upTo)
    {
        lock (sync)
        {
            if (upTo <= 0 || upTo > Messages.Count) throw new InvalidOperationException("分ける位置が不正です");
            var fork = new Conversation
            {
                Title = $"{Title} (分岐)",
                Model = Model,
                FolderId = FolderId,
                Options = Options?.DeepClone() as JsonObject,
                PermissionMode = PermissionMode == "bypass" ? "default" : PermissionMode,
                Messages = Messages.Take(upTo).Select(m => (JsonObject)m.DeepClone()).ToList(),
                TopEffort = TopEffort,
                EffortMarks = EffortMarks.Where(m => m.Index < upTo).Select(m => new EffortMark { Index = m.Index, Effort = m.Effort }).ToList(),
                Files = new Dictionary<string, string>(Files),
                Tags = [.. Tags],
                ForkedFrom = Id,
                ForkedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LastRunStatus = "done",
            };
            if (ApiStart <= upTo) (fork.ApiStart, fork.CompactionBlock) = (ApiStart, CompactionBlock?.DeepClone() as JsonObject);
            lock (Artifacts) fork.Artifacts = Json.ToNode(Artifacts).Deserialize<List<Artifact>>(Json.Wire) ?? [];
            return fork;
        }
    }

    public void ApplyCompaction(JsonObject? block, int apiStart)
    {
        lock (sync)
        {
            CompactionBlock = block;
            ApiStart = apiStart;
            // 履歴を作り替えたのは分かっているので、外れた理由を聞かない
            LastResponseId = null;
        }
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

    // 保存するときは分かれた版の中身まで。画面には版の数だけ送る
    public JsonObject ToWire(bool includeBranches = false)
    {
        lock (sync)
        {
            var wire = new JsonObject
            {
                ["id"] = Id,
                ["title"] = Title,
                ["starred"] = Starred,
                ["archived"] = Archived,
                ["model"] = Model,
                ["createdAt"] = CreatedAt,
                ["updatedAt"] = UpdatedAt,
                ["messages"] = new JsonArray(Messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
                ["usage"] = Json.ToNode(Usage.Copy()),
                ["folderId"] = FolderId,
                ["parentId"] = ParentId,
                ["kind"] = Kind,
                ["workflowId"] = WorkflowId,
                ["options"] = Options?.DeepClone(),
                ["permissionMode"] = PermissionMode,
                ["apiStart"] = ApiStart,
                ["compactionBlock"] = CompactionBlock?.DeepClone(),
                ["todos"] = Todos?.DeepClone(),
                ["artifacts"] = ArtifactsView(),
                ["containerId"] = ContainerId,
                ["worktreePath"] = WorktreePath,
                ["lastRunStatus"] = LastRunStatus,
                ["files"] = new JsonObject(Files.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))),
                ["incognito"] = Incognito,
                ["topEffort"] = TopEffort,
                ["forkedFrom"] = ForkedFrom,
                ["forkedAt"] = ForkedAt,
                ["tags"] = new JsonArray(Tags.Select(t => (JsonNode)t).ToArray()),
                ["effortMarks"] = Json.ToNode(EffortMarks),
                ["forkView"] = new JsonObject(Forks.Select(f => KeyValuePair.Create(f.Key.ToString(), (JsonNode?)new JsonObject { ["count"] = f.Value.Versions.Count, ["active"] = f.Value.Active }))),
            };
            if (includeBranches) wire["forks"] = Json.ToNode(Forks);
            return wire;
        }
    }
}

public sealed class ConversationSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Starred { get; set; }
    public bool Archived { get; set; }
    public long UpdatedAt { get; set; }
    public string Preview { get; set; } = "";
    public string? FolderId { get; set; }
    public string? ParentId { get; set; }
    public string Kind { get; set; } = "chat";
    public string? LastRunStatus { get; set; }
    public List<string> Tags { get; set; } = [];
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
    // ID はツールや画面からも来る。..\ や絶対パスで会話のフォルダの外の JSON を読ませない
    static bool IsValidId(string id) => Guid.TryParse(id, out _);
    string FilePath(string id) => IsValidId(id) ? Path.Combine(paths.ConversationsDir, $"{id}.json") : throw new InvalidOperationException("チャットの ID が不正です");

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
            if (deletedIds.Contains(id) || !IsValidId(id)) return null;
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

    // 外で作った会話 (分けたチャットなど) を、保存の前に手元の控えに載せる
    public void Adopt(Conversation conversation) => cache[conversation.Id] = conversation;

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
            if (conversation.Incognito) return;
            await Json.WriteAtomicAsync(FilePath(conversation.Id), conversation.ToWire(includeBranches: true), Json.Wire);
            var index = await LoadIndexAsync();
            index.RemoveAll(s => s.Id == conversation.Id);
            index.Add(new ConversationSummary
            {
                Id = conversation.Id,
                Title = conversation.Title,
                Starred = conversation.Starred,
                Archived = conversation.Archived,
                UpdatedAt = conversation.UpdatedAt,
                Preview = PreviewOf(conversation),
                FolderId = conversation.FolderId,
                ParentId = conversation.ParentId,
                Kind = conversation.Kind,
                LastRunStatus = conversation.LastRunStatus,
                Tags = [.. conversation.Tags],
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
