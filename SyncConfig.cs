using System.Collections.Frozen;

namespace Xsync;

public sealed record SyncConfig
{
    public required string LocalDir { get; init; }
    public required string RemoteDir { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string SshKeyPath { get; init; }
    public int Parallelism { get; init; } = 4;
    public bool DryRun { get; init; }
    public bool VerifyOnly { get; init; }
    public int Verbosity { get; init; }
    public string? FilterFile { get; init; }
    public required string DbPath { get; init; }
    public required string LogsDir { get; init; }

    public string Username => Host.Contains('@', StringComparison.Ordinal) ? Host.Split('@')[0] : "root";
    public string Hostname => Host.Contains('@', StringComparison.Ordinal) ? Host.Split('@')[1] : Host;

    public static readonly FrozenSet<string> ExcludePatterns =
        FrozenSet.ToFrozenSet([".crdownload", ".part", ".tmp"], StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> CompressedExtensions =
        FrozenSet.ToFrozenSet(
            ["tar.gz", "tgz", "gz", "xz", "bz2", "qcow2", "ova", "iso", "exe", "zip", "7z"],
            StringComparer.OrdinalIgnoreCase);

    public static string GetExtension(string name)
    {
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return "tar.gz";
        }

        if (name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
        {
            return "tar.xz";
        }

        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : "";
    }
}
