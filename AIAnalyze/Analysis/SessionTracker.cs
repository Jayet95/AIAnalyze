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
/// Groups frames into sessions. A session ends when there is an idle gap longer than
/// <paramref name="idleGap"/>, or when the application changes.
/// </summary>
public sealed class SessionTracker(TimeSpan idleGap)
{
    private readonly List<FrameRecord> _frames = [];
    private readonly SortedDictionary<long, int> _framesPerSecond = [];
    private string? _application;
    private DateTimeOffset _start;
    private DateTimeOffset _last;

    /// <summary>Adds a frame. Returns a session if the frame closed the previous one.</summary>
    public CompletedSession? AddFrame(FrameRecord frame, DateTimeOffset now)
    {
        CompletedSession? closed = null;

        if (_application is not null &&
            (frame.Application != _application || now - _last > idleGap))
        {
            closed = Close();
        }

        _application ??= frame.Application;
        if (_frames.Count == 0)
        {
            _start = now;
        }

        _frames.Add(frame);
        _last = now;

        var second = now.ToUnixTimeSeconds();
        _framesPerSecond[second] = _framesPerSecond.GetValueOrDefault(second) + 1;
        return closed;
    }

    /// <summary>Closes the current session, if any. Used on shutdown.</summary>
    public CompletedSession? Finish() => _application is null ? null : Close();

    private CompletedSession? Close()
    {
        var fps = BaselineRules.SessionFps(_frames);
        var seconds = _framesPerSecond.Select(p => new FpsSecond(p.Key, p.Value)).ToList();
        var result = fps is null
            ? null
            : new CompletedSession(_application!, _start, _last, _frames.Count, fps.Value, seconds);

        _frames.Clear();
        _framesPerSecond.Clear();
        _application = null;
        return result;
    }
}
