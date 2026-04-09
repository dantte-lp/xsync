using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Xsync.Logging;

public sealed class SyncLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter? _fileWriter;
    private readonly int _verbosity;
    private readonly ConcurrentDictionary<string, SyncLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Lock _fileLock = new();

    public SyncLoggerProvider(string? logFilePath, int verbosity)
    {
        _verbosity = verbosity;
        if (logFilePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
            _fileWriter = new StreamWriter(logFilePath, append: false, System.Text.Encoding.UTF8)
            { AutoFlush = true };
        }
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new SyncLogger(name, this));

    public void Dispose()
    {
        _fileWriter?.Dispose();
    }

    internal void WriteLog(string category, LogLevel level, string message)
    {
        var minLevel = _verbosity switch
        {
            -1 => LogLevel.Warning,
            0 => LogLevel.Information,
            _ => LogLevel.Debug,
        };

        if (level >= minLevel)
        {
            var color = level switch
            {
                LogLevel.Debug => "\x1b[90m",
                LogLevel.Warning => "\x1b[33m",
                LogLevel.Error or LogLevel.Critical => "\x1b[31m",
                _ => "",
            };
            var reset = color.Length > 0 ? "\x1b[0m" : "";
            Console.Error.WriteLine($"{color}{message}{reset}");
        }

        if (_fileWriter is not null)
        {
            var entry = JsonSerializer.Serialize(new
            {
                ts = DateTime.UtcNow.ToString("o"),
                level = level.ToString(),
                category,
                msg = message,
            });
            lock (_fileLock)
            {
                _fileWriter.WriteLine(entry);
            }
        }
    }
}
