using Microsoft.Extensions.Logging;

namespace Panelith.Cli.Logging;

/// <summary>Writes warnings and errors to the console. Info stays quiet unless asked.</summary>
internal sealed class ConsoleLog : ILogger, ILoggerFactory, ILoggerProvider
{
    private readonly LogLevel _minimum;

    internal ConsoleLog(bool verbose) =>
        _minimum = verbose ? LogLevel.Debug : LogLevel.Information;

    public ILogger CreateLogger(string categoryName) => this;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimum;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var line = formatter(state, exception);
        var writer = logLevel >= LogLevel.Error ? Console.Error : Console.Out;
        writer.WriteLine(line);
        if (exception is not null && (logLevel >= LogLevel.Error || _minimum <= LogLevel.Debug))
        {
            writer.WriteLine(exception.Message);
        }
    }
}
