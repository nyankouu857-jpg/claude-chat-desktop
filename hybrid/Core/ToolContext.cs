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
    // 送る量がこれ以上の会話はキャッシュを 1 時間持たせる。0 で使わない
    public int LongCacheAbove { get; init; } = 100_000;
    // 予定の実行などの急がない送信を Message Batches で送る
    public bool Batch { get; init; }
    // 単価 2 倍で速く返す。対応していないモデルでは無視する
    public bool Fast { get; init; }
    public string ThinkingDisplay { get; init; } = "summarized";
    // 「急いで」の合図。考えすぎている問い合わせを止めて、effort を下げて送り直す
    public HurrySignal? Hurry { get; init; }
    // 応答中に送られた指示を受け取る。受け取った分は消える
    public Func<IReadOnlyList<string>>? TakeInterjections { get; init; }
    // ワークフローのエージェントに決まった形の JSON で答えさせる
    public JsonObject? OutputSchema { get; init; }
    public Func<string>? RebuildSystemPrompt { get; init; }
}

// 画面からの「急いで」を、応答の途中のいま流れている問い合わせにだけ届ける
public sealed class HurrySignal
{
    readonly Lock gate = new();
    CancellationTokenSource? request;
    bool pending;

    public bool Pending
    {
        get { lock (gate) return pending; }
    }

    public CancellationToken Begin(CancellationToken run)
    {
        lock (gate)
        {
            request = CancellationTokenSource.CreateLinkedTokenSource(run);
            return request.Token;
        }
    }

    public void End()
    {
        lock (gate)
        {
            request?.Dispose();
            request = null;
        }
    }

    // ツールを動かしている間に押されたら、次の問い合わせから急がせる
    public void Request()
    {
        lock (gate)
        {
            pending = true;
            request?.Cancel();
        }
    }

    public bool Take()
    {
        lock (gate)
        {
            var was = pending;
            pending = false;
            return was;
        }
    }
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
