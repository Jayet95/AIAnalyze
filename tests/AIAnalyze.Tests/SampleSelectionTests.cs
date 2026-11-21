using AIAnalyze.Sensors;
using Xunit;

namespace AIAnalyze.Tests;

public class SampleSelectionTests
{
    [Fact]
    public void Only_sensors_the_app_reads_back_are_stored()
    {
        SensorSample[] samples =
        [
            new("/amdcpu/0/temperature/2", "CPU", "Core (Tctl/Tdie)", "Temperature", 70),
            new("/amdcpu/0/temperature/3", "CPU", "CCD1 (Tdie)", "Temperature", 60),
            new("/amdcpu/0/load/0", "CPU", "CPU Total", "Load", 40),
            new("/amdcpu/0/load/3", "CPU", "CPU Core #3", "Load", 90),
            new("/amdcpu/0/voltage/1", "CPU", "Core #1 VID", "Voltage", 1.2f),
            new("/amdcpu/0/power/0", "CPU", "Package", "Power", 45),
            new("/gpu-nvidia/0/clock/0", "GPU", "GPU Core", "Clock", 2700),
        ];

        var kept = SampleSelection.ToStore(samples).Select(s => s.SensorName).ToList();

        Assert.Equal(["Core (Tctl/Tdie)", "CPU Total", "Package", "GPU Core"], kept);
    }
}
