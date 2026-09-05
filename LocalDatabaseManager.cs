using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Data.Sqlite;
using Ergonomy.Configuration;
using Ergonomy.Database;
using Ergonomy.Services;

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

        /// <summary>
        /// نتیجه یک دور نگهداری را با تعداد حذف سنی و حذف ظرفیتی می‌سازد.
        /// </summary>
        /// <param name="deletedByAge">تعداد رکوردهای حذف‌شده به‌خاطر سن.</param>
        /// <param name="deletedByCapacity">تعداد رکوردهای حذف‌شده به‌خاطر ظرفیت.</param>
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
        private readonly SensitiveFileProtector? _protector;
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

        /// <summary>
        /// مدیر outbox را با تنظیمات و مسیر پیش‌فرض ProgramData می‌سازد.
        /// </summary>
        public LocalDatabaseManager()
            : this(new OutboxSettings(), new SqliteOutboxConnectionProvider())
        {
        }

        public LocalDatabaseManager(OutboxSettings settings, SqliteOutboxConnectionProvider connectionProvider)
            : this(settings, connectionProvider, protector: null)
        {
        }

        /// <summary>
        /// مدیر outbox را با تنظیمات ظرفیت و ارائه‌دهنده مسیر SQLite می‌سازد،
        /// جدول صف را ایجاد کرده و تایمر نگهداری را در صورت نیاز شروع می‌کند.
        /// </summary>
        public LocalDatabaseManager(
            OutboxSettings settings,
            SqliteOutboxConnectionProvider connectionProvider,
            SensitiveFileProtector? protector)
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
            _protector = protector;

            try
            {
                InitializeDatabase();
                ReconcileCount();
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] SQLite outbox initialized.");
                StartupLog.Info("local DB initialized");
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ SQLite outbox initialization failed; tray will continue. {ex.Message}");
                StartupLog.Error("local DB initialization failed; tray will continue.", ex);
            }

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
        }

        /// <summary>
        /// در هر تیک تایمر نگهداری، سیاست حذف سنی و ظرفیتی را روی صف SQLite اجرا می‌کند.
        /// </summary>
        /// <param name="sender">منبع رویداد تایمر.</param>
        /// <param name="e">اطلاعات زمان وقوع تیک.</param>
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

        /// <summary>
        /// اتصال SQLite را با حالت WAL، timeout مشغول بودن و همگام‌سازی NORMAL باز می‌کند.
        /// </summary>
        /// <returns>اتصال باز و آماده‌به‌کار.</returns>
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

        /// <summary>
        /// جدول sync_queue و ایندکس‌های created_at و message_id را در صورت نبود ایجاد می‌کند.
        /// </summary>
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

        /// <summary>
        /// ستون message_id را برای پایگاه‌های قدیمی اضافه کرده و مقادیر خالی را از id پر می‌کند.
        /// </summary>
        /// <param name="connection">اتصال باز به پایگاه outbox.</param>
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

        /// <summary>
        /// نام ستون‌های یک جدول SQLite را از PRAGMA table_info می‌خواند.
        /// </summary>
        /// <param name="connection">اتصال باز به پایگاه.</param>
        /// <param name="tableName">نام جدول مورد بررسی.</param>
        /// <returns>مجموعه نام ستون‌ها.</returns>
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
        /// <summary>
        /// اولویت هدف صف را تعیین می‌کند تا در بحران ظرفیت فقط رکوردهای حیاتی حفظ شوند.
        /// </summary>
        /// <param name="targetTable">نام جدول مقصد در صف.</param>
        /// <returns>اولویت Critical، Medium یا Low.</returns>
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
        /// <summary>
        /// اندازه فایل پایگاه و فایل WAL را برای محاسبه نسبت ظرفیت برمی‌گرداند.
        /// </summary>
        /// <returns>حجم تقریبی پایگاه به بایت.</returns>
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

        /// <summary>
        /// وضعیت ظرفیت outbox را بر اساس نسبت تعداد رکورد و حجم فایل محاسبه می‌کند
        /// و برای کاهش IO نتیجه را برای چند ثانیه کش می‌نماید.
        /// </summary>
        /// <param name="forceRefresh">اگر true باشد کش وضعیت نادیده گرفته می‌شود.</param>
        /// <returns>وضعیت Normal، Warning یا Critical.</returns>
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
        /// <summary>
        /// شیء داده را به JSON تبدیل کرده و در صف SQLite درج می‌کند.
        /// در وضعیت بحرانی فقط هدف‌های Critical پذیرفته می‌شوند.
        /// </summary>
        /// <param name="targetTableName">نام جدول مقصد برای مسیریابی SyncEngine.</param>
        /// <param name="dataObject">شیء یا رشته JSON قابل سریال‌سازی.</param>
        /// <returns>نتیجه ذخیره، حذف اولویت پایین یا شکست.</returns>
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

        /// <summary>
        /// قدیمی‌ترین رکوردهای pending را برای ارسال به کافکا از صف SQLite می‌خواند.
        /// </summary>
        /// <param name="limit">حداکثر تعداد رکورد در دسته.</param>
        /// <returns>فهرست رکوردهای آماده ارسال.</returns>
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

        /// <summary>
        /// یک رکورد تحویل‌شده یا سمی را از صف SQLite حذف کرده و شمارنده pending را کاهش می‌دهد.
        /// </summary>
        /// <param name="id">شناسه رکورد در جدول sync_queue.</param>
        /// <returns>اگر دقیقاً یک ردیف حذف شد true است.</returns>
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
        /// <summary>
        /// سیاست نگهداری را اجرا می‌کند: ابتدا رکوردهای منقضی و سپس در صورت بحران،
        /// قدیمی‌ترین رکوردهای کم‌اولویت را حذف می‌نماید.
        /// </summary>
        /// <returns>تعداد حذف‌های سنی و ظرفیتی.</returns>
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

        /// <summary>
        /// رکوردهای قدیمی‌تر از MaxRecordAgeDays را با مقایسه رشته‌ای created_at حذف می‌کند.
        /// </summary>
        /// <returns>تعداد ردیف‌های حذف‌شده.</returns>
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

        /// <summary>
        /// برای خروج از وضعیت بحرانی، ابتدا app_logs و سپس user_activity را از قدیمی‌ترین‌ها حذف می‌کند.
        /// </summary>
        /// <returns>تعداد کل رکوردهای حذف‌شده برای کاهش ظرفیت.</returns>
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

        /// <summary>
        /// قدیمی‌ترین رکوردهای یک هدف مشخص را تا سقف داده‌شده از صف SQLite حذف می‌کند.
        /// </summary>
        /// <param name="targetTable">هدف صف برای فیلتر حذف.</param>
        /// <param name="count">حداکثر تعداد حذف.</param>
        /// <returns>تعداد ردیف‌های حذف‌شده.</returns>
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

        /// <summary>
        /// شمارنده درون‌حافظه‌ای pending را با COUNT واقعی جدول همگام می‌کند تا drift اصلاح شود.
        /// </summary>
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

        /// <summary>
        /// شیء ورودی را به JSON فشرده تبدیل می‌کند؛ اگر از قبل رشته باشد همان را برمی‌گرداند.
        /// </summary>
        /// <param name="dataObject">شیء یا رشته JSON.</param>
        /// <returns>بدنه JSON قابل ذخیره در صف.</returns>
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


        /// <summary>
        /// تایمر نگهداری outbox را متوقف و آزاد می‌کند.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            _retentionTimer?.Stop();
            _retentionTimer?.Dispose();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }
            catch
            {
            }

            try
            {
                SqliteConnection.ClearAllPools();
            }
            catch
            {
            }

            try
            {
                _protector?.LockDatabase(_dbPath);
            }
            catch
            {
            }
        }
    }
}
