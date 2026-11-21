namespace AIAnalyze.Capture;

public enum CaptureState
{
    Starting,
    Disabled,
    WaitingForUser,
    Running,
    Restarting,
}

/// <summary>A program presenting frames right now, with its frame rate over the last few seconds.</summary>
public sealed record PresentingApp(string Name, double Fps, bool Excluded);

/// <summary>
/// What FPS capture is doing right now, shared between the capture service and the UI. Frames are counted per
/// program in 5-second windows, so the UI and testers can see which program PresentMon attributes frames to.
/// </summary>
public sealed class CaptureStatus
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Frames, bool Excluded)> _counting = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private IReadOnlyList<PresentingApp> _lastWindow = [];
    private DateTimeOffset _lastWindowEnd = DateTimeOffset.MinValue;

    public CaptureState State { get; private set; } = CaptureState.Starting;
    public string Message { get; private set; } = "Starting.";
    public bool UserSessionMode { get; private set; }
    public DateTimeOffset? LastFrameUtc { get; private set; }
    public DateTimeOffset? LastSessionUtc { get; private set; }

    /// <summary>The busiest program that can become a session (excluded programs such as browsers are skipped).</summary>
    public string? CurrentApplication => Applications.FirstOrDefault(a => !a.Excluded)?.Name;

    /// <summary>Programs presenting in the last complete 5-second window, busiest first; empty when capture went quiet.</summary>
    public IReadOnlyList<PresentingApp> Applications
    {
        get
        {
            lock (_gate)
            {
                return DateTimeOffset.UtcNow - _lastWindowEnd <= Window * 2 ? _lastWindow : [];
            }
        }
    }

    public void Set(CaptureState state, string message)
    {
        lock (_gate)
        {
            State = state;
            Message = message;
        }
    }

    public void SetMode(bool userSession) => UserSessionMode = userSession;

    public void FrameSeen(string application, DateTimeOffset at, bool excluded = false)
    {
        lock (_gate)
        {
            LastFrameUtc = at;
            if (_windowStart == DateTimeOffset.MinValue)
            {
                _windowStart = at;
            }
            else if (at - _windowStart >= Window)
            {
                var seconds = (at - _windowStart).TotalSeconds;
                _lastWindow = _counting
                    .Select(kv => new PresentingApp(kv.Key, Math.Round(kv.Value.Frames / seconds, 1), kv.Value.Excluded))
                    .OrderByDescending(a => a.Fps)
                    .ToList();
                _lastWindowEnd = at;
                _counting.Clear();
                _windowStart = at;
            }

            var current = _counting.GetValueOrDefault(application);
            _counting[application] = (current.Frames + 1, excluded);
        }
    }

    public void SessionSaved(DateTimeOffset at) => LastSessionUtc = at;
}
