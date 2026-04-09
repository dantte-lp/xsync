using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Xsync.Data;
using Xsync.Models;
using Xsync.Services;

namespace Xsync.Pipeline;

public static class VerifyStage
{
    public static async Task<(int Transferred, int Verified, int Skipped, int Failed)> RunAsync(
        ChannelReader<TransferResult> input,
        RemoteCommandService remote,
        MetadataStore db,
        SyncConfig config,
        ILogger logger,
        CancellationToken ct)
    {
        int transferred = 0, verified = 0, skipped = 0, failed = 0;

        await foreach (var result in input.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var file = result.Hash.File;
            var localHash = result.Hash.ContentHash;

            if (config.DryRun && result.Success)
            {
                skipped++;
                continue;
            }

            if (!result.Success)
            {
                failed++;
                continue;
            }

            logger.LogDebug("Verifying: {Name}", file.Name);
            var verifyPath = result.TempRemotePath ?? file.RemotePath;
            var remoteHash = remote.ComputeRemoteHash(verifyPath);

            if (remoteHash is null)
            {
                logger.LogError("{Name}: remote hash computation FAILED", file.Name);
                await db.UpsertFileAsync(file.Name, file.RemotePath, file.Size,
                    file.MtimeUnix, localHash, SyncState.Failed, "remote_hash_failed").ConfigureAwait(false);
                failed++;
                continue;
            }

            if (!localHash.AsSpan().SequenceEqual(remoteHash))
            {
                logger.LogError("{Name}: HASH MISMATCH! Local={Local} Remote={Remote}",
                    file.Name, HashService.HashToHex(localHash), HashService.HashToHex(remoteHash));

                if (result.TempRemotePath is not null)
                {
                    remote.Exec($"rm -f '{result.TempRemotePath}'");
                }

                await db.UpsertFileAsync(file.Name, file.RemotePath, file.Size,
                    file.MtimeUnix, localHash, SyncState.Failed, "hash_mismatch").ConfigureAwait(false);
                failed++;
                continue;
            }

            if (result.TempRemotePath is not null)
            {
                remote.Rename(result.TempRemotePath, file.RemotePath);
                transferred++;
                logger.LogDebug("{Name}: verified OK", file.Name);
            }
            else
            {
                verified++;
            }

            await db.UpsertFileAsync(file.Name, file.RemotePath, file.Size,
                file.MtimeUnix, localHash, SyncState.Done).ConfigureAwait(false);
        }

        return (transferred, verified, skipped, failed);
    }
}
