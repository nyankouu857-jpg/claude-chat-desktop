using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

// 単位はすべて 100 万トークンあたりの米ドル
public sealed class ModelPrice
{
    public decimal Input { get; set; }
    public decimal Output { get; set; }
    public decimal CacheWrite { get; set; }
    public decimal CacheRead { get; set; }
    // Haiku 5.5 は 1 リクエストの入力が 10 万トークンを超えると単価が変わる
    public long? LongContextThreshold { get; set; }
    public decimal? LongContextInput { get; set; }
    public decimal? LongContextOutput { get; set; }
}

public sealed class PricingTable
{
    public string Note { get; set; } = "";
    public Dictionary<string, ModelPrice> Models { get; set; } = [];
    public decimal WebSearchPer1000 { get; set; }

    public static PricingTable Default() => new()
    {
        Note = "2026年10月時点の公開価格。単位は100万トークンあたりの米ドル。価格改定があればここを書き換えてください",
        WebSearchPer1000 = 10m,
        Models = new()
        {
            ["claude-opus-5-5"] = new() { Input = 4m, Output = 20m, CacheWrite = 5m, CacheRead = 0.2m },
            ["claude-fable-5-1"] = new() { Input = 10m, Output = 50m, CacheWrite = 12.5m, CacheRead = 0.25m },
            ["claude-sonnet-5-5"] = new() { Input = 2m, Output = 10m, CacheWrite = 2.5m, CacheRead = 0.2m },
            ["claude-haiku-5-5"] = new()
            {
                Input = 0.1m, Output = 0.5m, CacheWrite = 0.125m, CacheRead = 0.01m,
                LongContextThreshold = 100_000, LongContextInput = 0.5m, LongContextOutput = 2.5m,
            },
            // 回答拒否時にサーバー側で切り替わる先のモデル
            ["claude-opus-5"] = new() { Input = 5m, Output = 25m, CacheWrite = 6.25m, CacheRead = 0.5m },
            ["claude-opus-4-8"] = new() { Input = 5m, Output = 25m, CacheWrite = 6.25m, CacheRead = 0.5m },
            ["claude-sonnet-5"] = new() { Input = 2m, Output = 10m, CacheWrite = 2.5m, CacheRead = 0.2m },
        },
    };

    public decimal? Cost(UsageRecord r)
    {
        if (!Models.TryGetValue(r.Model, out var price)) return null;
        var promptTokens = r.Input + r.CacheWrite + r.CacheRead;
        var isLong = price.LongContextThreshold is long threshold && promptTokens > threshold;
        var inputRate = isLong ? price.LongContextInput ?? price.Input : price.Input;
        var outputRate = isLong ? price.LongContextOutput ?? price.Output : price.Output;
        var tokens = r.Input * inputRate + r.Output * outputRate + r.CacheWrite * price.CacheWrite + r.CacheRead * price.CacheRead;
        return tokens / 1_000_000m + r.WebSearches * WebSearchPer1000 / 1000m;
    }
}

public sealed class UsageRecord
{
    public DateTimeOffset At { get; set; }
    public string? ConversationId { get; set; }
    public string Kind { get; set; } = "chat";
    public string Model { get; set; } = "";
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheWrite { get; set; }
    public long CacheRead { get; set; }
    public long WebSearches { get; set; }
    public decimal? CostUsd { get; set; }
}

public sealed class UsageLedger(AppPaths paths)
{
    readonly SemaphoreSlim gate = new(1, 1);
    PricingTable? pricing;
    // 手で直した pricing.json が壊れていても起動とチャットは止めず、画面で知らせる
    public string? PricingError { get; private set; }

    public async Task<PricingTable> GetPricingAsync()
    {
        if (pricing is not null) return pricing;
        try
        {
            pricing = await Json.ReadAsync<PricingTable>(paths.PricingFile);
            PricingError = null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // 直してもらうまで書き換えない
            PricingError = $"pricing.json を読めないため、初期の単価で計算しています: {e.Message}";
            pricing = PricingTable.Default();
            return pricing;
        }
        if (pricing is null)
        {
            pricing = PricingTable.Default();
            await Json.WriteAtomicAsync(paths.PricingFile, pricing);
        }
        return pricing;
    }

    public void ReloadPricing() => pricing = null;

