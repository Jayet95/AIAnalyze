using System.Globalization;
using System.Text;
using AIAnalyze.Storage;

namespace AIAnalyze.Analysis;

/// <summary>One second of a session: FPS plus the key hardware readings at that moment.</summary>
public sealed record TimelineRow(
    long UnixSeconds,
    int Fps,
    IReadOnlyDictionary<string, double?> Metrics);

public static class SessionTimeline
{
    /// <summary>
    /// The readings shown next to FPS. Matched by device kind, sensor type and sensor name.
    /// Names differ between vendors, so a metric can be missing on some machines; it then shows as "-".
    /// </summary>
    public static readonly IReadOnlyList<MetricDefinition> Metrics =
    [
        new("CPU °C", DeviceKind.Cpu, "Temperature", name => name.Contains("Tctl/Tdie") || name == "CPU Package"),
        new("CPU %", DeviceKind.Cpu, "Load", name => name == "CPU Total"),
        new("CPU W", DeviceKind.Cpu, "Power", name => name == "CPU Package"),
        new("GPU °C", DeviceKind.Gpu, "Temperature", name => name == "GPU Core"),
        new("GPU %", DeviceKind.Gpu, "Load", name => name == "GPU Core"),
        new("GPU MHz", DeviceKind.Gpu, "Clock", name => name == "GPU Core"),
    ];

    public sealed record MetricDefinition(
        string Label,
        DeviceKind Device,
        string SensorType,
        Func<string, bool> MatchesSensorName);

    /// <summary>
    /// Builds one row per second that has FPS data. Readings are matched by whole second;
    /// when a metric has several matching sensors (e.g. integrated and discrete GPU), the first one wins.
    /// </summary>
    public static IReadOnlyList<TimelineRow> Build(
        IReadOnlyList<FpsSecond> fpsSeconds,
        IReadOnlyList<StoredReading> readings)
    {
        // second -> metric label -> value
        var valuesBySecond = new Dictionary<long, Dictionary<string, double?>>();

        foreach (var reading in readings)
        {
            var second = reading.TimestampMs / 1000;
            var device = DeviceKinds.Of(reading.SensorId);
            if (device is null)
            {
                continue;
            }

            foreach (var metric in Metrics)
            {
                if (metric.Device != device
                    || metric.SensorType != reading.SensorType
                    || !metric.MatchesSensorName(reading.SensorName))
                {
                    continue;
                }

                if (!valuesBySecond.TryGetValue(second, out var metrics))
                {
                    metrics = new Dictionary<string, double?>();
                    valuesBySecond[second] = metrics;
                }

                metrics.TryAdd(metric.Label, reading.Value);
            }
        }

        var rows = new List<TimelineRow>(fpsSeconds.Count);
        foreach (var fps in fpsSeconds)
        {
            var values = valuesBySecond.GetValueOrDefault(fps.UnixSeconds);
            var metrics = new Dictionary<string, double?>();
            foreach (var metric in Metrics)
            {
                metrics[metric.Label] = values is not null && values.TryGetValue(metric.Label, out var v) ? v : null;
            }
            rows.Add(new TimelineRow(fps.UnixSeconds, fps.Frames, metrics));
        }

        return rows;
    }

    /// <summary>Plain-text table for the console. Times are shown in local time.</summary>
    public static string Format(StoredSession session, IReadOnlyList<TimelineRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{session.Application}  id={session.Id}");
        sb.AppendLine($"{session.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} to {session.EndUtc.ToLocalTime():HH:mm:ss}, " +
                      $"{session.FrameCount} frames, median {session.MedianFps:F1} FPS, {rows.Count} seconds");
        sb.AppendLine();

        sb.Append("Time      FPS ");
        foreach (var metric in Metrics)
        {
            sb.Append($"{metric.Label,8} ");
        }
        sb.AppendLine();

        foreach (var row in rows)
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(row.UnixSeconds).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            sb.Append($"{time}  {row.Fps,4} ");
            foreach (var metric in Metrics)
            {
                var value = row.Metrics[metric.Label];
                sb.Append(value is null ? $"{"-",8} " : $"{value.Value,8:F1} ");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
