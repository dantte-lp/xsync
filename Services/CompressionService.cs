namespace Xsync.Services;

public static class CompressionService
{
    public static bool ShouldCompress(string extension) =>
        !SyncConfig.CompressedExtensions.Contains(extension.ToLowerInvariant());
}
