using AIAnalyze.Capture;
using Xunit;

namespace AIAnalyze.Tests;

public class UserSessionArgumentsTests
{
    private const string Exe = @"C:\Program Files\AIAnalyze\PresentMon\PresentMon.exe";
    private const string Csv = @"C:\ProgramData\AIAnalyze\presentmon.csv";

    [Fact]
    public void Command_redirects_stdout_to_the_csv_file_through_cmd()
    {
        var line = UserSessionFrameSource.BuildCommandLine(
            Exe, "--output_stdout --no_console_stats --v1_metrics --session_name AIAnalyze", Csv);

        Assert.Contains("cmd.exe", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/c \"\"" + Exe + "\" --output_stdout --no_console_stats", line);
        Assert.EndsWith("> \"" + Csv + "\"\"", line);
    }

    [Fact]
    public void Stdout_option_is_added_once_when_missing()
    {
        var line = UserSessionFrameSource.BuildCommandLine(Exe, "--no_console_stats", Csv);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(line, "--output_stdout"));
    }

    [Fact]
    public void Output_file_option_is_not_used()
    {
        var line = UserSessionFrameSource.BuildCommandLine(Exe, "--output_stdout", Csv);

        Assert.DoesNotContain("--output_file", line);
    }
}
