using System.Globalization;
using System.Text;
using AIAnalyze.Storage;

namespace AIAnalyze.Analysis;

/// <summary>One second of a session: FPS plus the key hardware readings at that moment.</summary>
public sealed record TimelineRow(
    long UnixSeconds,
    int Fps,
    IReadOnlyDictionary<string, double?> Metrics);

/// <summary>The GPU whose readings the timeline shows.</summary>
public sealed record SelectedGpu(string DeviceKey, string Name);

public static class SessionTimeline
{
    public const string CpuTemp = "CPU °C";
    public const string CpuLoad = "CPU %";
    public const string CpuPower = "CPU W";
    public const string GpuTemp = "GPU °C";
    public const string GpuLoad = "GPU %";
    public const string GpuClock = "GPU MHz";

    /// <summary>
    /// The readings shown next to FPS. Matched by device kind, sensor type and sensor name.
    /// Names differ between vendors (AMD calls CPU power "Package", Intel "CPU Package"), so a
    /// metric can be missing on some machines; it then shows as "-".
    /// </summary>
    public static readonly IReadOnlyList<MetricDefinition> Metrics =
    [
        new(CpuTemp, DeviceKind.Cpu, "Temperature", name => name.Contains("Tctl") || name == "CPU Package"),
        new(CpuLoad, DeviceKind.Cpu, "Load", name => name == "CPU Total"),
        new(CpuPower, DeviceKind.Cpu, "Power", name => name is "CPU Package" or "Package"),
        new(GpuTemp, DeviceKind.Gpu, "Temperature", name => name == "GPU Core"),
        new(GpuLoad, DeviceKind.Gpu, "Load", name => name == "GPU Core"),
        new(GpuClock, DeviceKind.Gpu, "Clock", name => name == "GPU Core"),
    ];

    /// <summary>
    /// Sensor names the timeline can use, so queries can skip the hundreds of per-core sensors.
    /// Includes "GPU Memory Total", which is used to tell a discrete GPU from an integrated one.
    /// </summary>
    public static readonly IReadOnlyList<string> SensorNames =
    [
        "Core (Tctl/Tdie)", "CPU (Tctl/Tdie)", "Core (Tctl)", "CPU Package", "Package", "CPU Total",
        "GPU Core", "GPU Memory Total",
    ];

    public sealed record MetricDefinition(
        string Label,
        DeviceKind Device,
        string SensorType,
        Func<string, bool> MatchesSensorName);

    /// <summary>
    /// Picks the GPU that did the work. On a PC with an integrated and a discrete GPU, both report a
    /// "GPU Core" sensor, and the integrated one is often listed first. The GPU with the highest median
    /// load wins; when loads are within 5 points (an idle desktop), the one with more dedicated memory wins.
    /// Returns null when there are no GPU readings.
    /// </summary>
    public static SelectedGpu? SelectGpu(IReadOnlyList<StoredReading> readings)
    {
        var candidates = readings
            .Where(r => DeviceKinds.Of(r.SensorId) == DeviceKind.Gpu)
            .GroupBy(r => DeviceKinds.DeviceKey(r.SensorId))
            .Select(g => new
            {
                Key = g.Key,
                Name = g.First().HardwareName,
                MedianLoad = MedianOrZero(g.Where(r => r.SensorType == "Load" && r.SensorName == "GPU Core")),
                Memory = g.Where(r => r.SensorName == "GPU Memory Total" && r.Value is not null)
                          .Select(r => r.Value!.Value).DefaultIfEmpty(0).Max(),
            })
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var best = candidates[0];
        foreach (var other in candidates.Skip(1))
        {
            var loadDiff = other.MedianLoad - best.MedianLoad;
            if (loadDiff >= 5 || (Math.Abs(loadDiff) < 5 && other.Memory > best.Memory))
            {
                best = other;
            }
        }

        return new SelectedGpu(best.Key, best.Name);
    }

    /// <summary>
    /// Builds one row per second that has FPS data. Readings are matched by whole second.
    /// GPU columns all come from the GPU chosen by <see cref="SelectGpu"/>.
    /// </summary>
    public static IReadOnlyList<TimelineRow> Build(
        IReadOnlyList<FpsSecond> fpsSeconds,
        IReadOnlyList<StoredReading> readings)
    {
        var gpu = SelectGpu(readings);

        // second -> metric label -> value
        var valuesBySecond = new Dictionary<long, Dictionary<string, double?>>();

        foreach (var reading in readings)
        {
            var device = DeviceKinds.Of(reading.SensorId);
            if (device is null)
            {
                continue;
            }
            if (device == DeviceKind.Gpu && DeviceKinds.DeviceKey(reading.SensorId) != gpu?.DeviceKey)
            {
                continue;
            }

            var second = reading.TimestampMs / 1000;
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
    public static string Format(
        StoredSession session,
        IReadOnlyList<TimelineRow> rows,
        SelectedGpu? gpu = null,
        IReadOnlyList<SessionFinding>? findings = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{session.Application}  id={session.Id}");
        sb.AppendLine($"{session.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} to {session.EndUtc.ToLocalTime():HH:mm:ss}, " +
                      $"{session.FrameCount} frames, median {session.MedianFps:F1} FPS, {rows.Count} seconds");
        if (gpu is not null)
        {
            sb.AppendLine($"GPU columns: {gpu.Name}");
        }

        if (findings is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Findings:");
            foreach (var finding in findings)
            {
                sb.AppendLine($"  [{finding.Severity}] {finding.Message}");
            }
        }
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

    private static double MedianOrZero(IEnumerable<StoredReading> readings)
    {
        var values = readings.Where(r => r.Value is not null).Select(r => r.Value!.Value).ToList();
        return values.Count == 0 ? 0 : Stats.Median(values);
    }
}
