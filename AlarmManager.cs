using Ergonomy.Configuration;
using Ergonomy.Database;
using Ergonomy.UI;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace Ergonomy
{
    // این کلاس وظیفه مدیریت وضعیت آلارم‌ها، شمارشگرها و نمایش فرم‌ها را بر عهده دارد
    public class AlarmManager
    {
        private AppSettings _appSettings;
        private DatabaseManager _dbManager;
        private List<string> _imageNames;
        private int _currentImageIndex = 0;

        public bool IsAlarmActive { get; private set; } = false;
        public int SessionCloseCounter { get; private set; } = 0;
        public int PrimaryAlarmCount { get; private set; } = 0;
        public int SecondaryAlarmCount { get; private set; } = 0;

        public AlarmManager(AppSettings appSettings, DatabaseManager dbManager)
        {
            _appSettings = appSettings;
            _dbManager = dbManager;
            _imageNames = new List<string>();
        }

        // بارگذاری نام تصاویر از دیتابیس (فقط یکبار در شروع برنامه)
        public void LoadImagesFromDatabase()
        {
            if (_dbManager != null)
            {
                _imageNames = _dbManager.GetAllImageNames();
                Console.WriteLine($"✅ {_imageNames.Count} number of image was read from database");
            }
        }

        // نمایش آلارم اولیه
        public void ShowPrimaryAlarm()
        {
            IsAlarmActive = true;
            Image? currentImage = null;

            // دریافت تصویر بعدی از دیتابیس
            if (_imageNames?.Count > 0 && _dbManager != null)
            {
                string imageName = _imageNames[_currentImageIndex];
                currentImage = _dbManager.GetImageFromDatabase(imageName);
                _currentImageIndex = (_currentImageIndex + 1) % _imageNames.Count;
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

        // نمایش آلارم ثانویه (اجباری)
        private void ShowSecondaryAlarm()
        {
            SecondaryAlarmCount++;
            Image? randomImage = null;

            if (_imageNames?.Count > 0 && _dbManager != null)
            {
                var rand = new Random();
                string randomImageName = _imageNames[rand.Next(_imageNames.Count)];
                randomImage = _dbManager.GetImageFromDatabase(randomImageName);
            }

            var secondaryAlarm = new SecondaryAlarmForm(_appSettings, randomImage);
            secondaryAlarm.FormClosed += (s, args) => {
                // ریست کردن وضعیت بعد از بسته شدن آلارم اجباری
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
