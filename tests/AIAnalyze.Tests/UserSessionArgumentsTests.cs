using AIAnalyze.Capture;
using Xunit;

namespace AIAnalyze.Tests;

public class UserSessionArgumentsTests
{
    [Fact]
    public void Stdout_option_is_replaced_by_output_file()
    {
        var args = UserSessionFrameSource.BuildArguments(
            "--output_stdout --no_console_stats --v1_metrics --session_name AIAnalyze --stop_existing_session",
            @"C:\ProgramData\AIAnalyze\presentmon.csv");

        Assert.DoesNotContain("--output_stdout", args);
        Assert.Contains("--no_console_stats --v1_metrics --session_name AIAnalyze --stop_existing_session", args);
        Assert.EndsWith("--output_file \"C:\\ProgramData\\AIAnalyze\\presentmon.csv\"", args);
    }

    [Fact]
    public void Empty_arguments_give_only_the_output_file()
    {
        var args = UserSessionFrameSource.BuildArguments("", @"C:\x\presentmon.csv");

        Assert.Equal("--output_file \"C:\\x\\presentmon.csv\"", args);
    }
}
