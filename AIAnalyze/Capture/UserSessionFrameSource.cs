using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AIAnalyze.Capture;

/// <summary>
/// Runs PresentMon in the logged-on user's session and reads its CSV output from an anonymous pipe.
/// A service runs in session 0, where PresentMon receives no present events, so the process is
/// started in the user's session instead. Its stdout and stderr go to pipes that this class reads.
/// If the console session changes, or the service stops, PresentMon is killed and the iterator ends.
/// </summary>
public sealed class UserSessionFrameSource(PresentMonOptions options)
{
    private static readonly TimeSpan SessionCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The PresentMon command line. Adds --output_stdout if the arguments lack it, since stdout is the pipe.
    /// </summary>
    public static string BuildCommandLine(string exePath, string arguments)
    {
        var args = arguments.Contains("--output_stdout", StringComparison.OrdinalIgnoreCase)
            ? arguments.Trim()
            : ("--output_stdout " + arguments).Trim();
        return $"\"{exePath}\" {args}";
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Action<string>? onInfo = null,
        Action<string>? onError = null)
    {
        var sessionId = NativeSession.WTSGetActiveConsoleSessionId();
        if (sessionId == NativeSession.NoActiveSession)
        {
            throw new InvalidOperationException("No user is logged on at the console, so PresentMon cannot start.");
        }

        var exe = options.ExecutablePath;
        using var started = NativeSession.StartInSession(
            sessionId, BuildCommandLine(exe, options.Arguments), Path.GetDirectoryName(exe)!);
        onInfo?.Invoke($"Started PresentMon (pid {started.ProcessId}) in console session {sessionId}.");

        using var process = Process.GetProcessById(started.ProcessId);

        // Stopping the service kills PresentMon, which closes the pipes and ends the read below.
        using var registration = cancellationToken.Register(() => KillQuietly(process));
        _ = Task.Run(() => PumpLines(started.StandardError, onError));
        _ = Task.Run(() => WatchSessionAsync(process, sessionId, onInfo, cancellationToken));

        try
        {
            using var reader = new StreamReader(started.StandardOutput);
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }
                yield return line;
            }
        }
        finally
        {
            KillQuietly(process);
        }
    }

    private static async Task WatchSessionAsync(
        Process process,
        uint sessionId,
        Action<string>? onInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(SessionCheckInterval, cancellationToken);
                if (NativeSession.WTSGetActiveConsoleSessionId() != sessionId)
                {
                    onInfo?.Invoke("The console session changed; PresentMon will be restarted in the new session.");
                    KillQuietly(process);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Service stopping.
        }
    }

    private static void PumpLines(Stream stream, Action<string>? onLine)
    {
        try
        {
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Trim().Length > 0)
                {
                    onLine?.Invoke(line.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed when PresentMon stopped.
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exited.
        }
    }
}
