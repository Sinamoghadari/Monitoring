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
using Ergonomy.Services;

namespace Ergonomy
{
    internal static class Program
    {
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
            bool diagnose = HasFlag(args, "--diagnose-startup");
            EnsureConsoleAttached(forceAlloc: diagnose);

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
                provider.GetRequiredService<ILoggerFactory>().AddProvider(
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
            catch
            {
            }

            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }
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
            catch
            {
            }

            try { Thread.Sleep(TimeSpan.FromSeconds(20)); }
            catch { }
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
