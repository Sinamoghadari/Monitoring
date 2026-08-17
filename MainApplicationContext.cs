using Ergonomy.Configuration;
using Ergonomy.Core;
using Ergonomy.Database;
using Ergonomy.Hooks;
using Microsoft.Data.Sqlite;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;


namespace Ergonomy
{
    public class MainApplicationContext : ApplicationContext
    {
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _settingsRefreshLock = new(1, 1);

        private CommandManager? _commandManager;
        private AppSettings _appSettings = null!;
        private LocalDatabaseManager _localDb;
        private SyncEngine? _syncEngine;
        private NotifyIcon? _notifyIcon;
        private KafkaConnect? _kafkaConnect;
        private ErgonomyManager? _ergonomyManager;

        private System.Timers.Timer? _healthCheckTimer;
        private System.Timers.Timer? _wakeUpTimer;
        private System.Timers.Timer? _settingsUpdateTimer;
        private System.Timers.Timer? _advancedMetricsTimer;
        private System.Timers.Timer? _sqlitePermissionTimer;
        private System.Timers.Timer? _kafkaPermissionTimer;

        private bool _isLocalCollectionRunning;
        private bool _isSyncEngineRunning;
        private int _advancedMetricsExecutionGate;
        private bool _isDisposed;

        private readonly string _windowsSid;
        private readonly string _windowsUsername;
        private readonly string _sessionGuid;

        public MainApplicationContext()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            Application.ThreadException += (s, e) => HandleCriticalFailure(e.Exception.Message);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    HandleCriticalFailure(ex.Message);
            };

            LoadAppSettings();
            
            _windowsSid = GetWindowsSID();
            try { _windowsUsername = WindowsIdentity.GetCurrent().Name; }
            catch { _windowsUsername = Environment.UserName; }

            _sessionGuid = Guid.NewGuid().ToString();

            _localDb = new LocalDatabaseManager(_appSettings.Outbox);
            _kafkaConnect = new KafkaConnect(
            _appSettings.Kafka.BootstrapServers,
            _appSettings.Kafka.UserActivityTopic,
            _appSettings.Kafka.SystemMetricsTopic,
            _appSettings.Kafka.AppLogsTopic);



            TestKafkaConnectionAtStartup();

            _syncEngine = new SyncEngine(_kafkaConnect, _localDb, _appSettings.SyncEngineIntervalMinutes);
            _ergonomyManager = new ErgonomyManager(_appSettings, _localDb, _sessionGuid, _windowsSid, _windowsUsername);

            _commandManager = new CommandManager(
                _appSettings,
                _windowsUsername,
                _localDb)
            {
                OnLogRequired = SaveLogToDatabase,
                OnForceSync = () => _syncEngine?.ForceSyncAsync(),
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

            _notifyIcon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "Ergonomy"
            };

            StartHealthMonitoring();

            try
            {
                UpdateSettingsFromApiAsync(logFailures: true).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Initial settings refresh failed. Using Environment settings. Msg: {ex.Message}");
            }

            EvaluateSqlitePermission();
            EvaluateKafkaPermission();
            EvaluateErgonomyPermission();

