using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Panelith.App.Services;

/// <summary>One line in the activity log.</summary>
public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message)
{
    /// <summary>The time as shown in the UI.</summary>
    public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>A short level marker.</summary>
    public string LevelText => Level switch
    {
        LogLevel.Warning => "WARN",
        LogLevel.Error or LogLevel.Critical => "ERROR",
        LogLevel.Debug or LogLevel.Trace => "DEBUG",
        _ => "INFO",
    };
}

/// <summary>
/// Keeps recent log lines in memory for the activity view and appends them to
/// a daily file, so a problem the user reports can be diagnosed afterwards.
/// </summary>
/// <remarks>
/// Files roll over at <see cref="MaxFileBytes"/>, keeping <see cref="FilesPerDay"/>
/// per day, and are deleted after <see cref="RetentionDays"/>. The user's
/// profile folder is written as <c>%USERPROFILE%</c>, so a shared log does not
/// reveal the account name.
/// </remarks>
public sealed class AppLog : ILoggerFactory
{
    private const int Capacity = 500;

    /// <summary>The size at which a log file rolls over.</summary>
    internal const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>How many files one day may have, counting the one being written.</summary>
    internal const int FilesPerDay = 5;

    /// <summary>How long log files are kept.</summary>
    internal const int RetentionDays = 14;

    private static readonly string? UserProfile = NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private readonly Queue<LogEntry> _entries = new();
    private readonly Lock _gate = new();
    private readonly string _folder;
    private StreamWriter? _file;
    private DateOnly _fileDay;

    /// <summary>Creates the log, writing files into <paramref name="folder"/>.</summary>
    public AppLog(string folder)
    {
        _folder = folder;
        try
        {
            Directory.CreateDirectory(folder);
            DeleteOldFiles(folder, DateTime.Now - TimeSpan.FromDays(RetentionDays));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Writes anything buffered to disk.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                _file?.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Replaces the user's profile folder with <c>%USERPROFILE%</c>.</summary>
    internal static string Redact(string message, string? profile = null)
    {
        profile ??= UserProfile;
        return string.IsNullOrEmpty(profile)
            ? message
            : message.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The file for <paramref name="day"/>, or its <paramref name="index"/>th rolled-over copy.</summary>
    internal static string FileFor(string folder, DateOnly day, int index = 0) =>
        Path.Combine(folder, index == 0 ? $"panelith-{day:yyyyMMdd}.log" : $"panelith-{day:yyyyMMdd}.{index}.log");

    /// <summary>Shifts <c>.log</c> to <c>.1.log</c>, <c>.1</c> to <c>.2</c> and so on, dropping the oldest.</summary>
    internal static void Roll(string folder, DateOnly day)
    {
        var oldest = FileFor(folder, day, FilesPerDay - 1);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = FilesPerDay - 2; index >= 0; index--)
        {
            var from = FileFor(folder, day, index);
            if (File.Exists(from))
            {
                File.Move(from, FileFor(folder, day, index + 1));
            }
        }
    }

    /// <summary>Deletes Panelith log files last written before <paramref name="cutoff"/>.</summary>
    internal static void DeleteOldFiles(string folder, DateTime cutoff)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "panelith-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The writer for today, opening a new file at midnight or when the current one is full.</summary>
    private StreamWriter? CurrentFile()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        try
        {
            if (_file is not null && (_fileDay != today || _file.BaseStream.Length >= MaxFileBytes))
            {
                _file.Dispose();
                _file = null;
                if (_fileDay == today)
                {
                    Roll(_folder, today);
                }
            }

            if (_file is null)
            {
                var path = FileFor(_folder, today);
                if (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes)
                {
                    Roll(_folder, today);
                }

                _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                _fileDay = today;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _file = null;
        }

        return _file;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Raised on the logging thread for every new entry.</summary>
    public event Action<LogEntry>? EntryAdded;

    /// <summary>The most recent entries, oldest first.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(this, ShortCategory(categoryName));

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
            _disposed = true;
        }
    }

    private bool _disposed;

    private void Write(LogEntry entry)
    {
        entry = entry with { Message = Redact(entry.Message) };
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }

            try
            {
                (_disposed ? null : CurrentFile())?.WriteLine($"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} {entry.LevelText,-5} {entry.Category}: {entry.Message}");
            }
            catch (ObjectDisposedException)
            {
                // Shutting down; the in-memory copy is enough.
            }
        }

        EntryAdded?.Invoke(entry);
    }

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class Logger(AppLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

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

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += $" ({exception.Message})";
            }

            owner.Write(new LogEntry(DateTime.Now, logLevel, category, message));
        }
    }
}
