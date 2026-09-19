using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ergonomy.Logging;
using Ergonomy.Observability;

namespace Ergonomy.Database
{
    /// <summary>
    /// Worker that drains the SQLite outbox to Kafka Predictably. It is no longer timer-driven
    /// inside a monolithic context: it runs a single cancelable loop on a background task using
    /// <see cref="PeriodicTimer"/>, awaits shutdown, and never uses async void / fire-and-forget.
    /// A single <see cref="SemaphoreSlim"/> gate prevents overlapping batch processing.
    /// </summary>
    public sealed class SyncEngine : IDisposable
    {
        private const int DefaultBatchSize = 50;
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

        private readonly LocalDatabaseManager _localDb;
        private readonly KafkaConnect _kafkaConnect;
        private readonly ILogger<SyncEngine> _logger;
        private readonly AgentMetrics _metrics;
        private readonly SemaphoreSlim _syncGate = new(1, 1);
        private readonly object _sync = new();

        private double _baseIntervalMinutes;
        private int _consecutiveKafkaFailures;
        private DateTime _backoffUntilUtc = DateTime.MinValue;
        private bool _disposed;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public bool IsRunning { get; private set; }

        /// <summary>
        /// موتور همگام‌سازی outbox را با اتصال کافکا، پایگاه محلی، متریک‌ها و فاصله پایه می‌سازد.
        /// </summary>
        /// <param name="kafkaConnect">تولیدکننده کافکا برای تحویل رکوردها.</param>
        /// <param name="localDb">مدیر صف SQLite برای خواندن و حذف رکوردها.</param>
        /// <param name="logger">ثبت‌کننده رویدادهای همگام‌سازی و backoff.</param>
        /// <param name="metrics">رجیستری متریک پرومتئوس برای شمارنده‌های همگام‌سازی.</param>
        /// <param name="syncIntervalMinutes">فاصله پایه بین دورهای همگام‌سازی.</param>
        public SyncEngine(
            KafkaConnect kafkaConnect,
            LocalDatabaseManager localDb,
            ILogger<SyncEngine> logger,
            AgentMetrics metrics,
            double syncIntervalMinutes = 1)
        {
            _localDb = localDb ?? throw new ArgumentNullException(nameof(localDb));
            _kafkaConnect = kafkaConnect ?? throw new ArgumentNullException(nameof(kafkaConnect));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _baseIntervalMinutes = syncIntervalMinutes > 0 ? syncIntervalMinutes : 1;
        }

        /// <summary>
        /// حلقه پس‌زمینه همگام‌سازی را روی یک وظیفه جداگانه شروع می‌کند.
        /// </summary>
        public void Start()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (IsRunning)
                    return;

                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
                IsRunning = true;
            }

