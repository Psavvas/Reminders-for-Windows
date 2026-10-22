
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE IF NOT EXISTS lists (
  id TEXT PRIMARY KEY, title TEXT NOT NULL, color_hex TEXT,
  count INTEGER NOT NULL DEFAULT 0, is_group INTEGER NOT NULL DEFAULT 0,
  position INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS reminders (
  id TEXT PRIMARY KEY, list_id TEXT NOT NULL, title TEXT NOT NULL DEFAULT '',
  description TEXT NOT NULL DEFAULT '', due_date TEXT,
  priority INTEGER NOT NULL DEFAULT 0, completed INTEGER NOT NULL DEFAULT 0,
  completed_date TEXT, flagged INTEGER NOT NULL DEFAULT 0,
  all_day INTEGER NOT NULL DEFAULT 0, deleted INTEGER NOT NULL DEFAULT 0,
  created TEXT, modified TEXT, change_tag TEXT,
  notified INTEGER NOT NULL DEFAULT 0, dirty INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_reminders_list ON reminders(list_id);
CREATE INDEX IF NOT EXISTS idx_reminders_due ON reminders(due_date)
  WHERE completed = 0 AND deleted = 0;
CREATE INDEX IF NOT EXISTS idx_reminders_completed ON reminders(completed_date DESC)
  WHERE completed = 1 AND deleted = 0;
CREATE TABLE IF NOT EXISTS tags (
  id TEXT PRIMARY KEY, name TEXT NOT NULL, reminder_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS related_records (
  id TEXT PRIMARY KEY, record_type TEXT NOT NULL, json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_tags_reminder ON tags(reminder_id);
CREATE INDEX IF NOT EXISTS idx_tags_name ON tags(name);
CREATE TABLE IF NOT EXISTS outbox (
  seq INTEGER PRIMARY KEY AUTOINCREMENT, reminder_id TEXT NOT NULL,
  op TEXT NOT NULL, payload TEXT NOT NULL, base_tag TEXT,
  created_at TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
  last_error TEXT
);
CREATE INDEX IF NOT EXISTS idx_outbox_reminder_seq ON outbox(reminder_id,seq);
CREATE TABLE IF NOT EXISTS conflicts (
  id INTEGER PRIMARY KEY AUTOINCREMENT, reminder_id TEXT NOT NULL,
  local_json TEXT NOT NULL, remote_json TEXT NOT NULL,
  detected_at TEXT NOT NULL, resolved INTEGER NOT NULL DEFAULT 0
);
