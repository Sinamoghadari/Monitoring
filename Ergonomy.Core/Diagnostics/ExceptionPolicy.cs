using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Diagnostics
{
    /// <summary>Process that is reporting the fault. Low-cardinality fleet label.</summary>
    public enum AgentProcessKind
    {
        Unknown = 0,
        Service = 1,
        Task = 2,
        Tray = 3
    }

    /// <summary>
    /// Classification from the architecture process report.
    /// Expected = E0, Operational = E1, Propagate = E2 (logged here; caller still throws), Fatal = E3.
    /// </summary>
    public enum ExceptionSeverity
    {
        Expected = 0,
        Operational = 1,
        Propagate = 2,
        Fatal = 3
    }

    /// <summary>Optional per-call enrichment. Null fields are filled by <see cref="ExceptionPolicy"/>.</summary>
    public readonly struct ExceptionReportContext
    {
        public string? Module { get; init; }
        public string? ActivityId { get; init; }
        public string? Message { get; init; }
        public int? Win32 { get; init; }

        /// <summary>
        /// When true, <see cref="Marshal.GetLastWin32Error"/> is read at the report site.
        /// Callers must set this only immediately after a native failure.
        /// </summary>
        public bool CaptureWin32 { get; init; }
    }

    /// <summary>Immutable snapshot emitted by <see cref="ExceptionPolicy.Report"/>.</summary>
    public sealed class ExceptionReport
    {
        public DateTime TimestampUtc { get; init; }
        public AgentProcessKind Process { get; init; }
        public string MachineName { get; init; } = Environment.MachineName;
        public int ThreadId { get; init; }
        public string Module { get; init; } = "unknown";
        public string? ActivityId { get; init; }
        public string ExceptionType { get; init; } = "None";
        public int HResult { get; init; }
        public int? Win32 { get; init; }
        public ExceptionSeverity Severity { get; init; }
        public string Message { get; init; } = string.Empty;
        public Exception? Exception { get; init; }
    }

    /// <summary>
    /// Process-wide exception policy for Service, Task and the legacy tray.
    /// Core has no SQLite/WinForms dependency: persistence happens through the configured
    /// <see cref="ILogger"/> (ErrorOnlyAppLogLoggerProvider copies WARNING/ERROR to the outbox).
    /// Re-entrancy is suppressed with <see cref="AsyncLocal{T}"/> so a failed log cannot recurse.
    /// </summary>
    public static class ExceptionPolicy
    {
        public const string LoggerCategory = "Ergonomy.Diagnostics.ExceptionPolicy";

        private static readonly AsyncLocal<bool> Suppress = new();
        private static readonly object Sync = new();
        private static ILogger? _logger;
        private static Action<ExceptionReport>? _sink;
        private static AgentProcessKind _process;
        private static string _machineName = Environment.MachineName;
        private static int _handlersInstalled;

        /// <summary>True while a report is already in flight on this async flow.</summary>
        public static bool IsSuppressed => Suppress.Value;

        /// <summary>
        /// Binds the process kind and optional logger/sink. Safe to call more than once
        /// (host start wires a real logger after last-chance handlers are installed).
        /// </summary>
        public static void Configure(
            AgentProcessKind process,
            ILogger? logger = null,
            Action<ExceptionReport>? sink = null,
            string? machineName = null)
        {
            lock (Sync)
            {
                if (process != AgentProcessKind.Unknown)
                    _process = process;
                if (logger != null)
                    _logger = logger;
                if (sink != null)
                    _sink = sink;
                if (!string.IsNullOrWhiteSpace(machineName))
                    _machineName = machineName!;
            }
        }

        /// <summary>
        /// Hooks <see cref="AppDomain.UnhandledException"/> and
        /// <see cref="TaskScheduler.UnobservedTaskException"/>. Idempotent.
        /// WinForms <c>Application.ThreadException</c> is wired by the STA entry points.
        /// </summary>
        public static void InstallLastChanceHandlers(AgentProcessKind process)
        {
            Configure(process);
            if (Interlocked.Exchange(ref _handlersInstalled, 1) != 0)
                return;

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        /// <summary>WinForms STA hook. Must not block; never touches SQLite directly.</summary>
        public static void ReportThreadException(Exception? exception)
        {
            if (exception == null)
                return;
            Report(
                ExceptionSeverity.Operational,
                exception,
                new ExceptionReportContext { Module = "Application.ThreadException" });
        }

        /// <summary>
        /// E0 helper for cancellation, disposal and broken-pipe during shutdown.
        /// Unexpected types are promoted to Operational so they are not silent.
        /// </summary>
        public static void IgnoreIfShuttingDown(
            Exception? exception,
            string? module = null,
            [CallerFilePath] string? callerFile = null)
        {
            if (exception == null)
                return;

            string resolved = ResolveModule(module, callerFile);
            if (IsShutdownException(exception))
            {
                Report(
                    ExceptionSeverity.Expected,
                    exception,
                    new ExceptionReportContext { Module = resolved, Message = "Ignored shutdown exception." });
                return;
            }

            Report(
                ExceptionSeverity.Operational,
                exception,
                new ExceptionReportContext
                {
                    Module = resolved,
                    Message = "Non-shutdown exception in a shutdown/cancel handler."
                });
        }

        /// <summary>
        /// E0 helper for best-effort Dispose/flush/ACL probes. Always Expected (Debug).
        /// Never throws. Never allocates beyond the report snapshot.
        /// </summary>
        public static void IgnoreBestEffortDispose(
            Exception? exception,
            string? module = null,
            [CallerFilePath] string? callerFile = null)
        {
            if (exception == null)
                return;

            Report(
                ExceptionSeverity.Expected,
                exception,
                new ExceptionReportContext
                {
                    Module = ResolveModule(module, callerFile),
                    Message = "Best-effort dispose/flush ignored."
                });
        }

        /// <summary>
        /// Central report path. Never throws. Never re-enters. Does not crash the host.
        /// </summary>
        public static ExceptionReport Report(
            ExceptionSeverity severity,
            Exception? exception,
            ExceptionReportContext context = default,
            [CallerFilePath] string? callerFile = null)
        {
            var report = BuildReport(severity, exception, context, callerFile);
            if (Suppress.Value)
                return report;

            Suppress.Value = true;
            try
            {
                ILogger? logger;
                Action<ExceptionReport>? sink;
                lock (Sync)
                {
                    logger = _logger;
                    sink = _sink;
                }

                LogReport(logger, report);
                sink?.Invoke(report);
            }
            catch (Exception ex)
            {
                try
                {
                    System.Diagnostics.Trace.TraceError(
                        "ExceptionPolicy sink/logger failed: " + ex.GetType().FullName);
                }
                catch (Exception traceEx)
                {
                    IgnoreBestEffortDispose(traceEx);
                }
            }
            finally
            {
                Suppress.Value = false;
            }

            return report;
        }

        public static bool IsShutdownException(Exception? exception)
        {
            if (exception == null)
                return false;
            if (exception is OperationCanceledException or ObjectDisposedException)
                return true;
            if (exception is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.Flatten().InnerExceptions)
                {
                    if (!IsShutdownException(inner))
                        return false;
                }

                return aggregate.InnerExceptions.Count > 0;
            }

            if (exception is System.IO.IOException)
            {
                int hr = exception.HResult;
                // ERROR_BROKEN_PIPE (109 / 0x8007006D), ERROR_NO_DATA (232 / 0x800700E8),
                // ERROR_PIPE_NOT_CONNECTED (233 / 0x800700E9).
                if (hr == unchecked((int)0x8007006D) ||
                    hr == unchecked((int)0x800700E8) ||
                    hr == unchecked((int)0x800700E9) ||
                    hr == 109 || hr == 232 || hr == 233)
                {
                    return true;
                }
            }

            return false;
        }

        internal static void ResetForTests()
        {
            lock (Sync)
            {
                _logger = null;
                _sink = null;
                _process = AgentProcessKind.Unknown;
                _machineName = Environment.MachineName;
            }

            Suppress.Value = false;
        }

        internal static ExceptionReport BuildReport(
            ExceptionSeverity severity,
            Exception? exception,
            ExceptionReportContext context,
            string? callerFile)
        {
            AgentProcessKind process;
            string machine;
            lock (Sync)
            {
                process = _process;
                machine = _machineName;
            }

            int? win32 = context.Win32;
            if (context.CaptureWin32)
                win32 ??= Marshal.GetLastWin32Error();
            if (exception is ExternalException external)
                win32 ??= external.ErrorCode;

            return new ExceptionReport
            {
                TimestampUtc = DateTime.UtcNow,
                Process = process,
                MachineName = machine,
                ThreadId = Environment.CurrentManagedThreadId,
                Module = ResolveModule(context.Module, callerFile),
                ActivityId = context.ActivityId,
                ExceptionType = exception?.GetType().FullName ?? "None",
                HResult = exception?.HResult ?? 0,
                Win32 = win32,
                Severity = severity,
                Message = context.Message ?? exception?.Message ?? string.Empty,
                Exception = exception
            };
        }

        private static string ResolveModule(string? module, string? callerFile)
        {
            if (!string.IsNullOrWhiteSpace(module))
                return module!;
            if (string.IsNullOrWhiteSpace(callerFile))
                return "unknown";
            return System.IO.Path.GetFileNameWithoutExtension(callerFile);
        }

        private static void LogReport(ILogger? logger, ExceptionReport report)
        {
            const string template =
                "ExceptionPolicy Severity={Severity} Process={Process} Module={Module} ThreadId={ThreadId} " +
                "ExType={ExType} HResult={HResult} Win32={Win32} Machine={Machine} ActivityId={ActivityId} Message={Message}";

            object?[] args =
            {
                report.Severity,
                report.Process,
                report.Module,
                report.ThreadId,
                report.ExceptionType,
                report.HResult,
                report.Win32,
                report.MachineName,
                report.ActivityId ?? string.Empty,
                report.Message
            };

            if (logger == null)
            {
                if (report.Severity >= ExceptionSeverity.Operational)
                {
                    System.Diagnostics.Trace.TraceError(
                        $"[{report.TimestampUtc:o}] {report.Severity} {report.Process}/{report.Module} {report.ExceptionType}: {report.Message}");
                }

                return;
            }

            switch (report.Severity)
            {
                case ExceptionSeverity.Expected:
                    logger.LogDebug(report.Exception, template, args);
                    break;
                case ExceptionSeverity.Operational:
                    logger.LogWarning(report.Exception, template, args);
                    break;
                case ExceptionSeverity.Propagate:
                    logger.LogError(report.Exception, template, args);
                    break;
                default:
                    logger.LogCritical(report.Exception, template, args);
                    break;
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception
                           ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown unhandled exception.");
            Report(
                e.IsTerminating ? ExceptionSeverity.Fatal : ExceptionSeverity.Operational,
                ex,
                new ExceptionReportContext { Module = "AppDomain.UnhandledException" });
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Report(
                ExceptionSeverity.Operational,
                e.Exception,
                new ExceptionReportContext { Module = "TaskScheduler.UnobservedTaskException" });
            e.SetObserved();
        }
    }
}
