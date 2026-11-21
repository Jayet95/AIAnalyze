using System.Globalization;
using AIAnalyze.Analysis;
using AIAnalyze.Capture;
using AIAnalyze.Chat;
using AIAnalyze.Health;
using AIAnalyze.Storage;

namespace AIAnalyze.Monitoring;

/// <summary>A problem that is building up but has not broken anything yet, found from trends over days or weeks.</summary>
public sealed record PotentialProblem(string Id, FindingSeverity Severity, string Title, string Detail, string Question);

/// <summary>The latest trend analysis, shared with the API, the assistant and the overview.</summary>
public sealed class TrendState
{
    public DateTimeOffset? AtUtc { get; set; }
    public IReadOnlyList<PotentialProblem> Problems { get; set; } = [];
}

/// <summary>
/// Daily aggregates for trends. Readings are kept 2 days, so each day is boiled down to a few numbers that
/// are kept 180 days: temperatures under load and at idle, and free space per drive.
/// </summary>
public static class DailyStats
{
    public const string CpuTempLoad = "cpu_temp_load";
    public const string GpuTempLoad = "gpu_temp_load";
    public const string CpuTempIdle = "cpu_temp_idle";
    public const string GpuTempIdle = "gpu_temp_idle";
    public const string DriveFreePrefix = "drive_free_gb:";

    public static string Day(DateTimeOffset utc) => utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Computes today's aggregates from today's readings and the drives, and saves them.</summary>
    public static void Update(SampleStore store, DateTimeOffset now, IReadOnlyList<DriveSpace> drives)
    {
        var local = now.ToLocalTime();
        var midnight = new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
        var day = Day(now);

        var readings = store.GetReadings(midnight, now, SessionTimeline.SensorNames);
        if (readings.Count > 0)
        {
            var seconds = readings.Select(r => r.TimestampMs / 1000).Distinct().Order().Select(s => new FpsSecond(s, 0)).ToList();
            var rows = SessionTimeline.Build(seconds, readings);
            foreach (var (key, value) in Aggregate(rows))
            {
                store.SetDailyStat(day, key, value);
            }
        }
        foreach (var d in drives)
        {
            store.SetDailyStat(day, DriveFreePrefix + d.Name, Math.Round(d.FreeBytes / 1e9, 2));
        }
    }

    /// <summary>Temperature percentiles under load and at idle, when there are enough seconds of each.</summary>
    public static IEnumerable<(string Key, double Value)> Aggregate(IReadOnlyList<TimelineRow> rows)
    {
        IEnumerable<(string, double)> One(string key, string temp, string load, Func<double, bool> when, double p, int minSeconds)
        {
            var values = rows
                .Where(r => r.Metrics[load] is double l && when(l) && r.Metrics[temp] is not null)
                .Select(r => r.Metrics[temp]!.Value)
                .Order()
                .ToList();
            if (values.Count >= minSeconds)
            {
                yield return (key, Math.Round(values[(int)Math.Round(p * (values.Count - 1))], 1));
            }
        }

        return One(CpuTempLoad, SessionTimeline.CpuTemp, SessionTimeline.CpuLoad, l => l >= 50, 0.9, 300)
            .Concat(One(GpuTempLoad, SessionTimeline.GpuTemp, SessionTimeline.GpuLoad, l => l >= 80, 0.9, 300))
            .Concat(One(CpuTempIdle, SessionTimeline.CpuTemp, SessionTimeline.CpuLoad, l => l < 15, 0.5, 600))
            .Concat(One(GpuTempIdle, SessionTimeline.GpuTemp, SessionTimeline.GpuLoad, l => l < 10, 0.5, 600));
    }
}

