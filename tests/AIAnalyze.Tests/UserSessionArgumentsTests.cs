using AIAnalyze.Capture;
using Xunit;

namespace AIAnalyze.Tests;

public class UserSessionArgumentsTests
{
    private const string Exe = @"C:\Program Files\AIAnalyze\PresentMon\PresentMon.exe";

    [Fact]
    public void Command_starts_presentmon_directly_with_stdout_option()
    {
        var line = UserSessionFrameSource.BuildCommandLine(
            Exe, "--output_stdout --no_console_stats --v1_metrics --session_name AIAnalyze");

        Assert.Equal("\"" + Exe + "\" --output_stdout --no_console_stats --v1_metrics --session_name AIAnalyze", line);
    }

    [Fact]
    public void Stdout_option_is_added_once_when_missing()
    {
        var line = UserSessionFrameSource.BuildCommandLine(Exe, "--no_console_stats");

        Assert.Equal("\"" + Exe + "\" --output_stdout --no_console_stats", line);
    }

    [Fact]
    public void No_shell_or_file_redirect_is_used()
    {
        var line = UserSessionFrameSource.BuildCommandLine(Exe, "--output_stdout");

        Assert.DoesNotContain("cmd.exe", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(">", line);
        Assert.DoesNotContain("--output_file", line);
    }
}
