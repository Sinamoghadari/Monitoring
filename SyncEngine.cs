using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;


namespace Ergonomy.Database
{
    public sealed class SyncEngine : IDisposable
    {
        private const int DefaultBatchSize = 50;

        private readonly LocalDatabaseManager _localDb;
        private readonly KafkaConnect _kafkaConnect;
        private readonly System.Timers.Timer _syncTimer;
        private readonly SemaphoreSlim _syncGate = new(1, 1);

        private bool _disposed;

        public SyncEngine(
            KafkaConnect kafkaConnect,
            LocalDatabaseManager localDb,
            double syncIntervalMinutes = 1)
        {
            _localDb = localDb
                ?? throw new ArgumentNullException(nameof(localDb));

            _kafkaConnect = kafkaConnect
                ?? throw new ArgumentNullException(nameof(kafkaConnect));

            double intervalMs = syncIntervalMinutes > 0
                ? syncIntervalMinutes * 60 * 1000
                : 60_000;

            _syncTimer = new System.Timers.Timer(intervalMs)
            {
                AutoReset = true,
                Enabled = false
            };

            _syncTimer.Elapsed += OnSyncTimerElapsed;
        }

        public void Start()
        {
            ThrowIfDisposed();

            if (_syncTimer.Enabled)
                return;

            _syncTimer.Start();

            // تلاش اولیه برای تخلیه‌ی queue بدون انتظار برای اولین interval.
            _ = ProcessQueueAsync();
        }

        public void Stop()
        {
            if (_disposed)
                return;

            _syncTimer.Stop();
        }

        public async Task ForceSyncAsync()
        {
            ThrowIfDisposed();

            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] ⚠️ FORCE SYNC triggered.");

            await ProcessQueueAsync();
        }

        public void UpdateSyncInterval(double intervalMinutes)
        {
            ThrowIfDisposed();

            if (intervalMinutes <= 0)
                intervalMinutes = 1;

            bool wasRunning = _syncTimer.Enabled;

            _syncTimer.Stop();
            _syncTimer.Interval = intervalMinutes * 60 * 1000;

            if (wasRunning)
                _syncTimer.Start();

            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] 🕒 SyncEngine interval updated to " +
                $"{intervalMinutes.ToString(CultureInfo.InvariantCulture)} minute(s).");
        }

        private async void OnSyncTimerElapsed(
            object? sender,
            System.Timers.ElapsedEventArgs e)
        {
            try
            {
                await ProcessQueueAsync();
            }
            catch (Exception ex)
            {
                // محافظ نهایی برای event handler.
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ Sync timer error: {ex.Message}");
            }
        }

        private async Task ProcessQueueAsync()
        {
            if (_disposed)
                return;

            // جلوگیری از اجرای هم‌زمان Timer، Start و ForceSync.
            if (!await _syncGate.WaitAsync(0))
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ℹ️ Sync skipped: a previous sync is still running.");

                return;
            }

            try
            {
                var records = _localDb.GetPendingRecords(DefaultBatchSize);

                if (records == null || records.Count == 0)
                    return;

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] 📤 Syncing {records.Count} outbox record(s).");

                var jsonOptions = new JsonSerializerOptions
                {
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                    PropertyNameCaseInsensitive = true
                };

                foreach (var record in records)
                {
                    if (_disposed)
                        break;

                    try
                    {
                        await SendRecordToKafkaAsync(record, jsonOptions);

                        // فقط بعد از Kafka ACK موفق، رکورد از Outbox حذف می‌شود.
                        _localDb.DeleteRecord(record.Id);

                        Console.WriteLine(
                            $"[{DateTime.Now:yyyy/MM/dd HH:mm:ss}] ✅ " +
                            $"Data delivered to Kafka and removed from queue. " +
                            $"RecordId: {record.Id} | Target: {record.TargetTable}");
                    }
                    catch (JsonException ex)
                    {
                        HandlePoisonRecord(
                            record.Id,
                            record.TargetTable,
                            $"Invalid JSON payload: {ex.Message}");
                    }
                    catch (NotSupportedException ex)
                    {
                        HandlePoisonRecord(
                            record.Id,
                            record.TargetTable,
                            ex.Message);
                    }
                    catch (Exception ex)
                    {
                        // Kafka و خطاهای موقتی اینجا می‌آیند.
                        // رکورد حذف نمی‌شود تا در Sync بعدی retry شود.
                        Console.WriteLine(
                            $"[{DateTime.Now:HH:mm:ss}] ⚠️ Kafka delivery failed; " +
                            $"record remains pending. RecordId: {record.Id} | " +
                            $"Target: {record.TargetTable} | Error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ SyncEngine fatal error: {ex.Message}");
            }
            finally
            {
                _syncGate.Release();
            }
        }

        private async Task SendRecordToKafkaAsync(
            SyncRecord record,
            JsonSerializerOptions jsonOptions)
        {
            switch (record.TargetTable)
            {
                case QueueTargets.AdvancedSystemMetrics:
                {
                    var metrics = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        record.Payload,
                        jsonOptions);

                    if (metrics == null)
                    {
                        throw new JsonException(
                            "Advanced system metrics payload was null after deserialization.");
                    }

                    await _kafkaConnect.SendSystemMetricsAsync(metrics);
                    break;
                }

                case QueueTargets.UserActivity:
                {
                    var activity = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        record.Payload,
                        jsonOptions);

                    if (activity == null)
                    {
                        throw new JsonException(
                            "User activity payload was null after deserialization.");
                    }

                    await _kafkaConnect.SendUserActivityAsync(activity);
                    break;
                }

                case QueueTargets.AppLogs:
                {
                    var logData = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        record.Payload,
                        jsonOptions);

                    if (logData == null)
                    {
                        throw new JsonException(
                            "App log payload was null after deserialization.");
                    }

                    await _kafkaConnect.SendAppLogAsync(logData);
                    break;
                }

                default:
                    throw new NotSupportedException(
                        $"Unknown TargetTable '{record.TargetTable}'.");
            }
        }


        private void HandlePoisonRecord(
            Guid recordId,
            string targetTable,
            string reason)
        {
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] ❌ Poison outbox record detected. " +
                $"RecordId: {recordId} | Target: {targetTable} | Reason: {reason}");

            // فعلاً برای جلوگیری از retry بی‌نهایت حذف می‌شود.
            // نسخه‌ی production بهتر: انتقال به Dead Letter Queue.
            _localDb.DeleteRecord(recordId);

            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] 🗑️ Poison outbox record removed. " +
                $"RecordId: {recordId}");
        }


        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SyncEngine));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            _syncTimer.Stop();
            _syncTimer.Elapsed -= OnSyncTimerElapsed;
            _syncTimer.Dispose();

            _syncGate.Dispose();
        }
    }
}
