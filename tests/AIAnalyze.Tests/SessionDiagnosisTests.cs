using AIAnalyze.Analysis;
using Xunit;

namespace AIAnalyze.Tests;

public class SessionDiagnosisTests
{
    private static readonly ThermalLimits Limits = new();

    private static TimelineRow Row(
        int i,
        int fps,
        double? gpuLoad = null,
        double? gpuClock = null,
        double? gpuTemp = null,
        double? cpuTemp = null,
        double? cpuLoad = null) =>
        new(1_800_000_000 + i, fps, new Dictionary<string, double?>
        {
            [SessionTimeline.CpuTemp] = cpuTemp,
            [SessionTimeline.CpuLoad] = cpuLoad,
            [SessionTimeline.CpuPower] = null,
            [SessionTimeline.GpuTemp] = gpuTemp,
            [SessionTimeline.GpuLoad] = gpuLoad,
            [SessionTimeline.GpuClock] = gpuClock,
        });

    private static List<TimelineRow> Rows(int count, Func<int, TimelineRow> make) =>
        Enumerable.Range(0, count).Select(make).ToList();

    private static string[] Kinds(IEnumerable<SessionFinding> findings) => findings.Select(f => f.Kind).ToArray();

    [Fact]
    public void Too_few_seconds_give_no_findings()
    {
        var rows = Rows(5, i => Row(i, 60, gpuLoad: 99, gpuTemp: 95));

        Assert.Empty(SessionDiagnosis.Diagnose(rows, Limits));
    }

    [Fact]
    public void Fully_loaded_gpu_is_reported_as_the_limit()
    {
        var rows = Rows(60, i => Row(i, 97 + i % 5, gpuLoad: 98, gpuClock: 2700, gpuTemp: 70));

        var findings = SessionDiagnosis.Diagnose(rows, Limits);

        Assert.Contains("GpuBound", Kinds(findings));
        Assert.DoesNotContain("NotGpuBound", Kinds(findings));
    }

    [Fact]
    public void Steady_fps_at_a_common_cap_with_spare_gpu_is_reported_as_capped()
    {
        var rows = Rows(60, i => Row(i, 143 + i % 3, gpuLoad: 55));

        var findings = SessionDiagnosis.Diagnose(rows, Limits);

        Assert.Contains("FpsCapped", Kinds(findings));
        Assert.DoesNotContain("NotGpuBound", Kinds(findings));
    }

    [Fact]
    public void Low_gpu_load_without_a_cap_points_elsewhere()
    {
        var rows = Rows(60, i => Row(i, 70 + i % 25, gpuLoad: 50, cpuLoad: 45));

        var finding = Assert.Single(SessionDiagnosis.Diagnose(rows, Limits), f => f.Kind == "NotGpuBound");

        Assert.Contains("45%", finding.Message);
    }

    [Fact]
    public void Cpu_over_the_critical_limit_is_critical()
    {
        var rows = Rows(30, i => Row(i, 90, cpuTemp: i < 20 ? 80 : 96));

        var finding = Assert.Single(SessionDiagnosis.Diagnose(rows, Limits), f => f.Kind == "CpuHot");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Hot_gpu_whose_clock_falls_under_load_is_throttling()
    {
        var rows = Rows(60, i => Row(i, 100, gpuLoad: 99, gpuClock: i < 30 ? 2700 : 2200, gpuTemp: i < 30 ? 70 : 86));

        var findings = SessionDiagnosis.Diagnose(rows, Limits);

        Assert.Contains("GpuThrottling", Kinds(findings));
        Assert.Contains("GpuHot", Kinds(findings));
    }

    [Fact]
    public void Clock_drop_at_normal_temperature_is_only_info()
    {
        var rows = Rows(60, i => Row(i, 100, gpuLoad: 99, gpuClock: i < 30 ? 2700 : 2200, gpuTemp: 65));

        var finding = Assert.Single(SessionDiagnosis.Diagnose(rows, Limits), f => f.Kind == "GpuClockVaries");

        Assert.Equal(FindingSeverity.Info, finding.Severity);
    }

    [Fact]
    public void Frequent_drops_below_half_the_median_are_stutter()
    {
        var rows = Rows(100, i => Row(i, i % 10 == 0 ? 30 : 120));

        Assert.Contains("Stutter", Kinds(SessionDiagnosis.Diagnose(rows, Limits)));
    }

    [Fact]
    public void Rare_drops_are_not_stutter()
    {
        var rows = Rows(100, i => Row(i, i == 50 ? 30 : 120));

        Assert.DoesNotContain("Stutter", Kinds(SessionDiagnosis.Diagnose(rows, Limits)));
    }

    [Fact]
    public void Fps_drop_against_the_baseline_is_included()
    {
        var rows = Rows(30, i => Row(i, 60));
        var deviation = new DeviationResult(DeviationKind.FpsDrop, 60, 100, "game.exe: median FPS 60 is 40% below your baseline of 100.");

        var finding = Assert.Single(SessionDiagnosis.Diagnose(rows, Limits, deviation), f => f.Kind == "FpsDrop");

        Assert.Equal(deviation.Message, finding.Message);
    }

    [Fact]
    public void Missing_sensors_produce_no_guesses()
    {
        var rows = Rows(60, i => Row(i, 97 + i % 7));

        Assert.Empty(SessionDiagnosis.Diagnose(rows, Limits));
    }
}
