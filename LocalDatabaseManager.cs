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
        private readonly string _dbPath;
        private readonly string _connectionString;

        public LocalDatabaseManager()
        {
            _dbPath = Path.Combine(AppContext.BaseDirectory, "ergonomy_local.db");
            _connectionString = $"Data Source={_dbPath};Cache=Shared";

            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var pragmaCommand = connection.CreateCommand();
            pragmaCommand.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA busy_timeout = 5000;";
            pragmaCommand.ExecuteNonQuery();

            using var createCommand = connection.CreateCommand();
            createCommand.CommandText = @"
                CREATE TABLE IF NOT EXISTS sync_queue (
                    id TEXT PRIMARY KEY,
                    target_table TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                );

                CREATE INDEX IF NOT EXISTS idx_sync_queue_created_at
                ON sync_queue(created_at);";
            createCommand.ExecuteNonQuery();
        }

        public void SaveToLocalQueue(string targetTableName, object dataObject)
        {
            if (string.IsNullOrWhiteSpace(targetTableName))
                throw new ArgumentException("Target table name cannot be empty.", nameof(targetTableName));

            try
            {
                string jsonData = SerializePayload(dataObject);

                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO sync_queue (id, target_table, payload)
                    VALUES (@id, @target_table, @payload);";

                command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("@target_table", targetTableName);
                command.Parameters.AddWithValue("@payload", jsonData);

                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LocalDatabaseManager] Error saving to local queue: {ex.Message}");
            }
        }

        public List<SyncRecord> GetPendingRecords(int limit = 50)
        {
            var records = new List<SyncRecord>();

            if (limit <= 0)
                limit = 50;

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

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
                    if (!Guid.TryParse(reader.GetString(0), out var id))
                    {
                        Console.WriteLine($"[LocalDatabaseManager] Invalid queue record id: {reader.GetString(0)}");
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
                Console.WriteLine($"[LocalDatabaseManager] Error reading local queue: {ex.Message}");
            }

            return records;
        }

        public void DeleteRecord(Guid id)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM sync_queue WHERE id = @id;";
                command.Parameters.AddWithValue("@id", id.ToString());

                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LocalDatabaseManager] Error deleting record {id}: {ex.Message}");
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
