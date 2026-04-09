using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Xsync.Models;

namespace Xsync.Pipeline;

public static class FileScannerStage
{
    public static async Task RunAsync(SyncConfig config, ChannelWriter<FileEntry> output,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            var files = Directory.EnumerateFiles(config.LocalDir)
                .Where(f =>
                {
                    var name = Path.GetFileName(f);
                    return !SyncConfig.ExcludePatterns.Any(p =>
                        name.EndsWith(p, StringComparison.OrdinalIgnoreCase));
                })
                .Where(f => config.FilterFile is null ||
                    Path.GetFileName(f).Equals(config.FilterFile, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal);

            var count = 0;
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                var name = info.Name;
                var ext = SyncConfig.GetExtension(name);
                var remotePath = $"{config.RemoteDir}/{name}";
                var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

                var entry = new FileEntry(path, name, info.Length, mtime, ext, remotePath);
                await output.WriteAsync(entry, ct).ConfigureAwait(false);
                count++;
            }

            logger.LogInformation("Scan complete: {Count} files found", count);
        }
        finally
        {
            output.Complete();
        }
    }
}