            _logger.LogInformation(LogEvents.WorkerStartedId, "Sync Engine started (Kafka Allowed).");
        }

        /// <summary>
        /// حلقه همگام‌سازی را لغو کرده، برای خروج کوتاه منتظر می‌ماند و وضعیت اجرا را پاک می‌کند.
        /// </summary>
        /// <param name="reason">دلیل توقف برای ثبت در لاگ.</param>
        public void Stop(string reason = "requested")
        {
            CancellationTokenSource? cts = null;
            Task? loop = null;

            lock (_sync)
            {
                if (!IsRunning)
                {
                    _cts?.Cancel();
                    return;
                }

                cts = _cts;
                _cts = null;
                loop = _loop;
                _loop = null;
                IsRunning = false;
            }

            try
            {
                cts?.Cancel();
            }
            catch
            {
            }

            try
            {
                loop?.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
            }
            catch (Exception)
            {
            }

            cts?.Dispose();

            _logger.LogInformation(
                LogEvents.WorkerStoppedId, "Sync Engine stopped (reason: {Reason}).", reason);
        }

        /// <summary>
        /// به‌صورت ناهمگام یک دور همگام‌سازی فوری را بدون در نظر گرفتن backoff فعال اجرا می‌کند.
        /// </summary>
        /// <returns>وظیفه‌ای که پس از پردازش صف کامل می‌شود.</returns>
        public async Task ForceSyncAsync()
        {
            ThrowIfDisposed();

            _logger.LogInformation("FORCE SYNC triggered.");

            // ForceSync intentionally bypasses backoff.
            await ProcessQueueAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// فاصله پایه حلقه همگام‌سازی را در زمان اجرا به‌روز می‌کند.
        /// </summary>
        /// <param name="intervalMinutes">فاصله جدید به دقیقه.</param>
        public void UpdateSyncInterval(double intervalMinutes)
        {
            ThrowIfDisposed();

            _baseIntervalMinutes = intervalMinutes > 0 ? intervalMinutes : 1;

            _logger.LogInformation(
                "SyncEngine interval updated to {Minutes} minute(s).",
                _baseIntervalMinutes.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// حلقه ناهمگام همگام‌سازی است: یک دور فوری اجرا می‌کند و سپس با PeriodicTimer
        /// دورهای بعدی را با رعایت backoff زمان‌بندی می‌نماید.
        /// </summary>
        /// <param name="ct">توکن لغو حلقه هنگام توقف موتور.</param>
        /// <returns>وظیفه‌ای که تا پایان حلقه زنده می‌ماند.</returns>
        private async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                // First pass immediately (preserves the previous Start() "fire initial sync").
                try
                {
                    await ProcessQueueAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sync initial pass error.");
                }

                while (!ct.IsCancellationRequested)
                {
                    TimeSpan interval = TimeSpan.FromMinutes(_baseIntervalMinutes <= 0 ? 1 : _baseIntervalMinutes);
                    using var timer = new PeriodicTimer(interval);

                    try
                    {
                        await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Backoff active? skip this tick (predictable; next tick retries).
                    if (DateTime.UtcNow < _backoffUntilUtc)
                    {
                        _logger.LogInformation(
                            LogEvents.SyncSkippedId,
                            "Sync skipped: backoff active until {Until}.", _backoffUntilUtc.ToString("HH:mm:ss"));
                        continue;
                    }

                    try
                    {
                        await ProcessQueueAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Sync pass error.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SyncEngine loop failed unexpectedly.");
            }
        }

        /// <summary>
        /// یک دسته از رکوردهای pending را از SQLite می‌خواند، به کافکا می‌فرستد و
        /// پس از تحویل موفق حذف می‌کند. رکوردهای سمی حذف و خطاهای گذرا باعث backoff می‌شوند.
        /// </summary>
        /// <param name="cancellationToken">توکن لغو پردازش دسته.</param>
        /// <returns>وظیفه‌ای که پس از پایان دسته کامل می‌شود.</returns>
        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            if (_disposed)
                return;

            cancellationToken.ThrowIfCancellationRequested();

            if (!await _syncGate.WaitAsync(0).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    LogEvents.SyncSkippedId, "Sync skipped: a previous sync is still running.");
                return;
            }

            try
            {
                _metrics.SetGauge("ergonomy_outbox_pending_records",
                    "Number of records currently pending in the SQLite outbox.",
                    _localDb.PendingCount);

                if (_localDb.GetCapacityStatus() == CapacityStatus.Critical)
                {
                    _logger.LogInformation("Capacity critical; running retention before sync.");
                    _localDb.RunRetention();
                }

                var records = _localDb.GetPendingRecords(DefaultBatchSize);

                if (records == null || records.Count == 0)
                {
                    _consecutiveKafkaFailures = 0;
                    _backoffUntilUtc = DateTime.MinValue;
                    _metrics.SetGauge("ergonomy_sync_backoff_active", "1 if backoff is active.", 0);
                    return;
                }

                _logger.LogInformation(
                    LogEvents.SyncBatchStartId, "Syncing {Count} outbox record(s).", records.Count);
                _metrics.IncrementCounter("ergonomy_sync_batches_total", "Number of outbox batch sync passes.", 1);

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
                        await SendRecordToKafkaAsync(record, jsonOptions, cancellationToken).ConfigureAwait(false);

                        _localDb.DeleteRecord(record.Id);

                        _metrics.IncrementCounter(
                            "ergonomy_sync_records_sent_total",
                            "Records successfully delivered to Kafka.",
                            1,
                            new Dictionary<string, string> { ["target"] = record.TargetTable });

                        _logger.LogInformation(
                            LogEvents.SyncBatchCompleteId,
                            "Data delivered to Kafka and removed from queue. Target={Target}", record.TargetTable);
                    }
                    catch (JsonException ex)
                    {
                        HandlePoisonRecord(record.Id, record.TargetTable, "invalid-json");
                    }
                    catch (NotSupportedException ex)
                    {
                        HandlePoisonRecord(record.Id, record.TargetTable, "unsupported-payload");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Preserve the record; it was not deleted before the send completed.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        anyTransientFailure = true;

                        _logger.LogWarning(LogEvents.KafkaSendFailureId, ex,
                            "Kafka delivery failed; record remains pending. Target={Target}", record.TargetTable);
                    }
                }

                ApplyBackoff(anyTransientFailure);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SyncEngine fatal error.");
            }
            finally
            {
                _syncGate.Release();
            }
        }

        /// <summary>
        /// در صورت شکست گذرای کافکا، زمان backoff نمایی را محاسبه و متریک مربوط را به‌روز می‌کند.
        /// </summary>
        /// <param name="anyTransientFailure">اگر در دسته فعلی خطای گذرا رخ داده باشد true است.</param>
        private void ApplyBackoff(bool anyTransientFailure)
        {
            if (anyTransientFailure)
            {
                _consecutiveKafkaFailures++;

                double backoffMs = TimeSpan.FromMinutes(_baseIntervalMinutes <= 0 ? 1 : _baseIntervalMinutes).TotalMilliseconds
                                   * Math.Pow(2, _consecutiveKafkaFailures);
                backoffMs = Math.Min(backoffMs, MaxBackoff.TotalMilliseconds);

                _backoffUntilUtc = DateTime.UtcNow.AddMilliseconds(backoffMs);
                _metrics.SetGauge("ergonomy_sync_backoff_active", "1 if backoff is active.", 1);

                _logger.LogWarning(
                    LogEvents.SyncRetryBackoffId,
                    "Kafka backoff: next attempt in {Seconds}s (failure #{Failures}).",
                    Math.Round(backoffMs / 1000), _consecutiveKafkaFailures);
            }
            else
            {
                _consecutiveKafkaFailures = 0;
                _backoffUntilUtc = DateTime.MinValue;
                _metrics.SetGauge("ergonomy_sync_backoff_active", "1 if backoff is active.", 0);
            }
        }

        /// <summary>
        /// بر اساس TargetTable، payload را از JSON بازسازی کرده و به تاپیک مناسب کافکا ارسال می‌کند.
        /// </summary>
        /// <param name="record">رکورد صف محلی.</param>
        /// <param name="jsonOptions">گزینه‌های بازسازی JSON.</param>
        /// <param name="cancellationToken">توکن لغو ارسال.</param>
        /// <returns>وظیفه‌ای که پس از تحویل به کافکا کامل می‌شود.</returns>
        private async Task SendRecordToKafkaAsync(
            SyncRecord record,
            JsonSerializerOptions jsonOptions,
            CancellationToken cancellationToken)
        {
            switch (record.TargetTable)
            {
                case QueueTargets.AdvancedSystemMetrics:
                {
                    var metrics = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        record.Payload, jsonOptions);

                    if (metrics == null)
                        throw new JsonException("Advanced system metrics payload was null.");

                    AppLogNormalizer.NormalizeDictionary(metrics, normalizeLogLevel: false);
                    await _kafkaConnect.SendSystemMetricsAsync(record.MessageId, metrics, cancellationToken).ConfigureAwait(false);
                    break;
                }

                case QueueTargets.UserActivity:
                {
                    var activity = JsonSerializer.Deserialize<UserActivityPayload>(
                        record.Payload, jsonOptions);

                    if (activity == null)
                        throw new JsonException("User activity payload was null.");

                    await _kafkaConnect.SendUserActivityAsync(record.MessageId, activity, cancellationToken).ConfigureAwait(false);
                    break;
                }

                case QueueTargets.AppLogs:
                {
                    var logData = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        record.Payload, jsonOptions);

                    if (logData == null)
                        throw new JsonException("App log payload was null.");

                    AppLogNormalizer.NormalizeDictionary(logData, normalizeLogLevel: true);
                    await _kafkaConnect.SendAppLogAsync(record.MessageId, logData, cancellationToken).ConfigureAwait(false);
                    break;
                }

                default:
                    throw new NotSupportedException($"Unknown TargetTable '{record.TargetTable}'.");
            }
        }

        /// <summary>
        /// رکورد سمی با payload نامعتبر را از صف SQLite حذف کرده و شمارنده پرومتئوس را افزایش می‌دهد.
        /// </summary>
        /// <param name="recordId">شناسه رکورد در صف.</param>
        /// <param name="targetTable">جدول مقصد برای برچسب متریک.</param>
        /// <param name="reason">دلیل مسموم بودن رکورد.</param>
        private void HandlePoisonRecord(Guid recordId, string targetTable, string reason)
        {
            _metrics.IncrementCounter(
                "ergonomy_sync_poison_records_total",
                "Poison outbox records removed.",
                1,
                new Dictionary<string, string> { ["target"] = targetTable });

            _logger.LogWarning(
                LogEvents.SyncPoisonRecordId,
                "Poison outbox record removed. Target={Target}, ReasonCategory={ReasonCategory}",
                targetTable, "invalid-payload");

            _localDb.DeleteRecord(recordId);
        }

        /// <summary>
        /// اگر موتور آزاد شده باشد، از ادامه عملیات جلوگیری می‌کند.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SyncEngine));
        }

        /// <summary>
        /// حلقه همگام‌سازی را متوقف کرده و درگاه همپوشانی را آزاد می‌کند.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            Stop("disposed");
            _syncGate.Dispose();
        }
    }
}
