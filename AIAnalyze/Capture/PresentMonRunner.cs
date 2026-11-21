using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AIAnalyze.Capture;

/// <summary>Settings for the PresentMon child process, bound from the "PresentMon" config section.</summary>
public sealed class PresentMonOptions
{
    /// <summary>Arguments known to work with PresentMon 2.x: CSV with 1.x column names on stdout.</summary>
    public const string DefaultArguments =
        "--output_stdout --no_console_stats --v1_metrics --session_name AIAnalyze --stop_existing_session";

    /// <summary>
    /// Full path to PresentMon.exe. Empty means: use PresentMon\PresentMon.exe next to the app if it
    /// exists, otherwise FPS capture is disabled.
    /// </summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>Command-line arguments. Empty means <see cref="DefaultArguments"/>.</summary>
    public string Arguments { get; set; } = "";

    /// <summary>
    /// Start PresentMon in the logged-on user's session (needs the service to run as LocalSystem).
    /// Needed because services run in session 0, where PresentMon receives no present events.
    /// Only applies when running as a service.
    /// </summary>
    public bool UserSession { get; set; } = true;

    /// <summary>The settings with defaults filled in, so a fresh install needs no configuration.</summary>
    public PresentMonOptions Resolved(bool runningAsService)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "PresentMon", "PresentMon.exe");
        return new PresentMonOptions
        {
            ExecutablePath = !string.IsNullOrWhiteSpace(ExecutablePath)
                ? ExecutablePath
                : File.Exists(bundled) ? bundled : "",
            Arguments = string.IsNullOrWhiteSpace(Arguments) ? DefaultArguments : Arguments,
            UserSession = UserSession && runningAsService,
        };
    }
}

/// <summary>
/// Runs PresentMon as a child process and streams its stdout line by line.
/// The output is parsed by <see cref="Analysis.PresentMonParser"/>.
/// </summary>
public sealed class PresentMonRunner
{
    private readonly PresentMonOptions _options;

    public PresentMonRunner(PresentMonOptions options) => _options = options;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ExecutablePath);

    /// <param name="onErrorLine">Receives PresentMon's stderr lines, for diagnosing failures under the service.</param>
    public async IAsyncEnumerable<string> ReadLinesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Action<string>? onErrorLine = null)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("PresentMon:ExecutablePath is not set.");
        }
        if (!File.Exists(_options.ExecutablePath))
        {
            throw new FileNotFoundException("PresentMon executable not found.", _options.ExecutablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            Arguments = _options.Arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start PresentMon.");

        if (onErrorLine is not null)
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    onErrorLine(e.Data);
                }
            };
        }
        process.BeginErrorReadLine();

        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }
                yield return line;
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
