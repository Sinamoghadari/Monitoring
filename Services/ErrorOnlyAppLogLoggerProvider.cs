using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Ergonomy.Logging;

namespace Ergonomy.Services
{
    /// <summary>
    /// Forwards only Warning/Error/Critical ILogger records into the SQLite app_logs outbox.
    /// Healthy Information/Debug emit nothing. Re-entrancy is suppressed so a failed enqueue
    /// cannot recurse through the same provider.
    /// </summary>
    public sealed class ErrorOnlyAppLogLoggerProvider : ILoggerProvider
    {
        private static readonly AsyncLocal<bool> Suppress = new();
        private readonly MessageLogService _log;

        public ErrorOnlyAppLogLoggerProvider(MessageLogService log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private void Write(string category, LogLevel level, string message)
        {
            if (Suppress.Value)
                return;
            if (!AppLogNormalizer.IsProblemLogLevel(level))
                return;
            if (string.IsNullOrWhiteSpace(message))
                return;
            if (category.Contains("MessageLogService", StringComparison.Ordinal))
                return;

            try
            {
                Suppress.Value = true;
                _log.Log(AppLogNormalizer.FromMicrosoftLogLevel(level), message, category);
            }
            finally
            {
                Suppress.Value = false;
            }
        }

        private sealed class Logger : ILogger
        {
            private readonly ErrorOnlyAppLogLoggerProvider _provider;
            private readonly string _category;

            public Logger(ErrorOnlyAppLogLoggerProvider provider, string category)
            {
                _provider = provider;
                _category = category;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull
                => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel)
                => AppLogNormalizer.IsProblemLogLevel(logLevel);

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;

                string text = formatter != null ? formatter(state, exception) : state?.ToString() ?? string.Empty;
                if (exception != null)
                    text = string.IsNullOrEmpty(text) ? exception.Message : text + " " + exception.GetType().Name;
                _provider.Write(_category, logLevel, text);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
