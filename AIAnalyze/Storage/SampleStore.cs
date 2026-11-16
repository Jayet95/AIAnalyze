using AIAnalyze.Sensors;
using Microsoft.Data.Sqlite;

namespace AIAnalyze.Storage;

/// <summary>
/// Local SQLite store for sensor samples. Each sampling tick is written in its own
/// transaction so a crash loses at most the tick in progress.
/// </summary>
public sealed class SampleStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public SampleStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();

        using var pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();

        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS samples (
                ts_utc      INTEGER NOT NULL,  -- Unix milliseconds
                sensor_id   TEXT    NOT NULL,
                hardware    TEXT    NOT NULL,
                sensor_name TEXT    NOT NULL,
                sensor_type TEXT    NOT NULL,
                value       REAL             -- NULL = present but unreadable
            );
            CREATE INDEX IF NOT EXISTS ix_samples_ts ON samples (ts_utc);
            """;
        create.ExecuteNonQuery();
    }

    public void WriteTick(DateTimeOffset timestamp, IReadOnlyList<SensorSample> samples)
    {
        using var transaction = _connection.BeginTransaction();

        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO samples (ts_utc, sensor_id, hardware, sensor_name, sensor_type, value)
            VALUES ($ts, $id, $hw, $name, $type, $value);
            """;
        var ts = insert.Parameters.Add("$ts", SqliteType.Integer);
        var id = insert.Parameters.Add("$id", SqliteType.Text);
        var hw = insert.Parameters.Add("$hw", SqliteType.Text);
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var type = insert.Parameters.Add("$type", SqliteType.Text);
        var value = insert.Parameters.Add("$value", SqliteType.Real);

        var tickMs = timestamp.ToUnixTimeMilliseconds();
        foreach (var sample in samples)
        {
            ts.Value = tickMs;
            id.Value = sample.SensorId;
            hw.Value = sample.HardwareName;
            name.Value = sample.SensorName;
            type.Value = sample.SensorType;
            value.Value = sample.Value.HasValue ? sample.Value.Value : DBNull.Value;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Deletes samples older than the retention window.</summary>
    public int DeleteOlderThan(TimeSpan retention)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(retention).ToUnixTimeMilliseconds();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM samples WHERE ts_utc < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
