using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Ergonomy.Database
{
    public class SyncEngine
    {
        private readonly LocalDatabaseManager _localDb;
        private readonly DatabaseManager _remoteDb; // نگه داشته شد چون شاید برای کارهای دیگر (مثل آپدیت وضعیت) نیاز باشد
        private readonly System.Timers.Timer _syncTimer; 
        private bool _isSyncing = false;
        private readonly KafkaConnect _kafkaConnect;

        public SyncEngine(KafkaConnect kafkaConnect, LocalDatabaseManager localDb, DatabaseManager remoteDb, double syncIntervalMinutes = 1)
        {
            _localDb = localDb;
            _remoteDb = remoteDb;
            _kafkaConnect = kafkaConnect;
            
            double intervalMs = syncIntervalMinutes > 0 ? (syncIntervalMinutes * 60 * 1000) : 60000;
            
            _syncTimer = new System.Timers.Timer(intervalMs); 
            _syncTimer.Elapsed += async (sender, e) => await ProcessQueueAsync();
        }

        public void Start()
        {
            _syncTimer.Start();
            Task.Run(async () => await ProcessQueueAsync()); 
        }

        public void Stop()
        {
            _syncTimer.Stop();
        }

        public void ForceSync()
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ FORCE SYNC triggered before shutdown/restart.");
            Task.Run(async () => await ProcessQueueAsync()).Wait(); // فراخوانی فوری
        }

        public void UpdateSyncInterval(double intervalMinutes)
        {
            if (intervalMinutes <= 0)
                intervalMinutes = 1;

            _syncTimer.Stop();
            _syncTimer.Interval = intervalMinutes * 60 * 1000; // تبدیل دقیقه به میلی‌ثانیه
            _syncTimer.Start();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🕒 SyncEngine interval updated to {intervalMinutes} minutes.");
        }

        private async Task ProcessQueueAsync()
        {
            if (_isSyncing) return;
            _isSyncing = true;

            try
            {
                var records = _localDb.GetPendingRecords(50);
                if (records.Count == 0) return;

                var jsonOptions = new JsonSerializerOptions
                {
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                    PropertyNameCaseInsensitive = true
                };

                foreach (var record in records)
                {
                    bool success = false;
                    try
                    {
                    if (record.TargetTable == "advanced_system_metrics")
                    {
                        var metrics = JsonSerializer.Deserialize<Dictionary<string, object>>(record.Payload, jsonOptions); 
                        if (metrics != null)
                        {
                            await _kafkaConnect.SendSystemMetricsAsync(metrics);
                            success = true;
                        }
                    }
                    else if (record.TargetTable == "user_activity")
                    {
                        var activity = JsonSerializer.Deserialize<Dictionary<string, object>>(record.Payload, jsonOptions);
                        if (activity != null)
                        {
                            await _kafkaConnect.SendUserActivityAsync(activity);
                            success = true;
                        }
                    }
                    else if (record.TargetTable == "app_logs")
                    {
                        var logData = JsonSerializer.Deserialize<Dictionary<string, object>>(record.Payload, jsonOptions);
                        if (logData != null)
                        {
                            await _kafkaConnect.SendAppLogAsync(logData);
                            success = true;
                        }
                    }

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SyncEngine] Skip record {record.Id}: {ex.Message}");
                    }

                    if (success)
                    {
                        _localDb.DeleteRecord(record.Id);
                        Console.WriteLine($"[{DateTime.Now:yyyy/MM/dd HH:mm:ss}] ✅ Data was sent from SQLite to Kafka topic for: {record.TargetTable}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SyncEngine] Fatal error: {ex.Message}");
            }
            finally
            {
                _isSyncing = false;
            }
        }
    }
}
