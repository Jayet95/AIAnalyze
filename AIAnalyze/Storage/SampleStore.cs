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
            CREATE TABLE IF NOT EXISTS sessions (
                id           INTEGER PRIMARY KEY,
                application  TEXT    NOT NULL,
                settings_key TEXT    NOT NULL,
                start_utc    INTEGER NOT NULL,  -- Unix milliseconds
                end_utc      INTEGER NOT NULL,
                frame_count  INTEGER NOT NULL,
                median_fps   REAL    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sessions_app ON sessions (application, settings_key);
            CREATE TABLE IF NOT EXISTS fps_seconds (
                session_id INTEGER NOT NULL,
                ts_utc     INTEGER NOT NULL,  -- Unix milliseconds, start of the second
                frames     INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_fps_session ON fps_seconds (session_id, ts_utc);
            CREATE TABLE IF NOT EXISTS system_snapshots (
                id           INTEGER PRIMARY KEY,
                captured_utc INTEGER NOT NULL,  -- Unix milliseconds
                specs_json   TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS session_findings (
                session_id INTEGER NOT NULL,
                kind       TEXT    NOT NULL,
                severity   TEXT    NOT NULL,
                message    TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_findings_session ON session_findings (session_id);
            """;
        create.ExecuteNonQuery();
    }

    /// <summary>Median FPS of the sessions of this game that started before the given time.</summary>
    public IReadOnlyList<double> GetSessionFpsBefore(string application, string settingsKey, DateTimeOffset before)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT median_fps FROM sessions
            WHERE application = $app AND settings_key = $key AND start_utc < $before;
            """;
        cmd.Parameters.AddWithValue("$app", application);
        cmd.Parameters.AddWithValue("$key", settingsKey);
        cmd.Parameters.AddWithValue("$before", before.ToUnixTimeMilliseconds());

        var values = new List<double>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            values.Add(reader.GetDouble(0));
        }
        return values;
    }

    /// <summary>Replaces the stored findings for a session.</summary>
    public void SaveFindings(long sessionId, IReadOnlyList<Analysis.SessionFinding> findings)
    {
        using var transaction = _connection.BeginTransaction();

        using var clear = _connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM session_findings WHERE session_id = $id;";
        clear.Parameters.AddWithValue("$id", sessionId);
        clear.ExecuteNonQuery();

        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO session_findings (session_id, kind, severity, message) VALUES ($id, $kind, $sev, $msg);";
        var id = insert.Parameters.Add("$id", SqliteType.Integer);
        var kind = insert.Parameters.Add("$kind", SqliteType.Text);
        var sev = insert.Parameters.Add("$sev", SqliteType.Text);
        var msg = insert.Parameters.Add("$msg", SqliteType.Text);
        id.Value = sessionId;
        foreach (var finding in findings)
        {
            kind.Value = finding.Kind;
            sev.Value = finding.Severity.ToString();
            msg.Value = finding.Message;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Stored findings for a session, most severe first.</summary>
    public IReadOnlyList<Analysis.SessionFinding> GetFindings(long sessionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT kind, severity, message FROM session_findings WHERE session_id = $id;";
        cmd.Parameters.AddWithValue("$id", sessionId);

        var rows = new List<Analysis.SessionFinding>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var severity = Enum.TryParse<Analysis.FindingSeverity>(reader.GetString(1), out var s) ? s : Analysis.FindingSeverity.Info;
            rows.Add(new Analysis.SessionFinding(reader.GetString(0), severity, reader.GetString(2)));
        }
        return rows.OrderByDescending(f => f.Severity).ToList();
    }

    /// <summary>One saved session by id, or null.</summary>
    public StoredSession? GetSession(long sessionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, application, start_utc, end_utc, frame_count, median_fps
            FROM sessions WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new StoredSession(
                reader.GetInt64(0),
                reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                reader.GetInt32(4),
                reader.GetDouble(5))
            : null;
    }

    /// <summary>The highest finding severity per session, for session lists.</summary>
    public IReadOnlyDictionary<long, Analysis.FindingSeverity> GetWorstSeverities(IEnumerable<long> sessionIds)
    {
        var result = new Dictionary<long, Analysis.FindingSeverity>();
        foreach (var id in sessionIds)
        {
            var findings = GetFindings(id);
            if (findings.Count > 0)
            {
                result[id] = findings[0].Severity;
            }
        }
        return result;
    }

    /// <summary>The latest stored hardware snapshot, or null before the first one.</summary>
    public SystemSpecs? GetLatestSpecs()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT specs_json FROM system_snapshots ORDER BY id DESC LIMIT 1;";
        return cmd.ExecuteScalar() is string json ? SystemSpecs.FromJson(json) : null;
    }

    /// <summary>The newest sampling tick time, or null when nothing has been sampled.</summary>
    public DateTimeOffset? GetLatestSampleTime()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(ts_utc) FROM samples;";
        return cmd.ExecuteScalar() is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    }

    /// <summary>Stores a session and its per-second FPS in one transaction. Returns the new session id.</summary>
    public long InsertSession(Analysis.CompletedSession session, string settingsKey)
    {
        using var transaction = _connection.BeginTransaction();

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sessions (application, settings_key, start_utc, end_utc, frame_count, median_fps)
            VALUES ($app, $key, $start, $end, $frames, $fps);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$app", session.Application);
        cmd.Parameters.AddWithValue("$key", settingsKey);
        cmd.Parameters.AddWithValue("$start", session.StartUtc.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$end", session.EndUtc.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$frames", session.FrameCount);
        cmd.Parameters.AddWithValue("$fps", session.MedianFps);
        var sessionId = (long)cmd.ExecuteScalar()!;

        using var second = _connection.CreateCommand();
        second.Transaction = transaction;
        second.CommandText = "INSERT INTO fps_seconds (session_id, ts_utc, frames) VALUES ($id, $ts, $frames);";
        var id = second.Parameters.Add("$id", SqliteType.Integer);
        var ts = second.Parameters.Add("$ts", SqliteType.Integer);
        var frames = second.Parameters.Add("$frames", SqliteType.Integer);
        id.Value = sessionId;
        foreach (var point in session.Seconds)
        {
            ts.Value = point.UnixSeconds * 1000;
            frames.Value = point.Frames;
            second.ExecuteNonQuery();
        }

        transaction.Commit();
        return sessionId;
    }

    /// <summary>Number of sessions that started within the range (Unix milliseconds, inclusive).</summary>
    public long CountSessionsBetween(long fromMs, long toMs)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE start_utc BETWEEN $from AND $to;";
        cmd.Parameters.AddWithValue("$from", fromMs);
        cmd.Parameters.AddWithValue("$to", toMs);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Deletes sessions that started within the range (Unix milliseconds, inclusive) and their
    /// per-second FPS rows, in one transaction. Returns the number of sessions deleted.
    /// </summary>
    public long DeleteSessionsBetween(long fromMs, long toMs)
    {
        using var transaction = _connection.BeginTransaction();

        using var seconds = _connection.CreateCommand();
        seconds.Transaction = transaction;
        seconds.CommandText = "DELETE FROM fps_seconds WHERE session_id IN (SELECT id FROM sessions WHERE start_utc BETWEEN $from AND $to);";
        seconds.Parameters.AddWithValue("$from", fromMs);
        seconds.Parameters.AddWithValue("$to", toMs);
        seconds.ExecuteNonQuery();

        using var findings = _connection.CreateCommand();
        findings.Transaction = transaction;
        findings.CommandText = "DELETE FROM session_findings WHERE session_id IN (SELECT id FROM sessions WHERE start_utc BETWEEN $from AND $to);";
        findings.Parameters.AddWithValue("$from", fromMs);
        findings.Parameters.AddWithValue("$to", toMs);
        findings.ExecuteNonQuery();

        using var sessions = _connection.CreateCommand();
        sessions.Transaction = transaction;
        sessions.CommandText = "DELETE FROM sessions WHERE start_utc BETWEEN $from AND $to;";
        sessions.Parameters.AddWithValue("$from", fromMs);
        sessions.Parameters.AddWithValue("$to", toMs);
        var deleted = sessions.ExecuteNonQuery();

        transaction.Commit();
        return deleted;
    }

    /// <summary>Most recent sessions first, for picking one to inspect.</summary>
    public IReadOnlyList<StoredSession> ListSessions(int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, application, start_utc, end_utc, frame_count, median_fps
            FROM sessions ORDER BY start_utc DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<StoredSession>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StoredSession(
                reader.GetInt64(0),
                reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                reader.GetInt32(4),
                reader.GetDouble(5)));
        }
        return rows;
    }

    /// <summary>Per-second FPS rows for one session, in time order.</summary>
    public IReadOnlyList<Analysis.FpsSecond> GetSessionSeconds(long sessionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT ts_utc / 1000, frames FROM fps_seconds WHERE session_id = $id ORDER BY ts_utc;";
        cmd.Parameters.AddWithValue("$id", sessionId);

        var rows = new List<Analysis.FpsSecond>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new Analysis.FpsSecond(reader.GetInt64(0), reader.GetInt32(1)));
        }
        return rows;
    }

    /// <summary>
    /// Sensor readings between two instants (inclusive), in time order. Pass sensor names to read only
    /// those, which skips the many per-core sensors and keeps long sessions cheap to load.
    /// </summary>
    public IReadOnlyList<StoredReading> GetReadings(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<string>? sensorNames = null)
    {
        using var cmd = _connection.CreateCommand();
        var nameFilter = "";
        if (sensorNames is { Count: > 0 })
        {
            var placeholders = new List<string>();
            for (var i = 0; i < sensorNames.Count; i++)
            {
                placeholders.Add($"$n{i}");
                cmd.Parameters.AddWithValue($"$n{i}", sensorNames[i]);
            }
            nameFilter = $" AND sensor_name IN ({string.Join(", ", placeholders)})";
        }

        cmd.CommandText = $"""
            SELECT ts_utc, sensor_id, hardware, sensor_name, sensor_type, value
            FROM samples WHERE ts_utc BETWEEN $from AND $to{nameFilter} ORDER BY ts_utc;
            """;
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());

        var rows = new List<StoredReading>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StoredReading(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5)));
        }
        return rows;
    }

    /// <summary>Median FPS of every saved session for this game and settings profile.</summary>
    public IReadOnlyList<double> GetSessionFps(string application, string settingsKey)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT median_fps FROM sessions WHERE application = $app AND settings_key = $key;";
        cmd.Parameters.AddWithValue("$app", application);
        cmd.Parameters.AddWithValue("$key", settingsKey);

        var values = new List<double>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            values.Add(reader.GetDouble(0));
        }
        return values;
    }

    /// <summary>
    /// Stores a hardware/OS snapshot only when it differs from the latest stored one, so the
    /// history shows when something changed (a new GPU driver, a RAM upgrade) without repeating daily.
    /// Returns true when a new snapshot was written.
    /// </summary>
    public bool SaveSpecsIfChanged(DateTimeOffset capturedAt, SystemSpecs specs)
    {
        var json = specs.ToJson();

        using (var latest = _connection.CreateCommand())
        {
            latest.CommandText = "SELECT specs_json FROM system_snapshots ORDER BY id DESC LIMIT 1;";
            if (latest.ExecuteScalar() is string previous && previous == json)
            {
                return false;
            }
        }

        using var insert = _connection.CreateCommand();
        insert.CommandText = "INSERT INTO system_snapshots (captured_utc, specs_json) VALUES ($ts, $json);";
        insert.Parameters.AddWithValue("$ts", capturedAt.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$json", json);
        insert.ExecuteNonQuery();
        return true;
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

    /// <summary>
    /// Copies the write-ahead log into the database and truncates it. Freed pages from deleted rows are
    /// reused by new rows, so the database file stays at about the size of the retention window.
    /// </summary>
    public void Checkpoint()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
