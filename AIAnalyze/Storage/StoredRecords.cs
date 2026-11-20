namespace AIAnalyze.Storage;

/// <summary>A saved gaming session, as listed for inspection.</summary>
public sealed record StoredSession(
    long Id,
    string Application,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int FrameCount,
    double MedianFps);

/// <summary>One sensor value read from the samples table.</summary>
public sealed record StoredReading(
    long TimestampMs,
    string SensorId,
    string HardwareName,
    string SensorName,
    string SensorType,
    double? Value);
