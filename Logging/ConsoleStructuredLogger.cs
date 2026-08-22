using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Logging
{
    /// <summary>
    /// A lightweight <see cref="ILoggerProvider"/> that renders structured (message-template)
    /// logs as human-readable labeled lines on stdout. This keeps the existing
    /// Console.WriteLine-based output working while allowing services to use ILogger&lt;T&gt;.
    /// No secrets/tokens/keys are logged by design; callers must keep messages safe.
    /// </summary>
    public sealed class ConsoleStructuredLogProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, ConsoleStructuredLogger> _loggers =
            new(StringComparer.Ordinal);

        public ILogger CreateLogger(string categoryName) =>
            _loggers.GetOrAdd(categoryName, _ => new ConsoleStructuredLogger(categoryName));

        public void Dispose()
        {
            _loggers.Clear();
        }

        private sealed class ConsoleStructuredLogger : ILogger
        {
            private readonly string _category;
            private readonly object _lock = new();

            public ConsoleStructuredLogger(string category) => _category = category;

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
                    return;

                string message = formatter(state, exception);
                string line = BuildLine(logLevel, eventId, message, exception);

                lock (_lock)
                {
                    Console.WriteLine(line);
                }
            }

            private string BuildLine(
                LogLevel logLevel, EventId eventId, string message, Exception? exception)
            {
                var ts = DateTime.Now.ToString("HH:mm:ss");
                var sb = new StringBuilder();
                sb.Append('[').Append(ts).Append("] ");
                sb.Append('[').Append(logLevel.ToString().ToUpper()).Append("] ");
                sb.Append('[').Append(_category).Append(']');
                if (eventId.Id != 0)
                    sb.Append(" [eid=").Append(eventId.Id).Append(']');
                sb.Append(' ').Append(message);
                if (exception != null)
                    sb.Append(" | EX: ").Append(exception.Message);
                return sb.ToString();
            }
        }
    }
}
