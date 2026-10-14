using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

// ApiName は Claude に見せる名前。ツールの持ち主ごとに重ならないよう接頭辞を付ける
public sealed record ToolSpec(string ApiName, string Source, string Name, string? Title, string Description, JsonObject InputSchema, bool AutoApprove = false);

public sealed class ToolException(string message) : Exception(message);

public sealed record ToolCallResult(JsonArray Content, bool IsError);

// 今は MCP だけだが、ターミナルやブラウザ操作の組み込みツールも同じ口で足せるようにしておく
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
