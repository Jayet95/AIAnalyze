using AIAnalyze.Analysis;
using AIAnalyze.Sensors;
using Xunit;

namespace AIAnalyze.Tests;

public class TemperatureRulesTests
{
    private static readonly ThermalLimits Limits = new();

    private static SensorSample Temp(string id, float? value, string hardware = "AMD Ryzen 7 7800X3D") =>
        new(id, hardware, "Core (Tctl/Tdie)", "Temperature", value);

    [Fact]
    public void Cool_cpu_produces_no_finding()
    {
        var findings = TemperatureRules.Evaluate([Temp("/amdcpu/0/temperature/0", 60f)], Limits);

        Assert.Empty(findings);
    }

    [Fact]
    public void Cpu_at_warning_limit_is_a_warning()
    {
        var findings = TemperatureRules.Evaluate([Temp("/amdcpu/0/temperature/0", 85f)], Limits);

        var finding = Assert.Single(findings);
        Assert.Equal(TemperatureSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void Cpu_at_critical_limit_is_critical()
    {
        var findings = TemperatureRules.Evaluate([Temp("/amdcpu/0/temperature/0", 95f)], Limits);

        var finding = Assert.Single(findings);
        Assert.Equal(TemperatureSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Gpu_uses_gpu_limits()
    {
        // 84 °C is below the CPU warning (85) but above the GPU warning (83).
        var findings = TemperatureRules.Evaluate(
            [Temp("/gpu-nvidia/0/temperature/0", 84f, "NVIDIA GeForce RTX 4070")], Limits);

        var finding = Assert.Single(findings);
        Assert.Equal(TemperatureSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void Missing_values_and_other_sensor_types_are_ignored()
    {
        var samples = new[]
        {
            Temp("/amdcpu/0/temperature/0", null),
            new SensorSample("/amdcpu/0/load/0", "AMD Ryzen 7 7800X3D", "CPU Total", "Load", 99f),
        };

        Assert.Empty(TemperatureRules.Evaluate(samples, Limits));
    }

    [Fact]
    public void Unknown_device_kind_is_ignored()
    {
        var findings = TemperatureRules.Evaluate([Temp("/hdd/0/temperature/0", 120f, "Some Drive")], Limits);

        Assert.Empty(findings);
    }

    [Fact]
    public void Custom_limits_are_respected()
    {
        var strict = new ThermalLimits(CpuWarningC: 70f, CpuCriticalC: 80f);

        var findings = TemperatureRules.Evaluate([Temp("/amdcpu/0/temperature/0", 75f)], strict);

        Assert.Equal(TemperatureSeverity.Warning, Assert.Single(findings).Severity);
    }
}
