using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ergonomy.Database
{
    public class SyncEngine : IDisposable
    {
        private readonly LocalDatabaseManager _localDb;
        private readonly KafkaConnect _kafkaConnect;
        private readonly System.Timers.Timer _syncTimer;
        private readonly SemaphoreSlim _syncGate = new SemaphoreSlim(1, 1);
        private bool _disposed = false;

        public SyncEngine(KafkaConnect kafkaConnect, LocalDatabaseManager localDb, double syncIntervalMinutes = 1)
        {
            _localDb = localDb ?? throw new ArgumentNullException(nameof(localDb));
            _kafkaConnect = kafkaConnect ?? throw new ArgumentNullException(nameof(kafkaConnect));

            double intervalMs = syncIntervalMinutes > 0 ? (syncIntervalMinutes * 60 * 1000) : 60000;

            _syncTimer = new System.Timers.Timer(intervalMs)
            {
                AutoReset = true,
                Enabled = false
            };

            _syncTimer.Elapsed += async (sender, e) => await ProcessQueueAsync();
        }

        public void Start()
        {
            ThrowIfDisposed();
            _syncTimer.Start();
            _ = ProcessQueueAsync();
        }

        public void Stop()
        {
            if (_disposed) return;
            _syncTimer.Stop();
        }

        public async Task ForceSyncAsync()
        {
            ThrowIfDisposed();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ FORCE SYNC triggered before shutdown/restart.");
            await ProcessQueueAsync();
        }

        public void UpdateSyncInterval(double intervalMinutes)
        {
            ThrowIfDisposed();

            if (intervalMinutes <= 0)
                intervalMinutes = 1;

            _syncTimer.Stop();
            _syncTimer.Interval = intervalMinutes * 60 * 1000;
            _syncTimer.Start();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🕒 SyncEngine interval updated to {intervalMinutes} minutes.");
        }

        private async Task ProcessQueueAsync()
        {
            if (!await _syncGate.WaitAsync(0))
                return;

            try
            {
                var records = _localDb.GetPendingRecords(50);
                if (records == null || records.Count == 0)
                    return;

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
                        switch (record.TargetTable)
                        {
                            case QueueTargets.AdvancedSystemMetrics:
                            {
                                var metrics = JsonSerializer.Deserialize<Dictionary<string, object>>(record.Payload, jsonOptions);
                                if (metrics != null)
                                {
                                    await _kafkaConnect.SendSystemMetricsAsync(metrics);
                                    success = true;
                                }
                                break;
                            }

                            case QueueTargets.UserActivity:
                            {
                                var activity = JsonSerializer.Deserialize<Dictionary<string, object>>(record.Payload, jsonOptions);
                                if (activity != null)
                                {
                                    await _kafkaConnect.SendUserActivityAsync(activity);
                                    success = true;
                                }
                                break;
                            }

                            case QueueTargets.AppLogs:
                            {
                                var logData = JsonSerializer.Deserialize<Dictionary<string, object>>(record.Payload, jsonOptions);
                                if (logData != null)
                                {
                                    await _kafkaConnect.SendAppLogAsync(logData);
                                    success = true;
                                }
                                break;
                            }

                            default:
                                Console.WriteLine($"[SyncEngine] Unknown TargetTable: {record.TargetTable}");
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SyncEngine] Skip record {record.Id}: {ex.Message}");
                    }

                    if (success)
                    {
                        _localDb.DeleteRecord(record.Id);
                        Console.WriteLine($"[{DateTime.Now:yyyy/MM/dd HH:mm:ss}] ✅ Data sent to Kafka for: {record.TargetTable}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SyncEngine] Fatal error: {ex.Message}");
            }
            finally
            {
                _syncGate.Release();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SyncEngine));
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _syncTimer.Stop();
            _syncTimer.Dispose();
            _syncGate.Dispose();
        }
    }
}
