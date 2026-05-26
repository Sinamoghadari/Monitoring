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
        private AppSettings _appSettings;
        private List<Image> _loadedImages;
        private int _currentImageIndex = 0;
        private static readonly HttpClient _httpClient = new HttpClient();

        public bool IsAlarmActive { get; private set; } = false;
        public int SessionCloseCounter { get; private set; } = 0;
        public int PrimaryAlarmCount { get; private set; } = 0;
        public int SecondaryAlarmCount { get; private set; } = 0;

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
                    : "http://172.17.214.28:8082/api/images";

                var response = await _httpClient.GetStringAsync(apiUrl);
                var imagesData = JsonSerializer.Deserialize<List<ImageApiResponse>>(response);

                _loadedImages.Clear();
                
                if (imagesData != null)
                {
                    foreach (var img in imagesData)
                    {
                        try
                        {
                            byte[] imageBytes = Convert.FromBase64String(img.Data);
                            using (var ms = new MemoryStream(imageBytes))
                            {
                                _loadedImages.Add(new Bitmap(ms)); 
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ Error decoding image {img.Name}: {ex.Message}");
                        }
                    }
                }
                Console.WriteLine($"✅ {_loadedImages.Count} images were loaded from API ({apiUrl})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to load images from API: {ex.Message}");
            }
        }

        public void ShowPrimaryAlarm()
        {
            IsAlarmActive = true;
            Image? currentImage = null;

            if (_loadedImages?.Count > 0)
            {
                currentImage = _loadedImages[_currentImageIndex];
                _currentImageIndex = (_currentImageIndex + 1) % _loadedImages.Count;
            }

            if (SessionCloseCounter < _appSettings?.SessionCloseLimit)
            {
                PrimaryAlarmCount++;
                
                var primaryAlarm = new PrimaryAlarmForm(_appSettings, currentImage);
                primaryAlarm.FormClosedCallback += (isUserClose) => {
                    if (isUserClose)
                    {
                        SessionCloseCounter++;
                        if (SessionCloseCounter >= _appSettings?.SessionCloseLimit)
                        {
                            ShowSecondaryAlarm();
                        }
                        else
                        {
                            IsAlarmActive = false;
                        }
                    }
                    else
                    {
                        IsAlarmActive = false;
                    }
                };
                primaryAlarm.Show();
            }
        }

        private void ShowSecondaryAlarm()
        {
            SecondaryAlarmCount++;
            Image? randomImage = null;

            if (_loadedImages?.Count > 0)
            {
                var rand = new Random();
                randomImage = _loadedImages[rand.Next(_loadedImages.Count)];
            }

            var secondaryAlarm = new SecondaryAlarmForm(_appSettings, randomImage);
            secondaryAlarm.FormClosed += (s, args) => {
                SessionCloseCounter = 0;
                IsAlarmActive = false;
            };
            secondaryAlarm.Show();
        }

        public void StopAlarms()
        {
            IsAlarmActive = false;
        }
    }
}
