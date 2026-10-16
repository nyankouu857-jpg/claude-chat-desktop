using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

// ApiName は Claude に見せる名前。ツールの持ち主ごとに重ならないよう接頭辞を付ける
// AllowAlways が false のツールは「常に許可」を出さない。中身次第で何でもできるもの用。
// ReadOnly は計画モードでも使えるもの。Native は API 定義のツール (memory など) を独自の形で送るときの定義
// ReadOnlyCall は入力によって読むだけになるツール (memory の view など) で、その呼び出しを確認なし・計画モード可にする
public sealed record ToolSpec(string ApiName, string Source, string Name, string? Title, string Description, JsonObject InputSchema, bool AutoApprove = false, bool AllowAlways = true, bool ReadOnly = false, JsonObject? Native = null, Func<JsonObject, bool>? ReadOnlyCall = null);

public sealed class ToolException(string message) : Exception(message);

public sealed record ToolCallResult(JsonArray Content, bool IsError);

public interface IToolProvider
{
    IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources);
    Task<ToolCallResult> CallAsync(string apiName, JsonObject input, CancellationToken cancellationToken);
}

// auto モードの判定に、入力だけでは分からない操作の相手 (クリックする要素など) を見せる
public interface IActionTargetProvider
{
    Task<string?> DescribeTargetAsync(string apiName, JsonObject input, CancellationToken cancellationToken);
}

public sealed class ToolRegistry(IEnumerable<IToolProvider> providers)
{
    readonly List<IToolProvider> providers = providers.ToList();

    // 同じ名前は API が 400 にするので、先に登録した組み込みを優先して 1 つにする
    public IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources) =>
        providers.SelectMany(p => p.GetTools(enabledSources)).DistinctBy(t => t.ApiName).ToList();

    public IEnumerable<string> SourceNames() => GetTools(null).Select(t => t.Source).Distinct();

    public Task<string?> DescribeTargetAsync(string apiName, JsonObject input, ISet<string>? enabledSources, CancellationToken cancellationToken) =>
        providers.FirstOrDefault(p => p.GetTools(enabledSources).Any(t => t.ApiName == apiName)) is IActionTargetProvider provider
            ? provider.DescribeTargetAsync(apiName, input, cancellationToken)
            : Task.FromResult<string?>(null);

    public ToolSpec? Find(string apiName, ISet<string>? enabledSources) =>
        GetTools(enabledSources).FirstOrDefault(t => t.ApiName == apiName);

    // 許可を取ったツールと同じ持ち主に渡す。名前の接頭辞だけで選ぶと、同名の MCP サーバーに取られる
    public async Task<ToolCallResult> CallAsync(string apiName, JsonObject input, ISet<string>? enabledSources, CancellationToken cancellationToken)
    {
        var provider = providers.FirstOrDefault(p => p.GetTools(enabledSources).Any(t => t.ApiName == apiName));
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