    // 1か月1ファイルの追記型。後から料金表を直しても、トークン数から計算し直せるよう生の数も残す
    public async Task<UsageRecord> RecordAsync(UsageRecord record)
    {
        record.CostUsd = (await GetPricingAsync()).Cost(record);
        var file = Path.Combine(paths.UsageDir, $"{record.At.UtcDateTime:yyyy-MM}.jsonl");
        await gate.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(file, JsonSerializer.Serialize(record, Json.Wire) + "\n");
        }
        finally
        {
            gate.Release();
        }
        return record;
    }

    async Task<List<UsageRecord>> ReadAllAsync()
    {
        var records = new List<UsageRecord>();
        foreach (var file in Directory.EnumerateFiles(paths.UsageDir, "*.jsonl"))
        {
            foreach (var line in await File.ReadAllLinesAsync(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<UsageRecord>(line, Json.Wire) is { } r) records.Add(r);
                }
                catch (JsonException)
                {
                    // 途中で落ちて書きかけになった行だけ捨てる
                }
            }
        }
        return records;
    }

    public async Task<JsonObject> SummaryAsync(TimeZoneInfo zone)
    {
        var table = await GetPricingAsync();
        var records = await ReadAllAsync();
        foreach (var r in records) r.CostUsd = table.Cost(r);

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var lastMonthStart = monthStart.AddMonths(-1);
        DateTimeOffset Local(UsageRecord r) => TimeZoneInfo.ConvertTime(r.At, zone);

        JsonObject Totals(IEnumerable<UsageRecord> items)
        {
            var list = items.ToList();
            return new JsonObject
            {
                ["costUsd"] = list.Sum(r => r.CostUsd ?? 0),
                ["requests"] = list.Count,
                ["input"] = list.Sum(r => r.Input + r.CacheWrite + r.CacheRead),
                ["output"] = list.Sum(r => r.Output),
                ["cacheRead"] = list.Sum(r => r.CacheRead),
                ["webSearches"] = list.Sum(r => r.WebSearches),
                ["unpriced"] = list.Count(r => r.CostUsd is null),
            };
        }

        var thisMonth = records.Where(r => Local(r) >= monthStart).ToList();
        var daily = new JsonArray();
        for (var day = monthStart; day <= now; day = day.AddDays(1))
        {
            var cost = thisMonth.Where(r => Local(r).Date == day.Date).Sum(r => r.CostUsd ?? 0);
            daily.Add(new JsonObject { ["date"] = day.ToString("yyyy-MM-dd"), ["costUsd"] = cost });
        }

        var monthly = new JsonArray(records
            .GroupBy(r => Local(r).ToString("yyyy-MM"))
            .OrderBy(g => g.Key)
            .Select(g => (JsonNode)new JsonObject { ["month"] = g.Key, ["costUsd"] = g.Sum(r => r.CostUsd ?? 0) })
            .ToArray());

        JsonArray ByModel(IEnumerable<UsageRecord> items) => new(items
            .GroupBy(r => r.Model)
            .OrderByDescending(g => g.Sum(r => r.CostUsd ?? 0))
            .Select(g => (JsonNode)new JsonObject
            {
                ["model"] = g.Key,
                ["costUsd"] = g.Sum(r => r.CostUsd ?? 0),
                ["requests"] = g.Count(),
                ["input"] = g.Sum(r => r.Input + r.CacheWrite + r.CacheRead),
                ["output"] = g.Sum(r => r.Output),
            })
            .ToArray());

        return new JsonObject
        {
            ["thisMonth"] = Totals(thisMonth),
            ["lastMonth"] = Totals(records.Where(r => Local(r) >= lastMonthStart && Local(r) < monthStart)),
            ["lifetime"] = Totals(records),
            ["since"] = records.Count == 0 ? null : records.Min(r => r.At).ToString("O"),
            ["daily"] = daily,
            ["monthly"] = monthly,
            ["byModelThisMonth"] = ByModel(thisMonth),
            ["byModelLifetime"] = ByModel(records),
            ["pricingError"] = PricingError,
        };
    }
}

public sealed class OrgCostException(string message) : Exception(message);

// Console の請求データ。アプリを使う前の利用分や、他のツールからの利用分も含まれる
public sealed class OrgCostClient(AppPaths paths, HttpClient http)
{
    // 請求データは最大で数日遅れて確定するので、直近は毎回取り直す
    const int UnsettledDays = 3;
    static readonly DateOnly EarliestDay = new(2023, 3, 1);

    sealed class DayCost
    {
        public decimal TotalCents { get; set; }
        public Dictionary<string, decimal> ByModelCents { get; set; } = [];
        public Dictionary<string, decimal> ByTypeCents { get; set; } = [];
    }

    sealed class CostCache
    {
        public Dictionary<string, DayCost> Days { get; set; } = [];
        public string? FetchedAt { get; set; }
    }

    string CachePath => Path.Combine(paths.UsageDir, "anthropic-cost-cache.json");

