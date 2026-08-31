using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Services
{
    /// <summary>
    /// Periodically runs the API/SQLite/self-performance health probes. Runs once immediately
    /// at startup (mirrors the previous Task.Run(PerformAllHealthChecksAsync)) and then on a
    /// fixed 15-minute interval.
    /// </summary>
    public sealed class HealthMonitorWorker : WorkerBase
    {
        private readonly HealthCheckService _healthCheckService;

        /// <summary>
        /// کارگر پایش سلامت را به سرویس پروب متصل می‌کند.
        /// </summary>
        /// <param name="healthCheckService">سرویس اجرای پروب‌های سلامت.</param>
        /// <param name="logger">ثبت‌کننده چرخه کارگر.</param>
        public HealthMonitorWorker(
            HealthCheckService healthCheckService,
            ILogger<HealthMonitorWorker> logger)
            : base(logger)
        {
            _healthCheckService = healthCheckService ?? throw new ArgumentNullException(nameof(healthCheckService));
        }

        protected override string Name => nameof(HealthMonitorWorker);

        protected override bool ImmediateFirstRun => true;

        /// <summary>
        /// فاصله ثابت پانزده دقیقه‌ای پایش سلامت را برمی‌گرداند.
        /// </summary>
        /// <returns>فاصله حلقه.</returns>
        protected override TimeSpan GetInterval() => TimeSpan.FromMinutes(15);

        /// <summary>
        /// یک دور کامل پروب سلامت را به‌صورت ناهمگام اجرا می‌کند.
        /// </summary>
        /// <param name="ct">توکن لغو دور کاری.</param>
        /// <returns>وظیفه اجرای پروب‌ها.</returns>
        protected override async Task DoWorkAsync(CancellationToken ct)
        {
            await _healthCheckService.RunAllAsync().ConfigureAwait(false);
        }
    }
}
