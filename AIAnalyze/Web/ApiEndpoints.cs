using AIAnalyze.Analysis;
using AIAnalyze.Capture;
using AIAnalyze.Reports;
using AIAnalyze.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AIAnalyze.Web;

/// <summary>
/// Read-only JSON API for the local UI. Every request opens its own connection; SQLite in WAL mode
/// lets these reads run while the sampler writes.
/// </summary>
public static class ApiEndpoints
{
    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", (CaptureStatus capture) =>
        {
            using var store = Open();
            return Results.Ok(new
            {
                nowUtc = DateTimeOffset.UtcNow,
                lastSampleUtc = store.GetLatestSampleTime(),
                capture = new
                {
                    state = capture.State.ToString(),
                    message = capture.Message,
                    userSession = capture.UserSessionMode,
                    lastFrameUtc = capture.LastFrameUtc,
                    currentApplication = capture.CurrentApplication,
                    lastSessionUtc = capture.LastSessionUtc,
                },
                database = new
                {
                    path = TelemetryPaths.DatabasePath,
                    sizeBytes = FileSize(TelemetryPaths.DatabasePath) + FileSize(TelemetryPaths.DatabasePath + "-wal"),
                },
            });
        });

        api.MapGet("/specs", () =>
        {
            using var store = Open();
            return store.GetLatestSpecs() is { } specs ? Results.Ok(specs) : Results.NotFound();
        });

        api.MapGet("/live", (int? minutes) =>
        {
            using var store = Open();
            var latest = store.GetLatestSampleTime();
            if (latest is null)
            {
                return Results.Ok(new { gpu = (string?)null, points = Array.Empty<object>() });
            }

            var window = TimeSpan.FromMinutes(Math.Clamp(minutes ?? 5, 1, 60));
            var readings = store.GetReadings(latest.Value - window, latest.Value, SessionTimeline.SensorNames);
            var gpu = SessionTimeline.SelectGpu(readings);
            var seconds = readings
                .Select(r => r.TimestampMs / 1000)
                .Distinct()
                .Order()
                .Select(s => new FpsSecond(s, 0))
                .ToList();
            var rows = SessionTimeline.Build(seconds, readings);

            return Results.Ok(new
            {
                gpu = gpu?.Name,
                points = rows.Select(r => new { t = r.UnixSeconds, m = r.Metrics }),
            });
        });

        api.MapGet("/sessions", (int? limit) =>
        {
            using var store = Open();
            var sessions = store.ListSessions(Math.Clamp(limit ?? 100, 1, 1000));
            var worst = store.GetWorstSeverities(sessions.Select(s => s.Id));
            return Results.Ok(sessions.Select(s => new
            {
                s.Id,
                s.Application,
                s.StartUtc,
                s.EndUtc,
                durationSeconds = (s.EndUtc - s.StartUtc).TotalSeconds,
                s.FrameCount,
                s.MedianFps,
                worst = worst.TryGetValue(s.Id, out var severity) ? severity.ToString() : null,
            }));
        });

        api.MapGet("/sessions/{id:long}", (long id) =>
        {
            using var store = Open();
            var session = store.GetSession(id);
            if (session is null)
            {
                return Results.NotFound();
            }

            var report = SessionReports.Build(store, session);
            return Results.Ok(new
            {
                session,
                gpu = report.Gpu?.Name,
                baseline = new
                {
                    kind = report.Deviation.Kind.ToString(),
                    message = report.Deviation.Message,
                    fps = report.Deviation.BaselineFps,
                },
                findings = report.Findings.Select(f => new { f.Kind, severity = f.Severity.ToString(), f.Message }),
                rows = report.Rows.Select(r => new { t = r.UnixSeconds, fps = r.Fps, m = r.Metrics }),
            });
        });
    }

    private static SampleStore Open() => new(TelemetryPaths.DatabasePath);

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
