using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Files;

namespace ClaudeChat.Core;

// 大きな PDF は一度 Files API に上げ、問い合わせのときだけ ID に差し替えて送る。長い会話で毎回何 MB も送り直さない。
// 履歴には中身を残すので、API キーを替えても、上げたファイルが消えても、中身ごと送り直せる
public sealed class Uploads(AppPaths paths)
{
    // これより小さい添付はそのまま送る。上げる手間のほうが大きい
    const int UploadAboveBase64Chars = 1_000_000;

    sealed class Entry
    {
        public string FileId { get; set; } = "";
        // この添付を履歴に持つ会話。すべて消えたら Anthropic からも消す
        public List<string> Users { get; set; } = [];
    }

    readonly SemaphoreSlim gate = new(1, 1);
    string RegistryPath => Path.Combine(paths.DataDir, "uploads.json");

    // 中身のハッシュ -> 上げたファイル
    async Task<Dictionary<string, Entry>> LoadAsync()
    {
        try
        {
            return await Json.ReadAsync<Dictionary<string, Entry>>(RegistryPath) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            // 壊れた一覧で送信を止めない。上げ直しになるだけ
            return [];
        }
    }

    static string HashOf(string base64) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(base64)));

    static IEnumerable<JsonObject> LargePdfSources(IEnumerable<JsonObject> messages) => messages
        .SelectMany(m => (m["content"] as JsonArray ?? []).OfType<JsonObject>())
        .Where(b => (string?)b["type"] == "document")
        .Select(b => b["source"] as JsonObject)
        .OfType<JsonObject>()
        .Where(s => (string?)s["type"] == "base64" && (string?)s["media_type"] == "application/pdf" && ((string?)s["data"])?.Length > UploadAboveBase64Chars);

    // 送る写しの中の大きな PDF を、上げたファイルの ID に置き換える。上げられなかったものは中身のまま送る
    public async Task<List<string>> SubstituteAsync(AnthropicClient client, Conversation conversation, List<JsonObject> messages, CancellationToken cancellationToken)
    {
        var used = new List<string>();
        // シークレットチャットは Anthropic 側にも残さない
        if (conversation.Incognito) return used;
        foreach (var source in LargePdfSources(messages).ToList())
        {
            var data = (string)source["data"]!;
            var hash = HashOf(data);
            var fileId = await FileIdAsync(hash, conversation.Id);
            if (fileId is null)
            {
                try
                {
                    // バイト列から作ると、混雑で断られたときに SDK がやり直せる。Stream から作るとやり直さない
                    BinaryContent bytes = Convert.FromBase64String(data);
                    var body = bytes with { FileName = "document.pdf", ContentType = new MediaTypeHeaderValue("application/pdf") };
                    fileId = (await client.Files.Upload(new FileUploadParams { File = body }, cancellationToken)).ID;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    continue;
                }
                await RememberAsync(hash, fileId, conversation.Id);
            }
            source.Clear();
            source["type"] = "file";
            source["file_id"] = fileId;
            used.Add(fileId);
        }
        return used;
    }

    async Task<string?> FileIdAsync(string hash, string conversationId)
    {
        await gate.WaitAsync();
        try
        {
            var registry = await LoadAsync();
            if (!registry.TryGetValue(hash, out var entry)) return null;
            if (!entry.Users.Contains(conversationId))
            {
                entry.Users.Add(conversationId);
                await Json.WriteAtomicAsync(RegistryPath, registry);
            }
            return entry.FileId;
        }
        finally
        {
            gate.Release();
        }
    }

    async Task RememberAsync(string hash, string fileId, string conversationId)
    {
        await gate.WaitAsync();
        try
        {
            var registry = await LoadAsync();
            registry[hash] = new Entry { FileId = fileId, Users = [conversationId] };
            await Json.WriteAtomicAsync(RegistryPath, registry);
        }
        finally
        {
            gate.Release();
        }
    }

    // API が知らないと言ったファイルは忘れて、次からは上げ直す
    public async Task ForgetAsync(IEnumerable<string> fileIds)
    {
        var ids = fileIds.ToHashSet();
        await gate.WaitAsync();
        try
        {
            var registry = await LoadAsync();
            foreach (var hash in registry.Where(r => ids.Contains(r.Value.FileId)).Select(r => r.Key).ToList()) registry.Remove(hash);
            await Json.WriteAtomicAsync(RegistryPath, registry);
        }
        finally
        {
            gate.Release();
        }
    }

    // 分けたチャットも同じ添付を履歴に持つので、元を消しても残す
    public async Task ShareAsync(string fromId, string toId)
    {
        await gate.WaitAsync();
        try
        {
            var registry = await LoadAsync();
            var changed = false;
            foreach (var entry in registry.Values.Where(e => e.Users.Contains(fromId) && !e.Users.Contains(toId)))
            {
                entry.Users.Add(toId);
                changed = true;
            }
            if (changed) await Json.WriteAtomicAsync(RegistryPath, registry);
        }
        finally
        {
            gate.Release();
        }
    }

    // 会話を消したとき、ほかに使う会話が無くなった添付を Anthropic から消す。
    // 消せなかったものは使う会話が無いまま残し、次に会話を消すときにまた消してみる
    public async Task ReleaseAsync(AnthropicClient client, string conversationId)
    {
        List<(string Hash, string FileId)> orphaned;
        await gate.WaitAsync();
        try
        {
            var registry = await LoadAsync();
            foreach (var entry in registry.Values) entry.Users.Remove(conversationId);
            orphaned = registry.Where(r => r.Value.Users.Count == 0).Select(r => (r.Key, r.Value.FileId)).ToList();
            await Json.WriteAtomicAsync(RegistryPath, registry);
        }
        finally
        {
            gate.Release();
        }
        var deleted = new List<string>();
        foreach (var (hash, fileId) in orphaned)
        {
            try
            {
                await client.Files.Delete(fileId);
                deleted.Add(hash);
            }
            catch (Anthropic.Exceptions.AnthropicNotFoundException)
            {
                deleted.Add(hash);
            }
            catch (Exception)
            {
                // 通信できないときは次の機会に
            }
        }
        if (deleted.Count == 0) return;
        await gate.WaitAsync();
        try
        {
            var registry = await LoadAsync();
            foreach (var hash in deleted.Where(h => registry.TryGetValue(h, out var e) && e.Users.Count == 0)) registry.Remove(hash);
            await Json.WriteAtomicAsync(RegistryPath, registry);
        }
        finally
        {
            gate.Release();
        }
    }
}
