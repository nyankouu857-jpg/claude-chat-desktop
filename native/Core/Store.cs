using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace ClaudeChat.Native.Core;

public sealed class Store
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 履歴ファイルを開いたときに日本語が \u エスケープだらけにならないように
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    readonly string dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeChat.Native");
    readonly Dictionary<string, Conversation> conversations = [];
    // 削除中にファイルを読み直して復活させないため
    readonly HashSet<string> deletedIds = [];
    List<ConversationSummary>? summaries;
    ChatSettings? settings;

    string ConversationDir => Path.Combine(dataDir, "conversations");
    string IndexPath => Path.Combine(ConversationDir, "index.json");
    string SettingsPath => Path.Combine(dataDir, "settings.json");
    string ConversationPath(string id) => Path.Combine(ConversationDir, $"{id}.json");

    // 書き込み中に落ちても履歴が壊れないように
    static async Task WriteJsonAtomicAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    static async Task<T?> ReadJsonAsync<T>(string path)
    {
        if (!File.Exists(path)) return default;
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, Encoding.UTF8), JsonOptions);
    }

    public async Task<ChatSettings> GetSettingsAsync() =>
        settings ??= await ReadJsonAsync<ChatSettings>(SettingsPath) ?? new ChatSettings();

    public async Task SaveSettingsAsync(Action<ChatSettings> update)
    {
        var current = await GetSettingsAsync();
        update(current);
        await WriteJsonAtomicAsync(SettingsPath, current);
    }

    public async Task SetApiKeyAsync(string apiKey)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), null, DataProtectionScope.CurrentUser);
        await SaveSettingsAsync(s => s.ProtectedApiKey = Convert.ToBase64String(bytes));
    }

    public async Task<string?> GetApiKeyAsync()
    {
        var protectedKey = (await GetSettingsAsync()).ProtectedApiKey;
        if (protectedKey is null) return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedKey), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    // 履歴本体は画像の base64 で重くなるので、一覧は index.json だけ読む
    public async Task<List<ConversationSummary>> ListConversationsAsync()
    {
        summaries ??= await ReadJsonAsync<List<ConversationSummary>>(IndexPath) ?? [];
        return summaries.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public async Task<Conversation?> GetConversationAsync(string id)
    {
        if (deletedIds.Contains(id)) return null;
        if (conversations.TryGetValue(id, out var cached)) return cached;
        var loaded = await ReadJsonAsync<Conversation>(ConversationPath(id));
        if (loaded is not null) conversations[id] = loaded;
        return loaded;
    }

    public Conversation CreateConversation()
    {
        var conversation = new Conversation();
        conversations[conversation.Id] = conversation;
        return conversation;
    }

    public async Task SaveConversationAsync(Conversation conversation)
    {
        conversation.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await WriteJsonAtomicAsync(ConversationPath(conversation.Id), conversation);
        var list = await ListConversationsAsync();
        list.RemoveAll(s => s.Id == conversation.Id);
        list.Insert(0, new ConversationSummary { Id = conversation.Id, Title = conversation.Title, UpdatedAt = conversation.UpdatedAt });
        summaries = list;
        await WriteJsonAtomicAsync(IndexPath, summaries);
    }

    public async Task DeleteConversationAsync(string id)
    {
        deletedIds.Add(id);
        conversations.Remove(id);
        summaries = (await ListConversationsAsync()).Where(s => s.Id != id).ToList();
        await WriteJsonAtomicAsync(IndexPath, summaries);
        File.Delete(ConversationPath(id));
    }
}
