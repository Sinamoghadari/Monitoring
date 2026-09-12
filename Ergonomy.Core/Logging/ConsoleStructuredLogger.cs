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
        /// <summary>
        /// Optional sink that copies console/ILogger lines into the Kafka app_logs outbox.
        /// Wired after <c>MessageLogService</c> is constructed to avoid a DI cycle.
        /// </summary>
        public static Action<LogLevel, string, string, Exception?>? AppLogsSink { get; set; }

        private readonly ConcurrentDictionary<string, ConsoleStructuredLogger> _loggers =
            new(StringComparer.Ordinal);

        /// <summary>
        /// یک لاگر ساختاریافته برای دسته مشخص ایجاد یا بازیابی می‌کند.
        /// </summary>
        /// <param name="categoryName">نام دسته لاگ.</param>
        /// <returns>نمونه ILogger مربوط به دسته.</returns>
        public ILogger CreateLogger(string categoryName) =>
            _loggers.GetOrAdd(categoryName, _ => new ConsoleStructuredLogger(categoryName));

        /// <summary>
        /// کش لاگرها را پاک می‌کند.
        /// </summary>
        public void Dispose()
        {
            _loggers.Clear();
        }

        private sealed class ConsoleStructuredLogger : ILogger
        {
            private readonly string _category;
            private readonly object _lock = new();

            /// <summary>
            /// لاگر کنسول را برای یک دسته مشخص می‌سازد.
            /// </summary>
            /// <param name="category">نام دسته.</param>
            public ConsoleStructuredLogger(string category) => _category = category;

            /// <summary>
            /// دامنه لاگ پشتیبانی نمی‌شود و null برمی‌گرداند.
            /// </summary>
            /// <typeparam name="TState">نوع وضعیت دامنه.</typeparam>
            /// <param name="state">وضعیت دامنه.</param>
            /// <returns>همیشه null.</returns>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            /// <summary>
            /// مشخص می‌کند که سطح لاگ حداقل Information باشد.
            /// </summary>
            /// <param name="logLevel">سطح مورد بررسی.</param>
            /// <returns>اگر سطح فعال باشد true است.</returns>
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            /// <summary>
            /// پیام ساختاریافته را به یک خط خوانا تبدیل کرده و روی stdout می‌نویسد.
            /// </summary>
            /// <typeparam name="TState">نوع وضعیت قالب‌بندی.</typeparam>
            /// <param name="logLevel">سطح لاگ.</param>
            /// <param name="eventId">شناسه رویداد شناخته‌شده.</param>
            /// <param name="state">وضعیت قالب.</param>
            /// <param name="exception">استثنای اختیاری.</param>
            /// <param name="formatter">تابع ساخت متن پیام.</param>
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

                try
                {
                    AppLogsSink?.Invoke(logLevel, _category, message, exception);
                }
                catch
                {
                }
            }

            /// <summary>
            /// خط خروجی را با زمان، سطح، دسته و شناسه رویداد می‌سازد.
            /// </summary>
            /// <param name="logLevel">سطح لاگ.</param>
            /// <param name="eventId">شناسه رویداد.</param>
            /// <param name="message">متن قالب‌بندی‌شده.</param>
            /// <param name="exception">استثنای اختیاری.</param>
            /// <returns>خط آماده چاپ.</returns>
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
                    sb.Append(" | EX: ").Append(exception.GetType().Name);
                return sb.ToString();
            }
        }
    }
}
