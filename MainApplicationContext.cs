using Ergonomy.Configuration;
using Ergonomy.Database;
using Ergonomy.Hooks;
using Ergonomy.Logging;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Timers;
using System.Net.NetworkInformation;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Ergonomy.Core; 

namespace Ergonomy
{
    public class MainApplicationContext : ApplicationContext
    {
        // ==========================================
        // متغیرها و ماژول‌ها
        // ==========================================
        private readonly IConfiguration _configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();


        private CommandManager _commandManager;
        private System.Timers.Timer _healthCheckTimer;
        private AppSettings? _appSettings;
        private System.Timers.Timer? _wakeUpTimer;
        private System.Timers.Timer? _postgresPermissionTimer;
        private System.Windows.Forms.Timer _settingsUpdateTimer;
        
        // تایمرهای اصلی جمع‌آوری داده
        private System.Windows.Forms.Timer? _advancedMetricsTimer;
        
        // 🌟 تایمرهای بررسی سطح دسترسی برنامه‌ (دروازه‌های کنترلی)
        private System.Timers.Timer? _sqlitePermissionTimer;
        private System.Timers.Timer? _kafkaPermissionTimer; // جایگزین Postgres

        // تایمر زمان‌بندی (اجرای دستورات خاموش/روشن و ریموت)
        private System.Timers.Timer? _scheduleTimer;
        private string _lastExecutedSchedule = "";

        // فلگ‌های وضعیت سرویس‌ها (جلوگیری از اجرای تکراری)
        private bool _isLocalCollectionRunning = false;
        private bool _isSyncEngineRunning = false;

        // سرویس‌ها
        private DatabaseManager? _dbManager;      // اتصال به Postgres (برای تنظیمات و دستورات)
        private LocalDatabaseManager _localDb;    // اتصال به SQLite (بافر محلی)
        private SyncEngine? _syncEngine;          // موتور ارسال داده از SQLite به کافکا
        private NotifyIcon? _notifyIcon;
        private KafkaConnect? _kafkaConnect;      // کلاینت کافکا
        private ErgonomyManager? _ergonomyManager;

        
        // اطلاعات نشست
        private string _windowsSid;
        private string _windowsUsername;
        private string _sessionGuid; 
       

        public MainApplicationContext()
        {

            Application.ThreadException += (s, e) => HandleCriticalFailure(e.Exception.Message);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => 
            {
                if (e.ExceptionObject is Exception ex) HandleCriticalFailure(ex.Message);
            };

            // ۱. بارگذاری تنظیمات پایه از فایل JSON
            LoadAppSettings();

            // دریافت اطلاعات کاربر سیستم
            _windowsSid = GetWindowsSID();
            try { _windowsUsername = WindowsIdentity.GetCurrent().Name; }
            catch { _windowsUsername = Environment.UserName; }
            _sessionGuid = Guid.NewGuid().ToString(); 

            // ۲. راه‌اندازی بافر محلی (SQLite)
            _localDb = new LocalDatabaseManager();

            if (_appSettings?.Database == null)
            {
                Console.WriteLine("❌ Database settings in appsettings.json were not found!");
                SaveLogToDatabase("ERROR", "Database settings in appsettings.json were not found!");
                return;
            }

            // ۳. تست اتصال به کافکا در لحظه شروع
            TestKafkaConnectionAtStartup();

            StartHealthMonitoring(); 
            // ۴. مقداردهی اولیه سرویس‌ها
            var dbSettings = _appSettings.Database;
            _dbManager = new DatabaseManager(dbSettings.Host, dbSettings.Name, dbSettings.User, dbSettings.Password, dbSettings.Port);
            
            // موتور سینک حالا با استفاده از KafkaConnect و LocalDb کار می‌کند
            _syncEngine = new SyncEngine(_kafkaConnect!, _localDb, _dbManager, _appSettings?.SyncEngineIntervalMinutes ?? 1);

            
            _ergonomyManager = new ErgonomyManager(_appSettings, _dbManager, _localDb, _sessionGuid, _windowsSid, _windowsUsername);
            

            // تلاش برای اتصال به Postgres و دریافت آخرین تنظیمات آنلاین
            UpdateSettingsFromDatabase();


            int intervalSeconds = _appSettings?.SettingsCheckIntervalSeconds > 0 ? _appSettings.SettingsCheckIntervalSeconds : 60;
            _settingsUpdateTimer = new System.Windows.Forms.Timer();
            _settingsUpdateTimer.Interval = intervalSeconds * 1000;
            _settingsUpdateTimer.Tick += (s, e) => UpdateSettingsFromDatabase();
            _settingsUpdateTimer.Start();


            _commandManager = new CommandManager(_appSettings, _dbManager, _windowsUsername)
            {
                // ۲. اتصال متدهای این کلاس به اکشن‌های منیجر
                OnLogRequired = SaveLogToDatabase,
                OnForceSync = () => _syncEngine?.ForceSync(),
                OnStopCollection = () => 
                {
                    StopAllDataCollection();
                    _isLocalCollectionRunning = false;
                },
                OnStartCollection = () => 
                {
                    StartLocalDataCollection();
                    _isLocalCollectionRunning = true;
                }
            };

            // ۳. استارت کردن چک دوره‌ای
            _commandManager.Start();
        


            _notifyIcon = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Visible = true, Text = "Ergonomy" };

