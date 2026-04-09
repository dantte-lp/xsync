namespace Xsync.Models;

public sealed record HashResult(
    FileEntry File,
    byte[] ContentHash,
    bool FromCache);
