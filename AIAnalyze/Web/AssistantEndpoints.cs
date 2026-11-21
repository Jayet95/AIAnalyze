using AIAnalyze.Capture;
using AIAnalyze.Health;
using AIAnalyze.Monitoring;
using AIAnalyze.Sensors;
using AIAnalyze.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AIAnalyze.Web;

/// <summary>
/// The assistant's home screen data: GET /api/assistant/overview (verdict, headline, live issues, failing
/// checks, potential problems, suggested questions), GET /api/issues (live issues only, cheap to poll)
/// and GET /api/trends (potential problems with the daily history behind them).
/// </summary>
public static class AssistantEndpoints
{
    public static void MapAssistant(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/assistant/overview", (HealthState health, LiveIssueEngine live, TrendState trends, CaptureStatus capture, LatestSensors latest) =>
        {
            using var store = new SampleStore(TelemetryPaths.DatabasePath);
            var report = health.Latest ?? store.GetLatestHealthReport();
            return Results.Ok(AssistantOverview.Build(DateTimeOffset.UtcNow, report, live.Current, trends, capture, latest.Current,
                HealthCollector.ReadMemoryFast()));
        });

        api.MapGet("/issues", (LiveIssueEngine live) => Results.Ok(live.Current.Select(i => new
        {
            i.Id, severity = i.Severity.ToString(), i.Title, i.Detail, i.StartedUtc, i.LastSeenUtc, i.Active, i.Application, i.Question,
        })));

        api.MapGet("/trends", (TrendState trends) =>
        {
            using var store = new SampleStore(TelemetryPaths.DatabasePath);
            var history = store.GetDailyStats(DailyStats.Day(DateTimeOffset.UtcNow.AddDays(-45)))
                .GroupBy(s => s.Day)
                .Select(g => new { day = g.Key, values = g.ToDictionary(x => x.Key, x => x.Value) });
            var problems = trends.Problems.Select(p => new { p.Id, severity = p.Severity.ToString(), p.Title, p.Detail, p.Question });
            return Results.Ok(new { atUtc = trends.AtUtc, problems, history });
        });
    }
}
