using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

// ApiName は Claude に見せる名前。ツールの持ち主ごとに重ならないよう接頭辞を付ける
// AllowAlways が false のツールは「常に許可」を出さない。中身次第で何でもできるもの用
public sealed record ToolSpec(string ApiName, string Source, string Name, string? Title, string Description, JsonObject InputSchema, bool AutoApprove = false, bool AllowAlways = true);

public sealed class ToolException(string message) : Exception(message);

public sealed record ToolCallResult(JsonArray Content, bool IsError);

public interface IToolProvider
{
    IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources);
    bool Owns(string apiName);
    Task<ToolCallResult> CallAsync(string apiName, JsonObject input, CancellationToken cancellationToken);
}

public sealed class ToolRegistry(IEnumerable<IToolProvider> providers)
{
    readonly List<IToolProvider> providers = providers.ToList();

    public IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources) =>
        providers.SelectMany(p => p.GetTools(enabledSources)).ToList();

    public ToolSpec? Find(string apiName, ISet<string>? enabledSources) =>
        GetTools(enabledSources).FirstOrDefault(t => t.ApiName == apiName);

    public async Task<ToolCallResult> CallAsync(string apiName, JsonObject input, CancellationToken cancellationToken)
    {
        var provider = providers.FirstOrDefault(p => p.Owns(apiName));
        if (provider is null) return Error($"ツール {apiName} は現在使えません。接続が切れている可能性があります");
        try
        {
            return await provider.CallAsync(apiName, input, cancellationToken);
        }
        catch (ToolException e)
        {
            return Error(e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Error($"ツールの実行に失敗しました: {e.Message}");
        }
    }

    public static ToolCallResult Error(string message) =>
        new(new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }), true);
}

// 組み込みツールで共通の入出力
public static class ToolJson
{
    public static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] fields) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(fields.Select(f => KeyValuePair.Create(f.Name, (JsonNode?)new JsonObject { ["type"] = f.Type, ["description"] = f.Description }))),
        ["required"] = new JsonArray(fields.Where(f => f.Required).Select(f => (JsonNode)f.Name).ToArray()),
        ["additionalProperties"] = false,
    };

    public static ToolCallResult Text(string text) => new(new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), false);

    public static string Required(JsonObject input, string key) =>
        (string?)input[key] is { Length: > 0 } value ? value : throw new ToolException($"{key} を指定してください");
}
