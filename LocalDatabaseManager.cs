using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Ergonomy.Database
{
    public static class QueueTargets
    {
        public const string AdvancedSystemMetrics = "advanced_system_metrics";
        public const string UserActivity = "user_activity";
        public const string AppLogs = "app_logs";
    }

    public class LocalDatabaseManager
    {
        private const int DefaultBusyTimeoutMilliseconds = 5_000;

        private readonly string _dbPath;
        private readonly string _connectionString;

        public LocalDatabaseManager()
        {
            string dataDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData),
                "Ergonomy");

            Directory.CreateDirectory(dataDirectory);

            _dbPath = Path.Combine(dataDirectory, "ergonomy_local.db");

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true
            }.ToString();

            InitializeDatabase();

            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] 💾 SQLite outbox initialized: {_dbPath}");
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
                    target_table TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    created_at TEXT NOT NULL
                        DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                );

                CREATE INDEX IF NOT EXISTS idx_sync_queue_created_at
                ON sync_queue(created_at);

                CREATE INDEX IF NOT EXISTS idx_sync_queue_target_created_at
                ON sync_queue(target_table, created_at);";

            createCommand.ExecuteNonQuery();
        }

        /// <summary>
        /// ذخیره‌ی telemetry در SQLite Outbox.
        /// true: پیام با موفقیت durable شد.
        /// false: ذخیره انجام نشد و caller باید آن را لاگ یا monitor کند.
        /// </summary>
        public bool SaveToLocalQueue(string targetTableName, object dataObject)
        {
            if (string.IsNullOrWhiteSpace(targetTableName))
                throw new ArgumentException(
                    "Target table name cannot be empty.",
                    nameof(targetTableName));

            try
            {
                string jsonData = SerializePayload(dataObject);

                using var connection = CreateOpenConnection();
                using var command = connection.CreateCommand();

                command.CommandText = @"
                    INSERT INTO sync_queue (id, target_table, payload)
                    VALUES (@id, @targetTable, @payload);";

                command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("@targetTable", targetTableName.Trim());
                command.Parameters.AddWithValue("@payload", jsonData);

                int affectedRows = command.ExecuteNonQuery();

                if (affectedRows != 1)
                {
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] ❌ SQLite outbox insert failed. " +
                        $"Target: {targetTableName} | Affected rows: {affectedRows}");

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ SQLite outbox write failed. " +
                    $"Target: {targetTableName} | Error: {ex.Message}");

                return false;
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
                    SELECT id, target_table, payload
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
                            $"[{DateTime.Now:HH:mm:ss}] ⚠️ Invalid queue record ID. " +
                            $"Record remains in queue: {rawId}");

                        continue;
                    }

                    records.Add(new SyncRecord
                    {
                        Id = id,
                        TargetTable = reader.GetString(1),
                        Payload = reader.GetString(2)
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ SQLite outbox read failed: {ex.Message}");
            }

            return records;
        }

        /// <summary>
        /// حذف رکورد فقط پس از ACK موفق Kafka.
        /// </summary>
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
                    return true;

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ⚠️ SQLite outbox delete affected " +
                    $"{affectedRows} rows. RecordId: {id}");

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ SQLite outbox delete failed. " +
                    $"RecordId: {id} | Error: {ex.Message}");

                return false;
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
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            };

            return JsonSerializer.Serialize(dataObject, jsonOptions);
        }
    }
}
