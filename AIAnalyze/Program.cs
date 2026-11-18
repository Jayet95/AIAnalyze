using AIAnalyze;
using AIAnalyze.Sensors;

if (args.Contains("--dump-sensors"))
{
    // One-off diagnostic: print every sensor the library sees, then exit.
    using var dumpReader = new SensorReader();
    foreach (var line in dumpReader.DumpAll())
    {
        Console.WriteLine(line);
    }
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
