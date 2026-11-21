using AIAnalyze.Analysis;
using AIAnalyze.Health;
using AIAnalyze.Monitoring;
using AIAnalyze.Storage;
using Xunit;

namespace AIAnalyze.Tests;

public class LiveIssueEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 18, 0, 0, TimeSpan.Zero);

    /// <summary>Feeds the same reading every 5 seconds for the given duration and collects what was raised.</summary>
    private static List<LiveIssue> Feed(LiveIssueEngine engine, ref DateTimeOffset at, TimeSpan duration, Func<DateTimeOffset, LiveReading> reading)
    {
        var raised = new List<LiveIssue>();
        var end = at + duration;
        for (; at < end; at = at.AddSeconds(5))
        {
            raised.AddRange(engine.Evaluate(reading(at)));
        }
        return raised;
    }

    [Fact]
    public void Fps_drop_is_raised_after_20_seconds_with_its_likely_cause_and_ends_after_it_recovers()
    {
        var engine = new LiveIssueEngine();
        var at = T0;
        LiveReading Slow(DateTimeOffset t) => new(t, CpuTemp: 70, CpuLoad: 95, GpuTemp: 60, GpuLoad: 45, GpuClock: 2700,
            Game: "game.exe", GameFps: 50, GameBaselineFps: 120, TopCpuProcess: "MsMpEng (40% of the CPU)");

        Assert.Empty(Feed(engine, ref at, TimeSpan.FromSeconds(15), Slow));
        var raised = Feed(engine, ref at, TimeSpan.FromSeconds(10), Slow);

        var issue = Assert.Single(raised);
        Assert.Equal("fps-drop", issue.Id);
        Assert.Contains("50 FPS, well below its usual 120", issue.Detail);
        Assert.Contains("MsMpEng", issue.Detail);
        Assert.Equal("game.exe", issue.Application);

        Feed(engine, ref at, TimeSpan.FromSeconds(40), t => new LiveReading(t, CpuLoad: 50, GpuLoad: 97, Game: "game.exe", GameFps: 118, GameBaselineFps: 120));
        Assert.False(Assert.Single(engine.Current).Active);
    }

    [Fact]
    public void Short_spikes_do_not_raise_anything()
    {
        var engine = new LiveIssueEngine();
        var at = T0;
        for (var i = 0; i < 10; i++)
        {
            Assert.Empty(Feed(engine, ref at, TimeSpan.FromSeconds(10), t => new LiveReading(t, Game: "game.exe", GameFps: 30, GameBaselineFps: 120)));
            Assert.Empty(Feed(engine, ref at, TimeSpan.FromSeconds(10), t => new LiveReading(t, Game: "game.exe", GameFps: 115, GameBaselineFps: 120)));
        }
        Assert.Empty(engine.Current);
    }

    [Fact]
    public void An_issue_that_comes_back_soon_is_not_reported_twice()
    {
        var engine = new LiveIssueEngine();
        var at = T0;
        LiveReading Full(DateTimeOffset t) => new(t, MemoryUsedPercent: 95, MemoryTotalGb: 16);
        LiveReading Fine(DateTimeOffset t) => new(t, MemoryUsedPercent: 60, MemoryTotalGb: 16);

        Assert.Single(Feed(engine, ref at, TimeSpan.FromSeconds(70), Full));
        Feed(engine, ref at, TimeSpan.FromMinutes(2), Fine);
        Assert.Empty(Feed(engine, ref at, TimeSpan.FromSeconds(70), Full));
        Assert.True(Assert.Single(engine.Current).Active);

        Feed(engine, ref at, TimeSpan.FromMinutes(20), Fine);
        Assert.Single(Feed(engine, ref at, TimeSpan.FromSeconds(70), Full));
    }

    [Fact]
    public void Gpu_throttling_needs_a_clock_drop_under_full_load_while_hot()
    {
        var engine = new LiveIssueEngine();
        var at = T0;
        Feed(engine, ref at, TimeSpan.FromMinutes(1), t => new LiveReading(t, GpuTemp: 70, GpuLoad: 98, GpuClock: 2800, Game: "game.exe", GameFps: 140));
        var raised = Feed(engine, ref at, TimeSpan.FromSeconds(30), t => new LiveReading(t, GpuTemp: 82, GpuLoad: 98, GpuClock: 2250, Game: "game.exe", GameFps: 110));

        var issue = Assert.Single(raised);
        Assert.Equal("gpu-throttle", issue.Id);
        Assert.Contains("2250 MHz from 2800 MHz", issue.Detail);
    }

    [Fact]
    public void Background_cpu_load_is_raised_only_without_a_game_and_after_two_minutes()
    {
        var engine = new LiveIssueEngine();
        var at = T0;
        Assert.Empty(Feed(engine, ref at, TimeSpan.FromMinutes(3), t => new LiveReading(t, CpuLoad: 95, Game: "game.exe", GameFps: 90)));
        Assert.Empty(Feed(engine, ref at, TimeSpan.FromSeconds(100), t => new LiveReading(t, CpuLoad: 95, TopCpuProcess: "miner (80% of the CPU)")));
        var issue = Assert.Single(Feed(engine, ref at, TimeSpan.FromSeconds(30), t => new LiveReading(t, CpuLoad: 95, TopCpuProcess: "miner (80% of the CPU)")));
        Assert.Equal("cpu-busy", issue.Id);
        Assert.Contains("miner", issue.Detail);
    }

    [Fact]
    public void Heat_is_tracked_but_left_to_the_temperature_alerts_for_notifications()
    {
        var engine = new LiveIssueEngine();
        var at = T0;
        var issue = Assert.Single(Feed(engine, ref at, TimeSpan.FromSeconds(30), t => new LiveReading(t, GpuTemp: 95, GpuLoad: 99)));
        Assert.Equal("gpu-hot", issue.Id);
        Assert.Equal(FindingSeverity.Critical, issue.Severity);
        Assert.False(LiveIssueEngine.Notifies(issue.Id));
        Assert.True(LiveIssueEngine.Notifies("fps-drop"));
    }
}

