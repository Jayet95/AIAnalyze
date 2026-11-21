namespace AIAnalyze.Capture;

public static class TelemetryPaths
{
    /// <summary>
    /// Fixed machine-wide data folder. The Windows service runs as a system account, so
    /// per-user folders (LocalApplicationData) would differ from the interactive user's view.
    /// The AIANALYZE_DATA_DIR environment variable overrides it, for development and demos.
    /// </summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("AIANALYZE_DATA_DIR") is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AIAnalyze");

    /// <summary>Single local database shared by the sampler and the FPS monitor.</summary>
    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "telemetry.db");
}