/// <summary>
/// Finds potential problems from trends: temperatures creeping up over weeks (dust, dried thermal paste),
/// a drive that will be full soon at its current pace, crashes becoming more frequent, and a game getting
/// slower over its recent sessions. Each needs enough history; with too little data it stays quiet.
/// </summary>
public static class TrendAnalyzer
{
    public static IReadOnlyList<PotentialProblem> Analyze(
        IReadOnlyList<(string Day, string Key, double Value)> stats,
        IReadOnlyList<StoredSession> sessions,
        IReadOnlyList<HealthEvent> events,
        IReadOnlyList<DriveSpace> drives,
        DateTimeOffset now)
    {
        var problems = new List<PotentialProblem>();
        var today = DateOnly.FromDateTime(now.ToLocalTime().Date);

        // Temperatures: last 7 days against 8 to 42 days ago.
        foreach (var (key, part, state, threshold) in new[]
        {
            (DailyStats.GpuTempLoad, "GPU", "under load", 5.0),
            (DailyStats.CpuTempLoad, "CPU", "under load", 5.0),
            (DailyStats.GpuTempIdle, "GPU", "at idle", 5.0),
            (DailyStats.CpuTempIdle, "CPU", "at idle", 5.0),
        })
        {
            var series = stats.Where(s => s.Key == key).Select(s => (Age: Age(today, s.Day), s.Value)).ToList();
            var recent = series.Where(s => s.Age is >= 0 and < 7).Select(s => s.Value).ToList();
            var before = series.Where(s => s.Age is >= 8 and <= 42).Select(s => s.Value).ToList();
            if (recent.Count < 3 || before.Count < 3)
            {
                continue;
            }
            var rise = Median(recent) - Median(before);
            if (rise >= threshold && !problems.Any(p => p.Id == $"temp-rise-{part.ToLowerInvariant()}"))
            {
                problems.Add(new PotentialProblem($"temp-rise-{part.ToLowerInvariant()}", FindingSeverity.Warning,
                    $"{part} running hotter than before",
                    $"Over the last week the {part} ran {F(Median(recent))} °C {state}, {F(rise)} °C more than in the weeks before ({F(Median(before))} °C). " +
                    "A slow rise like this usually means dust in the fans and heatsink or ageing thermal paste.",
                    $"My {part} is running hotter than it used to. What should I check?"));
            }
        }

        // Drives: pace of free space over the last 14 days.
        foreach (var drive in drives)
        {
            var points = stats.Where(s => s.Key == DailyStats.DriveFreePrefix + drive.Name)
                .Select(s => (Age: Age(today, s.Day), s.Value))
                .Where(p => p.Age is >= 0 and <= 14)
                .ToList();
            if (points.Count < 4 || points.Max(p => p.Age) < 5)
            {
                continue;
            }
            var slope = Slope(points.Select(p => (-(double)p.Age, p.Value)).ToList()); // GB per day
            var freeGb = drive.FreeBytes / 1e9;
            if (slope >= -0.2)
            {
                continue;
            }
            var days = freeGb / -slope;
            if (days <= 60)
            {
                problems.Add(new PotentialProblem($"drive-filling-{drive.Name.TrimEnd(':').ToLowerInvariant()}",
                    days <= 14 ? FindingSeverity.Critical : FindingSeverity.Warning,
                    $"{drive.Name} filling up",
                    $"{drive.Name} lost about {F(-slope, "0.#")} GB a day over the last two weeks and has {F(freeGb)} GB left: full in about {F(days)} days at this pace.",
                    $"My {drive.Name} drive is filling up fast. What is taking the space?"));
            }
        }

        // Crashes: last 7 days against the 3 weeks before.
        foreach (var (category, label) in new[] { ("crashes", "Blue screens and unexpected shutdowns"), ("display_driver", "Graphics driver resets"), ("app_crashes", "Program crashes") })
        {
            var matching = events.Where(e => AssistantTools.InCategory(e, category)).ToList();
            var recent = matching.Count(e => now - e.AtUtc <= TimeSpan.FromDays(7));
            var before = matching.Count(e => now - e.AtUtc > TimeSpan.FromDays(7) && now - e.AtUtc <= TimeSpan.FromDays(28));
            var weeklyBefore = before / 3.0;
            var minimum = category == "app_crashes" ? 5 : 3;
            if (recent >= minimum && recent >= 2 * Math.Max(weeklyBefore, 1))
            {
                var earlier = weeklyBefore < 0.5 ? "none in the three weeks before" : $"about {F(weeklyBefore, "0.#")} a week in the three weeks before";
                problems.Add(new PotentialProblem($"crashes-rising-{category}", category == "app_crashes" ? FindingSeverity.Warning : FindingSeverity.Critical,
                    $"{label} becoming more frequent",
                    $"{recent} in the last 7 days, against {earlier}. Something changed recently: a driver or Windows update, a new program, overclocking, or a hardware problem.",
                    $"Why are {label.ToLowerInvariant()} happening more often lately?"));
            }
        }

        // Games: the last 3 sessions against the ones before (at least 3 earlier sessions).
        foreach (var game in sessions.GroupBy(s => s.Application, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = game.OrderByDescending(s => s.StartUtc).ToList();
            if (ordered.Count < 6)
            {
                continue;
            }
            var recent = Median(ordered.Take(3).Select(s => s.MedianFps).ToList());
            var before = Median(ordered.Skip(3).Take(20).Select(s => s.MedianFps).ToList());
            if (before > 0 && recent <= before * 0.85)
            {
                problems.Add(new PotentialProblem($"fps-decline-{game.Key.ToLowerInvariant()}", FindingSeverity.Warning,
                    $"{game.Key} getting slower",
                    $"The last 3 sessions of {game.Key} ran at a median {F(recent)} FPS, {F((1 - recent / before) * 100)}% below the earlier {F(before)} FPS.",
                    $"Why is {game.Key} running slower than it used to?"));
            }
        }

        return problems.OrderByDescending(p => p.Severity).ToList();
    }

    private static int Age(DateOnly today, string day) =>
        DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? today.DayNumber - d.DayNumber : -1;

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static double Slope(IReadOnlyList<(double X, double Y)> points)
    {
        var mx = points.Average(p => p.X);
        var my = points.Average(p => p.Y);
        var den = points.Sum(p => (p.X - mx) * (p.X - mx));
        return den == 0 ? 0 : points.Sum(p => (p.X - mx) * (p.Y - my)) / den;
    }

    private static string F(double v, string format = "0") => v.ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>Updates the daily aggregates every 3 hours and re-runs the trend analysis.</summary>
public sealed class TrendService(ILogger<TrendService> logger, TrendState state) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the sensors and the first health check start first.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Run(DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Trend analysis failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    public void Run(DateTimeOffset now)
    {
        var drives = HealthCollector.ReadDrives();
        using var store = new SampleStore(TelemetryPaths.DatabasePath);
        DailyStats.Update(store, now, drives);
        var stats = store.GetDailyStats(DailyStats.Day(now.AddDays(-45)));
        var problems = TrendAnalyzer.Analyze(stats, store.ListSessions(500), HealthCollector.ReadEvents(TimeSpan.FromDays(28)), drives, now);

        // Notify a potential problem once a week at most (remembered across restarts); the overview and the
        // assistant keep showing it while it holds.
        const string ReportedKey = "trends_reported";
        var reported = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(store.GetSetting(ReportedKey) ?? "{}") ?? [];
        foreach (var p in problems.Where(p => !reported.TryGetValue(p.Id, out var at) || now - at > TimeSpan.FromDays(7)))
        {
            store.AddAlert(now, "Trend", p.Severity, $"{p.Title}: {p.Detail}");
            logger.LogInformation("Potential problem: {Title}: {Detail}", p.Title, p.Detail);
            reported[p.Id] = now;
        }
        store.SetSetting(ReportedKey, System.Text.Json.JsonSerializer.Serialize(
            reported.Where(kv => now - kv.Value <= TimeSpan.FromDays(30)).ToDictionary()));
        state.Problems = problems;
        state.AtUtc = now;
    }
}
