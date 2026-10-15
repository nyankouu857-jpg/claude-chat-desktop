using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

public sealed record ChatOptions
{
    public string Model { get; init; } = "claude-opus-5-5";
    public string Effort { get; init; } = "medium";
    public bool WebSearch { get; init; }
    public int WebSearchMaxUses { get; init; } = 5;
    public bool WebFetch { get; init; }
    public int WebFetchMaxUses { get; init; } = 10;
    public bool CodeExecution { get; init; }
    public bool OfficeSkills { get; init; }
    public ISet<string>? EnabledSources { get; init; }
    public string? SystemPrompt { get; init; }
    public int MaxToolTurns { get; init; } = 30;
    public int CompactThreshold { get; init; } = 150_000;
    // ワークフローのエージェントに決まった形の JSON で答えさせる
    public JsonObject? OutputSchema { get; init; }
    public Func<string>? RebuildSystemPrompt { get; init; }
}

// 実行中のツールが、どの会話の何のためのものかを知る手段。引数で回すと全ツールの口が変わるため
public sealed class ToolContext
{
    static readonly AsyncLocal<ToolContext?> current = new();

    public static ToolContext? Current
    {
        get => current.Value;
        set => current.Value = value;
    }

    public required Conversation Conversation { get; init; }
    public required ChatOptions Options { get; init; }
    // 0 はユーザーのチャット、1 はサブエージェント。子はさらに子を作れない
    public int Depth { get; init; }
    // チャット同士の往復の回数。上限で打ち切って無限の応酬を防ぐ
    public int Hops { get; init; }
}
