using System;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using Ergonomy.Database; // برای دسترسی به کلاس SyncRecord 

namespace Ergonomy
{
    /// <summary>
    /// این کلاس نقش بافر محلی (Queue) را دارد. 
    /// داده‌ها ابتدا اینجا ذخیره می‌شوند و سپس توسط موتور همگام‌ساز (SyncEngine) به کافکا ارسال و از اینجا پاک می‌شوند.
    /// </summary>
    public class LocalDatabaseManager
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public LocalDatabaseManager()
        {
            // دیتابیس محلی در پوشه کنار فایل اجرایی برنامه ساخته می‌شود
            _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ergonomy_local.db");
            _connectionString = $"Data Source={_dbPath}";
            
            InitializeDatabase();
        }

        // ساخت جدول صف (Queue) در صورت عدم وجود
        private void InitializeDatabase()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // فیلد target_table اکنون نمایانگر نام Topic در کافکا خواهد بود
            string createTableQuery = @"
                CREATE TABLE IF NOT EXISTS sync_queue (
                    id TEXT PRIMARY KEY,
                    target_table TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                );";

            using var command = new SqliteCommand(createTableQuery, connection);
            command.ExecuteNonQuery();
        }

        // 🌟 متدی جامع برای ذخیره هر نوع دیتایی در صف محلی (اکتیویتی، متریک، لاگ)
        public void SaveToLocalQueue(string targetTableName, object dataObject)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                string insertQuery = @"
                    INSERT INTO sync_queue (id, target_table, payload) 
                    VALUES (@id, @target_table, @payload);";

                using var command = new SqliteCommand(insertQuery, connection);
                command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("@target_table", targetTableName);
                
                // 🌟 رفع مشکل Double-Serialization در اینجا
                string jsonData;
                if (dataObject is string alreadyJsonString)
                {
                    jsonData = alreadyJsonString; // اگر از قبل رشته است، دست نزن
                }
                else
                {
                    var jsonOptions = new JsonSerializerOptions 
                    { 
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals 
                    };
                    jsonData = JsonSerializer.Serialize(dataObject, jsonOptions); // اگر دیکشنری یا آبجکت است، سریالایز کن
                }

                command.Parameters.AddWithValue("@payload", jsonData);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving to local DB: {ex.Message}");
            }
        }


        // 🌟 خواندن رکوردهای معلق برای ارسال به کافکا (به ترتیب قدیمی‌ترین)
        public List<SyncRecord> GetPendingRecords(int limit = 50)
        {
            var records = new List<SyncRecord>();
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                
                // بر اساس زمان مرتب می‌کنیم (FIFO)
                string query = "SELECT id, target_table, payload FROM sync_queue ORDER BY created_at ASC LIMIT @limit";
                
                using var command = new SqliteCommand(query, connection);
                command.Parameters.AddWithValue("@limit", limit);
                
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(new SyncRecord
                    {
                        Id = Guid.Parse(reader.GetString(0)), 
                        TargetTable = reader.GetString(1), // معادل Topic در کافکا
                        Payload = reader.GetString(2)
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading from local queue: {ex.Message}");
            }
            return records;
        }

        // 🌟 حذف رکورد از بافر محلی (پس از تایید موفقیت‌آمیز بودن ارسال به کافکا)
        public void DeleteRecord(Guid id)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                
                string query = "DELETE FROM sync_queue WHERE id = @id";
                using var command = new SqliteCommand(query, connection);
                command.Parameters.AddWithValue("@id", id.ToString());
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting record {id} from local queue: {ex.Message}");
            }
        }
    }
}
