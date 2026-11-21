using System.Diagnostics;

namespace AIAnalyze.Capture;

/// <summary>
/// Cleans up PresentMon's ETW trace session. When PresentMon is killed, its named trace session can
/// stay alive. A stale session was the most likely reason capture stayed silent until a reboot, so the
/// service stops it before each start and after it stops PresentMon.
/// </summary>
public static class EtwSessions
{
    /// <summary>The value after --session_name in the arguments, or null when there is none.</summary>
    public static string? SessionNameFrom(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "--session_name", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1].Trim('"');
            }
        }
        return null;
    }

    /// <summary>
    /// Stops the named trace session with the built-in logman tool. Returns true if a session was
    /// stopped, false if there was none or it could not be stopped. Never throws.
    /// </summary>
    public static bool TryStop(string? sessionName)
    {
        if (string.IsNullOrWhiteSpace(sessionName) || sessionName.Contains('"'))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "logman.exe"),
                Arguments = $"stop \"{sessionName}\" -ets",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
