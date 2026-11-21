namespace AIAnalyze.Capture;

public enum CaptureState
{
    Starting,
    Disabled,
    WaitingForUser,
    Running,
    Restarting,
}

/// <summary>
/// What FPS capture is doing right now, shared between the capture service and the web UI.
/// Written by one thread, read by request threads; each property is a single reference or value write.
/// </summary>
public sealed class CaptureStatus
{
    private readonly object _gate = new();

    public CaptureState State { get; private set; } = CaptureState.Starting;
    public string Message { get; private set; } = "Starting.";
    public bool UserSessionMode { get; private set; }
    public DateTimeOffset? LastFrameUtc { get; private set; }
    public DateTimeOffset? LastSessionUtc { get; private set; }
    public string? CurrentApplication { get; private set; }

    public void Set(CaptureState state, string message)
    {
        lock (_gate)
        {
            State = state;
            Message = message;
        }
    }

    public void SetMode(bool userSession) => UserSessionMode = userSession;

    public void FrameSeen(string application, DateTimeOffset at)
    {
        LastFrameUtc = at;
        CurrentApplication = application;
    }

    public void SessionSaved(DateTimeOffset at) => LastSessionUtc = at;
}
