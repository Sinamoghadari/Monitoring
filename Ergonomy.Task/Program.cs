using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Forms;
using Ergonomy.Core.Ipc;
using Ergonomy.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ergonomy.TaskAgent
{
    /// <summary>
    /// Entry point of the interactive (user session) process.
    ///
    /// The WinForms STA message loop remains the process pump - the same rule that already
    /// applies to the legacy single-process app - because the low-level input hooks and the
    /// alarm forms require a pumped, interactive desktop. All background/persistence work
    /// belongs to Ergonomy.Service and is reached over the Named Pipe.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class Program
    {
        /// <summary>Guarantees a single interactive agent per logon session.</summary>
        private const string SingleInstanceMutexName = @"Local\Ergonomy.Task.SingleInstance.v1";

        /// <summary>
        /// نقطه ورود فرایند تعاملی است: تک‌نمونه‌ای بودن نشست را تضمین می‌کند،
        /// کانتینر DI را می‌سازد و حلقه پیام WinForms را اجرا می‌نماید.
        /// </summary>
        [STAThread]
        private static void Main()
        {
            using var singleInstance = new Mutex(true, SingleInstanceMutexName, out bool isOwner);
            if (!isOwner)
            {
                // Another interactive agent already serves this session; exit quietly.
                return;
            }

            ApplicationConfiguration.Initialize();

            using ServiceProvider provider = BuildServiceProvider();
            ILogger<object> logger = provider.GetRequiredService<ILogger<object>>();
            logger.LogInformation("Ergonomy.Task starting. Pid={Pid} WindowsSession={Session}",
                Environment.ProcessId, Process.GetCurrentProcess().SessionId);

            using var context = provider.GetRequiredService<TaskApplicationContext>();
            Application.Run(context);

            logger.LogInformation("Ergonomy.Task stopped.");
        }

        /// <summary>
        /// کانتینر DI فرایند Task را با لاگر ساختاریافته، کلاینت پایپ و پوسته چرخه حیات می‌سازد.
        /// </summary>
        /// <returns>ارائه‌دهنده سرویس آماده اجرا.</returns>
        private static ServiceProvider BuildServiceProvider()
        {
            var services = new ServiceCollection();

            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddProvider(new ConsoleStructuredLogProvider());
            });

            services.AddSingleton<NamedPipeIpcClient>(sp => new NamedPipeIpcClient(
                sp.GetRequiredService<ILogger<NamedPipeIpcClient>>()));

            // Migration seam: GlobalInputHook, ActivityMonitor, AlarmManager and the alarm forms
            // move here from the legacy Ergonomy project and publish through TaskApplicationContext.
            services.AddSingleton<TaskApplicationContext>();

            return services.BuildServiceProvider();
        }
    }
}
