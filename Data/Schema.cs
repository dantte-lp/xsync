namespace Xsync.Data;

public static class Schema
{
    public const string Pragmas = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = normal;
        PRAGMA temp_store = memory;
        PRAGMA cache_size = -64000;
        """;

    public const string CreateTables = """
        CREATE TABLE IF NOT EXISTS files (
            id            INTEGER PRIMARY KEY,
            path          TEXT NOT NULL UNIQUE,
            remote_path   TEXT,
            size          INTEGER NOT NULL,
            mtime         INTEGER NOT NULL,
            content_hash  BLOB,
            sync_state    INTEGER NOT NULL DEFAULT 0,
            last_synced   INTEGER,
            resume_offset INTEGER DEFAULT 0,
            error_message TEXT,
            version       INTEGER NOT NULL DEFAULT 1,
            updated_at    INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS block_hashes (
            file_id     INTEGER NOT NULL,
            block_index INTEGER NOT NULL,
            block_hash  BLOB NOT NULL,
            block_size  INTEGER NOT NULL,
            PRIMARY KEY (file_id, block_index),
            FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS sync_runs (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            started_at          TEXT NOT NULL,
            finished_at         TEXT,
            files_total         INTEGER,
            files_transferred   INTEGER,
            files_skipped       INTEGER,
            files_failed        INTEGER,
            bytes_total         INTEGER,
            bytes_transferred   INTEGER,
            avg_speed_mbps      REAL,
            status              TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_files_sync_state
            ON files(sync_state) WHERE sync_state != 4;
        """;
}
