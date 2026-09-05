using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ergonomy.Logging;

namespace Ergonomy.Configuration
{
    public interface ISettingsService
    {
        /// <summary>The current effective settings (bootstrap or API-refreshed).</summary>
        AppSettings Current { get; }

        /// <summary>The immutable bootstrap settings loaded from machine environment variables.</summary>
        AppSettings Bootstrap { get; }

        /// <summary>True once the current settings were refreshed from the Settings API.</summary>
        bool SettingsSourceIsApi { get; }

        /// <summary>Raised (on a background thread) whenever the effective settings are replaced.</summary>
        event Action<AppSettings>? SettingsChanged;

        /// <summary>
        /// تنظیمات بوت‌استرپ را از متغیرهای محیطی سطح ماشین بارگذاری می‌کند؛ فراخوانی تکرارپذیر است.
        /// </summary>
        void LoadBootstrap();

        /// <summary>
        /// به‌صورت ناهمگام تنظیمات را از API تنظیمات (پشتیبانی‌شده با PostgreSQL) می‌خواند
        /// و در صورت تفاوت، Current را جایگزین کرده و SettingsChanged را اعلام می‌کند.
        /// URL سرویس تنظیمات و سوئیچ‌های امنیتی ماشین از بوت‌استرپ حفظ می‌شوند؛ Kafka می‌تواند از API بیاید.
        /// </summary>
        /// <param name="logFailures">اگر true باشد شکست شبکه در سطح هشدار ثبت می‌شود.</param>
        /// <param name="cancellationToken">توکن لغو درخواست HTTP.</param>
        /// <returns>اگر تنظیمات مؤثر عوض شد true است.</returns>
        Task<bool> RefreshFromApiAsync(bool logFailures = false, CancellationToken cancellationToken = default);
    }

    public sealed class SettingsService : ISettingsService, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<SettingsService> _logger;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private readonly object _sync = new();

        private AppSettings _current = null!;
        private AppSettings _bootstrap = null!;
        private bool _sourceIsApi;
        private bool _disposed;

        public event Action<AppSettings>? SettingsChanged;

