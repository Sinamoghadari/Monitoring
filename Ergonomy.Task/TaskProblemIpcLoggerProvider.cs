using System;
using System.Threading;
using Ergonomy.Core.Ipc;
using Ergonomy.Logging;
using Microsoft.Extensions.Logging;

namespace Ergonomy.TaskAgent
{
    /// <summary>
    /// Sends only Warning/Error/Critical records to Ergonomy.Service over the pipe so they
    /// can be written to SQLite and forwarded to app_logs. Healthy Information emits nothing.
    /// </summary>
    internal sealed class TaskProblemIpcLoggerProvider : ILoggerProvider
    {
        private static readonly AsyncLocal<bool> Suppress = new();
        private readonly NamedPipeIpcClient _client;

        public TaskProblemIpcLoggerProvider(NamedPipeIpcClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private void Write(string category, LogLevel level, string message)
        {
            if (Suppress.Value || !AppLogNormalizer.IsProblemLogLevel(level) || string.IsNullOrWhiteSpace(message))
                return;
            if (!_client.IsConnected)
                return;

            try
            {
                Suppress.Value = true;
                var payload = new ProblemLogPayload
                {
                    Level = AppLogNormalizer.FromMicrosoftLogLevel(level),
                    Message = message,
                    Category = string.IsNullOrWhiteSpace(category) ? "Task" : category
                };
                _ = _client.TrySendAsync(IpcMessage.Create(IpcMessageTypes.ProblemLog, payload));
            }
            finally
            {
                Suppress.Value = false;
            }
        }

        private sealed class Logger : ILogger
        {
            private readonly TaskProblemIpcLoggerProvider _provider;
            private readonly string _category;

            public Logger(TaskProblemIpcLoggerProvider provider, string category)
            {
                _provider = provider;
                _category = category;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull
                => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => AppLogNormalizer.IsProblemLogLevel(logLevel);

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
