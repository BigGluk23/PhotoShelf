CREATE TABLE IF NOT EXISTS duplicate_scan_runs (
    id                  TEXT NOT NULL PRIMARY KEY,
    scope_kind          TEXT NOT NULL,
    scope_description   TEXT NOT NULL,
    algorithm           TEXT NOT NULL,
    started_at_utc      TEXT NOT NULL,
    completed_at_utc    TEXT NULL,
    status              TEXT NOT NULL,
    checked_file_count  INTEGER NOT NULL DEFAULT 0 CHECK (checked_file_count >= 0),
    duplicate_group_count INTEGER NOT NULL DEFAULT 0 CHECK (duplicate_group_count >= 0),
    extra_file_count    INTEGER NOT NULL DEFAULT 0 CHECK (extra_file_count >= 0)
);

CREATE TABLE IF NOT EXISTS duplicate_groups (
    id                  TEXT NOT NULL PRIMARY KEY,
    scan_run_id         TEXT NOT NULL,
    group_hash          TEXT NOT NULL,
    byte_length         INTEGER NOT NULL CHECK (byte_length >= 0),
    item_count          INTEGER NOT NULL CHECK (item_count >= 2),
    extra_count         INTEGER NOT NULL CHECK (extra_count >= 1),
    created_at_utc      TEXT NOT NULL,
    FOREIGN KEY (scan_run_id) REFERENCES duplicate_scan_runs(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS duplicate_group_items (
    duplicate_group_id  TEXT NOT NULL,
    file_id             TEXT NULL,
    absolute_path       TEXT NOT NULL,
    recommended_action  TEXT NOT NULL DEFAULT 'undecided',
    user_action         TEXT NOT NULL DEFAULT 'undecided',
    reason              TEXT NULL,
    PRIMARY KEY (duplicate_group_id, absolute_path),
    FOREIGN KEY (duplicate_group_id) REFERENCES duplicate_groups(id) ON DELETE CASCADE,
    FOREIGN KEY (file_id) REFERENCES media_files(id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS decoder_capabilities (
    id                  TEXT NOT NULL PRIMARY KEY,
    kind                TEXT NOT NULL,
    name                TEXT NOT NULL,
    supported_extensions TEXT NOT NULL,
    strategy            TEXT NOT NULL,
    is_bundled          INTEGER NOT NULL DEFAULT 0 CHECK (is_bundled IN (0, 1)),
    notes               TEXT NULL,
    updated_at_utc      TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_duplicate_scan_runs_status
    ON duplicate_scan_runs(status, started_at_utc);
CREATE INDEX IF NOT EXISTS ix_duplicate_groups_scan
    ON duplicate_groups(scan_run_id, byte_length);
CREATE INDEX IF NOT EXISTS ix_duplicate_group_items_path
    ON duplicate_group_items(absolute_path);
