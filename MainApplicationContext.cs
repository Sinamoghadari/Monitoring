using Ergonomy.Configuration;
using Ergonomy.Hooks;
using Ergonomy.Logging;
using Ergonomy.Database;
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
using System.Globalization;
using System.Net.Http; 
using System.Text.Json; 



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

        // اضافه شد: کلاینت Http برای ارتباط با API
        private static readonly HttpClient _httpClient = new HttpClient();

        private CommandManager _commandManager;
        private System.Timers.Timer _healthCheckTimer;
        private AppSettings? _appSettings;
        private System.Timers.Timer? _wakeUpTimer;
        
        // تغییر نام: از Postgres به API
        private System.Timers.Timer? _apiPermissionTimer;
        private System.Windows.Forms.Timer _settingsUpdateTimer;
        
        // تایمرهای اصلی جمع‌آوری داده
        private System.Windows.Forms.Timer? _advancedMetricsTimer;
        
        // 🌟 تایمرهای بررسی سطح دسترسی برنامه‌ (دروازه‌های کنترلی)
        private System.Timers.Timer? _sqlitePermissionTimer;
        private System.Timers.Timer? _kafkaPermissionTimer;

        // تایمر زمان‌بندی (اجرای دستورات خاموش/روشن و ریموت)
        private System.Timers.Timer? _scheduleTimer;
        private string _lastExecutedSchedule = "";

        // فلگ‌های وضعیت سرویس‌ها (جلوگیری از اجرای تکراری)
        private bool _isLocalCollectionRunning = false;
        private bool _isSyncEngineRunning = false;

        // سرویس‌ها
        // private DatabaseManager? _dbManager;   // ❌ حذف شد
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

            // ۳. تست اتصال به کافکا در لحظه شروع
            TestKafkaConnectionAtStartup();

            StartHealthMonitoring(); 
            
            // ۴. مقداردهی اولیه سرویس‌ها (بدون dbManager)
            // ⚠️ نکته مهم: باید در کلاس‌های زیر، DatabaseManager را از Constructor حذف کنید.
            _syncEngine = new SyncEngine(_kafkaConnect!, _localDb, _appSettings?.SyncEngineIntervalMinutes ?? 1);
            _ergonomyManager = new ErgonomyManager(_appSettings, _localDb, _sessionGuid, _windowsSid, _windowsUsername);

            // ۵. تلاش برای دریافت آخرین تنظیمات آنلاین از API به جای Postgres
            Task.Run(() => UpdateSettingsFromApiAsync());

            int intervalSeconds = _appSettings?.SettingsCheckIntervalSeconds > 0 ? _appSettings.SettingsCheckIntervalSeconds : 60;
            _settingsUpdateTimer = new System.Windows.Forms.Timer();
            _settingsUpdateTimer.Interval = intervalSeconds * 1000;
            _settingsUpdateTimer.Tick += async (s, e) => await UpdateSettingsFromApiAsync();
            _settingsUpdateTimer.Start();

            
            _commandManager = new CommandManager(_appSettings, _windowsUsername , _localDb)
            {
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
            _commandManager.Start();
        
            _notifyIcon = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Visible = true, Text = "Ergonomy" };

            // ۶. بررسی دسترسی‌ها و راه‌اندازی چرخه‌ها
            EvaluateSqlitePermission();
            EvaluateKafkaPermission();
            EvaluateErgonomyPermission();
            
            StartSqlitePermissionTimer();
            StartKafkaPermissionTimer();
            StartApiPermissionTimer(); // تغییر یافت
        }

        private void RestartPermissionTimers()
        {
            StartSqlitePermissionTimer();
            StartKafkaPermissionTimer();
            StartApiPermissionTimer();
        }

        private void HandleCriticalFailure(string errorMessage)
        {
            SaveLogToDatabase("FATAL", $"Critical error occurred: {errorMessage}. Forcing system to sleep state.");
            GoToSleepAndRetry();
        }

        private void GoToSleepAndRetry()
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 💤 Entering Sleep Mode due to critical failures...");
            
            StopAllDataCollection();
            _isLocalCollectionRunning = false;
            
            _syncEngine?.Stop();
            _isSyncEngineRunning = false;

            _sqlitePermissionTimer?.Stop();
            _kafkaPermissionTimer?.Stop();
            _scheduleTimer?.Stop();

            double sleepMinutes = _appSettings?.ConnectionFailureSleepMinutes ?? 5;
            
            _wakeUpTimer?.Stop();
            _wakeUpTimer?.Dispose();
            // فرمول محاسبه: $sleepMinutes \times 60 \times 1000$
            _wakeUpTimer = new System.Timers.Timer(sleepMinutes * 60 * 1000);
            _wakeUpTimer.Elapsed += (s, e) => WakeUp();
            _wakeUpTimer.AutoReset = false;
            _wakeUpTimer.Start();
        }

        private void WakeUp()
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ☀️ Waking up and re-evaluating connections...");
            
            Task.Run(() => UpdateSettingsFromApiAsync());
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

            Task.Run(async () => await PerformAllHealthChecksAsync());
        }

        private async Task PerformAllHealthChecksAsync()
        {
            // جایگزین شد: بررسی سلامت API
            await CheckApiHealthAsync();
            await CheckSqliteHealthAsync();
            await CheckSelfPerformanceAsync();
        }

        // بررسی سلامت API
        private async Task CheckApiHealthAsync()
        {
            string? apiUrl = _configuration["AppSettings:API:Settings"];
            if (string.IsNullOrEmpty(apiUrl)) return;

            try
            {
                var response = await _httpClient.GetAsync(apiUrl);
                if (response.IsSuccessStatusCode)
                {
                    await SendHealthLogAsync("INFO", "Settings API is healthy and accessible.", "ApiHealth");
                }
                else
                {
                    await SendHealthLogAsync("WARN", $"Settings API returned status code: {response.StatusCode}", "ApiHealth");
                }
            }
            catch (Exception ex)
            {
                await SendHealthLogAsync("ERROR", $"API Health Check Error: {ex.Message}", "ApiHealth");
            }
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
                HandleCriticalFailure("SQLite is inaccessible. Data collection cannot continue.");
            }

            await SendHealthLogAsync(logLevel, statusMessage, "SqliteHealth");
        }

        private async Task CheckSelfPerformanceAsync()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                // فرمول محاسبه مگابایت: $MB = \frac{Bytes}{1024 \times 1024}$
                long memoryUsedMB = process.WorkingSet64 / (1024 * 1024);
                
                string statusMessage = $"Agent Performance: Memory Usage is {memoryUsedMB} MB. Thread Count: {process.Threads.Count}";
                string logLevel = memoryUsedMB > 500 ? "WARN" : "INFO";

                await SendHealthLogAsync(logLevel, statusMessage, "AgentPerformance");
            }
            catch (Exception ex)
            {
                await SendHealthLogAsync("ERROR", $"Failed to check self performance: {ex.Message}", "AgentPerformance");
            }
        }

        private async Task SendHealthLogAsync(string logLevel, string message, string category)
        {

            DateTime currentTime = DateTime.Now;
            PersianCalendar pc = new PersianCalendar();
            if (_kafkaConnect == null) return;

            var logObj = new
            {
                CollectedAt = currentTime.ToString("yyyy-MM-dd HH:mm:ss"),
                CollectedAt_Shamsi = $"{pc.GetYear(currentTime):0000}/{pc.GetMonth(currentTime):00}/{pc.GetDayOfMonth(currentTime):00} {currentTime:HH:mm:ss}" ,
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

        // متد جدید: آپدیت تنظیمات از طریق API
        private async Task UpdateSettingsFromApiAsync()
        {
            try
            {
                string? apiUrl = _configuration["AppSettings:API:Settings"];
                if (string.IsNullOrEmpty(apiUrl)) return;

                HttpResponseMessage response = await _httpClient.GetAsync(apiUrl);
                if (response.IsSuccessStatusCode)
                {
                    string jsonString = await response.Content.ReadAsStringAsync();
                    
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var apiSettingsOverride = JsonSerializer.Deserialize<AppSettings>(jsonString, options);

                    if (apiSettingsOverride != null)
                    {
                        var oldSettingsJson = JsonSerializer.Serialize(_appSettings);
                        var newSettingsJson = JsonSerializer.Serialize(apiSettingsOverride);

                        if (oldSettingsJson != newSettingsJson)
                        {
                            _appSettings = apiSettingsOverride;
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Settings updated securely from API.");

                            SaveSettingsToAppSettingsJson(_appSettings);
                            RestartPermissionTimers();

                            if (_syncEngine != null)
                            {
                                _syncEngine.UpdateSyncInterval(_appSettings.SyncEngineIntervalMinutes);
                            }

                            if (_appSettings.SettingsCheckIntervalSeconds > 0 && _settingsUpdateTimer != null)
                            {
                                int intervalSeconds = _appSettings.SettingsCheckIntervalSeconds;
                                _settingsUpdateTimer.Interval = intervalSeconds * 1000;
                            }

                            if (_appSettings.AllowErgonomyCollection)
                            {
                                _ergonomyManager?.Start();
                                Console.WriteLine("Ergonomy Started.");
                            }
                            else
                            {
                                _ergonomyManager?.Stop();
                                Console.WriteLine("Ergonomy Stopped.");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Offline Mode or API Error! Using appsettings.json. Msg: {ex.Message}");
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
                if (!_isLocalCollectionRunning)
                {
                    StartLocalDataCollection();
                    _isLocalCollectionRunning = true;
                }
            }
            else
            {
                if (_isLocalCollectionRunning)
                {
                    StopAllDataCollection();
                    _isLocalCollectionRunning = false;
                    
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
            bool allowKafka = _appSettings?.AllowKafkaWrite ?? true; 
            double intervalHours = _appSettings?.PermissionKafkaRetryIntervalHours ?? 1;

            string msg = $"[Kafka Sync Status] Permission: {allowKafka} | Checking continuously every {intervalHours} hour(s).";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔍 {msg}");
            SaveLogToDatabase("INFO", msg);

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

        private void EvaluateErgonomyPermission()
        {
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
            _sqlitePermissionTimer.Elapsed += async (s, e) => 
            {
                await UpdateSettingsFromApiAsync(); 
                EvaluateSqlitePermission(); 
                EvaluateErgonomyPermission();
                StartSqlitePermissionTimer(); 
            };
            _sqlitePermissionTimer.AutoReset = false; 
            _sqlitePermissionTimer.Start();
        }

        private void StartKafkaPermissionTimer()
        {
            _kafkaPermissionTimer?.Stop();
            _kafkaPermissionTimer?.Dispose();

            double retryHours = _appSettings?.PermissionKafkaRetryIntervalHours ?? 1;
            _kafkaPermissionTimer = new System.Timers.Timer(retryHours * 60 * 60 * 1000); 
            _kafkaPermissionTimer.Elapsed += async (s, e) => 
            {
                await UpdateSettingsFromApiAsync(); 
                EvaluateKafkaPermission(); 
                StartKafkaPermissionTimer(); 
            };
            _kafkaPermissionTimer.AutoReset = false; 
            _kafkaPermissionTimer.Start();
        }

        private void StartLocalDataCollection()
        {
            StopAllDataCollection();
            
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
                Console.WriteLine("ERROR: AppSettings section not found in appsettings.json or could not be bound.");
            }
            if (_appSettings.SyncEngineIntervalMinutes <= 0)
            {
                _appSettings.SyncEngineIntervalMinutes = 1; 
            }
            Console.WriteLine($"Total enabled metrics count after loading: {_appSettings?.EnabledMetrics?.Count ?? 0}");
            
            configuration.GetSection("AppSettings").Bind(_appSettings); 
            
            _kafkaConnect = new KafkaConnect(configuration);
        }

        // تغییر نام و منطق از Postgres به API
        private void StartApiPermissionTimer()
        {
            _apiPermissionTimer?.Stop();
            _apiPermissionTimer?.Dispose();

            // استفاده از تایمر جایگزین (یا یک ویژگی جدید در صورت نیاز تعریف کنید)
            double retryHours = 1; 
            
            _apiPermissionTimer = new System.Timers.Timer(retryHours * 60 * 60 * 1000); 
            _apiPermissionTimer.Elapsed += async (s, e) =>
            {
                await UpdateSettingsFromApiAsync();
                StartApiPermissionTimer(); 
            };
            _apiPermissionTimer.AutoReset = false;
            _apiPermissionTimer.Start();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🎯 API permission check timer started. Interval = {retryHours} hour(s).");
        }

        private void SaveSettingsToAppSettingsJson(AppSettings? settings)
        {
            if (settings == null) return;

            try
            {
                string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                var root = new { AppSettings = settings };
                var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
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
            DateTime currentTime = DateTime.Now;
            PersianCalendar pc = new PersianCalendar();
            Task.Run(async () => 
            {
                try
                {
                    var startupLog = new
                    {
                        CollectedAt = currentTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        CollectedAt_Shamsi = $"{pc.GetYear(currentTime):0000}/{pc.GetMonth(currentTime):00}/{pc.GetDayOfMonth(currentTime):00} {currentTime:HH:mm:ss}" ,
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
            DateTime currentTime = DateTime.Now;
            PersianCalendar pc = new PersianCalendar();
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
                var logEntry = new
                {
                    CollectedAt = currentTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    CollectedAt_Shamsi = $"{pc.GetYear(currentTime):0000}/{pc.GetMonth(currentTime):00}/{pc.GetDayOfMonth(currentTime):00} {currentTime:HH:mm:ss}",
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
                _apiPermissionTimer?.Stop(); _apiPermissionTimer?.Dispose();
                
                StopAllDataCollection();
                _syncEngine?.Stop(); 
                _ergonomyManager?.Dispose();
                _notifyIcon?.Dispose();
                
                _kafkaConnect?.Dispose(); 
                _commandManager?.Dispose();
                _httpClient?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