            // ۶. بررسی دسترسی‌ها و راه‌اندازی چرخه‌ها
            EvaluateSqlitePermission();   // آیا اجازه ذخیره در بافر محلی را داریم؟
            EvaluateKafkaPermission();    // آیا اجازه ارسال از بافر به کافکا را داریم؟
            EvaluateErgonomyPermission();
            StartSqlitePermissionTimer(); // تایمر چک کردن دوره‌ای دسترسی SQLite
            StartKafkaPermissionTimer();  // تایمر چک کردن دوره‌ای دسترسی کافکا

            StartPostgresPermissionTimer();

        }

        private void RestartPermissionTimers()
        {
            StartSqlitePermissionTimer();
            StartKafkaPermissionTimer();
            StartPostgresPermissionTimer();
        }


        private void HandleCriticalFailure(string errorMessage)
        {
            SaveLogToDatabase("FATAL", $"Critical error occurred: {errorMessage}. Forcing system to sleep state.");
            GoToSleepAndRetry();
        }

        private void GoToSleepAndRetry()
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 💤 Entering Sleep Mode due to critical failures...");
            
            // متوقف کردن تمام سرویس‌ها برای جلوگیری از تولید خطای مجدد
            StopAllDataCollection();
            _isLocalCollectionRunning = false;
            
            _syncEngine?.Stop();
            _isSyncEngineRunning = false;

            _sqlitePermissionTimer?.Stop();
            _kafkaPermissionTimer?.Stop();
            _scheduleTimer?.Stop();

            // دریافت زمان خواب از تنظیمات یا 5 دقیقه پیش‌فرض
            double sleepMinutes = _appSettings?.ConnectionFailureSleepMinutes ?? 5;
            
