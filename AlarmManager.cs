using Ergonomy.Configuration;
using Ergonomy.UI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public class AlarmManager
    {
        private readonly object _lock = new object();
        private AppSettings _appSettings;
        private List<Image> _loadedImages;
        private int _currentImageIndex = 0;
        private static readonly HttpClient _httpClient = new HttpClient();

        private bool _isAlarmActive;
        private int _sessionCloseCounter;
        private int _primaryAlarmCount;
        private int _secondaryAlarmCount;

        public bool IsAlarmActive { get { lock (_lock) return _isAlarmActive; } }
        public int SessionCloseCounter { get { lock (_lock) return _sessionCloseCounter; } }
        public int PrimaryAlarmCount { get { lock (_lock) return _primaryAlarmCount; } }
        public int SecondaryAlarmCount { get { lock (_lock) return _secondaryAlarmCount; } }

        public void UpdateSettings(AppSettings appSettings)
        {
            if (appSettings == null) return;
            lock (_lock) { _appSettings = appSettings; }
        }

        public AlarmManager(AppSettings appSettings)
        {
            _appSettings = appSettings;
            _loadedImages = new List<Image>();
        }

        // آدرس به صورت خودکار از تنظیمات خوانده می‌شود
        public async Task LoadImagesFromApiAsync()
        {
            try
            {
                // خواندن آدرس از تنظیمات. اگر خالی بود از مقدار پیش‌فرض استفاده می‌شود
                string apiUrl = !string.IsNullOrEmpty(_appSettings?.API?.LoadImages) 
                    ? _appSettings.API.LoadImages 
                    : "http://172.17.214.38:8082/api/images";

                var response = await _httpClient.GetStringAsync(apiUrl);
                var imagesData = JsonSerializer.Deserialize<List<ImageApiResponse>>(response);

                var loaded = new List<Image>();

                if (imagesData != null)
                {
                    foreach (var img in imagesData)
                    {
                        try
                        {
                            byte[] imageBytes = Convert.FromBase64String(img.Data);
                            using (var ms = new MemoryStream(imageBytes))
                            {
                                loaded.Add(new Bitmap(ms));
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ Error decoding image {img.Name}: {ex.Message}");
                        }
                    }
                }

                lock (_lock)
                {
                    _loadedImages = loaded;
                }

                Console.WriteLine($"✅ {loaded.Count} images were loaded from API ({apiUrl})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to load images from API: {ex.Message}");
            }
        }

        // MUST be called on the WinForms UI thread: it creates and shows a Form.
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
                // Session close limit already reached; primary alarm is not allowed.
                // Reset the active flag so future notifications are not blocked forever.
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
            {
                // Runs on the UI thread (invoked from FormClosedCallback) so it is
                // safe to create and show the secondary form here.
                ShowSecondaryAlarmOnUiThread();
            }
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
