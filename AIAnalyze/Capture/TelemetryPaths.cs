namespace AIAnalyze.Capture;

public static class TelemetryPaths
{
    /// <summary>
    /// Fixed machine-wide data folder. The Windows service runs as a system account, so
    /// per-user folders (LocalApplicationData) would differ from the interactive user's view.
    /// </summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AIAnalyze");

    /// <summary>Single local database shared by the sampler and the FPS monitor.</summary>
    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "telemetry.db");
}
