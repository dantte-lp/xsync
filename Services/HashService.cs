using System.IO.Hashing;

namespace Xsync.Services;

public static class HashService
{
    private const int BufferSize = 65536;

    public static async Task<byte[]> ComputeHashAsync(string filePath, Action<double>? onProgress = null,
        CancellationToken ct = default)
    {
        var hash = new XxHash128();
        var total = new FileInfo(filePath).Length;
        long read = 0;
        var buffer = new byte[BufferSize];

        var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using (fs.ConfigureAwait(false))
        {
            int bytesRead;
            while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                hash.Append(buffer.AsSpan(0, bytesRead));
                read += bytesRead;
                var pct = total > 0 ? (double)read / total * 100 : 100;
                onProgress?.Invoke(pct);
            }
        }

        return hash.GetHashAndReset();
    }

    public static string HashToHex(byte[] hash) => Convert.ToHexStringLower(hash);
}
