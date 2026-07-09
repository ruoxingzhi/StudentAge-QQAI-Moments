CREATE TABLE IF NOT EXISTS telemetry_events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  received_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
  plugin_version TEXT NOT NULL DEFAULT '',
  install_id TEXT NOT NULL DEFAULT '',
  event_name TEXT NOT NULL DEFAULT '',
  job_type TEXT NOT NULL DEFAULT '',
  author_role_id INTEGER,
  post_author_role_id INTEGER,
  target_role_id INTEGER,
  raw_text_included INTEGER NOT NULL DEFAULT 0,
  final_state TEXT NOT NULL DEFAULT '',
  quality_issue TEXT NOT NULL DEFAULT '',
  model TEXT NOT NULL DEFAULT '',
  endpoint_kind TEXT NOT NULL DEFAULT '',
  request_attempts INTEGER NOT NULL DEFAULT 0,
  http_status INTEGER NOT NULL DEFAULT 0,
  source_len_bucket TEXT NOT NULL DEFAULT '',
  parent_len_bucket TEXT NOT NULL DEFAULT '',
  ai_len_bucket TEXT NOT NULL DEFAULT '',
  source_hash TEXT NOT NULL DEFAULT '',
  parent_hash TEXT NOT NULL DEFAULT '',
  ai_hash TEXT NOT NULL DEFAULT '',
  ip_hash TEXT NOT NULL DEFAULT '',
  user_agent_hash TEXT NOT NULL DEFAULT '',
  payload_json TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_telemetry_events_received_at ON telemetry_events(received_at);
CREATE INDEX IF NOT EXISTS idx_telemetry_events_install_id ON telemetry_events(install_id);
CREATE INDEX IF NOT EXISTS idx_telemetry_events_job_type ON telemetry_events(job_type);
CREATE INDEX IF NOT EXISTS idx_telemetry_events_raw_text ON telemetry_events(raw_text_included);
