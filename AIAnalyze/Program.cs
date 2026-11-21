using AIAnalyze;
using AIAnalyze.Capture;
using AIAnalyze.Reports;
using AIAnalyze.Sensors;
using AIAnalyze.Storage;
using AIAnalyze.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

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

if (args.Contains("--seed-demo"))
{
    // Development only: writes a synthetic session. Point AIANALYZE_DATA_DIR at a scratch folder first.
    using var demoStore = new SampleStore(TelemetryPaths.DatabasePath);
    var demoId = DemoData.Seed(demoStore, DateTimeOffset.UtcNow.AddMinutes(-10));
    Console.WriteLine($"Seeded demo session {demoId} into {TelemetryPaths.DatabasePath}");
    return;
}

// Session cleanup by start time range, in Unix milliseconds: --count-sessions <from> <to> and --delete-sessions <from> <to>.
var rangeCommand = args.FirstOrDefault(a => a is "--count-sessions" or "--delete-sessions");
if (rangeCommand is not null)
{
    var rangeIndex = Array.IndexOf(args, rangeCommand);
    if (rangeIndex + 2 >= args.Length
        || !long.TryParse(args[rangeIndex + 1], out var fromMs)
        || !long.TryParse(args[rangeIndex + 2], out var toMs))
    {
        Console.WriteLine($"Usage: AIAnalyze {rangeCommand} <fromUnixMs> <toUnixMs>");
        return;
    }

    // The service may be writing; stop it first so the delete does not wait on its lock.
    using var rangeStore = new SampleStore(TelemetryPaths.DatabasePath);
    if (rangeCommand == "--count-sessions")
    {
        Console.WriteLine($"Sessions in range: {rangeStore.CountSessionsBetween(fromMs, toMs)}");
    }
    else
    {
        Console.WriteLine($"Deleted sessions: {rangeStore.DeleteSessionsBetween(fromMs, toMs)}");
    }
    return;
}

if (args.Contains("--sessions"))
{
    // Lists recent saved sessions so one can be inspected with --timeline <id>.
    using var listStore = new SampleStore(TelemetryPaths.DatabasePath);
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
    // Prints the findings and one row per second of a session: FPS next to CPU and GPU readings.
    if (timelineIndex + 1 >= args.Length || !long.TryParse(args[timelineIndex + 1], out var sessionId))
    {
        Console.WriteLine("Usage: AIAnalyze --timeline <sessionId>   (list ids with --sessions)");
        return;
    }

    using var timelineStore = new SampleStore(TelemetryPaths.DatabasePath);
    var session = timelineStore.GetSession(sessionId);
    if (session is null)
    {
        Console.WriteLine($"No session with id {sessionId}.");
        return;
    }

    var report = SessionReports.Build(timelineStore, session);
    Console.Write(AIAnalyze.Analysis.SessionTimeline.Format(session, report.Rows, report.Gpu, report.Findings));
    if (report.Rows.Count == 0)
    {
        Console.WriteLine("No per-second FPS was stored for this session (it was saved before timelines existed).");
    }
    return;
}

// The service's content root must be the install folder, not System32, so the UI files are found.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Local overrides live in ProgramData, which the installer never overwrites. Values here win over appsettings.json.
builder.Configuration.AddJsonFile(
    Path.Combine(TelemetryPaths.DataDirectory, "appsettings.json"),
    optional: true,
    reloadOnChange: true);

// Runs as a Windows service when installed with the install script; a normal console run still works.
builder.Services.AddWindowsService(options => options.ServiceName = "AIAnalyze");
builder.Logging.AddEventLog();

// The UI listens on this machine only.
var port = builder.Configuration.GetValue("Web:Port", 5280);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

builder.Services.Configure<PresentMonOptions>(builder.Configuration.GetSection("PresentMon"));
builder.Services.AddSingleton<CaptureStatus>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<FpsMonitorService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapApi();
app.Run();
