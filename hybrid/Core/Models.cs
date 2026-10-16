namespace ClaudeChat.Core;

// 「他のモデル」で選べる前の世代は、API の受け付ける指定が今の世代と違う
public static class ModelCatalog
{
    // 思考の量を effort ではなくトークン数で決める世代
    public static bool UsesThinkingBudget(string model) => model == "claude-haiku-4-5";

    public static int MaxOutputTokens(string model) => model == "claude-haiku-4-5" ? 64_000 : 128_000;

    // サーバー側の要約 (compact) に対応していない
    public static bool SupportsCompaction(string model) => model != "claude-haiku-4-5";

    // 断られたときに別のモデルで答え直す fallbacks: "default" を受け付けるもの
    public static bool SupportsDefaultFallback(string model) => model is "claude-fable-5-1" or "claude-opus-5-5" or "claude-opus-5" or "claude-sonnet-5-5";

    // xhigh は Opus 4.7 から。それより前は 400 になるので一段下げる
    public static string EffortFor(string model, string effort) =>
        effort == "xhigh" && model is "claude-opus-4-6" or "claude-sonnet-4-6" ? "high" : effort;

    // 思考に回せる量。出力の上限より小さくする決まり
    public static int ThinkingBudget(string effort) => effort switch
    {
        "low" => 4_000,
        "high" => 24_000,
        "xhigh" or "max" => 48_000,
        _ => 10_000,
    };

    public static string DisplayName(string model) => model switch
    {
        "claude-opus-5-5" => "Claude Opus 5.5",
        "claude-fable-5-1" => "Claude Fable 5.1",
        "claude-sonnet-5-5" => "Claude Sonnet 5.5",
        "claude-haiku-5-5" => "Claude Haiku 5.5",
        "claude-haiku-4-5" => "Claude Haiku 4.5",
        "claude-sonnet-5" => "Claude Sonnet 5",
        "claude-opus-5" => "Claude Opus 5",
        "claude-fable-5" => "Claude Fable 5",
        "claude-opus-4-8" => "Claude Opus 4.8",
        "claude-opus-4-7" => "Claude Opus 4.7",
        "claude-opus-4-6" => "Claude Opus 4.6",
        "claude-sonnet-4-6" => "Claude Sonnet 4.6",
        _ => $"Claude ({model})",
    };

    // 公式のシステムプロンプトに書かれている、モデルごとの知識の期限
    public static string KnowledgeCutoff(string model) => model switch
    {
        "claude-haiku-4-5" => "the end of January 2025",
        "claude-opus-4-6" => "the end of May 2025",
        "claude-sonnet-4-6" => "the beginning of August 2025",
        "claude-sonnet-5" or "claude-fable-5" or "claude-opus-4-8" or "claude-opus-4-7" => "the end of January 2026",
        "claude-opus-5" => "the end of May 2026",
        _ => "the end of June 2026",
    };
}
