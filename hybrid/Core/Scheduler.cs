using System.Globalization;
using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

public sealed class Schedule
{
    public string Id { get; set; } = "sch_" + Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int? IntervalMinutes { get; set; }
    // HH:mm、ローカル時刻
    public string? DailyTime { get; set; }
    // null なら毎回新しいチャットを作る
    public string? ConversationId { get; set; }
    public string? FolderId { get; set; }
    // 新しい会話で動かすときの道具立て。null なら設定の既定
    public JsonObject? Options { get; set; }
    public bool Enabled { get; set; } = true;
    public long? NextRunAt { get; set; }
    public long? LastRunAt { get; set; }
    // Claude Code の /loop と同じく、頼まれた繰り返しは 7 日で切れる。設定画面から作ったものは切れない
    public long? ExpiresAt { get; set; }
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

// アプリを開いている間だけ動く。閉じている間に過ぎた分は、開いたときに 1 回だけ動かす
public sealed class Scheduler(AppPaths paths, ChatHub hub, ConversationStore conversations, IChatEvents events) : IDisposable
{
    const int MinIntervalMinutes = 5;
    // 1 週間より長い間隔は毎日の時刻指定で足りる
    const int MaxIntervalMinutes = 7 * 24 * 60;
    public static readonly TimeSpan LoopLifetime = TimeSpan.FromDays(7);
    static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    readonly SemaphoreSlim gate = new(1, 1);
    List<Schedule>? schedules;
    Timer? timer;
    int ticking;

    async Task<List<Schedule>> LoadAsync() => schedules ??= await Json.ReadAsync<List<Schedule>>(paths.SchedulesFile) ?? [];

    public void Start() => timer ??= new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(5), TickInterval);

