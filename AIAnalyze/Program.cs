using AIAnalyze;
using AIAnalyze.Sensors;

if (args.Contains("--dump-sensors"))
{
    // One-off diagnostic: print every sensor the library sees, then exit.
    var isAdmin = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    Console.WriteLine($"Running as administrator: {isAdmin}");

    using var dumpReader = new SensorReader();
    foreach (var line in dumpReader.DumpAll())
    {
        Console.WriteLine(line);
    }
    return;
}

if (args.Contains("--sessions"))
{
    // Lists recent saved sessions so one can be inspected with --timeline <id>.
    using var listStore = new AIAnalyze.Storage.SampleStore(AIAnalyze.Capture.TelemetryPaths.DatabasePath);
    foreach (var session in listStore.ListSessions(30))
    {
        Console.WriteLine(
            $"id={session.Id}  {session.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {session.Application}  " +
            $"{session.FrameCount} frames  median {session.MedianFps:F1} FPS");
    }
    return;
}

var timelineIndex = Array.IndexOf(args, "--timeline");
if (timelineIndex >= 0)
{
    // Prints one row per second of a session: FPS next to CPU and GPU readings.
    if (timelineIndex + 1 >= args.Length || !long.TryParse(args[timelineIndex + 1], out var sessionId))
    {
        Console.WriteLine("Usage: AIAnalyze --timeline <sessionId>   (list ids with --sessions)");
        return;
    }

    using var timelineStore = new AIAnalyze.Storage.SampleStore(AIAnalyze.Capture.TelemetryPaths.DatabasePath);
    var session = timelineStore.ListSessions(int.MaxValue).FirstOrDefault(s => s.Id == sessionId);
    if (session is null)
    {
        Console.WriteLine($"No session with id {sessionId}.");
        return;
    }

    var seconds = timelineStore.GetSessionSeconds(sessionId);
    // Pad the window by a second so readings at the edges are included.
    var readings = timelineStore.GetReadings(session.StartUtc.AddSeconds(-1), session.EndUtc.AddSeconds(1));
    Console.Write(AIAnalyze.Analysis.SessionTimeline.Format(
        session, AIAnalyze.Analysis.SessionTimeline.Build(seconds, readings)));
    if (seconds.Count == 0)
    {
        Console.WriteLine("No per-second FPS was stored for this session (it was saved before timelines existed).");
    }
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// Local overrides live in ProgramData, which the installer never overwrites. Values here win over appsettings.json.
builder.Configuration.AddJsonFile(
    Path.Combine(AIAnalyze.Capture.TelemetryPaths.DataDirectory, "appsettings.json"),
    optional: true,
    reloadOnChange: true);

// Runs as a Windows service when installed with the install script; a normal console run still works.
builder.Services.AddWindowsService(options => options.ServiceName = "AIAnalyze");
builder.Logging.AddEventLog();

builder.Services.Configure<AIAnalyze.Capture.PresentMonOptions>(
    builder.Configuration.GetSection("PresentMon"));
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<AIAnalyze.Capture.FpsMonitorService>();

var host = builder.Build();
host.Run();
