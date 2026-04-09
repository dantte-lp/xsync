using Microsoft.Extensions.Logging;

namespace Xsync.Logging;

internal sealed class SyncLogger(string category, SyncLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (exception is not null)
        {
            message += $" | {exception.Message}";
        }

        provider.WriteLog(category, logLevel, message);
    }
}
