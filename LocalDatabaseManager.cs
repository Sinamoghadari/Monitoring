using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Data.Sqlite;
using Ergonomy.Configuration;
using Ergonomy.Database;

namespace Ergonomy.Database
{
    public static class QueueTargets
    {
        public const string AdvancedSystemMetrics = "advanced_system_metrics";
        public const string UserActivity = "user_activity";
        public const string AppLogs = "app_logs";
    }

    public enum TargetPriority
    {
        Critical = 0,
        Medium = 1,
        Low = 2
    }

    public enum CapacityStatus
    {
        Normal = 0,
        Warning = 1,
        Critical = 2
    }

    public enum OutboxSaveResult
    {
        Saved = 0,
        DroppedLowPriority = 1,
        Failed = 2
    }

    public readonly struct RetentionResult
    {
        public int DeletedByAge { get; }
        public int DeletedByCapacity { get; }

        public RetentionResult(int deletedByAge, int deletedByCapacity)
        {
            DeletedByAge = deletedByAge;
            DeletedByCapacity = deletedByCapacity;
        }
    }

    public sealed class LocalDatabaseManager : IDisposable
    {
        private const int DefaultBusyTimeoutMilliseconds = 5_000;
        private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(5);

        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly OutboxSettings _settings;
        private System.Timers.Timer? _retentionTimer;

        // ظرفیت (کش‌شده در constructor برای پرهیز از محاسبه‌ی تکراری)
        private readonly int _maxRecords;
        private readonly double _maxDbBytes;
        private readonly double _warningThreshold;
        private readonly double _criticalThreshold;

        // شمارنده‌های درون‌حافظه‌ای — مسیر hot را O(1) نگه می‌دارند
        private long _pendingCount;
        private long _droppedLowPriorityCount;
        private long _deletedByAgeCount;
        private long _deletedByCapacityCount;

        private CapacityStatus _cachedStatus = CapacityStatus.Normal;
        private DateTime _lastStatusRefreshUtc = DateTime.MinValue;

        private bool _disposed;

        public LocalDatabaseManager()
            : this(new OutboxSettings(), new SqliteOutboxConnectionProvider())
        {
        }

        public LocalDatabaseManager(OutboxSettings settings, SqliteOutboxConnectionProvider connectionProvider)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _maxRecords = _settings.MaxRecords > 0 ? _settings.MaxRecords : 100_000;
            _maxDbBytes = _settings.MaxDbMb > 0
                ? _settings.MaxDbMb * 1024.0 * 1024.0
                : 500.0 * 1024.0 * 1024.0;
            _warningThreshold = _settings.WarningThreshold;
            _criticalThreshold = _settings.CriticalThreshold;

            if (connectionProvider == null) throw new ArgumentNullException(nameof(connectionProvider));
            _dbPath = connectionProvider.DatabasePath;
            _connectionString = connectionProvider.ConnectionString;

            InitializeDatabase();
            ReconcileCount();

