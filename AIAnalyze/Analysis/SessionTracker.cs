namespace AIAnalyze.Analysis;

/// <summary>Frames presented during one wall-clock second. The count is that second's FPS.</summary>
public sealed record FpsSecond(long UnixSeconds, int Frames);

/// <summary>A finished gaming session: one application, from first to last frame.</summary>
public sealed record CompletedSession(
    string Application,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int FrameCount,
    double MedianFps,
    IReadOnlyList<FpsSecond> Seconds);

/// <summary>
/// Groups frames into one open session per application. A session ends when that application
/// has no frame for longer than <paramref name="idleGap"/>, or when <see cref="Finish"/> is called.
/// Frames from other applications do not end it, because PresentMon reports every process that
/// presents (the game, the desktop compositor, browsers) and they interleave.
/// </summary>
/// <param name="idleGap">How long an application may be silent before its session ends.</param>
/// <param name="minFrames">Sessions with fewer frames are dropped instead of saved.</param>
/// <param name="excludedApps">Applications to ignore completely, such as dwm.exe.</param>
public sealed class SessionTracker(TimeSpan idleGap, int minFrames = 1, IEnumerable<string>? excludedApps = null)
{
    private readonly Dictionary<string, OpenSession> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excluded = new(excludedApps ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a frame. Returns sessions that ended because this application had been silent
    /// longer than the idle gap before this frame.
    /// </summary>
    public IReadOnlyList<CompletedSession> AddFrame(FrameRecord frame, DateTimeOffset now)
    {
        if (_excluded.Contains(frame.Application))
        {
            return [];
        }

        var closed = new List<CompletedSession>();
        if (_open.TryGetValue(frame.Application, out var existing) && now - existing.Last > idleGap)
        {
            AddIfClosed(closed, existing);
            _open.Remove(frame.Application);
        }

        if (!_open.TryGetValue(frame.Application, out var session))
        {
            session = new OpenSession(frame.Application, now);
            _open[frame.Application] = session;
        }

        session.Add(frame, now);
        return closed;
    }

    /// <summary>
    /// Closes every session whose application has been silent longer than the idle gap.
    /// Called on a timer, because a game that exits sends no frames to trigger <see cref="AddFrame"/>.
    /// </summary>
    public IReadOnlyList<CompletedSession> CloseIdle(DateTimeOffset now)
    {
        var closed = new List<CompletedSession>();
        foreach (var (app, session) in _open.Where(p => now - p.Value.Last > idleGap).ToList())
        {
            AddIfClosed(closed, session);
            _open.Remove(app);
        }
        return closed;
    }

    /// <summary>Closes every open session. Used on shutdown and before a PresentMon restart.</summary>
    public IReadOnlyList<CompletedSession> Finish()
    {
        var closed = new List<CompletedSession>();
        foreach (var session in _open.Values)
        {
            AddIfClosed(closed, session);
        }
        _open.Clear();
        return closed;
    }

    private void AddIfClosed(List<CompletedSession> closed, OpenSession session)
    {
        var completed = session.ToCompleted();
        if (completed is not null && completed.FrameCount >= minFrames)
        {
            closed.Add(completed);
        }
    }

    private sealed class OpenSession(string application, DateTimeOffset start)
    {
        private readonly List<FrameRecord> _frames = [];
        private readonly SortedDictionary<long, int> _framesPerSecond = [];
        private readonly DateTimeOffset _start = start;

        // Maps PresentMon's frame clock (seconds since capture start) to wall-clock time.
        private long _anchorWallMs;
        private double _anchorFrameSeconds = double.NaN;

        public DateTimeOffset Last { get; private set; } = start;

        /// <summary>
        /// Counts the frame in the wall-clock second given by its own PresentMon timestamp, not by
        /// when its line arrived. PresentMon's output arrives in bursts, so arrival time would turn a
        /// steady 550 FPS into alternating 680 and 430.
        /// </summary>
        public void Add(FrameRecord frame, DateTimeOffset now)
        {
            _frames.Add(frame);
            Last = now;

            // Anchor on the first frame; re-anchor if PresentMon's clock goes backwards (a restart).
            if (double.IsNaN(_anchorFrameSeconds) || frame.TimeSeconds < _anchorFrameSeconds)
            {
                _anchorWallMs = now.ToUnixTimeMilliseconds();
                _anchorFrameSeconds = frame.TimeSeconds;
            }

            var wallMs = _anchorWallMs + (long)((frame.TimeSeconds - _anchorFrameSeconds) * 1000);
            var second = Math.Floor(wallMs / 1000.0);
            var key = (long)second;
            _framesPerSecond[key] = _framesPerSecond.GetValueOrDefault(key) + 1;
        }

        public CompletedSession? ToCompleted()
        {
            var fps = BaselineRules.SessionFps(_frames);
            if (fps is null)
            {
                return null;
            }

            var seconds = _framesPerSecond.Select(p => new FpsSecond(p.Key, p.Value)).ToList();
            return new CompletedSession(application, _start, Last, _frames.Count, fps.Value, seconds);
        }
    }
}
