using System.Runtime.InteropServices;
using Ergonomy.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ergonomy.Core.Tests
{
    public sealed class ExceptionPolicyTests : IDisposable
    {
        public ExceptionPolicyTests()
        {
            ExceptionPolicy.ResetForTests();
        }

        public void Dispose()
        {
            ExceptionPolicy.ResetForTests();
        }

        [Fact]
        public void Report_enriches_machine_thread_process_module_timestamp_and_exception_type()
        {
            ExceptionPolicy.Configure(AgentProcessKind.Service, machineName: "TEST-HOST");
            var thrown = new InvalidOperationException("boom");

            ExceptionReport report = ExceptionPolicy.Report(
                ExceptionSeverity.Operational,
                thrown,
                new ExceptionReportContext { Module = "SyncEngine", Message = "kafka send failed" });

            Assert.Equal(AgentProcessKind.Service, report.Process);
            Assert.Equal("TEST-HOST", report.MachineName);
            Assert.Equal("SyncEngine", report.Module);
            Assert.Equal(typeof(InvalidOperationException).FullName, report.ExceptionType);
            Assert.Equal("kafka send failed", report.Message);
            Assert.Equal(thrown.HResult, report.HResult);
            Assert.Equal(Environment.CurrentManagedThreadId, report.ThreadId);
            Assert.True(report.TimestampUtc <= DateTime.UtcNow);
            Assert.True(report.TimestampUtc > DateTime.UtcNow.AddMinutes(-1));
            Assert.Same(thrown, report.Exception);
        }

        [Fact]
        public void Report_does_not_call_GetLastWin32Error_unless_CaptureWin32()
        {
            ExceptionPolicy.Configure(AgentProcessKind.Task);
            var ex = new ExternalException("native", 0x20);

            ExceptionReport without = ExceptionPolicy.Report(
                ExceptionSeverity.Operational,
                ex,
                new ExceptionReportContext { Module = "Ipc" });
            Assert.Equal(0x20, without.Win32);

            ExceptionReport captured = ExceptionPolicy.Report(
                ExceptionSeverity.Operational,
                new InvalidOperationException("x"),
                new ExceptionReportContext { Module = "Ipc", CaptureWin32 = true });
            Assert.NotNull(captured.Win32);
        }

        [Fact]
        public void Report_is_reentrant_safe_via_AsyncLocal()
        {
            int calls = 0;
            ExceptionPolicy.Configure(
                AgentProcessKind.Tray,
                sink: _ =>
                {
                    calls++;
                    ExceptionPolicy.Report(
                        ExceptionSeverity.Operational,
                        new InvalidOperationException("nested"),
                        new ExceptionReportContext { Module = "nested" });
                });

            ExceptionPolicy.Report(
                ExceptionSeverity.Operational,
                new InvalidOperationException("outer"),
                new ExceptionReportContext { Module = "outer" });

            Assert.Equal(1, calls);
            Assert.False(ExceptionPolicy.IsSuppressed);
        }

        [Fact]
        public void IgnoreIfShuttingDown_treats_oce_and_ode_as_expected()
        {
            ExceptionPolicy.Configure(AgentProcessKind.Service);
            ExceptionReport? seen = null;
            ExceptionPolicy.Configure(AgentProcessKind.Service, sink: r => seen = r);

            ExceptionPolicy.IgnoreIfShuttingDown(new OperationCanceledException(), "SyncEngine");
            Assert.NotNull(seen);
            Assert.Equal(ExceptionSeverity.Expected, seen!.Severity);

            ExceptionPolicy.IgnoreIfShuttingDown(new ObjectDisposedException("pipe"), "IpcConnection");
            Assert.Equal(ExceptionSeverity.Expected, seen.Severity);
            Assert.True(ExceptionPolicy.IsShutdownException(new OperationCanceledException()));
            Assert.True(ExceptionPolicy.IsShutdownException(new ObjectDisposedException("x")));
        }

        [Fact]
        public void IgnoreIfShuttingDown_promotes_unexpected_types()
        {
            ExceptionReport? seen = null;
            ExceptionPolicy.Configure(AgentProcessKind.Service, sink: r => seen = r);
            ExceptionPolicy.IgnoreIfShuttingDown(new InvalidOperationException("not shutdown"), "WorkerBase");
            Assert.NotNull(seen);
            Assert.Equal(ExceptionSeverity.Operational, seen!.Severity);
        }

        [Fact]
        public void IgnoreBestEffortDispose_never_throws_and_is_expected()
        {
            ExceptionReport? seen = null;
            ExceptionPolicy.Configure(AgentProcessKind.Task, sink: r => seen = r);
            ExceptionPolicy.IgnoreBestEffortDispose(new IOException("flush"), "KafkaConnect");
            Assert.NotNull(seen);
            Assert.Equal(ExceptionSeverity.Expected, seen!.Severity);
            ExceptionPolicy.IgnoreBestEffortDispose(null);
        }

        [Fact]
        public void Report_never_throws_when_sink_fails()
        {
            ExceptionPolicy.Configure(
                AgentProcessKind.Service,
                sink: _ => throw new InvalidOperationException("sink down"));
            ExceptionReport report = ExceptionPolicy.Report(
                ExceptionSeverity.Fatal,
                new OutOfMemoryException("simulated"),
                new ExceptionReportContext { Module = "last-chance" });
            Assert.Equal(ExceptionSeverity.Fatal, report.Severity);
        }

        [Fact]
        public void Report_uses_caller_file_when_module_omitted()
        {
            ExceptionPolicy.Configure(AgentProcessKind.Service);
            ExceptionReport report = ExceptionPolicy.Report(
                ExceptionSeverity.Operational,
                new Exception("x"));
            Assert.Equal(nameof(ExceptionPolicyTests), report.Module);
        }

        private sealed class CollectingLogger : ILogger
        {
            public readonly List<(LogLevel Level, Exception? Ex)> Entries = new();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Entries.Add((logLevel, exception));
        }

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }

        [Fact]
        public void Report_maps_severity_to_log_level()
        {
            var logger = new CollectingLogger();
            ExceptionPolicy.Configure(AgentProcessKind.Service, logger);
            ExceptionPolicy.Report(ExceptionSeverity.Expected, new Exception("a"), new ExceptionReportContext { Module = "t" });
            ExceptionPolicy.Report(ExceptionSeverity.Operational, new Exception("b"), new ExceptionReportContext { Module = "t" });
            ExceptionPolicy.Report(ExceptionSeverity.Propagate, new Exception("c"), new ExceptionReportContext { Module = "t" });
            ExceptionPolicy.Report(ExceptionSeverity.Fatal, new Exception("d"), new ExceptionReportContext { Module = "t" });

            Assert.Equal(LogLevel.Debug, logger.Entries[0].Level);
            Assert.Equal(LogLevel.Warning, logger.Entries[1].Level);
            Assert.Equal(LogLevel.Error, logger.Entries[2].Level);
            Assert.Equal(LogLevel.Critical, logger.Entries[3].Level);
        }
    }
}
