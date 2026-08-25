using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ergonomy.Configuration;
using Ergonomy.Logging;

namespace Ergonomy.Services
{
    /// <summary>
    /// Periodically re-evaluates runtime permissions (SQLite/Kafka/ergonomics) and starts/stops
    /// the associated components. Replaces the StartSqlitePermissionTimer / StartKafkaPermissionTimer
    /// in MainApplicationContext. The interval is the smaller of the SQLite and Kafka permission
    /// retry intervals so both gates are re-checked promptly.
    /// </summary>
    public sealed class PermissionMonitorWorker : WorkerBase
    {
        private readonly ISettingsService _settingsService;
        private readonly PermissionsEvaluator _permissions;

        /// <summary>
        /// کارگر بازبینی مجوز را به تنظیمات و ارزیاب مجوز متصل می‌کند.
        /// </summary>
        /// <param name="settingsService">منبع فاصله بازبینی SQLite و کافکا.</param>
        /// <param name="permissions">ارزیاب شروع و توقف اجزای جمع‌آوری.</param>
        /// <param name="logger">ثبت‌کننده چرخه کارگر.</param>
        public PermissionMonitorWorker(
            ISettingsService settingsService,
            PermissionsEvaluator permissions,
            ILogger<PermissionMonitorWorker> logger)
            : base(logger)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        }

        protected override string Name => nameof(PermissionMonitorWorker);

        /// <summary>
        /// کوچک‌ترین فاصله بازبینی مجوز SQLite و کافکا را به‌عنوان فاصله حلقه انتخاب می‌کند.
        /// </summary>
        /// <returns>فاصله حلقه به ساعت.</returns>
        protected override TimeSpan GetInterval()
        {
            AppSettings s = _settingsService.Current;
            double sqliteHours = s.PermissionSqliteRetryIntervalHours > 0
                ? s.PermissionSqliteRetryIntervalHours : 1;
            double kafkaHours = s.PermissionKafkaRetryIntervalHours > 0
                ? s.PermissionKafkaRetryIntervalHours : 1;

            double hours = Math.Min(sqliteHours, kafkaHours);
            return TimeSpan.FromHours(hours);
        }

        /// <summary>
        /// به‌صورت ناهمگام همه مجوزهای اجرایی را دوباره ارزیابی می‌کند.
        /// </summary>
        /// <param name="ct">توکن لغو دور کاری.</param>
        /// <returns>وظیفه ارزیابی مجوزها.</returns>
        protected override async Task DoWorkAsync(CancellationToken ct)
        {
            await Task.Yield();
            _permissions.EvaluateAll();
        }
    }
}