    public async Task<JsonObject> FetchAsync(string key, string range, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = range switch
        {
            "month" => new DateOnly(today.Year, today.Month, 1),
            "30d" => today.AddDays(-29),
            "lastMonth" => new DateOnly(today.Year, today.Month, 1).AddMonths(-1),
            _ => EarliestDay,
        };
        var to = range == "lastMonth" ? new DateOnly(today.Year, today.Month, 1) : today.AddDays(1);

        var cache = await Json.ReadAsync<CostCache>(CachePath) ?? new CostCache();
        var settledUntil = today.AddDays(-UnsettledDays);
        var missingFrom = from;
        while (missingFrom < settledUntil && cache.Days.ContainsKey(missingFrom.ToString("yyyy-MM-dd"))) missingFrom = missingFrom.AddDays(1);

        if (missingFrom < to)
        {
            await FetchRangeAsync(key, missingFrom, to, cache, cancellationToken);
            cache.FetchedAt = DateTimeOffset.UtcNow.ToString("O");
            await Json.WriteAtomicAsync(CachePath, cache);
        }

        var days = cache.Days
            .Where(d => DateOnly.Parse(d.Key) >= from && DateOnly.Parse(d.Key) < to)
            .OrderBy(d => d.Key)
            .ToList();
        var byModel = days.SelectMany(d => d.Value.ByModelCents)
            .GroupBy(p => p.Key)
            .Select(g => (JsonNode)new JsonObject { ["model"] = g.Key, ["costUsd"] = g.Sum(p => p.Value) / 100m })
            .OrderByDescending(n => (decimal)n["costUsd"]!)
            .ToArray();
        var byType = days.SelectMany(d => d.Value.ByTypeCents)
            .GroupBy(p => p.Key)
            .Select(g => (JsonNode)new JsonObject { ["type"] = g.Key, ["costUsd"] = g.Sum(p => p.Value) / 100m })
            .ToArray();

        return new JsonObject
        {
            ["range"] = range,
            ["from"] = from.ToString("yyyy-MM-dd"),
            ["to"] = to.AddDays(-1).ToString("yyyy-MM-dd"),
            ["totalUsd"] = days.Sum(d => d.Value.TotalCents) / 100m,
            ["byModel"] = new JsonArray(byModel),
            ["byType"] = new JsonArray(byType),
            ["daily"] = new JsonArray(days
                .Where(d => DateOnly.Parse(d.Key) >= to.AddDays(-31))
                .Select(d => (JsonNode)new JsonObject { ["date"] = d.Key, ["costUsd"] = d.Value.TotalCents / 100m })
                .ToArray()),
            ["fetchedAt"] = cache.FetchedAt,
        };
    }

    async Task FetchRangeAsync(string key, DateOnly from, DateOnly to, CostCache cache, CancellationToken cancellationToken)
    {
        string? page = null;
        do
        {
            // SDK と同じく ANTHROPIC_BASE_URL があればそちらを使う
            var baseUrl = (Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL") ?? "https://api.anthropic.com").TrimEnd('/');
            var url = $"{baseUrl}/v1/organizations/cost_report?starting_at={from:yyyy-MM-dd}T00:00:00Z&ending_at={to:yyyy-MM-dd}T00:00:00Z&group_by[]=description&limit=31";
            if (page is not null) url += $"&page={Uri.EscapeDataString(page)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ClaudeChat", "0.2"));
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
            {
                throw new OrgCostException("このキーでは請求データを読めません。組織の Admin API キー (sk-ant-admin で始まるもの) を設定してください。個人アカウントでは Console で組織を作成する必要があります");
            }
            if (!response.IsSuccessStatusCode) throw new OrgCostException($"請求データの取得に失敗しました ({(int)response.StatusCode}): {body}");

            var json = JsonNode.Parse(body)!;
            foreach (var bucket in json["data"]!.AsArray().OfType<JsonObject>())
            {
                var day = DateTimeOffset.Parse((string)bucket["starting_at"]!, CultureInfo.InvariantCulture).UtcDateTime.ToString("yyyy-MM-dd");
                var cost = new DayCost();
                foreach (var result in bucket["results"]!.AsArray().OfType<JsonObject>())
                {
                    var cents = decimal.Parse((string)result["amount"]!, CultureInfo.InvariantCulture);
                    cost.TotalCents += cents;
                    var model = (string?)result["model"] ?? "その他";
                    cost.ByModelCents[model] = cost.ByModelCents.GetValueOrDefault(model) + cents;
                    var type = (string?)result["cost_type"] ?? "other";
                    cost.ByTypeCents[type] = cost.ByTypeCents.GetValueOrDefault(type) + cents;
                }
                cache.Days[day] = cost;
            }
            page = (bool?)json["has_more"] == true ? (string?)json["next_page"] : null;
        }
        while (page is not null);
    }
}
