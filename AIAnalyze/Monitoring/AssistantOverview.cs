using AIAnalyze.Analysis;
using AIAnalyze.Capture;
using AIAnalyze.Health;
using AIAnalyze.Sensors;

namespace AIAnalyze.Monitoring;

/// <summary>One thing the assistant brings up: a live issue, a failing health check, or a potential problem.</summary>
public sealed record OverviewItem(
    string Source,          // live | health | trend
    string Id,
    string Severity,        // Info | Warning | Critical
    string Title,
    string Detail,
    bool Active,
    DateTimeOffset? SinceUtc,
    string Question);

public sealed record OverviewNow(double? CpuTemp, double? CpuLoad, double? GpuTemp, double? GpuLoad, double? MemoryPercent);

public sealed record OverviewGame(string Application, double Fps);

/// <summary>What the assistant's home screen shows: a verdict, a one-line headline and the items behind it.</summary>
public sealed record Overview(
    DateTimeOffset AtUtc,
    string Verdict,         // good | attention | problem
    string Headline,
    OverviewGame? Playing,
    OverviewNow? Now,
    IReadOnlyList<OverviewItem> Items,
    IReadOnlyList<string> SuggestedQuestions,
    DateTimeOffset? HealthCheckedUtc,
    DateTimeOffset? TrendsCheckedUtc);

/// <summary>
/// Builds the overview locally, with no model call, so it is free and instant: the assistant's greeting
/// and the "what needs attention" list. The user then asks the assistant about any item.
/// </summary>
public static class AssistantOverview
{
    public static readonly string[] GeneralQuestions =
    [
        "Is anything wrong with my PC right now?",
        "Why does my PC feel slow?",
        "How did my last game session go?",
        "Is my PC running too hot?",
    ];

    public static Overview Build(
        DateTimeOffset now,
        HealthReport? health,
        IReadOnlyList<LiveIssue> live,
        TrendState trends,
        CaptureStatus? capture,
        LatestSensors.Snapshot? sensors,
        MemoryUse? memory)
    {
        var items = new List<OverviewItem>();
        items.AddRange(live.Select(i => new OverviewItem("live", i.Id, i.Severity.ToString(), i.Title, i.Detail, i.Active, i.StartedUtc, i.Question)));
        items.AddRange(trends.Problems.Select(p => new OverviewItem("trend", p.Id, p.Severity.ToString(), p.Title, p.Detail, true, null, p.Question)));
        if (health is not null)
        {
            items.AddRange(health.Checks
                .Where(c => c.Severity is not null)
                .Select(c => new OverviewItem("health", c.Id, c.Severity!.Value.ToString(), c.Title, c.Summary, true, null,
                    $"Tell me more about \"{c.Title}\": what does it mean and what should I do?")));
        }

        var ordered = items
            .OrderByDescending(i => i.Active)
            .ThenByDescending(i => Rank(i.Severity))
            .ThenBy(i => i.Source switch { "live" => 0, "health" => 1, _ => 2 })
            .ToList();
        var open = ordered.Where(i => i.Active && i.Severity != nameof(FindingSeverity.Info)).ToList();

        var verdict = open.Any(i => i.Severity == nameof(FindingSeverity.Critical)) ? "problem" : open.Count > 0 ? "attention" : "good";
        var headline = open.Count switch
        {
            0 when health is null => "Getting to know your PC: the first health check is running.",
            0 => "Everything looks good. I'm keeping an eye on it.",
            1 => $"One thing needs attention: {open[0].Title}.",
            _ => $"{open.Count} things need attention: {string.Join("; ", open.Take(2).Select(i => i.Title))}{(open.Count > 2 ? "; and more" : "")}.",
        };

        var game = capture?.Applications.FirstOrDefault(a => !a.Excluded);
        OverviewNow? nowReadings = null;
        if (sensors is not null)
        {
            var m = LiveMetrics.From(sensors);
            nowReadings = new OverviewNow(m[SessionTimeline.CpuTemp], m[SessionTimeline.CpuLoad], m[SessionTimeline.GpuTemp],
                m[SessionTimeline.GpuLoad], memory is null ? null : Math.Round(memory.UsedPercent, 1));
        }

        var questions = open.Select(i => i.Question)
            .Concat(GeneralQuestions)
            .Distinct()
            .Take(4)
            .ToList();

        return new Overview(now, verdict, headline, game is null ? null : new OverviewGame(game.Name, game.Fps), nowReadings,
            ordered, questions, health?.AtUtc, trends.AtUtc);
    }

    private static int Rank(string severity) => severity switch { "Critical" => 2, "Warning" => 1, _ => 0 };
}
