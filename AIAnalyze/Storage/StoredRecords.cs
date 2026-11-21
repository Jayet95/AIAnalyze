namespace AIAnalyze.Storage;

/// <summary>A saved gaming session, as listed for inspection.</summary>
public sealed record StoredSession(
    long Id,
    string Application,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int FrameCount,
    double MedianFps);

/// <summary>Something the user should notice: a temperature warning or a serious session finding.</summary>
public sealed record StoredAlert(
    long Id,
    DateTimeOffset AtUtc,
    string Kind,
    string Severity,
    string Message,
    long? SessionId);

/// <summary>One sensor value read from the samples table.</summary>
public sealed record StoredReading(
    long TimestampMs,
    string SensorId,
    string HardwareName,
    string SensorName,
    string SensorType,
    double? Value);
