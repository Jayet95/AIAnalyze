using AIAnalyze.Analysis;
using AIAnalyze.Reports;
using AIAnalyze.Sensors;
using AIAnalyze.Storage;

namespace AIAnalyze.Web;

/// <summary>
/// Writes a realistic synthetic session into a database, for developing and demonstrating the UI
/// without a game or PresentMon. Use only with AIANALYZE_DATA_DIR pointing at a scratch folder.
/// </summary>
public static class DemoData
{
    public static long Seed(SampleStore store, DateTimeOffset end)
    {
        const int seconds = 180;
        var start = end.AddSeconds(-seconds);
        var random = new Random(42);
        var fpsSeconds = new List<FpsSecond>();
        var frames = new List<FrameRecord>();

        for (var i = 0; i < seconds; i++)
        {
            var t = start.AddSeconds(i);
            var progress = i / (double)seconds;

            // The discrete GPU heats up and starts throttling in the second half.
            var gpuTemp = 64 + 22 * progress + random.NextDouble();
            var gpuClock = gpuTemp > 80 ? 2250 + random.Next(-40, 40) : 2730 + random.Next(-20, 20);
            var fps = (int)(gpuClock / 19.0 + random.Next(-4, 4));
            if (i % 37 == 0 && i > 0)
            {
                fps /= 3; // an occasional hitch
            }

            fpsSeconds.Add(new FpsSecond(t.ToUnixTimeSeconds(), fps));
            for (var f = 0; f < fps; f++)
            {
                frames.Add(new FrameRecord("demo-game.exe", i, 1000.0 / fps));
            }

            store.WriteTick(t,
            [
                Sample("/amdcpu/0/temperature/2", "AMD Ryzen 7 7800X3D", "Core (Tctl/Tdie)", "Temperature", 68 + 6 * progress + random.NextDouble() * 2),
                Sample("/amdcpu/0/load/0", "AMD Ryzen 7 7800X3D", "CPU Total", "Load", 38 + random.Next(-6, 6)),
                Sample("/amdcpu/0/power/0", "AMD Ryzen 7 7800X3D", "Package", "Power", 62 + random.Next(-5, 5)),
                // Integrated GPU, listed first, idle.
                Sample("/gpu-amd/0/load/0", "AMD Radeon(TM) Graphics", "GPU Core", "Load", 0),
                Sample("/gpu-amd/0/clock/0", "AMD Radeon(TM) Graphics", "GPU Core", "Clock", 600),
                Sample("/gpu-amd/0/smalldata/0", "AMD Radeon(TM) Graphics", "GPU Memory Total", "SmallData", 512),
                // Discrete GPU, doing the work.
                Sample("/gpu-nvidia/0/temperature/0", "NVIDIA GeForce RTX 5080", "GPU Core", "Temperature", gpuTemp),
                Sample("/gpu-nvidia/0/load/0", "NVIDIA GeForce RTX 5080", "GPU Core", "Load", 97 + random.Next(0, 3)),
                Sample("/gpu-nvidia/0/clock/0", "NVIDIA GeForce RTX 5080", "GPU Core", "Clock", gpuClock),
                Sample("/gpu-nvidia/0/smalldata/0", "NVIDIA GeForce RTX 5080", "GPU Memory Total", "SmallData", 16303),
            ]);
        }

        var median = BaselineRules.SessionFps(frames) ?? 0;
        var session = new CompletedSession("demo-game.exe", start, end, frames.Count, median, fpsSeconds);
        var id = store.InsertSession(session, SessionReports.SettingsKey);
        var stored = store.GetSession(id)!;
        store.SaveFindings(id, SessionReports.Build(store, stored).Findings);
        return id;
    }

    private static SensorSample Sample(string id, string hardware, string name, string type, double value) =>
        new(id, hardware, name, type, (float)value);
}
