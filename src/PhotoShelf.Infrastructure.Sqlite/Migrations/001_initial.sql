CREATE TABLE IF NOT EXISTS schema_migrations (
    version             INTEGER NOT NULL PRIMARY KEY,
    name                TEXT NOT NULL,
    applied_at_utc      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS library_roots (
    id                  TEXT NOT NULL PRIMARY KEY,
    display_name        TEXT NOT NULL,
    absolute_path       TEXT NOT NULL,
    path_key            TEXT NOT NULL UNIQUE,
    volume_identity     TEXT NULL,
    is_available        INTEGER NOT NULL DEFAULT 1 CHECK (is_available IN (0, 1)),
    include_subfolders  INTEGER NOT NULL DEFAULT 1 CHECK (include_subfolders IN (0, 1)),
    last_scan_started_utc   TEXT NULL,
    last_scan_completed_utc TEXT NULL,
    created_at_utc      TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS media_assets (
    id                  TEXT NOT NULL PRIMARY KEY,
    kind                INTEGER NOT NULL,
    display_name        TEXT NOT NULL,
    primary_file_id     TEXT NULL,
    cataloged_at_utc    TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,
    is_hidden           INTEGER NOT NULL DEFAULT 0 CHECK (is_hidden IN (0, 1)),
    is_missing          INTEGER NOT NULL DEFAULT 0 CHECK (is_missing IN (0, 1)),
    FOREIGN KEY (primary_file_id) REFERENCES media_files(id)
        DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE IF NOT EXISTS media_files (
    id                  TEXT NOT NULL PRIMARY KEY,
    library_root_id     TEXT NOT NULL,
    relative_path       TEXT NOT NULL,
    path_key            TEXT NOT NULL,
    file_name           TEXT NOT NULL,
    extension           TEXT NOT NULL,
    kind                INTEGER NOT NULL,
    mime_type           TEXT NULL,
    byte_length         INTEGER NOT NULL CHECK (byte_length >= 0),
    file_created_utc    TEXT NOT NULL,
    file_modified_utc   TEXT NOT NULL,
    file_accessed_utc   TEXT NULL,
    volume_file_id      TEXT NULL,
    quick_fingerprint   TEXT NULL,
    content_hash        TEXT NULL,
    perceptual_hash     TEXT NULL,
    metadata_status     INTEGER NOT NULL DEFAULT 0,
    last_metadata_read_utc TEXT NULL,
    metadata_error      TEXT NULL,
    is_offline          INTEGER NOT NULL DEFAULT 0 CHECK (is_offline IN (0, 1)),
    last_seen_utc       TEXT NOT NULL,
    FOREIGN KEY (library_root_id) REFERENCES library_roots(id) ON DELETE CASCADE,
    UNIQUE (library_root_id, path_key)
);

CREATE TABLE IF NOT EXISTS asset_files (
    asset_id            TEXT NOT NULL,
    file_id             TEXT NOT NULL,
    role                INTEGER NOT NULL,
    sort_order          INTEGER NOT NULL DEFAULT 0,
    pairing_evidence    TEXT NULL,
    created_at_utc      TEXT NOT NULL,
    PRIMARY KEY (asset_id, file_id),
    FOREIGN KEY (asset_id) REFERENCES media_assets(id) ON DELETE CASCADE,
    FOREIGN KEY (file_id) REFERENCES media_files(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS normalized_metadata (
    asset_id                    TEXT NOT NULL PRIMARY KEY,

    datetime_original_local     TEXT NULL,
    datetime_original_offset_min INTEGER NULL,
    datetime_original_precision INTEGER NULL,
    datetime_original_utc       TEXT NULL,

    create_date_local           TEXT NULL,
    create_date_offset_min      INTEGER NULL,
    create_date_precision       INTEGER NULL,
    create_date_utc             TEXT NULL,

    modify_date_local           TEXT NULL,
    modify_date_offset_min      INTEGER NULL,
    modify_date_precision       INTEGER NULL,
    modify_date_utc             TEXT NULL,

    gps_latitude                REAL NULL CHECK (gps_latitude IS NULL OR gps_latitude BETWEEN -90.0 AND 90.0),
    gps_longitude               REAL NULL CHECK (gps_longitude IS NULL OR gps_longitude BETWEEN -180.0 AND 180.0),
    gps_altitude_meters         REAL NULL,

    camera_make                 TEXT NULL,
    camera_model                TEXT NULL,
    lens_make                   TEXT NULL,
    lens_model                  TEXT NULL,
    focal_length_mm             REAL NULL CHECK (focal_length_mm IS NULL OR focal_length_mm >= 0),
    aperture_f_number           REAL NULL CHECK (aperture_f_number IS NULL OR aperture_f_number >= 0),
    exposure_time_seconds       REAL NULL CHECK (exposure_time_seconds IS NULL OR exposure_time_seconds >= 0),
    iso                         INTEGER NULL CHECK (iso IS NULL OR iso >= 0),

    orientation                 INTEGER NULL CHECK (orientation IS NULL OR orientation BETWEEN 1 AND 8),
    pixel_width                 INTEGER NULL CHECK (pixel_width IS NULL OR pixel_width > 0),
    pixel_height                INTEGER NULL CHECK (pixel_height IS NULL OR pixel_height > 0),
    color_profile               TEXT NULL,
    rating                      INTEGER NULL CHECK (rating IS NULL OR rating BETWEEN 0 AND 5),

    title                       TEXT NULL,
    caption                     TEXT NULL,
    description                 TEXT NULL,
    author                      TEXT NULL,
    copyright                   TEXT NULL,
    software                    TEXT NULL,
    metadata_updated_at_utc     TEXT NOT NULL,

    FOREIGN KEY (asset_id) REFERENCES media_assets(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS metadata_properties (
    id                  TEXT NOT NULL PRIMARY KEY,
    asset_id            TEXT NOT NULL,
    source_file_id      TEXT NULL,
    source_kind         INTEGER NOT NULL,
    namespace_uri       TEXT NOT NULL DEFAULT '',
    group_name          TEXT NULL,
    property_key        TEXT NOT NULL,
    value_kind          INTEGER NOT NULL,
    raw_text            TEXT NULL,
    raw_blob            BLOB NULL,
    normalized_text     TEXT NULL,
    language_tag        TEXT NULL,
    ordinal             INTEGER NOT NULL DEFAULT 0,
    binary_digest       TEXT NULL,
    binary_length       INTEGER NULL CHECK (binary_length IS NULL OR binary_length >= 0),
    binary_locator      TEXT NULL,
    FOREIGN KEY (asset_id) REFERENCES media_assets(id) ON DELETE CASCADE,
    FOREIGN KEY (source_file_id) REFERENCES media_files(id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS metadata_unique_ids (
    asset_id            TEXT NOT NULL,
    id_kind             TEXT NOT NULL,
    id_value            TEXT NOT NULL,
    source_file_id      TEXT NULL,
    source_kind         INTEGER NOT NULL,
    PRIMARY KEY (asset_id, id_kind, id_value),
    FOREIGN KEY (asset_id) REFERENCES media_assets(id) ON DELETE CASCADE,
    FOREIGN KEY (source_file_id) REFERENCES media_files(id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS metadata_field_sources (
    asset_id            TEXT NOT NULL,
    field_name          TEXT NOT NULL,
    property_id         TEXT NOT NULL,
    resolution_priority INTEGER NOT NULL,
    PRIMARY KEY (asset_id, field_name),
    FOREIGN KEY (asset_id) REFERENCES media_assets(id) ON DELETE CASCADE,
    FOREIGN KEY (property_id) REFERENCES metadata_properties(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS tags (
    id                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    display_name        TEXT NOT NULL,
    normalized_name     TEXT NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS asset_tags (
    asset_id            TEXT NOT NULL,
    tag_id              INTEGER NOT NULL,
    source_kind         INTEGER NOT NULL,
    source_file_id      TEXT NULL,
    PRIMARY KEY (asset_id, tag_id, source_kind),
    FOREIGN KEY (asset_id) REFERENCES media_assets(id) ON DELETE CASCADE,
    FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE,
    FOREIGN KEY (source_file_id) REFERENCES media_files(id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS thumbnail_entries (
    asset_id            TEXT NOT NULL,
    pixel_size          INTEGER NOT NULL CHECK (pixel_size > 0),
    cache_key           TEXT NOT NULL,
    relative_cache_path TEXT NOT NULL,
    byte_length         INTEGER NOT NULL CHECK (byte_length >= 0),
    decoder_version     TEXT NOT NULL,
    created_at_utc      TEXT NOT NULL,
    last_accessed_utc   TEXT NOT NULL,
    PRIMARY KEY (asset_id, pixel_size, cache_key),
    FOREIGN KEY (asset_id) REFERENCES media_assets(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS quarantine_batches (
    id                  TEXT NOT NULL PRIMARY KEY,
    status              INTEGER NOT NULL,
    quarantine_root     TEXT NOT NULL,
    manifest_path       TEXT NOT NULL,
    reason              TEXT NOT NULL,
    created_at_utc      TEXT NOT NULL,
    completed_at_utc    TEXT NULL,
    restored_at_utc     TEXT NULL,
    permanently_deleted_at_utc TEXT NULL
);

CREATE TABLE IF NOT EXISTS quarantine_items (
    id                  TEXT NOT NULL PRIMARY KEY,
    batch_id            TEXT NOT NULL,
    asset_id            TEXT NULL,
    file_id             TEXT NULL,
    original_path       TEXT NOT NULL,
    quarantine_path     TEXT NOT NULL,
    expected_content_hash TEXT NULL,
    status              INTEGER NOT NULL,
    error_message       TEXT NULL,
    FOREIGN KEY (batch_id) REFERENCES quarantine_batches(id) ON DELETE CASCADE,
    FOREIGN KEY (asset_id) REFERENCES media_assets(id) ON DELETE SET NULL,
    FOREIGN KEY (file_id) REFERENCES media_files(id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS ix_media_files_root_path
    ON media_files(library_root_id, relative_path);
CREATE INDEX IF NOT EXISTS ix_media_files_content_hash
    ON media_files(content_hash) WHERE content_hash IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_media_files_perceptual_hash
    ON media_files(perceptual_hash) WHERE perceptual_hash IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_asset_files_file_role
    ON asset_files(file_id, role);
CREATE INDEX IF NOT EXISTS ix_asset_files_asset_role
    ON asset_files(asset_id, role, sort_order);
CREATE INDEX IF NOT EXISTS ix_metadata_capture_date
    ON normalized_metadata(datetime_original_utc, datetime_original_local, asset_id);
CREATE INDEX IF NOT EXISTS ix_metadata_camera
    ON normalized_metadata(camera_make, camera_model, asset_id);
CREATE INDEX IF NOT EXISTS ix_metadata_lens
    ON normalized_metadata(lens_make, lens_model, asset_id);
CREATE INDEX IF NOT EXISTS ix_metadata_rating
    ON normalized_metadata(rating, asset_id);
CREATE INDEX IF NOT EXISTS ix_metadata_gps
    ON normalized_metadata(gps_latitude, gps_longitude, asset_id)
    WHERE gps_latitude IS NOT NULL AND gps_longitude IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_metadata_properties_lookup
    ON metadata_properties(asset_id, namespace_uri, property_key, ordinal);
CREATE INDEX IF NOT EXISTS ix_metadata_unique_ids_value
    ON metadata_unique_ids(id_kind, id_value);
CREATE INDEX IF NOT EXISTS ix_metadata_field_sources_property
    ON metadata_field_sources(property_id);
CREATE INDEX IF NOT EXISTS ix_asset_tags_tag
    ON asset_tags(tag_id, asset_id);
CREATE INDEX IF NOT EXISTS ix_thumbnail_lru
    ON thumbnail_entries(last_accessed_utc);
CREATE INDEX IF NOT EXISTS ix_quarantine_items_batch_status
    ON quarantine_items(batch_id, status);
