using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ergonomy.Logging;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Core.Diagnostics
{
    /// <summary>
    /// Single reporting path for unexpected exceptions. Never throws. Re-entrant
    /// <see cref="Report"/> calls on the same async context are dropped so a sink
    /// failure cannot recurse (AsyncLocal depth).
    /// </summary>
    public static class ExceptionPolicy
    {
        private static readonly AsyncLocal<int> Depth = new();
        private static readonly string CachedProcessName = ReadProcessName();
        private static ILogger? _logger;
        private static int _lastChanceInstalled;

        /// <summary>True while <see cref="Report"/> is running on this async context.</summary>
        public static bool IsReporting => Depth.Value > 0;

        /// <summary>Optional ILogger used when a call site does not pass one.</summary>
        public static void Configure(ILogger? logger) => _logger = logger;

        /// <summary>
        /// Hooks <see cref="AppDomain.UnhandledException"/> and
        /// <see cref="TaskScheduler.UnobservedTaskException"/>. Idempotent.
        /// WinForms <c>ThreadException</c> is installed by the tray/Task entry points
        /// so this assembly stays free of a WinForms dependency.
        /// </summary>
        public static void InstallLastChance(ILogger? logger = null)
        {
            if (logger != null)
                _logger = logger;

            if (Interlocked.Exchange(ref _lastChanceInstalled, 1) != 0)
                return;

            AppDomain.CurrentDomain.UnhandledException += HandleUnhandled;
            TaskScheduler.UnobservedTaskException += HandleUnobserved;
        }

        /// <summary>
        /// Logs <paramref name="exception"/> with machine, thread, process, module,
        /// UTC, type, HRESULT and Win32 when available. Swallows every failure.
        /// </summary>
        public static void Report(
            Exception? exception,
            string context,
            ILogger? logger = null,
            EventId? eventId = null)
        {
            if (exception == null)
                return;

            if (Depth.Value > 0)
                return;

            Depth.Value = Depth.Value + 1;
            try
            {
                ExceptionReport report = Capture(exception, context);
                EventId id = eventId ?? LogEvents.OperationalExceptionId;
                ILogger? sink = logger ?? _logger;
                string message = Format(report);

                if (sink != null)
                {
                    sink.LogError(
                        id,
                        exception,
                        "{Context} {ExceptionType} HResult=0x{HResult:X8} Win32={Win32} Machine={Machine} Thread={ThreadId} Process={Process} Module={Module} Utc={Utc:o}",
                        report.Context,
                        report.ExceptionType,
                        report.HResult,
                        report.Win32Error?.ToString(CultureInfo.InvariantCulture) ?? "-",
                        report.MachineName,
                        report.ThreadId,
                        report.Process,
                        report.Module ?? "-",
                        report.Utc);
                }
                else
                {
                    Console.Error.WriteLine(message);
                    Console.Error.WriteLine(exception);
                }
            }
            catch (Exception fallback)
            {
                try { Console.Error.WriteLine(exception); }
                catch (Exception) { _ = fallback; }
            }
            finally
            {
                Depth.Value = Depth.Value - 1;
            }
        }

        /// <summary>
        /// Shutdown-shaped failures (cancel / dispose) are silent. Anything else is reported.
        /// </summary>
        public static void IgnoreIfShuttingDown(Exception? exception, string context, ILogger? logger = null)
        {
            if (exception == null || IsShutdown(exception))
                return;
            Report(exception, context, logger);
        }

        /// <summary>
        /// Dispose / release / Close failures are expected during teardown. Named so
        /// ERGONOMY001 can see a classified catch; does not emit a log line.
        /// </summary>
        public static void IgnoreBestEffortDispose(Exception? exception, string context)
        {
            _ = exception;
            _ = context;
        }

        internal static void HandleUnhandled(object? sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception
                ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "non-Exception unhandled");
            Report(ex, "unhandled", eventId: LogEvents.UnhandledExceptionId);
        }

        internal static void HandleUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Report(e.Exception, "unobserved-task", eventId: LogEvents.UnobservedTaskExceptionId);
            e.SetObserved();
        }

        internal static ExceptionReport Capture(Exception exception, string context)
        {
            return new ExceptionReport(
                context ?? string.Empty,
                DateTime.UtcNow,
                Environment.MachineName,
                Environment.CurrentManagedThreadId,
                CachedProcessName,
                exception.TargetSite?.Module?.Name,
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.HResult,
                TryWin32(exception));
        }

        private static bool IsShutdown(Exception exception)
        {
            if (exception is OperationCanceledException or ObjectDisposedException or TaskCanceledException)
                return true;
            if (exception is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    if (!IsShutdown(inner))
                        return false;
                }
                return aggregate.InnerExceptions.Count > 0;
            }
            return false;
        }

        private static int? TryWin32(Exception exception)
        {
            if (exception is Win32Exception win32)
                return win32.NativeErrorCode;
            if (exception is SocketException socket)
                return socket.NativeErrorCode;
            if (exception is ExternalException external && external.ErrorCode != 0)
                return external.ErrorCode;
            return null;
        }

        private static string Format(ExceptionReport report)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{report.Utc:o} {report.Context} {report.ExceptionType} HResult=0x{report.HResult:X8} Win32={report.Win32Error?.ToString(CultureInfo.InvariantCulture) ?? "-"} Machine={report.MachineName} Thread={report.ThreadId} Process={report.Process} Module={report.Module ?? "-"}");
        }

        private static string ReadProcessName()
        {
            try { return Process.GetCurrentProcess().ProcessName; }
            catch { return "unknown"; }
        }
    }

    /// <summary>Immutable snapshot written by <see cref="ExceptionPolicy.Report"/>.</summary>
    internal readonly struct ExceptionReport
    {
        public ExceptionReport(
            string context,
            DateTime utc,
            string machineName,
            int threadId,
            string process,
            string? module,
            string exceptionType,
            int hResult,
            int? win32Error)
        {
            Context = context;
            Utc = utc;
            MachineName = machineName;
            ThreadId = threadId;
            Process = process;
            Module = module;
            ExceptionType = exceptionType;
            HResult = hResult;
            Win32Error = win32Error;
        }

        public string Context { get; }
        public DateTime Utc { get; }
        public string MachineName { get; }
        public int ThreadId { get; }
        public string Process { get; }
        public string? Module { get; }
        public string ExceptionType { get; }
        public int HResult { get; }
        public int? Win32Error { get; }
    }
}
