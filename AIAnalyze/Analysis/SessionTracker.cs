namespace AIAnalyze.Analysis;

/// <summary>A finished gaming session: one application, from first to last frame.</summary>
public sealed record CompletedSession(
    string Application,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int FrameCount,
    double MedianFps);

/// <summary>
/// Groups frames into sessions. A session ends when there is an idle gap longer than
/// <paramref name="idleGap"/>, or when the application changes.
/// </summary>
public sealed class SessionTracker(TimeSpan idleGap)
{
    private readonly List<FrameRecord> _frames = [];
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
        return closed;
    }

    /// <summary>Closes the current session, if any. Used on shutdown.</summary>
    public CompletedSession? Finish() => _application is null ? null : Close();

    private CompletedSession? Close()
    {
        var fps = BaselineRules.SessionFps(_frames);
        var result = fps is null
            ? null
            : new CompletedSession(_application!, _start, _last, _frames.Count, fps.Value);

        _frames.Clear();
        _application = null;
        return result;
    }
}