public class TrendAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static string Day(int daysAgo) => DailyStats.Day(Now.AddDays(-daysAgo));

    [Fact]
    public void Gpu_temperature_creeping_up_over_weeks_is_a_potential_problem()
    {
        var stats = new List<(string, string, double)>();
        for (var d = 0; d < 7; d++) stats.Add((Day(d), DailyStats.GpuTempLoad, 79));
        for (var d = 10; d < 30; d++) stats.Add((Day(d), DailyStats.GpuTempLoad, 72));
        for (var d = 0; d < 30; d++) stats.Add((Day(d), DailyStats.CpuTempLoad, 70)); // CPU unchanged

        var problems = TrendAnalyzer.Analyze(stats, [], [], [], Now);

        var p = Assert.Single(problems);
        Assert.Equal("temp-rise-gpu", p.Id);
        Assert.Contains("79 °C under load, 7 °C more", p.Detail);
    }

    [Fact]
    public void A_drive_that_will_be_full_soon_is_flagged_with_days_left()
    {
        var stats = Enumerable.Range(0, 10).Select(d => (Day(d), DailyStats.DriveFreePrefix + "C:", 40.0 + 3 * d)).ToList();
        var drives = new[] { new DriveSpace("C:", "", 1_000_000_000_000, 40_000_000_000) };

        var p = Assert.Single(TrendAnalyzer.Analyze(stats, [], [], drives, Now));
        Assert.Equal("drive-filling-c", p.Id);
        Assert.Contains("about 3 GB a day", p.Detail);
        Assert.Contains("full in about 13 days", p.Detail);
        Assert.Equal(FindingSeverity.Critical, p.Severity);
    }

    [Fact]
    public void Steady_drives_and_too_little_history_stay_quiet()
    {
        var steady = Enumerable.Range(0, 10).Select(d => (Day(d), DailyStats.DriveFreePrefix + "C:", 200.0)).ToList();
        var shortHistory = new[] { (Day(0), DailyStats.GpuTempLoad, 90.0), (Day(20), DailyStats.GpuTempLoad, 60.0) };
        var drives = new[] { new DriveSpace("C:", "", 1_000_000_000_000, 200_000_000_000) };

        Assert.Empty(TrendAnalyzer.Analyze(steady.Concat(shortHistory).ToList(), [], [], drives, Now));
    }

    [Fact]
    public void Crashes_becoming_more_frequent_and_a_game_getting_slower_are_found()
    {
        var events = new List<HealthEvent>();
        for (var i = 0; i < 4; i++) events.Add(new HealthEvent(Now.AddDays(-i - 0.5), "System", HealthAnalyzer.ProviderDisplay, 4101, "Warning", "reset"));
        events.Add(new HealthEvent(Now.AddDays(-15), "System", HealthAnalyzer.ProviderDisplay, 4101, "Warning", "reset"));

        var sessions = new List<StoredSession>();
        for (var i = 0; i < 8; i++)
        {
            var start = Now.AddDays(-i);
            sessions.Add(new StoredSession(i, "game.exe", start, start.AddMinutes(30), 100_000, i < 3 ? 80 : 120));
        }

        var problems = TrendAnalyzer.Analyze([], sessions, events, [], Now);

        Assert.Contains(problems, p => p.Id == "crashes-rising-display_driver" && p.Detail.StartsWith("4 in the last 7 days"));
        Assert.Contains(problems, p => p.Id == "fps-decline-game.exe" && p.Detail.Contains("80 FPS, 33% below the earlier 120"));
    }

    [Fact]
    public void Daily_aggregates_need_enough_seconds_under_load()
    {
        var rows = Enumerable.Range(0, 400).Select(i => new TimelineRow(i, 0, new Dictionary<string, double?>
        {
            [SessionTimeline.CpuTemp] = 60, [SessionTimeline.CpuLoad] = 10, [SessionTimeline.CpuPower] = null,
            [SessionTimeline.GpuTemp] = i < 350 ? 75 : 80, [SessionTimeline.GpuLoad] = 95, [SessionTimeline.GpuClock] = 2700,
        })).ToList();

        var stats = DailyStats.Aggregate(rows).ToDictionary(x => x.Key, x => x.Value);

        Assert.Equal(80, stats[DailyStats.GpuTempLoad]);
        Assert.False(stats.ContainsKey(DailyStats.CpuTempLoad));  // CPU never under load
        Assert.False(stats.ContainsKey(DailyStats.CpuTempIdle));  // 400 idle seconds < 600
    }
}

