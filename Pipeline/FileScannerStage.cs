using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Xsync.Models;

namespace Xsync.Pipeline;

public static class FileScannerStage
{
    public static async Task RunAsync(IReadOnlyList<FileInfo> files, SyncConfig config,
        ChannelWriter<FileEntry> output, ILogger logger, CancellationToken ct)
    {
        try
        {
            foreach (var info in files)
            {
                ct.ThrowIfCancellationRequested();
                var name = info.Name;
                var ext = SyncConfig.GetExtension(name);
                var remotePath = $"{config.RemoteDir}/{name}";
                var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

                var entry = new FileEntry(info.FullName, name, info.Length, mtime, ext, remotePath);
                await output.WriteAsync(entry, ct).ConfigureAwait(false);
            }

            logger.LogInformation("Scan complete: {Count} files found", files.Count);
        }
        finally
        {
            output.Complete();
        }
    }
}
