using AIAnalyze.Sensors;
using Xunit;

namespace AIAnalyze.Tests;

public class CoolingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static SensorSample S(string id, string hardware, string name, string type, float? value) =>
        new(id, hardware, name, type, value);

    private static readonly SensorSample[] TwoFanGpu =
    [
        S("/gpu-nvidia/0/fan/1", "NVIDIA GeForce RTX 4070", "GPU Fan 1", "Fan", 1480),
        S("/gpu-nvidia/0/fan/2", "NVIDIA GeForce RTX 4070", "GPU Fan 2", "Fan", 1495),
        S("/gpu-nvidia/0/control/1", "NVIDIA GeForce RTX 4070", "GPU Fan 1", "Control", 52),
        S("/gpu-nvidia/0/control/2", "NVIDIA GeForce RTX 4070", "GPU Fan 2", "Control", 52),
        S("/gpu-nvidia/0/temperature/0", "NVIDIA GeForce RTX 4070", "GPU Core", "Temperature", 66),
    ];

    [Fact]
    public void Gpu_fans_are_counted_with_rpm_and_duty()
    {
        var report = CoolingSummary.Build(Now, TwoFanGpu);

        var gpu = Assert.Single(report.Gpus);
        Assert.Equal(2, gpu.FanCount);
        Assert.Equal(new FanReading("GPU Fan 1", 1480, 52), gpu.Fans[0]);
        Assert.False(gpu.ZeroRpm);
    }

    [Fact]
    public void A_running_pump_means_liquid_cooling()
    {
        SensorSample[] samples =
        [
            .. TwoFanGpu,
            S("/lpc/nct6798d/0/fan/1", "Nuvoton NCT6798D", "CPU Fan", "Fan", 1100),
            S("/lpc/nct6798d/0/fan/2", "Nuvoton NCT6798D", "AIO Pump", "Fan", 2700),
            S("/lpc/nct6798d/0/fan/3", "Nuvoton NCT6798D", "Fan #4", "Fan", 0),
        ];

        var report = CoolingSummary.Build(Now, samples);

        Assert.Equal(CoolingSummary.Liquid, report.CoolingType);
        Assert.Equal("AIO Pump", Assert.Single(report.Pumps).Name);
        Assert.Equal("CPU Fan", Assert.Single(report.SystemFans).Name); // the empty header (0 RPM) is left out
    }

    [Fact]
    public void Fans_without_a_pump_mean_air_cooling()
    {
        var report = CoolingSummary.Build(Now, [S("/lpc/it8689e/0/fan/0", "ITE IT8689E", "CPU Fan", "Fan", 950)]);

        Assert.Equal(CoolingSummary.Air, report.CoolingType);
    }

    [Fact]
    public void No_fan_data_is_unknown_and_stopped_gpu_fans_are_zero_rpm()
    {
        var report = CoolingSummary.Build(Now,
        [
            S("/gpu-amd/0/fan/0", "AMD Radeon RX 7800 XT", "GPU Fan", "Fan", 0),
            S("/gpu-amd/0/control/0", "AMD Radeon RX 7800 XT", "GPU Fan", "Control", 0),
        ]);

        Assert.Equal(CoolingSummary.Unknown, report.CoolingType);
        Assert.True(Assert.Single(report.Gpus).ZeroRpm);
    }
}
