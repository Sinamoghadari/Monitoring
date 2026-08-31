using System;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Ergonomy.Configuration;
using Ergonomy.Services;

namespace Ergonomy
{
    internal static class Program
    {
        /// <summary>
        /// نقطه ورود برنامه تعاملی ارگونومی است.
        /// ارائه‌دهنده تزریق وابستگی را روی نخ رابط کاربری می‌سازد، تنظیمات اولیه را بارگذاری می‌کند
        /// و حلقه پیام WinForms را به‌عنوان پمپ اصلی فرایند اجرا می‌کند.
        /// </summary>
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            // Hidden UI anchor / marshalling control, created on the UI thread. It is used to
            // marshal alarm + notification forms onto the UI thread from worker/timer threads.
            var uiAnchor = new Control();
            try
            {
                uiAnchor.CreateControl();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ [Ergonomy] Could not create UI anchor control: {ex.Message}");
            }

            // Reset the WinForms SynchronizationContext so the blocking startup Settings-API refresh
            // (performed inside MainApplicationContext) does not deadlock the UI thread before the
            // message loop starts. Application.Run installs a fresh WindowsFormsSynchronizationContext.
            SynchronizationContext.SetSynchronizationContext(null);

            using var provider = ServiceRegistrar.Build(uiAnchor);

            var settingsService = provider.GetRequiredService<ISettingsService>();
            settingsService.LoadBootstrap();

            using var context = provider.GetRequiredService<MainApplicationContext>();
            Application.Run(context);
        }
    }
}