public class AssistantOverviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Headline_and_questions_come_from_live_issues_health_and_trends()
    {
        var health = new HealthReport(Now, TimeSpan.FromDays(2),
        [
            new HealthCheckResult("disk-space", "Disk space", FindingSeverity.Warning, "C: has 8% free.", []),
            new HealthCheckResult("crashes", "System crashes", null, "None.", []),
        ], null, [], [], []);
        var live = new[] { new LiveIssue("fps-drop", FindingSeverity.Warning, "FPS drop in game.exe", "50 vs 120", Now, Now, true, "game.exe", "Why did my FPS drop?") };
        var trends = new TrendState { AtUtc = Now, Problems = [new PotentialProblem("temp-rise-gpu", FindingSeverity.Critical, "GPU running hotter than before", "x", "What should I check?")] };

        var o = AssistantOverview.Build(Now, health, live, trends, null, null, null);

        Assert.Equal("problem", o.Verdict);
        Assert.Equal("3 things need attention: GPU running hotter than before; FPS drop in game.exe; and more.", o.Headline);
        Assert.Equal(["temp-rise-gpu", "fps-drop", "disk-space"], o.Items.Select(i => i.Id));
        Assert.Equal("What should I check?", o.SuggestedQuestions[0]);
        Assert.Equal(4, o.SuggestedQuestions.Count);
    }

    [Fact]
    public void All_clear_reads_as_good()
    {
        var health = new HealthReport(Now, TimeSpan.FromDays(1), [new HealthCheckResult("crashes", "System crashes", null, "None.", [])], null, [], [], []);
        var o = AssistantOverview.Build(Now, health, [], new TrendState(), null, null, null);
        Assert.Equal("good", o.Verdict);
        Assert.StartsWith("Everything looks good", o.Headline);
        Assert.Equal(AssistantOverview.GeneralQuestions, o.SuggestedQuestions);
    }
}
