using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AIAnalyze.Capture;

/// <summary>
/// Runs PresentMon in the logged-on user's session and reads its CSV output from a file.
/// A service runs in session 0, where PresentMon receives no present events, so the process
/// is started in the user's session instead. PresentMon's stdout is redirected to the file
/// through cmd.exe (PresentMon's own --output_file wrote nothing in testing). This class follows
/// the file as it grows. If the console session changes, PresentMon is stopped and the
/// iterator ends, so the caller restarts it in the new session.
/// </summary>
public sealed class UserSessionFrameSource(PresentMonOptions options, string csvPath)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Builds the cmd.exe command line that runs PresentMon with --output_stdout and writes
    /// stdout to <paramref name="csvPath"/>. Adds --output_stdout if the arguments lack it.
    /// </summary>
    public static string BuildCommandLine(string exePath, string arguments, string csvPath)
    {
        var args = arguments.Contains("--output_stdout", StringComparison.OrdinalIgnoreCase)
            ? arguments
            : "--output_stdout " + arguments;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return $"\"{cmd}\" /c \"\"{exePath}\" {args.Trim()} > \"{csvPath}\"\"";
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Action<string>? onInfo = null)
    {
        var session = NativeSession.WTSGetActiveConsoleSessionId();
        if (session == NativeSession.NoActiveSession)
        {
            throw new InvalidOperationException("No user is logged on at the console, so PresentMon cannot start.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        TryDelete(csvPath);

        var exe = options.ExecutablePath;
        var commandLine = BuildCommandLine(exe, options.Arguments, csvPath);
        var pid = NativeSession.StartInSession(session, commandLine, Path.GetDirectoryName(exe)!);
        onInfo?.Invoke($"Started PresentMon (pid {pid}) in console session {session}.");

        using var process = Process.GetProcessById(pid);
        FileStream? file = null;
        StreamReader? reader = null;
        try
        {
            // PresentMon creates the file shortly after it starts.
            while (!File.Exists(csvPath))
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException("PresentMon exited before writing its output file.");
                }
                await Task.Delay(PollInterval, cancellationToken);
            }

            file = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            reader = new StreamReader(file);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is not null)
                {
                    yield return line;
                    continue;
                }

                if (process.HasExited)
                {
                    // Read whatever was written before the exit, then stop.
                    var rest = await reader.ReadToEndAsync(cancellationToken);
                    foreach (var tail in rest.Split('\n'))
                    {
                        if (tail.Length > 0)
                        {
                            yield return tail;
                        }
                    }
                    break;
                }

                if (NativeSession.WTSGetActiveConsoleSessionId() != session)
                {
                    onInfo?.Invoke("The console session changed; PresentMon will be restarted in the new session.");
                    break;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        finally
        {
            reader?.Dispose();
            file?.Dispose();
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Still in use by a previous run; PresentMon will overwrite it.
        }
    }
}
