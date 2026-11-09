using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ergonomy.Database;
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
        /// تنظیمات بوت‌استرپ را از متغیرهای محیطی سطح ماشین می‌خواند و سپس آخرین پاسخ معتبر
        /// ذخیره‌شده در SQLite را، در صورت وجود، به‌عنوان تنظیمات مؤثر بارگذاری می‌کند؛
        /// فراخوانی تکرارپذیر است.
        /// </summary>
        void LoadBootstrap();

        /// <summary>
        /// به‌صورت ناهمگام تنظیمات را از API تنظیمات (پشتیبانی‌شده با PostgreSQL) می‌خواند
        /// و پس از اعتبارسنجی، پاسخ موفق را فوراً در SQLite ذخیره می‌کند؛ سپس Current را
        /// به‌صورت اتمیک جایگزین کرده و در صورت تفاوت SettingsChanged را اعلام می‌کند.
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
        private SettingsCacheStore? _settingsCache;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private readonly object _sync = new();

        private AppSettings _current = new();
        private AppSettings _bootstrap = new();
        private bool _sourceIsApi;
        private bool _disposed;

        public event Action<AppSettings>? SettingsChanged;

        /// <summary>
        /// سرویس تنظیمات را با کلاینت HTTP مشترک و ثبت‌کننده رویداد می‌سازد.
        /// </summary>
        /// <param name="httpClient">کلاینت HTTP برای فراخوانی API تنظیمات.</param>
        /// <param name="logger">ثبت‌کننده بارگذاری، تازه‌سازی و خطای اعتبارسنجی.</param>
        /// <param name="settingsCache">کش SQLite مشترک با outbox برای حفظ آخرین تنظیمات API.</param>
        public SettingsService(
            HttpClient httpClient,
            ILogger<SettingsService> logger,
            SettingsCacheStore? settingsCache = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsCache = settingsCache;
        }

        private SettingsCacheStore SettingsCache
        {
            get
            {
                return _settingsCache ??= new SettingsCacheStore(
                    new SqliteOutboxConnectionProvider());
            }
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
            AppSettings bootstrap = EnvironmentSettingsProvider.Load();
            AppDefaults.Apply(bootstrap);

            AppSettings effective = bootstrap;
            bool loadedFromCache = false;
            string? cachedJson = SettingsCache.LoadSerialized();

            if (!string.IsNullOrWhiteSpace(cachedJson))
            {
                try
                {
                    if (!IsUsableCacheDocument(cachedJson))
                    {
                        _logger.LogWarning(
                            "Local settings cache is empty or has an invalid shape; bootstrap defaults will be used.");
                    }
                    else
                    {
                        AppSettings cached = DeserializeSettingsPayload(cachedJson, bootstrap);

                        // Cached values are authoritative for the runtime. The fallback is
                        // used only to fill a malformed/partial cached document, while the
                        // machine-owned API URL and other infrastructure boundaries remain
                        // available for the next refresh.
                        AppDefaults.Apply(cached, bootstrap);
                        PreserveEnvironmentInfrastructureSettings(cached, bootstrap);

                        if (TryValidate(cached))
                        {
                            effective = cached;
                            loadedFromCache = true;
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Local settings cache failed validation; bootstrap defaults will be used.");
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(
                        LogEvents.SettingsRefreshFailedId,
                        ex,
                        "Local settings cache is corrupt; bootstrap defaults will be used.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        LogEvents.SettingsRefreshFailedId,
                        ex,
                        "Local settings cache could not be applied; bootstrap defaults will be used.");
                }
            }

            lock (_sync)
            {
                _bootstrap = bootstrap;
                _current = effective;
                // A cache row is the result of a prior successful API synchronization, so it is
                // exposed as the remote source even before the first refresh of this process.
                _sourceIsApi = loadedFromCache;
            }

            TryValidate(effective);

            _logger.LogInformation(
                "{SettingsSource} settings loaded. AllowErgonomyCollection={AllowErgonomy} " +
                "UpdateEnabled={UpdateEnabled} Enabled metrics count: {EnabledMetricsCount}",
                loadedFromCache ? "Cached API" : "Bootstrap",
                effective.AllowErgonomyCollection,
                effective.Update?.Enabled ?? false,
                effective.EnabledMetrics?.Count ?? 0);
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
                        _logger.LogWarning("Settings API URL is empty in the effective settings.");
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
                AppSettings currentSnapshot;
                lock (_sync) currentSnapshot = _current;

                AppSettings remoteSettings;
                try
                {
                    // Treat the API response as a partial update over the active settings. This
                    // keeps omitted fields at their last known-good values rather than invoking
                    // compiled defaults during a successful refresh.
                    remoteSettings = DeserializeSettingsPayload(jsonString, currentSnapshot);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(LogEvents.SettingsRefreshFailedId, ex,
                        "Settings API JSON could not be deserialized; retaining the existing settings. " +
                        "PayloadLength={Length}",
                        jsonString.Length);
                    return false;
                }

                AppDefaults.Apply(remoteSettings, currentSnapshot);

                // Settings API URL stays machine-authoritative.
                // Kafka/topics and image URLs may come from the Control API.
                PreserveEnvironmentInfrastructureSettings(remoteSettings);

                if (!TryValidate(remoteSettings))
                    return false;

                string currentJson = NormalizeSettings(currentSnapshot);
                string newJson = NormalizeSettings(remoteSettings);

                // Every successful, validated API response is persisted, including an unchanged
                // response. That makes a first successful synchronization durable immediately.
                bool persisted = SettingsCache.SaveSerialized(newJson);

                lock (_sync)
                {
                    _current = remoteSettings;
                    _sourceIsApi = true;
                }

                bool changed = currentJson != newJson;
                _logger.LogInformation(
                    LogEvents.SettingsRefreshedId,
                    "Settings updated from API successfully. PersistedLocally={PersistedLocally} " +
                    "AllowErgonomyCollection={AllowErgonomy} UpdateEnabled={UpdateEnabled} " +
                    "LatestVersion={LatestVersion} CheckIntervalSeconds={CheckInterval}",
                    persisted,
                    remoteSettings.AllowErgonomyCollection,
                    remoteSettings.Update?.Enabled ?? false,
                    remoteSettings.Update?.TargetVersion,
                    remoteSettings.SettingsCheckIntervalSeconds);

                if (changed)
                    SettingsChanged?.Invoke(remoteSettings);
                return changed;
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
            PreserveEnvironmentInfrastructureSettings(remoteSettings, bootstrap);
        }

        private static void PreserveEnvironmentInfrastructureSettings(
            AppSettings remoteSettings,
            AppSettings bootstrap)
        {
            remoteSettings.API = MergeApiSettings(bootstrap.API, remoteSettings.API);
            remoteSettings.Kafka = MergeKafkaSettings(bootstrap.Kafka, remoteSettings.Kafka);
            remoteSettings.Update = MergeUpdateSettings(bootstrap.Update, remoteSettings.Update);

            int checkInterval = remoteSettings.CheckIntervalMinutes > 0
                ? remoteSettings.CheckIntervalMinutes
                : remoteSettings.Update?.CheckIntervalMinutes ?? 0;
            remoteSettings.CheckIntervalMinutes = checkInterval > 0
                ? checkInterval
                : bootstrap.CheckIntervalMinutes;

            remoteSettings.Update ??= new AgentUpdateSettings();
            remoteSettings.Update.CheckIntervalMinutes = remoteSettings.CheckIntervalMinutes;

            if (remoteSettings.VersionCheckerMinute <= 0)
            {
                remoteSettings.VersionCheckerMinute = bootstrap.VersionCheckerMinute;
            }
        }

        /// <summary>
        /// آدرس API تنظیمات همیشه از محیط ماشین است؛ LoadImages در صورت ارسال معتبر از API پذیرفته می‌شود.
        /// </summary>
        private static ApiSettings MergeApiSettings(ApiSettings? bootstrap, ApiSettings? remote)
        {
            ApiSettings env = bootstrap ?? new ApiSettings();
            if (remote == null)
                return env;

            return new ApiSettings
            {
                Settings = string.IsNullOrWhiteSpace(env.Settings) ? remote.Settings : env.Settings,
                LoadImages = FirstNonEmpty(remote.LoadImages, env.LoadImages)
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
        /// API payload را به‌صورت یک به‌روزرسانی جزئی روی آخرین تنظیمات مؤثر اعمال می‌کند.
        /// این merge عمیق است تا حذف فیلدهای تو در تو نیز باعث بازگشت به مقدار کامپایل‌شده نشود.
        /// </summary>
        private static AppSettings DeserializeSettingsPayload(string json, AppSettings baseline)
        {
            JsonDocumentOptions documentOptions = new()
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };

            JsonNode? baselineNode = JsonSerializer.SerializeToNode(
                baseline,
                SettingsJson.CreateOptions());
            JsonNode? incomingNode = JsonNode.Parse(
                json,
                nodeOptions: null,
                documentOptions: documentOptions);

            if (baselineNode is not JsonObject baselineObject
                || incomingNode is not JsonObject incomingObject)
            {
                throw new JsonException("Settings API payload must be a JSON object.");
            }

            MergeJsonObjects(baselineObject, incomingObject);
            AppSettings? merged = baselineObject.Deserialize<AppSettings>(SettingsJson.CreateOptions());
            if (merged == null)
                throw new JsonException("Settings API payload deserialized to null.");

            // CheckIntervalMinutes is canonical at AppSettings level. Accept the historical
            // nested update-manifest location on the wire, but immediately project it into the
            // canonical property and keep the nested manifest field synchronized below.
            if (!HasProperty(incomingObject, nameof(AppSettings.CheckIntervalMinutes))
                && TryGetProperty(incomingObject, "Update", out JsonNode? updateNode)
                && updateNode is JsonObject updateObject
                && TryGetProperty(updateObject, nameof(AgentUpdateSettings.CheckIntervalMinutes), out JsonNode? intervalNode)
                && TryReadPositiveInt(intervalNode, out int nestedInterval))
            {
                merged.CheckIntervalMinutes = nestedInterval;
            }

            return merged;
        }

        private static void MergeJsonObjects(JsonObject target, JsonObject incoming)
        {
            foreach (KeyValuePair<string, JsonNode?> property in incoming)
            {
                string? targetName = null;
                foreach (KeyValuePair<string, JsonNode?> existing in target)
                {
                    if (string.Equals(existing.Key, property.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        targetName = existing.Key;
                        break;
                    }
                }

                if (property.Value is JsonObject incomingObject
                    && targetName != null
                    && target[targetName] is JsonObject targetObject)
                {
                    MergeJsonObjects(targetObject, incomingObject);
                }
                else
                {
                    target[targetName ?? property.Key] = property.Value?.DeepClone();
                }
            }
        }

        private static bool HasProperty(JsonObject objectNode, string propertyName)
        {
            return TryGetProperty(objectNode, propertyName, out _);
        }

        private static bool TryGetProperty(
            JsonObject objectNode,
            string propertyName,
            out JsonNode? value)
        {
            foreach (KeyValuePair<string, JsonNode?> property in objectNode)
            {
                if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static bool TryReadPositiveInt(JsonNode? node, out int value)
        {
            value = 0;
            if (node == null)
                return false;

            return int.TryParse(
                node.ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value)
                && value > 0;
        }

        private static bool IsUsableCacheDocument(string json)
        {
            JsonNode? node = JsonNode.Parse(
                json,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

            if (node is not JsonObject objectNode)
                return false;

            // A cache row written by this agent always contains these canonical fields. Requiring
            // more than a non-empty JSON object lets an empty/placeholder row select bootstrap.
            return HasProperty(objectNode, nameof(AppSettings.ActivityThresholdSeconds))
                && HasProperty(objectNode, nameof(AppSettings.SettingsCheckIntervalSeconds));
        }

        /// <summary>
        /// تنظیمات را به JSON فشرده تبدیل می‌کند تا مقایسه برابری نسخه‌های مؤثر ممکن شود.
        /// </summary>
        /// <param name="settings">تنظیمات مورد مقایسه.</param>
        /// <returns>رشته JSON نرمال‌شده.</returns>
        private static string NormalizeSettings(AppSettings settings)
        {
            return JsonSerializer.Serialize(settings, SettingsJson.CreateOptions());
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
