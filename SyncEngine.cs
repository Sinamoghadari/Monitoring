using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ergonomy.Database
{
    public sealed class SyncEngine : IDisposable
    {
        private const int DefaultBatchSize = 50;
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

        private readonly LocalDatabaseManager _localDb;
        private readonly KafkaConnect _kafkaConnect;
        private readonly System.Timers.Timer _syncTimer;
        private readonly SemaphoreSlim _syncGate = new(1, 1);
        private readonly double _baseIntervalMs;

        // backoff نمایی هنگام قطع Kafka
        private int _consecutiveKafkaFailures;
        private DateTime _backoffUntilUtc = DateTime.MinValue;

        private bool _disposed;

        public SyncEngine(
            KafkaConnect kafkaConnect,
            LocalDatabaseManager localDb,
            double syncIntervalMinutes = 1)
        {
            _localDb = localDb ?? throw new ArgumentNullException(nameof(localDb));
            _kafkaConnect = kafkaConnect ?? throw new ArgumentNullException(nameof(kafkaConnect));

            double intervalMs = syncIntervalMinutes > 0
                ? syncIntervalMinutes * 60 * 1000
                : 60_000;

            _baseIntervalMs = intervalMs;

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

            // ForceSync عمداً backoff را دور می‌زند.
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
                // backoff فعال است؟ این tick را رد کن.
                if (DateTime.UtcNow < _backoffUntilUtc)
                {
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] ⏸️ Sync skipped: backoff active until " +
                        $"{_backoffUntilUtc:HH:mm:ss}.");
                    return;
                }

                await ProcessQueueAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ Sync timer error: {ex.Message}");
            }
        }

        private async Task ProcessQueueAsync()
        {
            if (_disposed)
                return;

            if (!await _syncGate.WaitAsync(0))
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ℹ️ Sync skipped: a previous sync is still running.");
                return;
            }

            try
            {
                // اگر صف بحرانی است، اول retention اجرا شود تا فشار کم شود.
                if (_localDb.GetCapacityStatus() == CapacityStatus.Critical)
                {
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] 🧹 Capacity critical; running retention before sync.");
                    _localDb.RunRetention();
                }

                var records = _localDb.GetPendingRecords(DefaultBatchSize);

                if (records == null || records.Count == 0)
                {
                    // بدون رکورد یعنی صف خالی است؛ backoff را ریست کن.
                    _consecutiveKafkaFailures = 0;
                    _backoffUntilUtc = DateTime.MinValue;
                    return;
                }

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] 📤 Syncing {records.Count} outbox record(s).");

                var jsonOptions = new JsonSerializerOptions
                {
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                    PropertyNameCaseInsensitive = true
                };

                bool anyTransientFailure = false;

                foreach (var record in records)
                {
                    if (_disposed)
                        break;

                    try
                    {
                        await SendRecordToKafkaAsync(record, jsonOptions);

                        _localDb.DeleteRecord(record.Id);

                        Console.WriteLine(
                            $"[{DateTime.Now:yyyy/MM/dd HH:mm:ss}] ✅ " +
                            $"Data delivered to Kafka and removed from queue. " +
                            $"RecordId: {record.Id} | Target: {record.TargetTable}");
                    }
                    catch (JsonException ex)
                    {
                        HandlePoisonRecord(record.Id, record.TargetTable, $"Invalid JSON payload: {ex.Message}");
                    }
                    catch (NotSupportedException ex)
                    {
                        HandlePoisonRecord(record.Id, record.TargetTable, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        // خطای موقتی Kafka — رکورد می‌ماند و backoff فعال می‌شود.
                        anyTransientFailure = true;

                        Console.WriteLine(
                            $"[{DateTime.Now:HH:mm:ss}] ⚠️ Kafka delivery failed; " +
                            $"record remains pending. RecordId: {record.Id} | " +
                            $"Target: {record.TargetTable} | Error: {ex.Message}");
                    }
                }

                ApplyBackoff(anyTransientFailure);
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

        private void ApplyBackoff(bool anyTransientFailure)
        {
            if (anyTransientFailure)
            {
                _consecutiveKafkaFailures++;

                double backoffMs = _baseIntervalMs * Math.Pow(2, _consecutiveKafkaFailures);
                backoffMs = Math.Min(backoffMs, MaxBackoff.TotalMilliseconds);

                _backoffUntilUtc = DateTime.UtcNow.AddMilliseconds(backoffMs);

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ⏳ Kafka backoff: next attempt in " +
                    $"{backoffMs / 1000:0}s (failure #{_consecutiveKafkaFailures}).");
            }
            else
            {
                _consecutiveKafkaFailures = 0;
                _backoffUntilUtc = DateTime.MinValue;
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
                        record.Payload, jsonOptions);

                    if (metrics == null)
                        throw new JsonException("Advanced system metrics payload was null.");

                    await _kafkaConnect.SendSystemMetricsAsync(record.MessageId, metrics);
                    break;
                }

                case QueueTargets.UserActivity:
                {
                    var activity = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        record.Payload, jsonOptions);

                    if (activity == null)
                        throw new JsonException("User activity payload was null.");

                    await _kafkaConnect.SendUserActivityAsync(record.MessageId, activity);
                    break;
                }

                case QueueTargets.AppLogs:
                {
                    var logData = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        record.Payload, jsonOptions);

                    if (logData == null)
                        throw new JsonException("App log payload was null.");

                    await _kafkaConnect.SendAppLogAsync(record.MessageId, logData);
                    break;
                }

                default:
                    throw new NotSupportedException($"Unknown TargetTable '{record.TargetTable}'.");
            }
        }

        private void HandlePoisonRecord(Guid recordId, string targetTable, string reason)
        {
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] ❌ Poison outbox record detected. " +
                $"RecordId: {recordId} | Target: {targetTable} | Reason: {reason}");

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
