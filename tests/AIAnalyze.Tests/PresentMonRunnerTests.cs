using AIAnalyze.Analysis;
using AIAnalyze.Capture;
using Xunit;

namespace AIAnalyze.Tests;

public class PresentMonRunnerTests
{
    [Fact]
    public async Task Missing_executable_is_reported_clearly()
    {
        var runner = new PresentMonRunner(new PresentMonOptions
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), "does-not-exist", "PresentMon.exe"),
        });

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await foreach (var _ in runner.ReadLinesAsync(CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task Unconfigured_runner_does_not_start_anything()
    {
        var runner = new PresentMonRunner(new PresentMonOptions());

        Assert.False(runner.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in runner.ReadLinesAsync(CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task Child_process_output_is_streamed_and_parsed()
    {
        // Stand-in for PresentMon: cmd.exe prints a header and two frame rows, then exits.
        var runner = new PresentMonRunner(new PresentMonOptions
        {
            ExecutablePath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/c echo Application,TimeInSeconds,MsBetweenPresents & echo game.exe,1.0,16.6 & echo game.exe,1.0167,16.7",
        });

        var lines = new List<string>();
        await foreach (var line in runner.ReadLinesAsync(CancellationToken.None))
        {
            lines.Add(line);
        }

        var frames = PresentMonParser.Parse(lines);
        Assert.Equal(2, frames.Count);
        Assert.Equal("game.exe", frames[0].Application);
    }
}
