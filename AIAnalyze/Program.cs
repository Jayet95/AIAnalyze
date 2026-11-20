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

var builder = Host.CreateApplicationBuilder(args);

// Runs as a Windows service when installed with the install script; a normal console run still works.
builder.Services.AddWindowsService(options => options.ServiceName = "AIAnalyze");
builder.Logging.AddEventLog();

builder.Services.Configure<AIAnalyze.Capture.PresentMonOptions>(
    builder.Configuration.GetSection("PresentMon"));
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<AIAnalyze.Capture.FpsMonitorService>();

var host = builder.Build();
host.Run();
