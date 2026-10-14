using System.Text.Json.Nodes;

namespace ClaudeChat.Native.Core;

public abstract class ViewBlock;

public sealed class TextView : ViewBlock
{
    public string Text { get; set; } = "";
}

public sealed class ThinkingView : ViewBlock
{
    public string Text { get; set; } = "";
}

public sealed class SearchView : ViewBlock
{
    public string Query { get; set; } = "";
}

public sealed record SearchHit(string Title, string Url);

public sealed class SearchResultView : ViewBlock
{
    public List<SearchHit> Hits { get; init; } = [];
    public string? Error { get; init; }
}

public sealed class ImageView : ViewBlock
{
    public required byte[] Data { get; init; }
}

public sealed class NoticeView : ViewBlock
{
    public required string Text { get; init; }
}

public static class ViewBlocks
{
    public static ViewBlock? FromWire(JsonObject block)
    {
        switch ((string?)block["type"])
        {
            case "text":
                return new TextView { Text = (string?)block["text"] ?? "" };
            case "thinking":
                return new ThinkingView { Text = (string?)block["thinking"] ?? "" };
            case "server_tool_use" when ((string?)block["name"]) == "web_search":
                return new SearchView { Query = (string?)block["input"]?["query"] ?? "" };
            case "web_search_tool_result":
                if (block["content"] is JsonArray results)
                {
                    return new SearchResultView
                    {
                        Hits = results.OfType<JsonObject>()
                            .Select(r => new SearchHit((string?)r["title"] ?? "", (string?)r["url"] ?? ""))
                            .ToList(),
                    };
                }
                return new SearchResultView { Error = (string?)block["content"]?["error_code"] ?? "unknown" };
            case "fallback":
                return new NoticeView
                {
                    Text = $"{block["from"]?["model"]} が応答を断ったため {block["to"]?["model"]} が続きを書きました",
                };
            case "image" when ((string?)block["source"]?["type"]) == "base64":
                return new ImageView { Data = Convert.FromBase64String((string?)block["source"]?["data"] ?? "") };
            default:
                return null;
        }
    }

    public static List<ViewBlock> FromContent(JsonNode? content)
    {
        if (content is JsonValue value) return [new TextView { Text = (string?)value ?? "" }];
        var blocks = new List<ViewBlock>();
        foreach (var block in (content as JsonArray ?? []).OfType<JsonObject>())
        {
            if (FromWire(block) is { } view) Push(blocks, view);
        }
        return blocks;
    }

    // Web検索ありの回答は引用ごとに text が細切れで届く。段落の途中で切れないよう繋げる
    public static int Push(List<ViewBlock> blocks, ViewBlock view)
    {
        if (view is TextView text && blocks.LastOrDefault() is TextView last)
        {
            last.Text += text.Text;
            return blocks.Count - 1;
        }
        blocks.Add(view);
        return blocks.Count - 1;
    }
}
