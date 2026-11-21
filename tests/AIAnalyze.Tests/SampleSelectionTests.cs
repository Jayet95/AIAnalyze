using AIAnalyze.Sensors;
using Xunit;

namespace AIAnalyze.Tests;

public class SampleSelectionTests
{
    private static readonly SensorSample[] Samples =
    [
        new("/amdcpu/0/temperature/2", "CPU", "Core (Tctl/Tdie)", "Temperature", 70),
        new("/amdcpu/0/load/0", "CPU", "CPU Total", "Load", 40),
        new("/amdcpu/0/load/3", "CPU", "CPU Core #3", "Load", 90),
        new("/amdcpu/0/voltage/1", "CPU", "Core #1 VID", "Voltage", 1.2f),
        new("/gpu-nvidia/0/clock/0", "GPU", "GPU Core", "Clock", 2700),
    ];

    [Fact]
    public void Every_tenth_tick_stores_all_sensors()
    {
        Assert.Equal(Samples.Length, SampleSelection.ForTick(Samples, 0).Count);
        Assert.Equal(Samples.Length, SampleSelection.ForTick(Samples, 20).Count);
    }

    [Fact]
    public void Other_ticks_store_only_temperatures_and_timeline_sensors()
    {
        var kept = SampleSelection.ForTick(Samples, 7).Select(s => s.SensorName).ToList();

        Assert.Equal(["Core (Tctl/Tdie)", "CPU Total", "GPU Core"], kept);
    }
}
