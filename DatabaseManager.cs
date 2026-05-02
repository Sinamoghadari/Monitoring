using Npgsql;
using System;
using System.Data;
using System.IO; 
using System.Drawing; 
using System.Collections.Generic;
using Ergonomy.Configuration;
using System.Text.Json;
using Microsoft.Extensions.Configuration;



namespace Ergonomy.Database
{
    /// <summary>
    /// این کلاس صرفاً برای خواندن تنظیمات، تصاویر و مدیریت دستورات ریموت از PostgreSQL استفاده می‌شود.
    /// ارسال داده‌های عملیاتی (لاگ، اکتیویتی و متریک) به کافکا منتقل شده است.
    /// </summary>
    public class DatabaseManager : IDisposable
    {
        private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .Build();
        private NpgsqlConnection? _connection;
        private readonly string _host;
        private readonly string _database;
        private readonly string _user;
        private readonly string _password;
        private readonly int _port;

        public bool IsConnected => _connection != null && _connection.State == ConnectionState.Open;

        public DatabaseManager(string host, string database, string user, string password, int port)
        {
            _host = host;
            _database = database;
            _user = user;
            _password = password;
            _port = port;
        }

        // 🌟 اتصال به دیتابیس
        public bool Connect()
        {
            try
            {
                string connectionString = $"Host={_host};Port={_port};Username={_user};Password={_password};Database={_database}";
                _connection = new NpgsqlConnection(connectionString);
                _connection.Open();
                return true;
            }
            catch (Exception ex) 
            {
                Console.WriteLine($"❌ Error connecting to Postgres: {ex.Message}");
                return false;
            }
        }

        // 🌟 دریافت تنظیمات اصلی برنامه از دیتابیس آنلاین
        public AppSettings? GetSettingsFromDatabase()
        {
            try
            {
                string query = "SELECT settings_json FROM app_configuration LIMIT 1;";
                using var cmd = new NpgsqlCommand(query, _connection);
                var result = cmd.ExecuteScalar();
                
                if (result != null && result != DBNull.Value)
                {
                    string jsonString = result.ToString()!;
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<AppSettings>(jsonString, options);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Failed to load settings from DB: {ex.Message}");
            }
            return null; 
        }
        // چک کردن کاکشن به postgres
        public async Task CheckAndLogPostgresConnectionAsync(KafkaConnect kafkaConnect, string windowsUsername)
        {
            bool checkEnabled = _configuration.GetValue<bool>("AppSettings:CheckPostgresConnection");
            if (!checkEnabled) return;

            string connectionString = $"Host={_configuration["AppSettings:Database:Host"]};Port={_configuration["AppSettings:Database:Port"]};Database={_configuration["AppSettings:Database:Name"]};Username={_configuration["AppSettings:Database:User"]};Password={_configuration["AppSettings:Database:Password"]};";

            string statusMessage;
            string logLevel;

            try
            {
                using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();
                statusMessage = "PostgreSQL Connection is OK.";
                logLevel = "INFO";
            }
            catch (Exception ex)
            {
                statusMessage = $"PostgreSQL Connection Failed: {ex.Message}";
                logLevel = "ERROR";
            }

            if (kafkaConnect != null)
            {
                var pgLog = new
                {
                    Timestamp = DateTime.UtcNow,
                    LogLevel = logLevel,
                    Message = statusMessage,
                    WindowsUsername = windowsUsername,
                    MachineName = Environment.MachineName,
                    Category = "PostgresHealth"
                };
                await kafkaConnect.SendAppLogAsync(pgLog);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [PG STATUS] {statusMessage}");
            }
        }

        // 🌟 دریافت نام تمام عکس‌های موجود در دیتابیس
        public List<string> GetAllImageNames()
        {
            var names = new List<string>();
            if (!IsConnected) return names;

            try
            {
                string query = "SELECT image_name FROM alarm_images ORDER BY image_name";
                using var cmd = new NpgsqlCommand(query, _connection);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    names.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading image names from DB: {ex.Message}");
            }
            return names;
        }

        // 🌟 دریافت فایل باینری تصویر از دیتابیس و تبدیل آن به کلاس Image
        public Image? GetImageFromDatabase(string imageName)
        {
            if (!IsConnected) return null;

            try
            {
                string query = "SELECT image_data FROM alarm_images WHERE image_name = @imageName";
                using var cmd = new NpgsqlCommand(query, _connection);
                cmd.Parameters.AddWithValue("@imageName", imageName);
                var result = cmd.ExecuteScalar();
                
                if (result != null && result != DBNull.Value)
                {
                    byte[] imageData = (byte[])result;
                    using MemoryStream ms = new MemoryStream(imageData);
                    using Image img = Image.FromStream(ms);
                    return new Bitmap(img); 
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading image {imageName} from DB: {ex.Message}");
            }
            
            return null; 
        }

        // =========================================================
        // 🌟 بخش مدیریت دستورات ریموت (Remote Commands)
        // =========================================================

        // خواندن دستوراتی که وضعیت pending دارند
        public List<ClientCommand> GetPendingCommands(string computerName, string windowsUsername)
        {
            var commands = new List<ClientCommand>();
            if (!IsConnected) return commands;

            try
            {
                using var cmd = new NpgsqlCommand(@"
                    SELECT id, command 
                    FROM client_commands 
                    WHERE status = 'pending' 
                    AND (computer_name = @computerName OR windows_username = @windowsUsername)", _connection);
                
                cmd.Parameters.AddWithValue("computerName", string.IsNullOrEmpty(computerName) ? DBNull.Value : computerName);
                cmd.Parameters.AddWithValue("windowsUsername", string.IsNullOrEmpty(windowsUsername) ? DBNull.Value : windowsUsername);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    commands.Add(new ClientCommand
                    {
                        Id = reader.GetInt32(0),
                        Command = reader.GetString(1)
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] Error getting pending commands: {ex.Message}");
            }
            return commands;
        }

        // مارک کردن دستور به عنوان تاریخ گذشته (در صورت نیاز)
        public void MarkCommandAsOutdated(int commandId)
        {
            if (!IsConnected) return;
            try
            {
                using var cmd = new NpgsqlCommand("UPDATE client_commands SET status = 'outdated' WHERE id = @id", _connection);
                cmd.Parameters.AddWithValue("id", commandId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] Error marking command as outdated: {ex.Message}");
            }
        }

        // ثبت دستور به عنوان "اجرا شده" (بلافاصله پس از دریافت در کلاینت)
        public void MarkCommandAsExecuted(int commandId)
        {
            if (!IsConnected) return;
            try
            {
                string query = "UPDATE client_commands SET status = 'executed' WHERE id = @id";
                using var cmd = new NpgsqlCommand(query, _connection);
                cmd.Parameters.AddWithValue("id", commandId);
                int rowsAffected = cmd.ExecuteNonQuery();
                Console.WriteLine($"[DB] Command ID {commandId} updated. Rows affected: {rowsAffected}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Error updating command status: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }

    public class ClientCommand
    {
        public int Id { get; set; }
        public string Command { get; set; } = string.Empty;
    }
}
