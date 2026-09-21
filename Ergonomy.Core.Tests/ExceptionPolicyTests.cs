using System.ComponentModel;
using Ergonomy.Core.Diagnostics;
using Ergonomy.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ergonomy.Core.Tests
{
    public sealed class ExceptionPolicyTests
    {
        [Fact]
        public void Report_never_throws_when_logger_throws()
        {
            var logger = new ThrowingLogger();
            ExceptionPolicy.Report(new InvalidOperationException("boom"), "test-throw", logger);
        }

        [Fact]
        public void Report_drops_reentrant_calls_on_the_same_async_context()
        {
            int outerWrites = 0;
            var logger = new CallbackLogger(_ =>
            {
                outerWrites++;
                ExceptionPolicy.Report(new InvalidOperationException("nested"), "nested");
            });

            ExceptionPolicy.Report(new InvalidOperationException("outer"), "outer", logger);

            Assert.Equal(1, outerWrites);
            Assert.False(ExceptionPolicy.IsReporting);
        }

        [Fact]
        public void Capture_includes_machine_thread_process_type_hresult_and_win32()
        {
            var ex = new Win32Exception(5);
            ExceptionReport report = ExceptionPolicy.Capture(ex, "win32-sample");

            Assert.Equal("win32-sample", report.Context);
            Assert.False(string.IsNullOrWhiteSpace(report.MachineName));
            Assert.Equal(Environment.CurrentManagedThreadId, report.ThreadId);
            Assert.False(string.IsNullOrWhiteSpace(report.Process));
            Assert.Contains("Win32Exception", report.ExceptionType, StringComparison.Ordinal);
            Assert.Equal(ex.HResult, report.HResult);
            Assert.Equal(5, report.Win32Error);
            Assert.True(report.Utc <= DateTime.UtcNow.AddSeconds(2));
            Assert.True(report.Utc >= DateTime.UtcNow.AddMinutes(-1));
        }

        [Fact]
        public void IgnoreIfShuttingDown_does_not_report_canceled_or_disposed()
        {
            int writes = 0;
            var logger = new CallbackLogger(_ => writes++);

            ExceptionPolicy.IgnoreIfShuttingDown(new OperationCanceledException(), "shutdown", logger);
            ExceptionPolicy.IgnoreIfShuttingDown(new ObjectDisposedException("x"), "shutdown", logger);
            ExceptionPolicy.IgnoreIfShuttingDown(new InvalidOperationException("real"), "shutdown", logger);

            Assert.Equal(1, writes);
        }

        [Fact]
        public void HandleUnobserved_marks_the_exception_observed()
        {
            var args = new UnobservedTaskExceptionEventArgs(
                new AggregateException(new InvalidOperationException("unobserved")));
            ExceptionPolicy.HandleUnobserved(null, args);
            Assert.True(args.Observed);
        }

        [Fact]
        public void HandleUnhandled_accepts_non_exception_payload()
        {
            var args = new UnhandledExceptionEventArgs("not-an-exception", isTerminating: false);
            ExceptionPolicy.HandleUnhandled(null, args);
        }

        [Fact]
        public void Last_chance_event_ids_are_stable()
        {
            Assert.Equal(9000, LogEvents.UnhandledException);
            Assert.Equal(9010, LogEvents.UnobservedTaskException);
            Assert.Equal(9020, LogEvents.WinFormsThreadException);
            Assert.Equal(9030, LogEvents.OperationalException);
        }

        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => throw new InvalidOperationException("logger-failed");
        }

        private sealed class CallbackLogger : ILogger
        {
            private readonly Action<string> _onLog;
            public CallbackLogger(Action<string> onLog) => _onLog = onLog;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => _onLog(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
