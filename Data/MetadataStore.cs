using Microsoft.Data.Sqlite;
using Xsync.Models;

namespace Xsync.Data;

public sealed class MetadataStore : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MetadataStore(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
    }

    public async Task InitializeAsync()
    {
        await _conn.OpenAsync().ConfigureAwait(false);
        using var pragmaCmd = _conn.CreateCommand();
        pragmaCmd.CommandText = Schema.Pragmas;
        await pragmaCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        using var schemaCmd = _conn.CreateCommand();
        schemaCmd.CommandText = Schema.CreateTables;
        await schemaCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<byte[]?> GetCachedHashAsync(string path, long mtime)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT content_hash, mtime FROM files WHERE path = @path";
            cmd.Parameters.AddWithValue("@path", path);
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                var storedMtime = reader.GetInt64(1);
                if (storedMtime == mtime && !reader.IsDBNull(0))
                {
                    var hash = (byte[])reader.GetValue(0);
                    return hash.Length > 0 ? hash : null;
                }
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertFileAsync(string path, string remotePath, long size, long mtime,
        byte[]? hash, SyncState state, string? error = null, long resumeOffset = 0)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO files (path, remote_path, size, mtime, content_hash, sync_state,
                                   last_synced, resume_offset, error_message, updated_at)
                VALUES (@path, @remote, @size, @mtime, @hash, @state,
                        @synced, @offset, @error, @now)
                ON CONFLICT(path) DO UPDATE SET
                    remote_path=@remote, size=@size, mtime=@mtime, content_hash=COALESCE(@hash, content_hash),
                    sync_state=@state, last_synced=@synced, resume_offset=@offset,
                    error_message=@error, version=version+1, updated_at=@now
                """;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            cmd.Parameters.AddWithValue("@path", path);
            cmd.Parameters.AddWithValue("@remote", remotePath);
            cmd.Parameters.AddWithValue("@size", size);
            cmd.Parameters.AddWithValue("@mtime", mtime);
            cmd.Parameters.AddWithValue("@hash", hash is { Length: > 0 } ? (object)hash : DBNull.Value);
            cmd.Parameters.AddWithValue("@state", (int)state);
            cmd.Parameters.AddWithValue("@synced", state == SyncState.Done ? now : DBNull.Value);
            cmd.Parameters.AddWithValue("@offset", resumeOffset);
            cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@now", now);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateResumeOffsetAsync(string path, long offset)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE files SET resume_offset = @offset WHERE path = @path";
            cmd.Parameters.AddWithValue("@offset", offset);
            cmd.Parameters.AddWithValue("@path", path);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<long> GetResumeOffsetAsync(string path)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT resume_offset FROM files WHERE path = @path AND sync_state = @state";
            cmd.Parameters.AddWithValue("@path", path);
            cmd.Parameters.AddWithValue("@state", (int)SyncState.Transferring);
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return result is long offset ? offset : 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> StartSyncRunAsync(int totalFiles, long totalBytes)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sync_runs (started_at, files_total, bytes_total, status)
                VALUES (@ts, @files, @bytes, 'running')
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@files", totalFiles);
            cmd.Parameters.AddWithValue("@bytes", totalBytes);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task FinishSyncRunAsync(int runId, int transferred, int skipped, int failed,
        long bytesTransferred, double avgSpeed)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE sync_runs SET
                    finished_at = @ts, files_transferred = @tx, files_skipped = @sk,
                    files_failed = @fail, bytes_transferred = @bytes,
                    avg_speed_mbps = @speed, status = @status
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@tx", transferred);
            cmd.Parameters.AddWithValue("@sk", skipped);
            cmd.Parameters.AddWithValue("@fail", failed);
            cmd.Parameters.AddWithValue("@bytes", bytesTransferred);
            cmd.Parameters.AddWithValue("@speed", avgSpeed);
            cmd.Parameters.AddWithValue("@status", failed > 0 ? "completed_with_errors" : "completed");
            cmd.Parameters.AddWithValue("@id", runId);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _conn.DisposeAsync().ConfigureAwait(false);
        _lock.Dispose();
    }
}
