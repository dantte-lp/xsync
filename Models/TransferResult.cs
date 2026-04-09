namespace Xsync.Models;

public sealed record TransferResult(
    HashResult Hash,
    bool Success,
    long BytesTransferred,
    TimeSpan Duration,
    long ResumeOffset,
    string? TempRemotePath,
    string? Error);
