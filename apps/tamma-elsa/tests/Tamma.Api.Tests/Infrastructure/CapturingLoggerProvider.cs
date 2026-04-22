using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Tamma.Api.Tests.Infrastructure;

/// <summary>
/// <see cref="ILoggerProvider"/> that captures every formatted message for
/// later assertion in tests. Used to verify that email-subsystem logs contain
/// only the transaction id and never the recipient, subject, or body.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    /// <summary>Every formatted message seen, in emission order.</summary>
    public IReadOnlyList<string> Messages => _messages.ToArray();

    /// <summary>Rich log entries (category + level + message) for richer assertions.</summary>
    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    public void Clear()
    {
        while (_messages.TryDequeue(out _)) { }
        while (_entries.TryDequeue(out _)) { }
    }

    public ILogger CreateLogger(string categoryName)
        => new CapturingLogger(categoryName, _messages, _entries);

    public void Dispose()
    {
        // nothing to dispose
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<string> _messages;
        private readonly ConcurrentQueue<CapturedLogEntry> _entries;

        public CapturingLogger(
            string category,
            ConcurrentQueue<string> messages,
            ConcurrentQueue<CapturedLogEntry> entries)
        {
            _category = category;
            _messages = messages;
            _entries = entries;
        }

        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            _messages.Enqueue(message);
            _entries.Enqueue(new CapturedLogEntry(_category, logLevel, message, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

/// <summary>Single captured log entry. Used by tests asserting on level + message.</summary>
public sealed record CapturedLogEntry(
    string Category, LogLevel Level, string Message, Exception? Exception);
