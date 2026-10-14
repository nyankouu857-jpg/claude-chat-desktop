using System.Text.Json.Nodes;

namespace ClaudeChat.Native.Core;

public sealed class TokenUsage
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
}

public sealed class Conversation
{
    public const string DefaultTitle = "新しいチャット";

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = DefaultTitle;
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    // SDK の型に寄せると思考ブロックの署名やWeb検索の暗号化コンテンツを取りこぼすので、API の JSON をそのまま持つ
    public List<JsonObject> Messages { get; set; } = [];
    public TokenUsage Usage { get; set; } = new();
}

public sealed class ConversationSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public long UpdatedAt { get; set; }
}

public sealed class ChatSettings
{
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "medium";
    public string SystemPrompt { get; set; } = "";
    public bool WebSearch { get; set; }
    public string? ProtectedApiKey { get; set; }
}

public static class ModelCatalog
{
    public static readonly (string Id, string Label)[] Models =
    [
        ("claude-opus-5-5", "Opus 5.5"),
        ("claude-fable-5-1", "Fable 5.1"),
        ("claude-sonnet-5-5", "Sonnet 5.5"),
        ("claude-haiku-5-5", "Haiku 5.5"),
    ];

    public static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
}
