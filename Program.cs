using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ergonomy.Configuration;
using Ergonomy.Core.Diagnostics;
using Ergonomy.Core.Hosting;
using Ergonomy.Logging;
using Ergonomy.Services;

namespace Ergonomy
{
    internal static class Program
    {
        private const string SingleInstanceMutexName = @"Global\Ergonomy_Agent_SingleInstance_Mutex";
        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        /// <summary>
        /// نقطه ورود برنامه تعاملی ارگونومی است.
        /// ارائه‌دهنده تزریق وابستگی را روی نخ رابط کاربری می‌سازد، تنظیمات اولیه را بارگذاری می‌کند
        /// و حلقه پیام WinForms را به‌عنوان پمپ اصلی فرایند اجرا می‌کند.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            ExceptionPolicy.InstallLastChance();
            StartupLog.EnsureDirectories();
            StartupLog.Info("startup process entered");

            if (!TryAcquireSingleInstanceMutex(out Mutex? singleInstance) || singleInstance == null)
            {
                StartupLog.Warn(@"Exit: could not claim single-instance mutex (Global\Ergonomy_Agent_SingleInstance_Mutex). Another instance may already be running.");
                return;
            }

            IsolationClaim? sqliteOwner = null;
            using (singleInstance)
            {
                try
                {
                    GC.KeepAlive(singleInstance);

                    if (RuntimeIsolation.IsServiceRunning())
                    {
                        StartupLog.Info("Ergonomy.Service owns SQLite; tray will not open the database.");
                    }
                    else
                    {
                        sqliteOwner = RuntimeIsolation.TryClaimSqliteOwner();
                        if (sqliteOwner == null)
                        {
                            StartupLog.Warn(@"Exit: SQLite owner mutex already held (Global\Ergonomy_Sqlite_Owner_v1).");
                            return;
                        }
                    }

                    RunApplication(args);
                }
                finally
                {
                    sqliteOwner?.Dispose();
                    try { singleInstance.ReleaseMutex(); }
                    catch (ApplicationException ex) { ExceptionPolicy.IgnoreBestEffortDispose(ex, "tray-mutex-release"); }
                }
            }
        }

