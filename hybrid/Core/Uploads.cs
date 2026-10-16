using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Files;

namespace ClaudeChat.Core;

// 大きな PDF は一度 Files API に上げ、以後は ID だけで送る。長い会話で毎回何 MB も送り直さない。
// 上げたファイルは、それを使う会話がすべて消えたら Anthropic からも消す
public sealed class Uploads(AppPaths paths)
{
    // これより小さい添付はそのまま送る。上げる手間のほうが大きい
    const int UploadAboveBase64Chars = 1_000_000;

    readonly SemaphoreSlim gate = new(1, 1);
    string RegistryPath => Path.Combine(paths.DataDir, "uploads.json");

    // file_id -> その添付を履歴に持つ会話
    async Task<Dictionary<string, List<string>>> LoadAsync() => await Json.ReadAsync<Dictionary<string, List<string>>>(RegistryPath) ?? [];

    public async Task<JsonNode> ReplaceLargeAsync(AnthropicClient client, Conversation conversation, JsonNode content, CancellationToken cancellationToken)
    {
        // シークレットチャットは Anthropic 側にも残さない
        if (conversation.Incognito || content is not JsonArray blocks) return content;
        foreach (var block in blocks.OfType<JsonObject>())
        {
            if ((string?)block["type"] != "document" || block["source"] is not JsonObject source) continue;
            if ((string?)source["type"] != "base64" || (string?)source["media_type"] != "application/pdf") continue;
            if ((string?)source["data"] is not { Length: > UploadAboveBase64Chars } data) continue;
            var body = new BinaryContent
            {
                Stream = new MemoryStream(Convert.FromBase64String(data)),
                FileName = string.IsNullOrWhiteSpace((string?)block["title"]) ? "document.pdf" : $"{Path.GetFileNameWithoutExtension((string?)block["title"])}.pdf",
                ContentType = new MediaTypeHeaderValue("application/pdf"),
            };
            var file = await client.Files.Upload(new FileUploadParams { File = body }, cancellationToken);
            block["source"] = new JsonObject { ["type"] = "file", ["file_id"] = file.ID };
            await ReferAsync(file.ID, conversation.Id);
        }
        return content;
    }

    async Task ReferAsync(string fileId, string conversationId)
    {
        await gate.WaitAsync();
        try
        {
            var registry = await LoadAsync();
            if (!registry.TryGetValue(fileId, out var users)) registry[fileId] = users = [];
            if (!users.Contains(conversationId)) users.Add(conversationId);
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
            foreach (var users in registry.Values.Where(u => u.Contains(fromId) && !u.Contains(toId)))
            {
                users.Add(toId);
                changed = true;
            }
            if (changed) await Json.WriteAtomicAsync(RegistryPath, registry);
        }
        finally
        {
            gate.Release();
        }
    }

    // 会話を消したとき、ほかに使う会話が無くなった添付を Anthropic から消す。消せなくても会話の削除は止めない
    public async Task ReleaseAsync(AnthropicClient client, string conversationId)
    {
        List<string> orphaned;
        await gate.WaitAsync();
        try
        {
            var registry = await LoadAsync();
            if (!registry.Values.Any(u => u.Contains(conversationId))) return;
            foreach (var users in registry.Values) users.Remove(conversationId);
            orphaned = registry.Where(r => r.Value.Count == 0).Select(r => r.Key).ToList();
            foreach (var id in orphaned) registry.Remove(id);
            await Json.WriteAtomicAsync(RegistryPath, registry);
        }
        finally
        {
            gate.Release();
        }
        foreach (var id in orphaned)
        {
            try
            {
                await client.Files.Delete(id);
            }
            catch (Exception)
            {
                // 期限切れなどでもう無いものは消せなくてよい
            }
        }
    }
}