        /// <summary>
        /// سرویس تنظیمات را با کلاینت HTTP مشترک و ثبت‌کننده رویداد می‌سازد.
        /// </summary>
        /// <param name="httpClient">کلاینت HTTP برای فراخوانی API تنظیمات.</param>
        /// <param name="logger">ثبت‌کننده بارگذاری، تازه‌سازی و خطای اعتبارسنجی.</param>
        public SettingsService(HttpClient httpClient, ILogger<SettingsService> logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public AppSettings Current
        {
            get { lock (_sync) return _current; }
        }

        public AppSettings Bootstrap
        {
            get { lock (_sync) return _bootstrap; }
        }

        public bool SettingsSourceIsApi
        {
            get { lock (_sync) return _sourceIsApi; }
        }

        /// <summary>
        /// تنظیمات بوت‌استرپ را از محیط ماشین می‌خواند، نرمال می‌کند و به‌عنوان تنظیمات مؤثر اولیه قرار می‌دهد.
        /// </summary>
        public void LoadBootstrap()
        {
            AppSettings bootstrap;
            lock (_sync)
            {
                bootstrap = EnvironmentSettingsProvider.Load();
                AppDefaults.Apply(bootstrap);
                _bootstrap = bootstrap;
                _current = bootstrap;
                _sourceIsApi = false;
            }

            TryValidate(bootstrap);

            _logger.LogInformation(
                "Bootstrap settings loaded from Machine Environment Variables. " +
                "AllowErgonomyCollection={AllowErgonomy} UpdateEnabled={UpdateEnabled} " +
                "Enabled metrics count: {EnabledMetricsCount}",
                _current.AllowErgonomyCollection,
                _current.Update?.Enabled ?? false,
                _current.EnabledMetrics?.Count ?? 0);
        }

        /// <summary>
        /// تنظیمات اجباری API و کافکا را اعتبارسنجی کرده و در صورت نقص فقط هشدار می‌دهد.
        /// </summary>
        /// <param name="settings">تنظیمات مورد بررسی.</param>
        /// <returns>اگر اعتبارسنجی موفق باشد true است.</returns>
        private bool TryValidate(AppSettings settings)
        {
            try
            {
                AppDefaults.ValidateRequired(settings);
                return true;
            }
            catch (SettingsValidationException ex)
            {
                _logger.LogWarning(LogEvents.SettingsValidationFailedId, ex,
                    "Required settings validation failed. Reason={Reason}", "required-setting-missing-or-invalid");
                return false;
            }
        }

        /// <summary>
        /// به‌صورت ناهمگام تنظیمات را از API می‌گیرد، زیرساخت محیطی را حفظ می‌کند
        /// و فقط در صورت تفاوت واقعی، تنظیمات مؤثر را جایگزین می‌نماید.
        /// </summary>
        /// <param name="logFailures">اگر true باشد خطاها با سطح Warning ثبت می‌شوند.</param>
        /// <param name="cancellationToken">توکن لغو درخواست.</param>
        /// <returns>اگر تنظیمات جدید اعمال شد true است.</returns>
        public async Task<bool> RefreshFromApiAsync(bool logFailures = false, CancellationToken cancellationToken = default)
        {
            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                string? apiUrl = _current.API?.Settings;
                if (string.IsNullOrWhiteSpace(apiUrl))
                {
                    if (logFailures)
                        _logger.LogWarning("Settings API URL is empty in Environment settings.");
                    return false;
                }

                using HttpResponseMessage response = await _httpClient.GetAsync(apiUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    if (logFailures)
                        _logger.LogWarning("Settings API returned status code: {StatusCode}", response.StatusCode);
                    return false;
                }

                string jsonString = await response.Content.ReadAsStringAsync(cancellationToken);
                AppSettings? remoteSettings;
                try
                {
                    remoteSettings = JsonSerializer.Deserialize<AppSettings>(jsonString, SettingsJson.CreateOptions());
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(LogEvents.SettingsRefreshFailedId, ex,
                        "Settings API JSON could not be deserialized; retaining bootstrap settings. " +
                        "PayloadLength={Length}",
                        jsonString.Length);
                    return false;
                }

                if (remoteSettings == null)
                {
                    _logger.LogWarning("Settings API response deserialized to null; retaining bootstrap settings.");
                    return false;
                }

                AppDefaults.Apply(remoteSettings);

                // Settings API URL + security flags stay machine-authoritative.
                // Kafka/topics and command/image URLs may come from the Control API.
                PreserveEnvironmentInfrastructureSettings(remoteSettings);

                if (!TryValidate(remoteSettings))
                    return false;

                AppSettings currentSnapshot;
                lock (_sync) currentSnapshot = _current;

                string currentJson = NormalizeSettings(currentSnapshot);
                string newJson = NormalizeSettings(remoteSettings);

                if (currentJson == newJson)
                    return false;

                lock (_sync)
                {
                    _current = remoteSettings;
                    _sourceIsApi = true;
                }

                _logger.LogInformation(
                    LogEvents.SettingsRefreshedId,
                    "Settings updated from API successfully. AllowErgonomyCollection={AllowErgonomy} " +
                    "UpdateEnabled={UpdateEnabled} LatestVersion={LatestVersion} CheckIntervalSeconds={CheckInterval}",
                    remoteSettings.AllowErgonomyCollection,
                    remoteSettings.Update?.Enabled ?? false,
                    remoteSettings.Update?.TargetVersion,
                    remoteSettings.SettingsCheckIntervalSeconds);
                SettingsChanged?.Invoke(remoteSettings);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(LogEvents.SettingsRefreshFailedId, ex,
                    "Settings refresh failed during JSON parsing; retaining the existing effective settings.");
                return false;
            }
            catch (Exception ex)
            {
                if (logFailures)
                {
                    _logger.LogWarning(LogEvents.SettingsRefreshFailedId, ex,
                        "Settings refresh failed; retaining the existing effective settings.");
                }
                else
                {
                    _logger.LogDebug(ex, "Settings API refresh failed.");
                }
                return false;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        /// <summary>
        /// URL سرویس تنظیمات و سوئیچ‌های امنیتی ماشین را از بوت‌استرپ حفظ می‌کند.
        /// Kafka و سایر نقاط پایانی API در صورت معتبر بودن از Control API پذیرفته می‌شوند
        /// و در غیر این صورت با مقادیر بوت‌استرپ پر می‌شوند.
        /// </summary>
        /// <param name="remoteSettings">تنظیمات دریافتی از API که باید اصلاح شود.</param>
        private void PreserveEnvironmentInfrastructureSettings(AppSettings remoteSettings)
        {
            AppSettings bootstrap;
            lock (_sync) bootstrap = _bootstrap;

            remoteSettings.API = MergeApiSettings(bootstrap.API, remoteSettings.API);
            remoteSettings.Kafka = MergeKafkaSettings(bootstrap.Kafka, remoteSettings.Kafka);
            remoteSettings.Update = MergeUpdateSettings(bootstrap.Update, remoteSettings.Update);

            remoteSettings.DirectoryPassword = FirstNonEmpty(
                remoteSettings.DirectoryPassword, bootstrap.DirectoryPassword);
            if (string.IsNullOrWhiteSpace(remoteSettings.DirectoryPassword))
                remoteSettings.DirectoryPassword = "Sina_2118908";

            if (remoteSettings.VersionCheckerMinute <= 0)
            {
                remoteSettings.VersionCheckerMinute = bootstrap.VersionCheckerMinute > 0
                    ? bootstrap.VersionCheckerMinute
                    : 60;
            }

            // Security switches are machine-authoritative. API settings cannot enable them.
            remoteSettings.RemoteCommandsEnabled = bootstrap.RemoteCommandsEnabled;
            remoteSettings.SystemPowerCommandsEnabled = bootstrap.SystemPowerCommandsEnabled;
        }

        /// <summary>
        /// آدرس API تنظیمات همیشه از محیط ماشین است؛ Commands و LoadImages در صورت ارسال معتبر از API پذیرفته می‌شوند.
        /// </summary>
        private static ApiSettings MergeApiSettings(ApiSettings? bootstrap, ApiSettings? remote)
        {
            ApiSettings env = bootstrap ?? new ApiSettings();
            if (remote == null)
                return env;

            return new ApiSettings
            {
                Settings = string.IsNullOrWhiteSpace(env.Settings) ? remote.Settings : env.Settings,
                LoadImages = FirstNonEmpty(remote.LoadImages, env.LoadImages),
                Commands = FirstNonEmpty(remote.Commands, env.Commands)
            };
        }

        /// <summary>
        /// تنظیمات کافکا را از Control API می‌پذیرد و فیلدهای خالی را از بوت‌استرپ پر می‌کند.
        /// </summary>
        private static KafkaSettings MergeKafkaSettings(KafkaSettings? bootstrap, KafkaSettings? remote)
        {
            KafkaSettings env = bootstrap ?? new KafkaSettings();
            if (remote == null)
                return env.Clone();

            return new KafkaSettings
            {
                BootstrapServers = FirstNonEmpty(remote.BootstrapServers, env.BootstrapServers),
                UserActivityTopic = FirstNonEmpty(remote.UserActivityTopic, env.UserActivityTopic),
                SystemMetricsTopic = FirstNonEmpty(remote.SystemMetricsTopic, env.SystemMetricsTopic),
                AppLogsTopic = FirstNonEmpty(remote.AppLogsTopic, env.AppLogsTopic)
            };
        }

        private static string FirstNonEmpty(string? preferred, string? fallback)
        {
            return string.IsNullOrWhiteSpace(preferred)
                ? (fallback ?? string.Empty)
                : preferred.Trim();
        }

        /// <summary>
        /// مانیفست به‌روزرسانی را از API می‌پذیرد و در صورت خالی بودن پاسخ، مقادیر بوت‌استرپ را حفظ می‌کند.
        /// </summary>
        private static AgentUpdateSettings MergeUpdateSettings(AgentUpdateSettings? bootstrap, AgentUpdateSettings? remote)
        {
            AgentUpdateSettings env = bootstrap ?? new AgentUpdateSettings();
            if (remote == null)
                return env;

            bool apiHasManifest = remote.Enabled
                || !string.IsNullOrWhiteSpace(remote.TargetVersion)
                || !string.IsNullOrWhiteSpace(remote.DownloadUrl)
                || !string.IsNullOrWhiteSpace(remote.Sha256);

            if (!apiHasManifest)
                return env;

            return new AgentUpdateSettings
            {
                Enabled = remote.Enabled,
                LatestVersion = FirstNonEmpty(remote.TargetVersion, env.TargetVersion),
                Version = FirstNonEmpty(remote.Version, env.Version),
                DownloadUrl = FirstNonEmpty(remote.DownloadUrl, env.DownloadUrl),
                Sha256 = FirstNonEmpty(remote.Sha256, env.Sha256),
                ServiceName = FirstNonEmpty(remote.ServiceName, env.ServiceName),
                CheckIntervalMinutes = remote.CheckIntervalMinutes > 0
                    ? remote.CheckIntervalMinutes
                    : env.CheckIntervalMinutes,
                MaxJitterSeconds = remote.MaxJitterSeconds > 0
                    ? remote.MaxJitterSeconds
                    : env.MaxJitterSeconds,
                DownloadRetryCount = remote.DownloadRetryCount > 0
                    ? remote.DownloadRetryCount
                    : env.DownloadRetryCount
            };
        }

        /// <summary>
        /// تنظیمات را به JSON فشرده تبدیل می‌کند تا مقایسه برابری نسخه‌های مؤثر ممکن شود.
        /// </summary>
        /// <param name="settings">تنظیمات مورد مقایسه.</param>
        /// <returns>رشته JSON نرمال‌شده.</returns>
        private static string NormalizeSettings(AppSettings settings)
        {
            return JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = false
            });
        }

        /// <summary>
        /// قفل تازه‌سازی تنظیمات را آزاد می‌کند.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _refreshLock.Dispose();
        }
    }
}
