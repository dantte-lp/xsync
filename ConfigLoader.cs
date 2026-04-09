using System.Collections.Frozen;
using Tomlyn;
using Tomlyn.Model;

namespace Xsync;

public static class ConfigLoader
{
    private const string FileName = "xsync.toml";

    public static SyncConfig Load(string baseDir, string[] args)
    {
        // 1. Find and parse TOML (optional)
        var tomlPath = FindTomlFile(baseDir);
        var tomlDir = tomlPath is not null ? Path.GetDirectoryName(tomlPath)! : baseDir;
        var toml = tomlPath is not null ? ParseToml(tomlPath, tomlDir) : new TomlDefaults();

        // 2. CLI args override TOML
        return ApplyCliArgs(toml, tomlDir, args);
    }

    private static string? FindTomlFile(string baseDir)
    {
        // Search order: CWD, baseDir, exe dir
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, FileName),
            Path.Combine(baseDir, FileName),
            Path.Combine(AppContext.BaseDirectory, FileName),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static TomlDefaults ParseToml(string path, string tomlDir)
    {
        var text = File.ReadAllText(path);
        var model = TomlSerializer.Deserialize<TomlTable>(text)
            ?? throw new InvalidOperationException($"Failed to parse TOML: {path}");
        var d = new TomlDefaults();

        if (model.TryGetValue("source", out var sourceObj) && sourceObj is TomlTable source)
        {
            d.LocalDir = ResolvePath(tomlDir, GetString(source, "local_dir", d.LocalDir));
        }

        if (model.TryGetValue("target", out var targetObj) && targetObj is TomlTable target)
        {
            d.Host = GetString(target, "host", d.Host);
            d.Port = GetInt(target, "port", d.Port);
            d.RemoteDir = GetString(target, "remote_dir", d.RemoteDir);
            d.SshKey = ResolvePath(tomlDir, GetString(target, "ssh_key", d.SshKey));
        }

        if (model.TryGetValue("transfer", out var transferObj) && transferObj is TomlTable transfer)
        {
            d.Parallelism = GetInt(transfer, "parallelism", d.Parallelism);
        }

        if (model.TryGetValue("storage", out var storageObj) && storageObj is TomlTable storage)
        {
            d.DbPath = ResolvePath(tomlDir, GetString(storage, "db", d.DbPath));
            d.LogsDir = ResolvePath(tomlDir, GetString(storage, "logs_dir", d.LogsDir));
        }

        if (model.TryGetValue("filter", out var filterObj) && filterObj is TomlTable filter)
        {
            if (filter.TryGetValue("exclude", out var excl) && excl is TomlArray exclArr)
            {
                d.Exclude = exclArr.OfType<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);
            }

            if (filter.TryGetValue("compressed_extensions", out var comp) && comp is TomlArray compArr)
            {
                d.CompressedExtensions = compArr.OfType<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        return d;
    }

    private static SyncConfig ApplyCliArgs(TomlDefaults d, string tomlDir, string[] args)
    {
        // CLI overrides
        bool dryRun = false, verifyOnly = false, quiet = false, verbose = false;
        string? filterFile = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run": dryRun = true; break;
                case "--verify-only": verifyOnly = true; break;
                case "--file": filterFile = args[++i]; break;
                case "--parallel": d.Parallelism = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "-q" or "--quiet": quiet = true; break;
                case "-v" or "--verbose": verbose = true; break;
                case "--host": d.Host = args[++i]; break;
                case "--port": d.Port = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--key": d.SshKey = args[++i]; break;
                case "--remote-dir": d.RemoteDir = args[++i]; break;
                case "--db": d.DbPath = args[++i]; break;
                case "--local-dir": d.LocalDir = args[++i]; break;
                case "--logs-dir": d.LogsDir = args[++i]; break;
                case "--config": /* handled by find, skip value */ i++; break;
            }
        }

        return new SyncConfig
        {
            LocalDir = d.LocalDir,
            RemoteDir = d.RemoteDir,
            Host = d.Host,
            Port = d.Port,
            SshKeyPath = d.SshKey,
            Parallelism = d.Parallelism,
            DryRun = dryRun,
            VerifyOnly = verifyOnly,
            Verbosity = quiet ? -1 : verbose ? 1 : 0,
            FilterFile = filterFile,
            DbPath = d.DbPath,
            LogsDir = d.LogsDir,
        };
    }

    private static string ResolvePath(string baseDir, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));

    private static string GetString(TomlTable table, string key, string fallback) =>
        table.TryGetValue(key, out var val) && val is string s ? s : fallback;

    private static int GetInt(TomlTable table, string key, int fallback) =>
        table.TryGetValue(key, out var val) && val is long l ? (int)l : fallback;

    private sealed class TomlDefaults
    {
        public string LocalDir { get; set; } = "img";
        public string Host { get; set; } = "root@localhost";
        public int Port { get; set; } = 22;
        public string RemoteDir { get; set; } = "/tmp/xsync";
        public string SshKey { get; set; } = ".ssh/id_ed25519";
        public int Parallelism { get; set; } = 4;
        public string DbPath { get; set; } = "xsync.db";
        public string LogsDir { get; set; } = "logs";
        public FrozenSet<string>? Exclude { get; set; }
        public FrozenSet<string>? CompressedExtensions { get; set; }
    }
}
