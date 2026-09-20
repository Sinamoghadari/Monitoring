using Ergonomy.Configuration;
using Ergonomy.Logging;
using Ergonomy.UI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ergonomy
{
    public class ImageApiResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("data")]
        public string Data { get; set; }
    }

    public class AlarmManager : IAlarmImageLoader
    {
        private readonly object _lock = new object();
        private readonly ILogger<AlarmManager>? _logger;
        private readonly SemaphoreSlim _fetchGate = new(1, 1);
        private static readonly HttpClient Http = CreateHttpClient();

        private AppSettings _appSettings;
        private List<Image> _loadedImages;
        private int _currentImageIndex;
        private AlarmImageAssetState _assetState = AlarmImageAssetState.Pending;
        private string? _lastFetchedUrl;
        private DateTime? _nextAttemptUtc;
        private int _consecutiveFailures;

        private bool _isAlarmActive;
        private int _sessionCloseCounter;
        private int _primaryAlarmCount;
        private int _secondaryAlarmCount;

        public bool IsAlarmActive { get { lock (_lock) return _isAlarmActive; } }
        public int SessionCloseCounter { get { lock (_lock) return _sessionCloseCounter; } }
        public int PrimaryAlarmCount { get { lock (_lock) return _primaryAlarmCount; } }
        public int SecondaryAlarmCount { get { lock (_lock) return _secondaryAlarmCount; } }

        public AlarmImageAssetState AssetState { get { lock (_lock) return _assetState; } }
        public int CachedImageCount { get { lock (_lock) return _loadedImages?.Count ?? 0; } }

        /// <summary>
        /// مرجع تنظیمات هشدار را به‌صورت امن برای چندنخ جایگزین می‌کند
        /// تا حد بستن نشست و زمان‌بندی فرم‌ها از مقادیر جدید پیروی کنند.
        /// </summary>
        public void UpdateSettings(AppSettings appSettings)
        {
            if (appSettings == null) return;
            lock (_lock) { _appSettings = appSettings; }
        }

        public AlarmManager(AppSettings appSettings, ILogger<AlarmManager>? logger = null)
        {
            _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
            _logger = logger;
            _loadedImages = new List<Image>();
        }

        /// <summary>
        /// Compatibility wrapper used by ErgonomyManager.Start.
        /// </summary>
        public Task LoadImagesFromApiAsync() => EnsureLoadedAsync(CancellationToken.None);

        /// <summary>
        /// Re-evaluates the in-memory alarm-image cache. Failed or empty assets are
        /// retried with exponential backoff; a successful fetch transitions Failed → Ready
        /// without requiring a process restart. Last-good images are kept on failure.
        /// </summary>
        public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
        {
            string apiUrl;
            AlarmImageAssetState state;
            int cached;
            string? lastUrl;
            DateTime? nextAttempt;
            lock (_lock)
            {
                apiUrl = !string.IsNullOrWhiteSpace(_appSettings?.API?.LoadImages)
                    ? _appSettings.API.LoadImages.Trim()
                    : AgentEndpoints.ApiImages;
                state = _assetState;
                cached = _loadedImages?.Count ?? 0;
                lastUrl = _lastFetchedUrl;
                nextAttempt = _nextAttemptUtc;
            }

            if (!AlarmImageAssetPolicy.ShouldFetch(state, cached, apiUrl, lastUrl, DateTime.UtcNow, nextAttempt))
                return;

            if (!await _fetchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return;

            try
            {
                lock (_lock)
                {
                    state = _assetState;
                    cached = _loadedImages?.Count ?? 0;
                    lastUrl = _lastFetchedUrl;
                    nextAttempt = _nextAttemptUtc;
                    apiUrl = !string.IsNullOrWhiteSpace(_appSettings?.API?.LoadImages)
                        ? _appSettings.API.LoadImages.Trim()
                        : AgentEndpoints.ApiImages;
                }

                if (!AlarmImageAssetPolicy.ShouldFetch(state, cached, apiUrl, lastUrl, DateTime.UtcNow, nextAttempt))
                    return;

                if (state != AlarmImageAssetState.Ready || cached == 0)
                {
                    _logger?.LogInformation(
                        LogEvents.AlarmAssetReloadId,
                        "[Assets] Alarm images missing/failed on startup; attempting reload during settings sync... Url={Url} State={State} Cached={Cached}",
                        apiUrl, state, cached);
                }

                List<Image> loaded = await FetchAndDecodeAsync(apiUrl, cancellationToken).ConfigureAwait(false);
                AlarmImageAssetState next = AlarmImageAssetPolicy.StateAfterFetch(loaded.Count);

                lock (_lock)
                {
                    _lastFetchedUrl = apiUrl;
                    if (next == AlarmImageAssetState.Ready)
                    {
                        _loadedImages = loaded;
                        _currentImageIndex = 0;
                        _assetState = AlarmImageAssetState.Ready;
                        _consecutiveFailures = 0;
                        _nextAttemptUtc = null;
                    }
                    else
                    {
                        MarkFailedLocked();
                    }
                }

                if (next == AlarmImageAssetState.Ready)
                {
                    _logger?.LogInformation(
                        LogEvents.AlarmAssetRecoveredId,
                        "[Assets] Successfully recovered and cached alarm images. Count={Count} Url={Url}",
                        loaded.Count, apiUrl);
                }
                else
                {
                    _logger?.LogWarning(
                        LogEvents.AlarmAssetFailedId,
                        "[Assets] Alarm image fetch returned no usable images. Url={Url}",
                        apiUrl);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (_lock) { MarkFailedLocked(); }
                _logger?.LogWarning(
                    LogEvents.AlarmAssetFailedId,
                    ex,
                    "[Assets] Alarm image fetch failed; keeping last-good cache. Url={Url}",
                    apiUrl);
            }
            finally
            {
                _fetchGate.Release();
            }
        }

        private void MarkFailedLocked()
        {
            _assetState = AlarmImageAssetState.Failed;
            _consecutiveFailures++;
            _nextAttemptUtc = DateTime.UtcNow + AlarmImageAssetPolicy.ComputeBackoff(_consecutiveFailures);
        }

        private static async Task<List<Image>> FetchAndDecodeAsync(string apiUrl, CancellationToken ct)
        {
            using HttpResponseMessage response = await Http.GetAsync(apiUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            List<ImageApiResponse>? imagesData = JsonSerializer.Deserialize<List<ImageApiResponse>>(json);

            var loaded = new List<Image>();
            if (imagesData == null)
                return loaded;

            foreach (ImageApiResponse img in imagesData)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(img?.Data))
                        continue;
                    byte[] imageBytes = Convert.FromBase64String(img.Data);
                    using var ms = new MemoryStream(imageBytes);
                    loaded.Add(new Bitmap(ms));
                }
                catch (Exception ex)
                {
                    Ergonomy.Diagnostics.ExceptionPolicy.Report(
                        Ergonomy.Diagnostics.ExceptionSeverity.Operational,
                        ex,
                        new Ergonomy.Diagnostics.ExceptionReportContext
                        {
                            Module = nameof(AlarmManager),
                            Message = "Skipped a corrupt alarm image payload."
                        });
                }
            }

            return loaded;
        }

        private static HttpClient CreateHttpClient()
        {
            return new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>
        /// هشدار اولیه را روی نخ رابط کاربری نمایش می‌دهد، شمارنده را افزایش می‌دهد
        /// و در صورت رسیدن به حد بستن نشست از نمایش فرم جلوگیری می‌کند.
        /// </summary>
        public void ShowPrimaryAlarm()
        {
            Image? currentImage = null;
            bool showForm;

            lock (_lock)
            {
                if (_isAlarmActive)
                    return;

                _isAlarmActive = true;
                _primaryAlarmCount++;

                if (_loadedImages != null && _loadedImages.Count > 0)
                {
                    currentImage = _loadedImages[_currentImageIndex];
                    _currentImageIndex = (_currentImageIndex + 1) % _loadedImages.Count;
                }

                showForm = _sessionCloseCounter < (_appSettings?.SessionCloseLimit ?? int.MaxValue);
            }

            if (!showForm)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] No alarm shown because: session close limit " +
                    $"{_appSettings?.SessionCloseLimit} already reached.");
                lock (_lock) { _isAlarmActive = false; }
                return;
            }

            if (currentImage != null)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Image selected: " +
                    $"{_loadedImages.Count} image(s) available, using index {_currentImageIndex}.");
            }
            else
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] No alarm image available (images may not be " +
                    $"loaded yet); showing alarm without image.");
            }

            var primaryAlarm = new PrimaryAlarmForm(_appSettings, currentImage);
            primaryAlarm.FormClosedCallback += (isUserClose) => OnPrimaryAlarmClosed(isUserClose);
            primaryAlarm.Show();

            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Primary alarm shown on UI thread.");
        }

        private void OnPrimaryAlarmClosed(bool isUserClose)
        {
            bool showSecondary = false;

            lock (_lock)
            {
                if (isUserClose)
                {
                    _sessionCloseCounter++;
                    if (_sessionCloseCounter >= (_appSettings?.SessionCloseLimit ?? int.MaxValue))
                    {
                        _secondaryAlarmCount++;
                        showSecondary = true;
                    }
                    else
                    {
                        _isAlarmActive = false;
                    }
                }
                else
                {
                    _isAlarmActive = false;
                }
            }

            if (showSecondary)
                ShowSecondaryAlarmOnUiThread();
        }

        private void ShowSecondaryAlarmOnUiThread()
        {
            Image? randomImage = null;

            lock (_lock)
            {
                if (_loadedImages != null && _loadedImages.Count > 0)
                {
                    var rand = new Random();
                    randomImage = _loadedImages[rand.Next(_loadedImages.Count)];
                }
            }

            if (randomImage != null)
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Secondary alarm image selected.");
            else
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Secondary alarm shown without image.");

            var secondaryAlarm = new SecondaryAlarmForm(_appSettings, randomImage);
            secondaryAlarm.FormClosed += (s, args) =>
            {
                lock (_lock)
                {
                    _sessionCloseCounter = 0;
                    _isAlarmActive = false;
                }
            };
            secondaryAlarm.Show();

            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Secondary alarm shown on UI thread.");
        }

        public void StopAlarms()
        {
            lock (_lock) { _isAlarmActive = false; }
        }
    }
}