            // راه‌اندازی تایمر بیداری
            _wakeUpTimer?.Stop();
            _wakeUpTimer?.Dispose();
            _wakeUpTimer = new System.Timers.Timer(sleepMinutes * 60 * 1000); // محاسبه ریاضی: $sleepMinutes \times 60 \times 1000$
            _wakeUpTimer.Elapsed += (s, e) => WakeUp();
            _wakeUpTimer.AutoReset = false; // فقط یک بار اجرا شود
            _wakeUpTimer.Start();
        }

        private void WakeUp()
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ☀️ Waking up and re-evaluating connections...");
            
            // تلاش مجدد برای دریافت تنظیمات و اجرای دروازه‌های کنترلی
            UpdateSettingsFromDatabase();
            EvaluateSqlitePermission();
            EvaluateKafkaPermission();
            
            StartSqlitePermissionTimer();
            StartKafkaPermissionTimer();
            _commandManager?.Start(); 
        }

        private void StartHealthMonitoring()
        {
            double intervalMinutes = _configuration.GetValue<double>("AppSettings:HealthCheckIntervalMinutes", 15);
            
            _healthCheckTimer = new System.Timers.Timer(intervalMinutes * 60 * 1000);
            _healthCheckTimer.Elapsed += async (sender, e) => await PerformAllHealthChecksAsync();
            _healthCheckTimer.AutoReset = true;
            _healthCheckTimer.Start();

            // اجرای اولیه در زمان استارتاپ
            Task.Run(async () => await PerformAllHealthChecksAsync());
        }

        private async Task PerformAllHealthChecksAsync()
        {
            // 1. بررسی دیتابیس Postgres
            await _dbManager.CheckAndLogPostgresConnectionAsync(_kafkaConnect, _windowsUsername);

            // 2. بررسی دیتابیس SQLite
            await CheckSqliteHealthAsync();

            // 3. بررسی منابع برنامه (RAM)
            await CheckSelfPerformanceAsync();
        }
        private async Task CheckSqliteHealthAsync()
        {
            string statusMessage;
            string logLevel;
            string sqliteDbPath = _configuration["AppSettings:SQLite:ConnectionString"] ?? "Data Source=localbuffer.db";

            try
            {
                using var conn = new SqliteConnection(sqliteDbPath);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1;";
                await cmd.ExecuteScalarAsync();
                
                statusMessage = "SQLite database is healthy and accessible.";
                logLevel = "INFO";
            }
            catch (Exception ex)
            {
                statusMessage = $"SQLite Error (Possible lock or corruption): {ex.Message}";
                logLevel = "ERROR";
                
                // 🌟 اگر SQLite (بافر اصلی ما) هم قطع شد، برنامه به خواب برود
                HandleCriticalFailure("SQLite is inaccessible. Data collection cannot continue.");
            }

            await SendHealthLogAsync(logLevel, statusMessage, "SqliteHealth");
        }

        private async Task CheckSelfPerformanceAsync()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                // فرمول محاسبه مگابایت
                // $$MB = \frac{Bytes}{1024 \times 1024}$$
                long memoryUsedMB = process.WorkingSet64 / (1024 * 1024);
                
                string statusMessage = $"Agent Performance: Memory Usage is {memoryUsedMB} MB. Thread Count: {process.Threads.Count}";
                string logLevel = memoryUsedMB > 500 ? "WARN" : "INFO"; // اگر بیش از 500 مگابایت بود هشدار بده

                await SendHealthLogAsync(logLevel, statusMessage, "AgentPerformance");
            }
            catch (Exception ex)
            {
                await SendHealthLogAsync("ERROR", $"Failed to check self performance: {ex.Message}", "AgentPerformance");
            }
        }

        // یک متد کمکی برای جلوگیری از تکرار کد ارسال لاگ
        private async Task SendHealthLogAsync(string logLevel, string message, string category)
        {
            if (_kafkaConnect == null) return;

            var logObj = new
            {
                Timestamp = DateTime.UtcNow,
                LogLevel = logLevel,
                Message = message,
                WindowsUsername = _windowsUsername,
                MachineName = Environment.MachineName,
                Category = category
            };

            try
            {
                await _kafkaConnect.SendAppLogAsync(logObj);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{logLevel}] [{category}] {message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ [KAFKA SEND ERROR] Failed to send {category} log: {ex.Message}");
            }
        }


        private void UpdateSettingsFromDatabase()
        {
            if (_dbManager == null) return;

            var dbSettingsOverride = _dbManager.GetSettingsFromDatabase();
            if (dbSettingsOverride != null)
            {
                var oldSettingsJson = System.Text.Json.JsonSerializer.Serialize(_appSettings);
                var newSettingsJson = System.Text.Json.JsonSerializer.Serialize(dbSettingsOverride);

                if (oldSettingsJson != newSettingsJson)
                {
                    // بروز رسانی تنظیمات
                    _appSettings = dbSettingsOverride;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Settings updated from Postgres.");

                    SaveSettingsToAppSettingsJson(_appSettings);
                    RestartPermissionTimers();

                    if (_syncEngine != null)
                    {
                        _syncEngine.UpdateSyncInterval(_appSettings.SyncEngineIntervalMinutes);
                    }

                    // --- ساختن نمونه فقط برای بار اول ---
                    if (_appSettings.SettingsCheckIntervalSeconds > 0 && _settingsUpdateTimer != null)
                    {
                        int intervalSeconds = _appSettings.SettingsCheckIntervalSeconds;
                        _settingsUpdateTimer.Interval = intervalSeconds * 1000;
                    }

                    // --- فقط استارت یا استاپ کردن ---
                    if (_appSettings.AllowErgonomyCollection)
                    {
                        _ergonomyManager.Start();
                        Console.WriteLine("Ergonomy Started.");
                    }
                    else
                    {
                        _ergonomyManager.Stop();
                        Console.WriteLine("Ergonomy Stopped.");
                    }
                }
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Offline Mode! Using appsettings.json.");
            }
        }

        private void EvaluateSqlitePermission()
        {
            bool allowSqlite = _appSettings?.AllowSqliteWrite ?? true;
            double intervalHours = _appSettings?.PermissionSqliteRetryIntervalHours ?? 1;

            string msg = $"[SQLite Status] Permission: {allowSqlite} | Checking continuously every {intervalHours} hour(s).";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔍 {msg}");
            SaveLogToDatabase("INFO", msg);

            if (allowSqlite)
            {
                // اگر اجازه داریم و سرویس خاموش است، روشنش کن
                if (!_isLocalCollectionRunning)
                {
                    StartLocalDataCollection();
                    _isLocalCollectionRunning = true;
                }
            }
            else
            {
                // اگر اجازه نداریم و سرویس روشن است، خاموشش کن
                if (_isLocalCollectionRunning)
                {
                    StopAllDataCollection();
                    _isLocalCollectionRunning = false;
                    
                    // اگر جمع‌آوری محلی متوقف شود، سینک به کافکا هم باید متوقف شود
                    if (_isSyncEngineRunning)
                    {
                        _syncEngine?.Stop();
                        _isSyncEngineRunning = false;
                    }
                }
                SaveLogToDatabase("WARNING", "Local Collection (SQLite): Access DENIED. Process is CANCELED/SLEEPING.");
            }
        }

        private void EvaluateKafkaPermission()
        {
            bool allowSqlite = _appSettings?.AllowSqliteWrite ?? true;
            bool allowKafka = _appSettings?.AllowKafkaWrite ?? true; // استفاده از متغیر جدید
            double intervalHours = _appSettings?.PermissionKafkaRetryIntervalHours ?? 1;

            string msg = $"[Kafka Sync Status] Permission: {allowKafka} | Checking continuously every {intervalHours} hour(s).";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔍 {msg}");
            SaveLogToDatabase("INFO", msg);

            // فقط در صورتی به کافکا می‌فرستیم که هم SQLite روشن باشد هم اجازه کافکا داشته باشیم
            if (allowSqlite && allowKafka)
            {
                if (!_isSyncEngineRunning)
                {
                    _syncEngine?.Start();
                    _isSyncEngineRunning = true;
                    SaveLogToDatabase("INFO", "Sync Engine Started (Kafka Allowed).");
                }
            }
            else 
            {
                if (_isSyncEngineRunning)
                {
                    _syncEngine?.Stop();
                    _isSyncEngineRunning = false;
                }
                SaveLogToDatabase("WARNING", "Data Sync (Kafka): Access DENIED. Sync Process is CANCELED.");
            }
        }

        // ==========================================
        // تایمرهای بررسی دوره‌ای تنظیمات
        // ==========================================
        private void EvaluateErgonomyPermission()
        {
            // فرض بر این است که پراپرتی AllowErgonomyCollection در AppSettings اضافه شده باشد.
            // در غیر اینصورت پیش‌فرض true در نظر گرفته می‌شود
            bool allowErgonomy = _appSettings?.AllowErgonomyCollection ?? true;

            string msg = $"[Ergonomy Status] Permission: {allowErgonomy}";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔍 {msg}");
            SaveLogToDatabase("INFO", msg);

            if (allowErgonomy)
            {
                _ergonomyManager?.Start();
            }
            else
            {
                _ergonomyManager?.Stop();
                SaveLogToDatabase("WARNING", "Ergonomy Collection: Access DENIED or Disabled. Process is STOPPED.");
            }
        }

        private void StartSqlitePermissionTimer()
        {
            _sqlitePermissionTimer?.Stop();
            _sqlitePermissionTimer?.Dispose();

            double retryHours = _appSettings?.PermissionSqliteRetryIntervalHours ?? 1;
            _sqlitePermissionTimer = new System.Timers.Timer(retryHours * 60 * 60 * 1000); 
            _sqlitePermissionTimer.Elapsed += (s, e) => 
            {
                UpdateSettingsFromDatabase(); 
                EvaluateSqlitePermission(); 
                EvaluateErgonomyPermission();
                StartSqlitePermissionTimer(); // استارت مجدد با تنظیمات احتمالاً جدید
            };
            _sqlitePermissionTimer.AutoReset = false; 
            _sqlitePermissionTimer.Start();
        }

        private void StartKafkaPermissionTimer()
        {
            _kafkaPermissionTimer?.Stop();
            _kafkaPermissionTimer?.Dispose();

            // استفاده از بازه زمانی مخصوص کافکا
            double retryHours = _appSettings?.PermissionKafkaRetryIntervalHours ?? 1;
            _kafkaPermissionTimer = new System.Timers.Timer(retryHours * 60 * 60 * 1000); 
            _kafkaPermissionTimer.Elapsed += (s, e) => 
            {
                UpdateSettingsFromDatabase(); 
                EvaluateKafkaPermission(); 
                StartKafkaPermissionTimer(); 
            };
            _kafkaPermissionTimer.AutoReset = false; 
            _kafkaPermissionTimer.Start();
        }


        // ==========================================
        // بخش مدیریت سرویس‌های داخلی و جمع‌آوری داده
        // ==========================================

        private void StartLocalDataCollection()
        {
            StopAllDataCollection();

            // در اینجا فقط متریک‌های سیستم (Advanced Metrics) استارت می‌خورد
            // چون ارگونومی توسط EvaluateErgonomyPermission کنترل می‌شود
            
            _advancedMetricsTimer = new System.Windows.Forms.Timer();
            double advancedIntervalMinutes = (_appSettings?.AdvancedMetricsIntervalMinutes ?? 120) > 0 ? (_appSettings?.AdvancedMetricsIntervalMinutes ?? 120) : 120;
            _advancedMetricsTimer.Interval = (int)(advancedIntervalMinutes * 60 * 1000); 
            _advancedMetricsTimer.Tick += OnAdvancedMetricsTimerTick;
            _advancedMetricsTimer.Start();

            SaveLogToDatabase("INFO", "Local System Metrics Collection Started.");
        }

        private void StopAllDataCollection()
        {
            _advancedMetricsTimer?.Stop();
            // ارگونومی را هم اینجا متوقف می‌کنیم که در مواقع خطای بحرانی کامل خاموش شود
            _ergonomyManager?.Stop();
        }

        private void LoadAppSettings()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            IConfigurationRoot configuration = builder.Build();
            
            _appSettings = new AppSettings();

            if (_appSettings == null)
            {
                _appSettings = new AppSettings();
                // می‌توانید اینجا یک لاگ خطا هم ثبت کنید
                Console.WriteLine("ERROR: AppSettings section not found in appsettings.json or could not be bound.");
            }
            if (_appSettings.SyncEngineIntervalMinutes <= 0)
            {
                _appSettings.SyncEngineIntervalMinutes = 1; // مقدار پیش‌فرض
            }
            Console.WriteLine($"Total enabled metrics count after loading: {_appSettings?.EnabledMetrics?.Count ?? 0}");
            
            configuration.GetSection("AppSettings").Bind(_appSettings); 
            
            _kafkaConnect = new KafkaConnect(configuration);
        }

        private void StartPostgresPermissionTimer()
        {
            _postgresPermissionTimer?.Stop();
            _postgresPermissionTimer?.Dispose();

            double retryHours = _appSettings?.PermissionPostgresRetryIntervalHours ?? 1;

            _postgresPermissionTimer = new System.Timers.Timer(retryHours * 60 * 60 * 1000); // تبدیل ساعت به میلی‌ثانیه
            _postgresPermissionTimer.Elapsed += (s, e) =>
            {
                UpdateSettingsFromDatabase(); // تلاش برای دریافت تنظیمات جدید
                // اگر تنظیمات تغییر کرد باید روی فایل appsettings.json نیز آپدیت شود
                SaveSettingsToAppSettingsJson(_appSettings);
                StartPostgresPermissionTimer(); // مجدد راه‌اندازی تایمر
            };
            _postgresPermissionTimer.AutoReset = false;
            _postgresPermissionTimer.Start();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🎯 Postgres permission check timer started. Interval = {retryHours} hour(s).");
        }

        private void SaveSettingsToAppSettingsJson(AppSettings? settings)
        {
            if (settings == null) return;

            try
            {
                // مسیر فایل appsettings.json معمولاً در فولدر برنامه
                string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

                var root = new
                {
                    AppSettings = settings
                };

                // استفاده از Newtonsoft.Json یا System.Text.Json
                var json = System.Text.Json.JsonSerializer.Serialize(root, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(filePath, json);

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 💾 appsettings.json updated successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Failed to update appsettings.json: {ex.Message}");
            }
        }


        private void TestKafkaConnectionAtStartup()
        {
            Task.Run(async () => 
            {
                try
                {
                    var startupLog = new
                    {
                        Timestamp = DateTime.UtcNow,
                        LogLevel = "INFO",
                        Message = "Application Started and Successfully Connected to Kafka.",
                        WindowsUsername = _windowsUsername,
                        MachineName = Environment.MachineName
                    };
                    await _kafkaConnect!.SendAppLogAsync(startupLog);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🚀 [KAFKA OK] Startup log sent.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ [KAFKA ERROR] {ex.Message}");
                }
            });
        }

        private async void OnAdvancedMetricsTimerTick(object? sender, EventArgs e)
        {
            await Task.Run(() =>
            {
                try
                {

                    var collector = new AdvancedMetricsCollector(_appSettings.EnabledMetrics, _appSettings.TopProcessesCount, _appSettings.NetworkTraceTargetIP);

                    var metrics = collector.Collect();
                    _localDb.SaveToLocalQueue("advanced_system_metrics", metrics);
                }
                catch (Exception ex)
                {
                    SaveLogToDatabase("ERROR", $"Error queuing metrics: {ex.Message}");
                }
            });
        }

        private string GetWindowsSID()
        {
            try { return WindowsIdentity.GetCurrent()?.User?.Value ?? "UNKNOWN"; }
            catch { return "UNKNOWN"; }
        }

        private void SaveLogToDatabase(string level, string message)
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
                var logEntry = new
                {
                    Timestamp = DateTime.UtcNow,
                    LogLevel = level,
                    Message = message,
                    WindowsUsername = _windowsUsername,
                    WindowsSid = _windowsSid,
                    MachineName = Environment.MachineName
                };
                _localDb?.SaveToLocalQueue("app_logs", logEntry);
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SaveLogToDatabase("INFO", "Application shutting down.");
                
                _scheduleTimer?.Stop(); _scheduleTimer?.Dispose();
                _sqlitePermissionTimer?.Stop(); _sqlitePermissionTimer?.Dispose();
                _kafkaPermissionTimer?.Stop(); _kafkaPermissionTimer?.Dispose();
                
                StopAllDataCollection();
                _syncEngine?.Stop(); 
                _ergonomyManager?.Dispose();
                _notifyIcon?.Dispose();
                
                _kafkaConnect?.Dispose(); 
                _commandManager?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
