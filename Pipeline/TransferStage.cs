using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Xsync.Data;
using Xsync.Logging;
using Xsync.Models;
using Xsync.Services;
using Polly;

namespace Xsync.Pipeline;

public static class TransferStage
{
    public static async Task RunAsync(
        ChannelReader<HashResult> input,
        ChannelWriter<TransferResult> output,
        SftpConnectionPool pool,
        RemoteCommandService remote,
        MetadataStore db,
        ResiliencePipeline polly,
        LiveTracker tracker,
        SyncConfig config,
        ILogger logger,
        CancellationToken ct)
    {
        var sem = new SemaphoreSlim(config.Parallelism);
        var tasks = new List<Task>();

        try
        {
            await foreach (var hashResult in input.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await ProcessOneAsync(
                            hashResult, pool, remote, db, polly, tracker, config, logger, ct).ConfigureAwait(false);
                        await output.WriteAsync(result, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        sem.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            output.Complete();
        }
    }

    private static async Task<TransferResult> ProcessOneAsync(
        HashResult hashResult, SftpConnectionPool pool, RemoteCommandService remote,
        MetadataStore db, ResiliencePipeline polly, LiveTracker tracker,
        SyncConfig config, ILogger logger, CancellationToken ct)
    {
        var file = hashResult.File;
        var status = tracker.Get(file.Name);
        var sw = Stopwatch.StartNew();

        var skipResult = CheckRemoteAndMode(hashResult, file, status, config, remote, sw);
        if (skipResult is not null)
        {
            return skipResult;
        }

        return await UploadFileAsync(
            hashResult, file, status, pool, db, polly, logger, sw, ct).ConfigureAwait(false);
    }

    private static TransferResult? CheckRemoteAndMode(
        HashResult hashResult, FileEntry file, FileStatus status, SyncConfig config,
        RemoteCommandService remote, Stopwatch sw)
    {
        var (exists, remoteSize) = remote.FileExists(file.RemotePath);
        if (exists && remoteSize == file.Size)
        {
            // File exists with matching size — pass through to verify with real hash
            return new TransferResult(hashResult, true, 0, sw.Elapsed, 0, null, null);
        }

        if (config.VerifyOnly)
        {
            status.Phase = FilePhase.Failed;
            status.Error = "Missing";
            return new TransferResult(hashResult, false, 0, sw.Elapsed, 0, null, "missing_or_mismatch");
        }

        if (config.DryRun)
        {
            status.Phase = FilePhase.DryRun;
            return new TransferResult(hashResult, true, 0, sw.Elapsed, 0, null, null);
        }

        return null;
    }

    private static async Task<TransferResult> UploadFileAsync(
        HashResult hashResult, FileEntry file, FileStatus status,
        SftpConnectionPool pool, MetadataStore db,
        ResiliencePipeline polly, ILogger logger, Stopwatch sw, CancellationToken ct)
    {
        var tempPath = $"{file.RemotePath}.tmp.{Guid.NewGuid():N}";
        status.Phase = FilePhase.Transferring;
        status.Stopwatch.Restart();

        try
        {
            await polly.ExecuteAsync(async pollyToken =>
            {
                await db.UpsertFileAsync(file.Name, file.RemotePath, file.Size,
                    file.MtimeUnix, hashResult.ContentHash, SyncState.Transferring).ConfigureAwait(false);

                var sftp = await pool.RentAsync(pollyToken).ConfigureAwait(false);
                try
                {
                    var fs = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
                    await using (fs.ConfigureAwait(false))
                    {
                        var resumeOffset = await db.GetResumeOffsetAsync(file.Name).ConfigureAwait(false);
                        if (resumeOffset > 0)
                        {
                            fs.Seek(resumeOffset, SeekOrigin.Begin);
                        }

                        const long checkpointInterval = 10 * 1024 * 1024;
                        using var progressStream = new ProgressStream(fs, checkpointInterval,
                            offset =>
                            {
                                db.UpdateResumeOffsetAsync(file.Name, offset).GetAwaiter().GetResult();
                                status.BytesTransferred = offset;
                                status.ProgressPct = file.Size > 0 ? (double)offset / file.Size * 100 : 100;
                                var elapsed = status.Stopwatch.Elapsed.TotalSeconds;
                                status.SpeedMbps = elapsed > 0 ? offset / elapsed / 1024 / 1024 : 0;
                            });

                        sftp.UploadFile(progressStream, tempPath);
                    }
                }
                finally
                {
                    await pool.ReturnAsync(sftp).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);

            sw.Stop();
            var speed = sw.Elapsed.TotalSeconds > 0 ? file.Size / sw.Elapsed.TotalSeconds / 1024 / 1024 : 0;
            status.SpeedMbps = speed;
            status.ProgressPct = 100;
            status.BytesTransferred = file.Size;

            logger.LogDebug("{Name}: transferred in {Dur}s ({Speed} MB/s)",
                file.Name,
                sw.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture),
                speed.ToString("F1", CultureInfo.InvariantCulture));

            return new TransferResult(hashResult, true, file.Size, sw.Elapsed, 0, tempPath, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            status.Phase = FilePhase.Failed;
            status.Error = ex.Message.Length > 30 ? ex.Message[..30] : ex.Message;
            logger.LogError(ex, "{Name}: transfer FAILED", file.Name);
            await db.UpsertFileAsync(file.Name, file.RemotePath, file.Size,
                file.MtimeUnix, hashResult.ContentHash, SyncState.Failed, ex.Message).ConfigureAwait(false);
            return new TransferResult(hashResult, false, 0, sw.Elapsed, 0, tempPath, ex.Message);
        }
    }
}