            if (_settings.RetentionCheckIntervalSeconds > 0)
            {
                _retentionTimer = new System.Timers.Timer(
                    _settings.RetentionCheckIntervalSeconds * 1000)
                {
                    AutoReset = true
                };
                _retentionTimer.Elapsed += OnRetentionTimerElapsed;
                _retentionTimer.Start();
            }

            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] SQLite outbox initialized.");
        }

        private void OnRetentionTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                RunRetention();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] Retention timer error.");
            }
        }

        private SqliteConnection CreateOpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var pragmaCommand = connection.CreateCommand();

            pragmaCommand.CommandText = $@"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = {DefaultBusyTimeoutMilliseconds};";

            pragmaCommand.ExecuteNonQuery();

            return connection;
        }

        private void InitializeDatabase()
        {
            using var connection = CreateOpenConnection();

            using var createCommand = connection.CreateCommand();

            createCommand.CommandText = @"
                CREATE TABLE IF NOT EXISTS sync_queue (
                    id TEXT PRIMARY KEY,
                    message_id TEXT NOT NULL,
                    target_table TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    created_at TEXT NOT NULL
                        DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                );";

            createCommand.ExecuteNonQuery();

            EnsureMessageIdColumn(connection);

            using var indexCommand = connection.CreateCommand();

            indexCommand.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_sync_queue_created_at
                ON sync_queue(created_at);

                CREATE INDEX IF NOT EXISTS idx_sync_queue_target_created_at
                ON sync_queue(target_table, created_at);

                CREATE UNIQUE INDEX IF NOT EXISTS idx_sync_queue_message_id
                ON sync_queue(message_id);";

            indexCommand.ExecuteNonQuery();
        }

        private static void EnsureMessageIdColumn(SqliteConnection connection)
        {
            var existingColumns = GetColumnNames(connection, "sync_queue");

            if (!existingColumns.Contains("message_id"))
            {
                using var addColumnCommand = connection.CreateCommand();

                addColumnCommand.CommandText = @"
                    ALTER TABLE sync_queue
                    ADD COLUMN message_id TEXT;";

                addColumnCommand.ExecuteNonQuery();
            }

            using var backfillCommand = connection.CreateCommand();

            backfillCommand.CommandText = @"
                UPDATE sync_queue
                SET message_id = id
                WHERE message_id IS NULL
                   OR TRIM(message_id) = '';";

            backfillCommand.ExecuteNonQuery();
        }

        private static HashSet<string> GetColumnNames(
            SqliteConnection connection,
            string tableName)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var command = connection.CreateCommand();

            command.CommandText = $"PRAGMA table_info({tableName});";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                string columnName = reader.GetString(1);
                columns.Add(columnName);
            }

            return columns;
        }

        // ─────────────────────────────────────────────
        //  اولویت target — مبنای سیاست drop در بحران
        // ─────────────────────────────────────────────
        private static TargetPriority GetTargetPriority(string targetTable)
        {
            switch (targetTable)
            {
                case QueueTargets.AdvancedSystemMetrics:
                    return TargetPriority.Critical;
                case QueueTargets.UserActivity:
                    return TargetPriority.Medium;
                case QueueTargets.AppLogs:
                    return TargetPriority.Low;
                default:
                    // target ناشناخته را متوسط فرض می‌کنیم تا در بحران drop شود،
                    // نه اینکه بی‌نهایت جمع شود.
                    return TargetPriority.Medium;
            }
        }

        // ─────────────────────────────────────────────
        //  اندازه‌گیری ظرفیت
        // ─────────────────────────────────────────────
        public long GetDatabaseSizeBytes()
        {
            long total = 0;

            try
            {
                if (File.Exists(_dbPath))
                    total += new FileInfo(_dbPath).Length;

                string walPath = _dbPath + "-wal";
                if (File.Exists(walPath))
                    total += new FileInfo(walPath).Length;
            }
            catch
            {
                // در صورت خطای IO، صفر برمی‌گردد و سیاست به‌سوی safe-side می‌رود.
            }

            return total;
        }

        public long PendingCount => Interlocked.Read(ref _pendingCount);
        public string DatabasePath => _dbPath;
        public long DroppedLowPriorityCount => Interlocked.Read(ref _droppedLowPriorityCount);
        public long DeletedByAgeCount => Interlocked.Read(ref _deletedByAgeCount);
        public long DeletedByCapacityCount => Interlocked.Read(ref _deletedByCapacityCount);

        public CapacityStatus GetCapacityStatus(bool forceRefresh = false)
        {
            if (!forceRefresh &&
                DateTime.UtcNow - _lastStatusRefreshUtc < StatusRefreshInterval)
            {
                return _cachedStatus;
            }

            double recordRatio = _maxRecords > 0
                ? (double)Interlocked.Read(ref _pendingCount) / _maxRecords
                : 0;

            double sizeRatio = _maxDbBytes > 0
                ? GetDatabaseSizeBytes() / _maxDbBytes
                : 0;

            double ratio = Math.Max(recordRatio, sizeRatio);

            _cachedStatus = ratio >= _criticalThreshold
                ? CapacityStatus.Critical
                : ratio >= _warningThreshold
                    ? CapacityStatus.Warning
                    : CapacityStatus.Normal;

            _lastStatusRefreshUtc = DateTime.UtcNow;

            return _cachedStatus;
        }

        // ─────────────────────────────────────────────
        //  ذخیره با gating اولویت
        // ─────────────────────────────────────────────
        public OutboxSaveResult SaveUserActivity(
            string targetTableName,
            object dataObject)
        {
            if (string.IsNullOrWhiteSpace(targetTableName))
            {
                throw new ArgumentException(
                    "Target table name cannot be empty.",
                    nameof(targetTableName));
            }

            string normalizedTargetTable = targetTableName.Trim();

            // در وضعیت بحرانی فقط target حیاتی ذخیره می‌شود.
            if (GetCapacityStatus() == CapacityStatus.Critical &&
                GetTargetPriority(normalizedTargetTable) != TargetPriority.Critical)
            {
                Interlocked.Increment(ref _droppedLowPriorityCount);

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ⛔ Outbox critical: dropping " +
                    $"low-priority target '{normalizedTargetTable}'.");

                return OutboxSaveResult.DroppedLowPriority;
            }

            try
            {
                string jsonData = SerializePayload(dataObject);

                string recordId = Guid.NewGuid().ToString();
                string messageId = recordId;

                using var connection = CreateOpenConnection();
                using var command = connection.CreateCommand();

                command.CommandText = @"
                    INSERT INTO sync_queue
                    (id, message_id, target_table, payload)
                    VALUES
                    (@id, @messageId, @targetTable, @payload);";

                command.Parameters.AddWithValue("@id", recordId);
                command.Parameters.AddWithValue("@messageId", messageId);
                command.Parameters.AddWithValue("@targetTable", normalizedTargetTable);
                command.Parameters.AddWithValue("@payload", jsonData);

                int affectedRows = command.ExecuteNonQuery();

                if (affectedRows != 1)
                {
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] SQLite outbox insert failed. " +
                        $"Target: {normalizedTargetTable} | Affected rows: {affectedRows}");

                    return OutboxSaveResult.Failed;
                }

                Interlocked.Increment(ref _pendingCount);

                return OutboxSaveResult.Saved;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] SQLite outbox write failed. " +
                    $"Target: {targetTableName} | Error.");

                return OutboxSaveResult.Failed;
            }
        }

        public List<SyncRecord> GetPendingRecords(int limit = 50)
        {
            var records = new List<SyncRecord>();

            if (limit <= 0)
                limit = 50;

            try
            {
                using var connection = CreateOpenConnection();
                using var command = connection.CreateCommand();

                command.CommandText = @"
                    SELECT id, message_id, target_table, payload, created_at
                    FROM sync_queue
                    ORDER BY created_at ASC
                    LIMIT @limit;";

                command.Parameters.AddWithValue("@limit", limit);

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    string rawId = reader.GetString(0);

                    if (!Guid.TryParse(rawId, out Guid id))
                    {
                        Console.WriteLine(
                            $"[{DateTime.Now:HH:mm:ss}] Invalid queue record ID. " +
                            $"Record remains in queue: {rawId}");
                        continue;
                    }

                    string messageId = reader.IsDBNull(1) ? rawId : reader.GetString(1);

                    if (string.IsNullOrWhiteSpace(messageId))
                        messageId = rawId;

                    string createdAt = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

                    records.Add(new SyncRecord
                    {
                        Id = id,
                        MessageId = messageId,
                        TargetTable = reader.GetString(2),
                        Payload = reader.GetString(3),
                        CreatedAt = createdAt
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] SQLite outbox read failed.");
            }

            return records;
        }

        public bool DeleteRecord(Guid id)
        {
            try
            {
                using var connection = CreateOpenConnection();
                using var command = connection.CreateCommand();

                command.CommandText = @"
                    DELETE FROM sync_queue
                    WHERE id = @id;";

                command.Parameters.AddWithValue("@id", id.ToString());

                int affectedRows = command.ExecuteNonQuery();

                if (affectedRows == 1)
                {
                    Interlocked.Decrement(ref _pendingCount);
                    return true;
                }

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] SQLite outbox delete affected " +
                    $"{affectedRows} rows.");

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] SQLite outbox delete failed. " +
                    $"Record deletion failed.");

                return false;
            }
        }

        // ─────────────────────────────────────────────
        //  Retention
        // ─────────────────────────────────────────────
        public RetentionResult RunRetention()
        {
            int deletedByAge = DeleteExpiredRecords();
            Interlocked.Add(ref _deletedByAgeCount, deletedByAge);

            int deletedByCapacity = 0;

            // بعد از حذف سنی، اگر هنوز بحرانی است، ظرفیت را با حذف کم‌ارزش‌ها کم کن.
            if (GetCapacityStatus(forceRefresh: true) == CapacityStatus.Critical)
            {
                deletedByCapacity = DeleteLowPriorityToRelieve();
                Interlocked.Add(ref _deletedByCapacityCount, deletedByCapacity);
            }

            // اصلاح دوره‌ای شمارنده برای جبران هر drift.
            ReconcileCount();

            if (deletedByAge > 0 || deletedByCapacity > 0)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] 🧹 Retention: " +
                    $"{deletedByAge} by age, {deletedByCapacity} by capacity. " +
                    $"Pending now: {PendingCount}");
            }

            return new RetentionResult(deletedByAge, deletedByCapacity);
        }

        private int DeleteExpiredRecords()
        {
            // cutoff با همان فرمت strftime ساخته می‌شود تا مقایسه‌ی رشتهای
            // از ایندکس created_at استفاده کند.
            string cutoff = DateTime.UtcNow
                .AddDays(-_settings.MaxRecordAgeDays)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

            try
            {
                using var connection = CreateOpenConnection();
                using var command = connection.CreateCommand();

                command.CommandText = @"
                    DELETE FROM sync_queue
                    WHERE created_at < @cutoff;";

                command.Parameters.AddWithValue("@cutoff", cutoff);

                int deleted = command.ExecuteNonQuery();

                if (deleted > 0)
                    Interlocked.Add(ref _pendingCount, -deleted);

                return deleted;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] Retention (age) failed.");
                return 0;
            }
        }

        private int DeleteLowPriorityToRelieve()
        {
            // هدف: برگرداندن شمارنده به سطح Warning (نه لزوماً Normal).
            long targetCount = (long)(_maxRecords * _warningThreshold);
            long excess = Interlocked.Read(ref _pendingCount) - targetCount;

            if (excess <= 0)
                return 0;

            int deleted = 0;

            // اول کم‌ارزش‌ترین (app_logs)، بعد متوسط (user_activity).
            deleted += DeleteOldestByTarget(QueueTargets.AppLogs, (int)Math.Min(excess, int.MaxValue));

            if (deleted < excess)
            {
                deleted += DeleteOldestByTarget(
                    QueueTargets.UserActivity,
                    (int)Math.Min(excess - deleted, int.MaxValue));
            }

            return deleted;
        }

        private int DeleteOldestByTarget(string targetTable, int count)
        {
            if (count <= 0)
                return 0;

            try
            {
                using var connection = CreateOpenConnection();
                using var command = connection.CreateCommand();

                // SQLite از DELETE ... LIMIT پشتیبانی نمی‌کند؛
                // بنابراین از subquery با LIMIT استفاده می‌کنیم.
                command.CommandText = @"
                    DELETE FROM sync_queue
                    WHERE id IN (
                        SELECT id
                        FROM sync_queue
                        WHERE target_table = @target
                        ORDER BY created_at ASC
                        LIMIT @count
                    );";

                command.Parameters.AddWithValue("@target", targetTable);
                command.Parameters.AddWithValue("@count", count);

                int deleted = command.ExecuteNonQuery();

                if (deleted > 0)
                    Interlocked.Add(ref _pendingCount, -deleted);

                return deleted;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] Retention (capacity) failed for " +
                    $"'{targetTable}'.");
                return 0;
            }
        }

        private void ReconcileCount()
        {
            try
            {
                using var connection = CreateOpenConnection();
                using var command = connection.CreateCommand();

                command.CommandText = "SELECT COUNT(*) FROM sync_queue;";

                long count = Convert.ToInt64(command.ExecuteScalar());

                Interlocked.Exchange(ref _pendingCount, count);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] Count reconcile failed.");
            }
        }

        private static string SerializePayload(object dataObject)
        {
            if (dataObject == null)
                return "{}";

            if (dataObject is string alreadyJsonString)
                return alreadyJsonString;

            var jsonOptions = new JsonSerializerOptions
            {
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                PropertyNamingPolicy = null,
                WriteIndented = false
            };

            return JsonSerializer.Serialize(dataObject, jsonOptions);
        }


        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            _retentionTimer?.Stop();
            _retentionTimer?.Dispose();
        }
    }
}
