using System.Diagnostics;

namespace Xsync.Logging;

public sealed record FileStatus
{
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required string Extension { get; init; }
    public FilePhase Phase { get; set; } = FilePhase.Pending;
    public double ProgressPct { get; set; }
    public double SpeedMbps { get; set; }
    public string? Hash { get; set; }
    public string? Error { get; set; }
    public long BytesTransferred { get; set; }
    public Stopwatch Stopwatch { get; } = new();
}