        /// <summary>
        /// Creates the process-wide mutex. A second instance exits immediately with no UI.
        /// An abandoned mutex (previous crash) is treated as ownership of this instance.
        /// </summary>
        private static bool TryAcquireSingleInstanceMutex(out Mutex? mutex)
        {
            mutex = null;
            try
            {
                mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
                if (!createdNew)
                {
                    mutex.Dispose();
                    mutex = null;
                    return false;
                }

                return true;
            }
            catch (AbandonedMutexException ex)
            {
                mutex = ex.Mutex ?? mutex;
                if (mutex == null)
                {
                    try
                    {
                        mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out _);
                    }
                    catch (Exception recreateEx)
                    {
                        ExceptionPolicy.IgnoreBestEffortDispose(recreateEx, "tray-mutex-abandoned-recreate");
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                ExceptionPolicy.IgnoreBestEffortDispose(ex, "tray-mutex-acquire");
                mutex?.Dispose();
                mutex = null;
                return false;
            }
        }

        private static void RunApplication(string[] args)
        {
            bool diagnose = HasFlag(args, "--diagnose-startup");
            EnsureConsoleAttached(forceAlloc: diagnose);

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) =>
                ExceptionPolicy.Report(e.Exception, "winforms-thread", eventId: LogEvents.WinFormsThreadExceptionId);

            try
            {
                StartupLog.EnsureDirectories();
                StartupLog.Info("startup started");
                StartupLog.Info($"ProgramData verified: {StartupLog.RootDirectory}");

                ApplicationConfiguration.Initialize();

                var uiAnchor = new Control();
                try
                {
                    uiAnchor.CreateControl();
                }
                catch (Exception ex)
                {
                    StartupLog.Error("Could not create UI anchor control.", ex);
                }

                // Reset the WinForms SynchronizationContext so blocking startup work does not
                // deadlock the UI thread before the message loop starts.
                SynchronizationContext.SetSynchronizationContext(null);

                using var provider = ServiceRegistrar.Build(uiAnchor);
                ILoggerFactory loggerFactory = provider.GetRequiredService<ILoggerFactory>();
                ExceptionPolicy.Configure(loggerFactory.CreateLogger("ExceptionPolicy"));
                loggerFactory.AddProvider(
                    new ErrorOnlyAppLogLoggerProvider(provider.GetRequiredService<MessageLogService>()));

                var settingsService = provider.GetRequiredService<ISettingsService>();
                settingsService.LoadBootstrap();
                StartupLog.Info("config loaded");

                if (diagnose)
                    RunStartupDiagnostics(provider);

                using var context = provider.GetRequiredService<MainApplicationContext>();
                StartupLog.Info("MainApplicationContext created");
                StartupLog.Info("Application.Run entered");
                Application.Run(context);
                StartupLog.Info("shutdown completed");
            }
            catch (Exception ex)
            {
                ExceptionPolicy.Report(ex, "tray-startup");
                StartupLog.WriteException(ex, "Fatal startup exception. Tray did not stay alive.");
                PauseIfInteractive();
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            if (args == null || args.Length == 0)
                return false;
            return args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        }

        private static void EnsureConsoleAttached(bool forceAlloc)
        {
            try
            {
                if (!AttachConsole(AttachParentProcess) && forceAlloc)
                    AllocConsole();
            }
            catch (Exception ex)
            {
                ExceptionPolicy.IgnoreBestEffortDispose(ex, "attach-console");
            }

            try { Console.OutputEncoding = Encoding.UTF8; }
            catch (Exception ex) { ExceptionPolicy.IgnoreBestEffortDispose(ex, "console-encoding"); }
        }

        private static void PauseIfInteractive()
        {
            try
            {
                Console.WriteLine();
                Console.WriteLine("A fatal startup error was written to:");
                Console.WriteLine("  " + StartupLog.ErrorLogPath);
                Console.WriteLine("The window will stay open for 20 seconds so the message can be read.");
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                ExceptionPolicy.IgnoreBestEffortDispose(ex, "pause-console");
            }

            try { Thread.Sleep(TimeSpan.FromSeconds(20)); }
            catch (Exception ex) { ExceptionPolicy.IgnoreIfShuttingDown(ex, "pause-sleep"); }
        }

        private static void RunStartupDiagnostics(IServiceProvider provider)
        {
            try
            {
                Console.WriteLine("=== Ergonomy --diagnose-startup ===");
                Console.WriteLine("Root: " + StartupLog.RootDirectory);
                Console.WriteLine("DB:   " + StartupLog.DatabasePath);
                Console.WriteLine("Ver:  " + StartupLog.AppliedVersionPath);
                Console.WriteLine("Log:  " + StartupLog.ErrorLogPath);

                var settings = provider.GetRequiredService<ISettingsService>().Current;
                Console.WriteLine("Config loaded: yes");
                Console.WriteLine("SQLCipher key: " + (File.Exists(SqlCipherKeyStore.KeyFilePath) ? "present" : "missing"));
                Console.WriteLine("Kafka bootstrap: " + (string.IsNullOrWhiteSpace(settings.Kafka?.BootstrapServers) ? "(empty)" : settings.Kafka!.BootstrapServers));
                Console.WriteLine("Kafka topics: activity=" + (settings.Kafka?.UserActivityTopic ?? "") +
                                  " metrics=" + (settings.Kafka?.SystemMetricsTopic ?? "") +
                                  " logs=" + (settings.Kafka?.AppLogsTopic ?? ""));
                Console.WriteLine("Update enabled: " + (settings.Update?.Enabled ?? false));
                Console.WriteLine("DB exists: " + File.Exists(StartupLog.DatabasePath));
                Console.WriteLine("Marker exists: " + File.Exists(StartupLog.AppliedVersionPath));

                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app_icon.ico");
                string pngPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app_icon.png");
                Console.WriteLine("Icon ico exists: " + File.Exists(icoPath) + " (" + icoPath + ")");
                Console.WriteLine("Icon png exists: " + File.Exists(pngPath) + " (" + pngPath + ")");
                Console.WriteLine("ProcessPath: " + (Environment.ProcessPath ?? "(null)"));
                Console.WriteLine("=== end diagnose-startup ===");
                StartupLog.Info("diagnose-startup completed");
            }
            catch (Exception ex)
            {
                StartupLog.Error("diagnose-startup failed.", ex);
            }
        }
    }
}
