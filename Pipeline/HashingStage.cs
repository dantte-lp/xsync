using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Xsync.Data;
using Xsync.Logging;
using Xsync.Models;
using Xsync.Services;

namespace Xsync.Pipeline;

public static class HashingStage
{
    public static async Task RunAsync(
        ChannelReader<FileEntry> input,
        ChannelWriter<HashResult> output,
        MetadataStore db,
        LiveTracker tracker,
        SyncConfig config,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await foreach (var file in input.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var status = tracker.Get(file.Name);

                var cached = await db.GetCachedHashAsync(file.Name, file.MtimeUnix).ConfigureAwait(false);
                if (cached is not null)
                {
                    status.Hash = HashService.HashToHex(cached);
                    logger.LogDebug("{Name}: hash from cache", file.Name);
                    await output.WriteAsync(new HashResult(file, cached, FromCache: true), ct).ConfigureAwait(false);
                    continue;
                }

                status.Phase = FilePhase.Hashing;
                status.Stopwatch.Restart();

                var hash = await HashService.ComputeHashAsync(file.Path,
                    pct => status.ProgressPct = pct, ct).ConfigureAwait(false);

                status.Hash = HashService.HashToHex(hash);
                await db.UpsertFileAsync(file.Name, file.RemotePath, file.Size,
                    file.MtimeUnix, hash, SyncState.Hashing).ConfigureAwait(false);

                logger.LogDebug("{Name}: XXH128 = {Hash}", file.Name, status.Hash[..16]);
                await output.WriteAsync(new HashResult(file, hash, FromCache: false), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            output.Complete();
        }
    }
}
