namespace Xsync.Models;

public sealed record FileEntry(
    string Path,
    string Name,
    long Size,
    long MtimeUnix,
    string Extension,
    string RemotePath);
