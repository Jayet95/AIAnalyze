using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AIAnalyze.Capture;

/// <summary>Settings for the PresentMon child process, bound from the "PresentMon" config section.</summary>
public sealed class PresentMonOptions
{
    /// <summary>Full path to PresentMon.exe. Empty means FPS capture is disabled.</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>Command-line arguments. Check them against the installed PresentMon version's --help.</summary>
    public string Arguments { get; set; } = "";
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
