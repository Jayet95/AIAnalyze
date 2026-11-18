namespace AIAnalyze.Capture;

public static class TelemetryPaths
{
    /// <summary>Single local database shared by the sampler and the FPS monitor.</summary>
    public static string DatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIAnalyze",
        "telemetry.db");
}
