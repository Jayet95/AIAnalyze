-- D1 schema for the AIAnalyze API. Apply with:
--   npx wrangler d1 execute aianalyze --remote --file=schema.sql

-- Questions asked per user per UTC day, for the free daily quota.
CREATE TABLE IF NOT EXISTS usage (
    user_id   TEXT    NOT NULL,
    day       TEXT    NOT NULL,          -- YYYY-MM-DD (UTC)
    questions INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, day)
);

-- Opt-in diagnostics snapshots. No names, paths or message text; see docs in the app.
CREATE TABLE IF NOT EXISTS telemetry (
    id           INTEGER PRIMARY KEY,
    device_id    TEXT NOT NULL,          -- random id created at install, not tied to hardware
    user_id      TEXT,                   -- set only when the device is signed in
    received_utc TEXT NOT NULL,
    payload      TEXT NOT NULL           -- JSON
);
CREATE INDEX IF NOT EXISTS ix_telemetry_device ON telemetry (device_id, received_utc);

-- "Helpful / not helpful" votes on findings, health checks and assistant answers.
CREATE TABLE IF NOT EXISTS feedback (
    id          INTEGER PRIMARY KEY,
    device_id   TEXT    NOT NULL,
    user_id     TEXT,
    created_utc TEXT    NOT NULL,
    target      TEXT    NOT NULL,        -- finding | health | answer
    kind        TEXT    NOT NULL,        -- finding kind, check id, or "chat"
    helpful     INTEGER NOT NULL         -- 1 or 0
);
CREATE INDEX IF NOT EXISTS ix_feedback_kind ON feedback (target, kind);