    public async Task<List<Schedule>> ListAsync()
    {
        await gate.WaitAsync();
        try
        {
            return (await LoadAsync()).Select(Clone).ToList();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Schedule> SaveAsync(Schedule schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.Title)) throw new ToolException("名前を入れてください");
        if (string.IsNullOrWhiteSpace(schedule.Prompt)) throw new ToolException("内容を入れてください");
        if (schedule.IntervalMinutes is null == string.IsNullOrWhiteSpace(schedule.DailyTime)) throw new ToolException("間隔 (分) か毎日の時刻のどちらか一方を指定してください");
        if (schedule.IntervalMinutes is { } minutes && (minutes < MinIntervalMinutes || minutes > MaxIntervalMinutes)) throw new ToolException($"間隔は {MinIntervalMinutes} 分から {MaxIntervalMinutes} 分の間にしてください");
        if (schedule.DailyTime is { Length: > 0 } time && !TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new ToolException("時刻は HH:mm で指定してください");

        var saved = Clone(schedule);
        saved.Title = saved.Title.Trim();
        saved.NextRunAt = NextRun(saved, DateTimeOffset.Now);
        await gate.WaitAsync();
        try
        {
            var list = await LoadAsync();
            var index = list.FindIndex(s => s.Id == saved.Id);
            if (index >= 0) list[index] = saved;
            else list.Add(saved);
            await Json.WriteAtomicAsync(paths.SchedulesFile, list);
        }
        finally
        {
            gate.Release();
        }
        events.Emit("schedules.changed", null);
        return Clone(saved);
    }

    public async Task DeleteAsync(string id)
    {
        await gate.WaitAsync();
        try
        {
            var list = await LoadAsync();
            if (list.RemoveAll(s => s.Id == id) == 0) throw new ToolException($"{id} という予定はありません");
            await Json.WriteAtomicAsync(paths.SchedulesFile, list);
        }
        finally
        {
            gate.Release();
        }
        events.Emit("schedules.changed", null);
    }

    public static string Describe(Schedule s)
    {
        var when = s.IntervalMinutes is { } m
            ? m % 60 == 0 ? $"{m / 60} 時間ごと" : $"{m} 分ごと"
            : $"毎日 {s.DailyTime}";
        var target = s.ConversationId is null ? "毎回新しいチャット" : "同じチャット";
        var next = s.NextRunAt is { } at ? $"、次回 {DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime:M/d HH:mm}" : "";
        return $"{when}、{target}{next}";
    }

    static long NextRun(Schedule s, DateTimeOffset now)
    {
        if (s.IntervalMinutes is { } minutes) return now.AddMinutes(minutes).ToUnixTimeMilliseconds();
        var time = TimeOnly.ParseExact(s.DailyTime!, "HH:mm", CultureInfo.InvariantCulture);
        var today = new DateTimeOffset(now.Date + time.ToTimeSpan(), now.Offset);
        return (today > now ? today : today.AddDays(1)).ToUnixTimeMilliseconds();
    }

    async Task TickAsync()
    {
        // 前の回が長引いても重ねて動かさない
        if (Interlocked.Exchange(ref ticking, 1) == 1) return;
        try
        {
            var now = DateTimeOffset.Now;
            var nowMs = now.ToUnixTimeMilliseconds();
            List<Schedule> due;
            await gate.WaitAsync();
            try
            {
                var list = await LoadAsync();
                var expired = list.RemoveAll(s => s.ExpiresAt is { } e && e <= nowMs);
                due = list.Where(s => s.Enabled && s.NextRunAt is { } at && at <= nowMs).ToList();
                foreach (var s in due)
                {
                    s.LastRunAt = nowMs;
                    s.NextRunAt = NextRun(s, now);
                }
                if (due.Count > 0 || expired > 0) await Json.WriteAtomicAsync(paths.SchedulesFile, list);
                if (expired > 0) events.Emit("schedules.changed", null);
                due = due.Select(Clone).ToList();
            }
            finally
            {
                gate.Release();
            }
            foreach (var schedule in due) await RunAsync(schedule);
            if (due.Count > 0) events.Emit("schedules.changed", null);
        }
        catch (Exception)
        {
            // 1 回の失敗で予定全体を止めない
        }
        finally
        {
            Volatile.Write(ref ticking, 0);
        }
    }

    async Task RunAsync(Schedule schedule)
    {
        var text = $"<scheduled-task title=\"{schedule.Title.Replace("\"", "'")}\" id=\"{schedule.Id}\">\n{schedule.Prompt}\n</scheduled-task>";
        if (schedule.ConversationId is { } id)
        {
            if (await conversations.GetAsync(id) is { } target)
            {
                await hub.DeliverAsync(target, text, hops: 0);
                return;
            }
            // 送り先のチャットが消えたら、それ以上動かさない
            await SetEnabledAsync(schedule.Id, false);
            return;
        }
        var conversation = conversations.Create();
        conversation.Title = schedule.Title;
        conversation.FolderId = schedule.FolderId;
        conversation.Options = schedule.Options;
        var options = await hub.OptionsForAsync(conversation);
        await hub.StartAsync(conversation, () =>
        {
            conversation.Append(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) });
            return Task.CompletedTask;
        }, options);
    }

    // 画面の古い写しで上書きしないよう、オンオフだけを変える
    public async Task SetEnabledAsync(string id, bool enabled)
    {
        await gate.WaitAsync();
        try
        {
            var list = await LoadAsync();
            var schedule = list.FirstOrDefault(s => s.Id == id) ?? throw new ToolException($"{id} という予定はありません");
            schedule.Enabled = enabled;
            // 止めている間に過ぎた分をまとめて動かさない
            if (enabled) schedule.NextRunAt = NextRun(schedule, DateTimeOffset.Now);
            await Json.WriteAtomicAsync(paths.SchedulesFile, list);
        }
        finally
        {
            gate.Release();
        }
        events.Emit("schedules.changed", null);
    }

    static Schedule Clone(Schedule s) => new()
    {
        Id = s.Id,
        Title = s.Title,
        Prompt = s.Prompt,
        IntervalMinutes = s.IntervalMinutes,
        DailyTime = s.DailyTime,
        ConversationId = s.ConversationId,
        FolderId = s.FolderId,
        Options = s.Options?.DeepClone() as JsonObject,
        Enabled = s.Enabled,
        NextRunAt = s.NextRunAt,
        LastRunAt = s.LastRunAt,
        ExpiresAt = s.ExpiresAt,
        CreatedAt = s.CreatedAt,
    };

    public void Dispose() => timer?.Dispose();
}