            StartSettingsUpdateTimer();
            StartSqlitePermissionTimer();
            StartKafkaPermissionTimer();
        }

        private void LoadAppSettings()
        {
            _appSettings = EnvironmentSettingsProvider.Load();
            ApplyDefaultValues(_appSettings);

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🌍 Bootstrap settings loaded from Machine Environment Variables.");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Enabled metrics count: {_appSettings.EnabledMetrics?.Count ?? 0}");
        }

        private void ApplyDefaultValues(AppSettings settings)
        {
            if (settings.SyncEngineIntervalMinutes <= 0)
                settings.SyncEngineIntervalMinutes = 1;

            if (settings.AdvancedMetricsIntervalMinutes <= 0)
                settings.AdvancedMetricsIntervalMinutes = 120;

            if (settings.SettingsCheckIntervalSeconds <= 0)
                settings.SettingsCheckIntervalSeconds = 60;

            if (settings.PermissionSqliteRetryIntervalHours <= 0)
                settings.PermissionSqliteRetryIntervalHours = 1;

            if (settings.PermissionKafkaRetryIntervalHours <= 0)
                settings.PermissionKafkaRetryIntervalHours = 1;

            if (settings.ConnectionFailureSleepMinutes <= 0)
                settings.ConnectionFailureSleepMinutes = 5;
        }

        private AppSettings PreserveEnvironmentInfrastructureSettings(AppSettings remoteSettings)
        {
            remoteSettings.API = _appSettings.API;
            remoteSettings.Kafka = _appSettings.Kafka;
            return remoteSettings;
        }


        private string? GetBootstrapApiSettingsUrl()
        {
            return _appSettings.API.Settings;
        }

        private string NormalizeSettings(AppSettings settings)
        {
            return JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = false
            });
        }

        private async Task UpdateSettingsFromApiAsync(bool logFailures = false)
        {
            await _settingsRefreshLock.WaitAsync();
            try
            {
                string? apiUrl = GetBootstrapApiSettingsUrl();
                if (string.IsNullOrWhiteSpace(apiUrl))
                {
                    if (logFailures)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Settings API URL is empty in Environment settings.");
                    return;
                }

                using HttpResponseMessage response = await _httpClient.GetAsync(apiUrl);
                if (!response.IsSuccessStatusCode)
                {
                    if (logFailures)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Settings API returned status code: {response.StatusCode}");
                    return;
                }

                string jsonString = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                AppSettings? remoteSettings = JsonSerializer.Deserialize<AppSettings>(jsonString, options);

                if (remoteSettings == null)
                {
                    if (logFailures)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Settings API response could not be deserialized.");
                    return;
                }

                ApplyDefaultValues(remoteSettings);

                // حفظ URLهای API از Environment، چون اینها bootstrap هستند
                remoteSettings = PreserveEnvironmentInfrastructureSettings(remoteSettings);

                string currentJson = NormalizeSettings(_appSettings);
                string newJson = NormalizeSettings(remoteSettings);

                if (currentJson == newJson)
                    return;

                _appSettings = remoteSettings;

                ReconfigureRuntimeBasedOnSettings();

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Settings updated from API successfully.");
            }
            catch (Exception ex)
            {
                if (logFailures)
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Offline Mode or API Error! Using Environment settings. Msg: {ex.Message}");
            }
            finally
            {
                _settingsRefreshLock.Release();
            }
        }


        private void ReconfigureRuntimeBasedOnSettings()
        {
            _syncEngine?.UpdateSyncInterval(_appSettings.SyncEngineIntervalMinutes);

            _commandManager?.UpdateSettings(_appSettings);

            RestartSettingsUpdateTimer();
            RestartPermissionTimers();

            EvaluateSqlitePermission();
            EvaluateKafkaPermission();
            EvaluateErgonomyPermission();
        }


        private void RestartPermissionTimers()
        {
            StartSqlitePermissionTimer();
            StartKafkaPermissionTimer();
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

            StopTimer(ref _sqlitePermissionTimer);
            StopTimer(ref _kafkaPermissionTimer);
            StopTimer(ref _settingsUpdateTimer);
            StopTimer(ref _healthCheckTimer);

            double sleepMinutes = _appSettings.ConnectionFailureSleepMinutes;

            StopTimer(ref _wakeUpTimer);
            _wakeUpTimer = new System.Timers.Timer(sleepMinutes * 60 * 1000)
            {
                AutoReset = false
            };
            _wakeUpTimer.Elapsed += async (s, e) => await WakeUpAsync();
            _wakeUpTimer.Start();
        }

        private async Task WakeUpAsync()
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ☀️ Waking up and re-evaluating connections...");

            StartHealthMonitoring();

            await UpdateSettingsFromApiAsync(logFailures: true);

            EvaluateSqlitePermission();
            EvaluateKafkaPermission();
            EvaluateErgonomyPermission();

            StartSettingsUpdateTimer();
            StartSqlitePermissionTimer();
            StartKafkaPermissionTimer();

            _commandManager?.Start();
        }

        private void StartHealthMonitoring()
        {
            StopTimer(ref _healthCheckTimer);

            const double intervalMinutes = 15;

            _healthCheckTimer = new System.Timers.Timer(intervalMinutes * 60 * 1000)
            {
                AutoReset = false
            };

            _healthCheckTimer.Elapsed += async (sender, e) =>
            {
                try
                {
                    await PerformAllHealthChecksAsync();
                }
                finally
                {
                    if (!_isDisposed)
                        _healthCheckTimer?.Start();
                }
            };

            _healthCheckTimer.Start();
            _ = Task.Run(PerformAllHealthChecksAsync);
        }



        private async Task PerformAllHealthChecksAsync()
        {
            await CheckApiHealthAsync();
            await CheckSqliteHealthAsync();
            await CheckSelfPerformanceAsync();
        }

        private async Task CheckApiHealthAsync()
        {
            string? apiUrl = GetBootstrapApiSettingsUrl();
            if (string.IsNullOrWhiteSpace(apiUrl))
                return;

            try
            {
                using var response = await _httpClient.GetAsync(apiUrl);
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
            string sqliteDbPath = Environment.GetEnvironmentVariable("ERGONOMY_SQLITE_CONNECTION_STRING", EnvironmentVariableTarget.Machine)
                ?? "Data Source=localbuffer.db";

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
                using Process process = Process.GetCurrentProcess();
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

        private Task SendHealthLogAsync(
            string logLevel,
            string message,
            string category)
        {
            if (_localDb == null)
                return Task.CompletedTask;

            DateTime currentTime = DateTime.Now;
            PersianCalendar pc = new PersianCalendar();

            var logObj = new
            {
                CollectedAt = currentTime.ToString("yyyy-MM-dd HH:mm:ss"),
                CollectedAt_Shamsi =
                    $"{pc.GetYear(currentTime):0000}/" +
                    $"{pc.GetMonth(currentTime):00}/" +
                    $"{pc.GetDayOfMonth(currentTime):00} " +
                    $"{currentTime:HH:mm:ss}",

                LogLevel = logLevel,
                Message = message,
                WindowsUsername = _windowsUsername,
                WindowsSid = _windowsSid,
                MachineName = Environment.MachineName,
                Category = category
            };

            try
            {
                var result = _localDb.SaveToLocalQueue(
                    QueueTargets.AppLogs,
                    logObj);

                switch (result)
                {
                    case OutboxSaveResult.Saved:
                        Console.WriteLine(
                            $"[{DateTime.Now:HH:mm:ss}] " +
                            $"[{logLevel}] [{category}] {message}");
                        break;

                    case OutboxSaveResult.DroppedLowPriority:
                        Console.WriteLine(
                            $"[{DateTime.Now:HH:mm:ss}] ⚠️ [OUTBOX] " +
                            $"Health log dropped due to critical capacity: {category}");
                        break;

                    case OutboxSaveResult.Failed:
                    default:
                        Console.WriteLine(
                            $"[{DateTime.Now:HH:mm:ss}] ❌ [OUTBOX ERROR] " +
                            $"Failed to enqueue {category} health log.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ [OUTBOX ERROR] " +
                    $"Failed to queue {category} log: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private void EvaluateSqlitePermission()
        {
            bool allowSqlite = _appSettings.AllowSqliteWrite;
            double intervalHours = _appSettings.PermissionSqliteRetryIntervalHours;

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
                }

                if (_isSyncEngineRunning)
                {
                    _syncEngine?.Stop();
                    _isSyncEngineRunning = false;
                }

                SaveLogToDatabase("WARNING", "Local Collection (SQLite): Access DENIED. Process is CANCELED/SLEEPING.");
            }
        }

        private void EvaluateKafkaPermission()
        {
            bool allowSqlite = _appSettings.AllowSqliteWrite;
            bool allowKafka = _appSettings.AllowKafkaWrite;
            double intervalHours = _appSettings.PermissionKafkaRetryIntervalHours;

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
            bool allowErgonomy = _appSettings.AllowErgonomyCollection;

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

        private void StartSettingsUpdateTimer()
        {
            StopTimer(ref _settingsUpdateTimer);

            _settingsUpdateTimer = new System.Timers.Timer(_appSettings.SettingsCheckIntervalSeconds * 1000)
            {
                AutoReset = false
            };

            _settingsUpdateTimer.Elapsed += async (s, e) =>
            {
                try
                {
                    await UpdateSettingsFromApiAsync(logFailures: false);
                }
                finally
                {
                    if (!_isDisposed)
                        _settingsUpdateTimer?.Start();
                }
            };

            _settingsUpdateTimer.Start();
        }

        private void RestartSettingsUpdateTimer()
        {
            StartSettingsUpdateTimer();
        }

        private void StartSqlitePermissionTimer()
        {
            StopTimer(ref _sqlitePermissionTimer);

            _sqlitePermissionTimer = new System.Timers.Timer(_appSettings.PermissionSqliteRetryIntervalHours * 60 * 60 * 1000)
            {
                AutoReset = false
            };

            _sqlitePermissionTimer.Elapsed += (s, e) =>
            {
                try
                {
                    EvaluateSqlitePermission();
                    EvaluateErgonomyPermission();
                }
                finally
                {
                    if (!_isDisposed)
                        _sqlitePermissionTimer?.Start();
                }
            };

            _sqlitePermissionTimer.Start();
        }

        private void StartKafkaPermissionTimer()
        {
            StopTimer(ref _kafkaPermissionTimer);

            _kafkaPermissionTimer = new System.Timers.Timer(_appSettings.PermissionKafkaRetryIntervalHours * 60 * 60 * 1000)
            {
                AutoReset = false
            };

            _kafkaPermissionTimer.Elapsed += (s, e) =>
            {
                try
                {
                    EvaluateKafkaPermission();
                }
                finally
                {
                    if (!_isDisposed)
                        _kafkaPermissionTimer?.Start();
                }
            };

            _kafkaPermissionTimer.Start();
        }

        private void StartLocalDataCollection()
        {
            StopAllDataCollection();

            double advancedIntervalMinutes = _appSettings.AdvancedMetricsIntervalMinutes > 0
                ? _appSettings.AdvancedMetricsIntervalMinutes
                : 120;

            _advancedMetricsTimer = new System.Timers.Timer(advancedIntervalMinutes * 60 * 1000)
            {
                AutoReset = true
            };

            _advancedMetricsTimer.Elapsed += OnAdvancedMetricsTimerElapsed;
            _advancedMetricsTimer.Start();

            SaveLogToDatabase("INFO", "Local System Metrics Collection Started.");
        }

        private void StopAllDataCollection()
        {
            StopTimer(ref _advancedMetricsTimer);
            _ergonomyManager?.Stop();
        }

        private async void OnAdvancedMetricsTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (Interlocked.Exchange(ref _advancedMetricsExecutionGate, 1) == 1)
                return;

            try
            {
                if (_appSettings == null || _appSettings.EnabledMetrics == null)
                {
                    SaveLogToDatabase("WARNING", "Advanced metrics collection skipped because settings are not ready.");
                    return;
                }

                await Task.Run(() =>
                {
                    var collector = new AdvancedMetricsCollector(
                        _appSettings.EnabledMetrics,
                        _appSettings.TopProcessesCount,
                        _appSettings.NetworkTraceTargetIP);

                    var metrics = collector.Collect();
                    _localDb.SaveToLocalQueue(QueueTargets.AdvancedSystemMetrics, metrics);
                });
            }
            catch (Exception ex)
            {
                SaveLogToDatabase("ERROR", $"Error queuing metrics: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _advancedMetricsExecutionGate, 0);
            }
        }

        private void TestKafkaConnectionAtStartup()
        {
            DateTime currentTime = DateTime.Now;
            PersianCalendar pc = new PersianCalendar();

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_kafkaConnect == null)
                        return;

                    var startupLog = new
                    {
                        CollectedAt = currentTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        CollectedAt_Shamsi =
                            $"{pc.GetYear(currentTime):0000}/" +
                            $"{pc.GetMonth(currentTime):00}/" +
                            $"{pc.GetDayOfMonth(currentTime):00} " +
                            $"{currentTime:HH:mm:ss}",

                        LogLevel = "INFO",
                        Message = "Application Started and Kafka delivery probe succeeded.",
                        WindowsUsername = _windowsUsername,
                        WindowsSid = _windowsSid,
                        MachineName = Environment.MachineName,
                        Category = "KafkaStartupProbe"
                    };

                    await _kafkaConnect.SendAppLogAsync(
                        Guid.NewGuid().ToString("N"),
                        startupLog);

                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] " +
                        $"🚀 [KAFKA OK] Startup delivery probe succeeded.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] " +
                        $"❌ [KAFKA ERROR] Startup delivery probe failed: " +
                        $"{ex.Message}");
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
            catch
            {
            }
        }

        private void StopTimer(ref System.Timers.Timer? timer)
        {
            if (timer == null)
                return;

            try
            {
                timer.Stop();
                timer.Dispose();
            }
            catch
            {
            }
            finally
            {
                timer = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_isDisposed)
            {
                _isDisposed = true;

                SaveLogToDatabase("INFO", "Application shutting down.");

                StopTimer(ref _healthCheckTimer);
                StopTimer(ref _wakeUpTimer);
                StopTimer(ref _settingsUpdateTimer);
                StopTimer(ref _advancedMetricsTimer);
                StopTimer(ref _sqlitePermissionTimer);
                StopTimer(ref _kafkaPermissionTimer);

                _syncEngine?.Stop();
                _ergonomyManager?.Stop();

                _ergonomyManager?.Dispose();
                _notifyIcon?.Dispose();
                _kafkaConnect?.Dispose();
                _commandManager?.Dispose();
                _httpClient.Dispose();
                _settingsRefreshLock.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
