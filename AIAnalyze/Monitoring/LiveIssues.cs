using System.Globalization;
using AIAnalyze.Analysis;

namespace AIAnalyze.Monitoring;

/// <summary>
/// A problem noticed while the PC runs, such as an FPS drop in the game being played, a throttling GPU,
/// memory running out, or a program keeping the CPU busy in the background. Question is what the user
/// can ask the assistant about it.
/// </summary>
public sealed record LiveIssue(
    string Id,
    FindingSeverity Severity,
    string Title,
    string Detail,
    DateTimeOffset StartedUtc,
    DateTimeOffset LastSeenUtc,
    bool Active,
    string? Application,
    string Question);

/// <summary>What the PC is doing at one moment, as the live monitor reads it every few seconds.</summary>
public sealed record LiveReading(
    DateTimeOffset AtUtc,
    double? CpuTemp = null,
    double? CpuLoad = null,
    double? GpuTemp = null,
    double? GpuLoad = null,
    double? GpuClock = null,
    string? Game = null,
    double? GameFps = null,
    double? GameBaselineFps = null,
    double? MemoryUsedPercent = null,
    double? MemoryTotalGb = null,
    string? TopCpuProcess = null,
    string? OtherPresentingApp = null);

/// <summary>
/// Turns a stream of <see cref="LiveReading"/>s into issues. A condition must hold for a while before it
/// becomes an issue and must be gone for a while before the issue ends, so short spikes and loading
/// screens don't raise anything. An issue that ends and comes back soon after is reopened quietly
/// instead of being reported again.
/// </summary>
public sealed class LiveIssueEngine(ThermalLimits? limits = null)
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan KeepResolved = TimeSpan.FromHours(1);
    public static readonly TimeSpan ReportAgainAfter = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ClockWindow = TimeSpan.FromMinutes(15);

    private readonly ThermalLimits _limits = limits ?? new ThermalLimits();
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _pendingSince = new();
    private readonly Dictionary<string, DateTimeOffset> _lastTrue = new();
    private readonly Dictionary<string, LiveIssue> _issues = new();
    private readonly Dictionary<string, DateTimeOffset> _lastReported = new();
    private readonly Queue<(DateTimeOffset At, double Clock)> _loadedClocks = new();

    /// <summary>Whether a new issue is worth a notification. Heat is notified by the temperature alerts already.</summary>
    public static bool Notifies(string id) => id is not ("cpu-hot" or "gpu-hot");

    private sealed record Condition(string Id, TimeSpan Sustain, FindingSeverity Severity, string Title, string Detail, string Question, string? App = null);

    /// <summary>Active issues first, then those that ended within the last hour, newest first.</summary>
    public IReadOnlyList<LiveIssue> Current
    {
        get
        {
            lock (_gate)
            {
                return _issues.Values
                    .OrderByDescending(i => i.Active)
                    .ThenByDescending(i => i.Severity)
                    .ThenByDescending(i => i.LastSeenUtc)
                    .ToList();
            }
        }
    }

    /// <summary>Evaluates one reading. Returns the issues that should be reported now (new, not recently reported).</summary>
    public IReadOnlyList<LiveIssue> Evaluate(LiveReading r)
    {
        lock (_gate)
        {
            TrackClock(r);
            var now = r.AtUtc;
            var conditions = Conditions(r).ToDictionary(c => c.Id);
            var raised = new List<LiveIssue>();

            foreach (var c in conditions.Values)
            {
                _lastTrue[c.Id] = now;
                if (_issues.TryGetValue(c.Id, out var existing) && (existing.Active || now - existing.LastSeenUtc < ClearAfter))
                {
                    _issues[c.Id] = existing with
                    {
                        LastSeenUtc = now, Active = true, Detail = c.Detail, Title = c.Title,
                        Severity = (FindingSeverity)Math.Max((int)existing.Severity, (int)c.Severity),
                    };
                    continue;
                }

                var since = _pendingSince.TryGetValue(c.Id, out var s) ? s : _pendingSince[c.Id] = now;
                if (now - since < c.Sustain)
                {
                    continue;
                }

                var issue = new LiveIssue(c.Id, c.Severity, c.Title, c.Detail, since, now, true, c.App, c.Question);
                _issues[c.Id] = issue;
                _pendingSince.Remove(c.Id);
                if (!_lastReported.TryGetValue(c.Id, out var reported) || now - reported >= ReportAgainAfter)
                {
                    _lastReported[c.Id] = now;
                    raised.Add(issue);
                }
            }

            // Conditions that stopped: forget pending ones, end issues after a quiet period, drop old ones.
            foreach (var id in _pendingSince.Keys.Where(id => !conditions.ContainsKey(id)).ToList())
            {
                _pendingSince.Remove(id);
            }
            foreach (var issue in _issues.Values.ToList())
            {
                if (issue.Active && !conditions.ContainsKey(issue.Id) && now - _lastTrue.GetValueOrDefault(issue.Id, now) >= ClearAfter)
                {
                    _issues[issue.Id] = issue with { Active = false };
                }
                else if (!issue.Active && now - issue.LastSeenUtc > KeepResolved)
                {
                    _issues.Remove(issue.Id);
                }
            }
            return raised;
        }
    }

    private void TrackClock(LiveReading r)
    {
        if (r.GpuClock is double clock && r.GpuLoad >= 90)
        {
            _loadedClocks.Enqueue((r.AtUtc, clock));
        }
        while (_loadedClocks.Count > 0 && r.AtUtc - _loadedClocks.Peek().At > ClockWindow)
        {
            _loadedClocks.Dequeue();
        }
    }

    private IEnumerable<Condition> Conditions(LiveReading r)
    {
        var game = r.Game;
        var inGame = game is not null && r.GameFps is > 0;
        var where = inGame ? $" while playing {game}" : "";

        if (r.CpuTemp is double cpuTemp && cpuTemp >= _limits.CpuWarningC)
        {
            var critical = cpuTemp >= _limits.CpuCriticalC;
            yield return new Condition("cpu-hot", TimeSpan.FromSeconds(critical ? 5 : 20),
                critical ? FindingSeverity.Critical : FindingSeverity.Warning,
                "CPU running hot",
                $"The CPU is at {F(cpuTemp)} °C{where} (load {F(r.CpuLoad)}%), above the {(critical ? _limits.CpuCriticalC : _limits.CpuWarningC):0} °C limit.",
                "Why is my CPU running hot, and what can I do?", game);
        }

        if (r.GpuTemp is double gpuTemp && gpuTemp >= _limits.GpuWarningC)
        {
            var critical = gpuTemp >= _limits.GpuCriticalC;
            yield return new Condition("gpu-hot", TimeSpan.FromSeconds(critical ? 5 : 20),
                critical ? FindingSeverity.Critical : FindingSeverity.Warning,
                "GPU running hot",
                $"The GPU is at {F(gpuTemp)} °C{where} (load {F(r.GpuLoad)}%), above the {(critical ? _limits.GpuCriticalC : _limits.GpuWarningC):0} °C limit.",
                "Why is my GPU running hot, and what can I do?", game);
        }

        // Throttling: fully loaded, warm, and the clock well below what this GPU reached under load recently.
        if (inGame && r.GpuLoad >= 90 && r.GpuClock is double clock && r.GpuTemp >= _limits.GpuWarningC - 6 && _loadedClocks.Count >= 10)
        {
            var peak = _loadedClocks.Max(c => c.Clock);
            if (clock <= peak * 0.85)
            {
                yield return new Condition("gpu-throttle", TimeSpan.FromSeconds(20), FindingSeverity.Warning,
                    "GPU slowing down to stay cool",
                    $"In {game} the GPU clock dropped to {F(clock)} MHz from {F(peak)} MHz earlier, at {F(r.GpuTemp)} °C and {F(r.GpuLoad)}% load. That usually means it is throttling from heat.",
                    $"My GPU is throttling in {game}. How do I fix it?", game);
            }
        }

        if (inGame && r.GameBaselineFps is double baseline && baseline > 0 && r.GameFps < baseline * 0.7)
        {
            var cause = r.GpuLoad < 70 && r.CpuLoad >= 80
                ? $" The CPU is busy ({F(r.CpuLoad)}%) while the GPU is only at {F(r.GpuLoad)}%: something else is using the CPU, or the game is CPU-limited here.{(r.TopCpuProcess is null ? "" : $" Busiest other program: {r.TopCpuProcess}.")}"
                : r.GpuTemp >= _limits.GpuWarningC
                    ? $" The GPU is hot ({F(r.GpuTemp)} °C), so it may be slowing down."
                    : r.GpuLoad >= 95
                        ? " The GPU is fully loaded, so this part of the game is simply heavier."
                        : "";
            yield return new Condition("fps-drop", TimeSpan.FromSeconds(20), FindingSeverity.Warning,
                $"FPS drop in {game}",
                $"{game} is running at {F(r.GameFps)} FPS, well below its usual {F(baseline)} FPS.{cause}",
                $"Why did my FPS drop in {game} just now?", game);
        }

        if (r.MemoryUsedPercent is double mem && mem >= 92)
        {
            var critical = mem >= 97;
            yield return new Condition("memory-full", TimeSpan.FromSeconds(critical ? 20 : 60),
                critical ? FindingSeverity.Critical : FindingSeverity.Warning,
                "Memory almost full",
                $"{F(mem)}% of {F(r.MemoryTotalGb)} GB RAM is in use{where}. Windows starts moving data to disk, which causes stutter and slowness.",
                "What is using my memory, and how do I free it?", game);
        }

        if (!inGame && r.CpuLoad >= 85)
        {
            yield return new Condition("cpu-busy", TimeSpan.FromMinutes(2), FindingSeverity.Warning,
                "CPU busy in the background",
                $"The CPU has been above 85% for over 2 minutes with no game running (now {F(r.CpuLoad)}%).{(r.TopCpuProcess is null ? "" : $" Busiest program: {r.TopCpuProcess}.")}",
                "Something is keeping my CPU busy. What is it, and is it normal?");
        }

        if (!inGame && r.GpuLoad >= 80)
        {
            yield return new Condition("gpu-busy", TimeSpan.FromMinutes(2), FindingSeverity.Info,
                "GPU busy with no game running",
                $"The GPU has been above 80% for over 2 minutes with no game running (now {F(r.GpuLoad)}%).{(r.OtherPresentingApp is null ? "" : $" {r.OtherPresentingApp} is drawing frames.")}",
                "Why is my GPU busy when I'm not playing?");
        }
    }

    private static string F(double? v) => v is null ? "?" : v.Value.ToString("0", CultureInfo.InvariantCulture);
}
