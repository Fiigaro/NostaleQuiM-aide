using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NosSmoothCustomClient.Diagnostics;

/// <summary>
/// One captured log entry.
/// </summary>
/// <param name="Timestamp">When it was written.</param>
/// <param name="Level">Its severity.</param>
/// <param name="Category">The shortened logger category.</param>
/// <param name="Message">The formatted message.</param>
public readonly record struct LogLine(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message);

/// <summary>
/// A bounded, thread-safe ring of the most recent log lines.
/// </summary>
/// <remarks>
/// The UI reads this instead of tailing a file, so it shows the same stream the console front-end
/// prints. Bounded on purpose: at a 300 ms tick an unbounded buffer would grow without limit over a
/// long farming session.
/// </remarks>
public sealed class LogBuffer
{
    private readonly ConcurrentQueue<LogLine> _lines = new();
    private readonly int _capacity;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogBuffer"/> class.
    /// </summary>
    /// <param name="capacity">How many lines to retain.</param>
    public LogBuffer(int capacity = 500)
        => _capacity = capacity;

    /// <summary>Raised whenever a line is appended. Handlers run on the logging thread.</summary>
    public event Action<LogLine>? LineAdded;

    /// <summary>Appends a line, evicting the oldest once the capacity is exceeded.</summary>
    /// <param name="line">The line.</param>
    public void Add(LogLine line)
    {
        _lines.Enqueue(line);

        while (_lines.Count > _capacity && _lines.TryDequeue(out _))
        {
            // Evicting the overflow.
        }

        LineAdded?.Invoke(line);
    }

    /// <summary>Takes a snapshot of the retained lines, oldest first.</summary>
    /// <returns>The lines.</returns>
    public IReadOnlyList<LogLine> Snapshot()
        => _lines.ToArray();
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that feeds a <see cref="LogBuffer"/>.
/// </summary>
[ProviderAlias("Buffer")]
public sealed class LogBufferProvider : ILoggerProvider
{
    private readonly LogBuffer _buffer;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogBufferProvider"/> class.
    /// </summary>
    /// <param name="buffer">The buffer to write to.</param>
    public LogBufferProvider(LogBuffer buffer)
        => _buffer = buffer;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
        => new BufferLogger(_buffer, Shorten(categoryName));

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release.
    }

    private static string Shorten(string category)
    {
        var lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
    }

    private sealed class BufferLogger : ILogger
    {
        private readonly LogBuffer _buffer;
        private readonly string _category;

        public BufferLogger(LogBuffer buffer, string category)
        {
            _buffer = buffer;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel >= LogLevel.Debug;

        public void Log<TState>
        (
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message} ({exception.GetType().Name}: {exception.Message})";
            }

            _buffer.Add(new LogLine(DateTimeOffset.Now, logLevel, _category, message));
        }
    }
}
