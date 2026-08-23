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

        /// <summary>Loads the machine environment bootstrap settings; idempotent.</summary>
        void LoadBootstrap();

        /// <summary>
        /// Fetches settings from the Settings API (backed by PostgreSQL) and, if they differ,
        /// replaces <see cref="Current"/> and raises <see cref="SettingsChanged"/>.
        /// Infrastructure (API endpoints + Kafka topics) is always preserved from bootstrap.
        /// </summary>
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
                "Enabled metrics count: {EnabledMetricsCount}",
                _current.EnabledMetrics?.Count ?? 0);
        }

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

                string jsonString = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                AppSettings? remoteSettings =
                    JsonSerializer.Deserialize<AppSettings>(jsonString, options);

                if (remoteSettings == null)
                {
                    if (logFailures)
                        _logger.LogWarning("Settings API response could not be deserialized.");
                    return false;
                }

                AppDefaults.Apply(remoteSettings);

                // Infrastructure settings (API endpoints + Kafka topics) are authoritative
                // only from the machine environment (bootstrap); the API cannot override them.
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

                _logger.LogInformation(LogEvents.SettingsRefreshedId, "Settings updated from API successfully.");
                SettingsChanged?.Invoke(remoteSettings);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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

        private void PreserveEnvironmentInfrastructureSettings(AppSettings remoteSettings)
        {
            AppSettings bootstrap;
            lock (_sync) bootstrap = _bootstrap;
            remoteSettings.API = bootstrap.API;
            remoteSettings.Kafka = bootstrap.Kafka;
            // Security switches are machine-authoritative. API settings cannot enable them.
            remoteSettings.RemoteCommandsEnabled = bootstrap.RemoteCommandsEnabled;
            remoteSettings.SystemPowerCommandsEnabled = bootstrap.SystemPowerCommandsEnabled;
        }

        private static string NormalizeSettings(AppSettings settings)
        {
            return JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = false
            });
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _refreshLock.Dispose();
        }
    }
}
