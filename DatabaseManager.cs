using Npgsql;
using System;
using System.Data;
using System.IO;
using System.Drawing;
using System.Collections.Generic;
using Ergonomy.Configuration;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System.Globalization;

namespace Ergonomy.Database
{
    public sealed class DatabaseManager
    {
        private readonly LocalDatabaseManager _localDb;

        // توجه: شما در کل پروژه در حال حذف appsettings هستید،
        // اما فعلاً این کلاس هنوز از appsettings.json می‌خواند.
        // دست نزنیم تا رفتار فعلی نشکند؛ بعداً باید با Env Vars جایگزین شود.
        private readonly IConfiguration _configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        private readonly string _host;
        private readonly string _database;
        private readonly string _user;
        private readonly string _password;
        private readonly int _port;

        private string GetConnectionString() =>
            $"Host={_host};Port={_port};Username={_user};Password={_password};Database={_database}";

        public DatabaseManager(
            string host,
            string database,
            string user,
            string password,
            int port,
            LocalDatabaseManager? localDb = null)
        {
            _host = host;
            _database = database;
            _user = user;
            _password = password;
            _port = port;

            _localDb = localDb ?? new LocalDatabaseManager();
        }

        public AppSettings? GetSettingsFromDatabase()
        {
            try
            {
                using var conn = new NpgsqlConnection(GetConnectionString());
                conn.Open();

                const string query = "SELECT settings_json FROM app_configuration LIMIT 1;";
                using var cmd = new NpgsqlCommand(query, conn);

                object? result = cmd.ExecuteScalar();

                if (result != null && result != DBNull.Value)
                {
                    string jsonString = result.ToString()!;
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };

                    return JsonSerializer.Deserialize<AppSettings>(jsonString, options);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Failed to load settings from DB: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Health check اتصال PostgreSQL.
        /// طبق معماری جدید، این لاگ مستقیم Kafka نمی‌رود و وارد SQLite Outbox می‌شود
        /// تا durable باشد و SyncEngine آن را با MessageId ثابت ارسال کند.
        /// </summary>
        public async Task CheckAndLogPostgresConnectionAsync(
            string windowsUsername)
        {
            bool checkEnabled = _configuration.GetValue<bool>(
                "AppSettings:CheckPostgresConnection");

            if (!checkEnabled)
                return;

            string statusMessage;
            string logLevel;

            try
            {
                using var conn = new NpgsqlConnection(GetConnectionString());
                await conn.OpenAsync();

                statusMessage = "PostgreSQL Connection is OK.";
                logLevel = "INFO";
            }
            catch (Exception ex)
            {
                statusMessage = $"PostgreSQL Connection Failed: {ex.Message}";
                logLevel = "ERROR";
            }

            DateTime now = DateTime.Now;
            PersianCalendar pc = new PersianCalendar();

            var pgLog = new
            {
                CollectedAt = now.ToString("yyyy-MM-dd HH:mm:ss"),
                CollectedAt_Shamsi =
                    $"{pc.GetYear(now):0000}/{pc.GetMonth(now):00}/{pc.GetDayOfMonth(now):00} {now:HH:mm:ss}",

                LogLevel = logLevel,
                Message = statusMessage,
                WindowsUsername = windowsUsername,
                MachineName = Environment.MachineName,
                Category = "PostgresHealth"
            };

            var result = _localDb.SaveToLocalQueue(
                QueueTargets.AppLogs,
                pgLog);

            switch (result)
            {
                case OutboxSaveResult.Saved:
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] [{logLevel}] [PostgresHealth] {statusMessage}");
                    break;

                case OutboxSaveResult.DroppedLowPriority:
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] ⚠️ [OUTBOX] " +
                        "Postgres health log dropped due to critical capacity.");
                    break;

                case OutboxSaveResult.Failed:
                default:
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] ❌ [OUTBOX ERROR] " +
                        "Failed to enqueue Postgres health log.");
                    break;
            }
        }


        public List<string> GetAllImageNames()
        {
            var names = new List<string>();

            try
            {
                using var conn = new NpgsqlConnection(GetConnectionString());
                conn.Open();

                const string query = "SELECT image_name FROM alarm_images ORDER BY image_name;";
                using var cmd = new NpgsqlCommand(query, conn);

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

        public Image? GetImageFromDatabase(string imageName)
        {
            if (string.IsNullOrWhiteSpace(imageName))
                return null;

            try
            {
                using var conn = new NpgsqlConnection(GetConnectionString());
                conn.Open();

                const string query = @"
                    SELECT image_data
                    FROM alarm_images
                    WHERE image_name = @imageName
                    LIMIT 1;";

                using var cmd = new NpgsqlCommand(query, conn);
                cmd.Parameters.AddWithValue("@imageName", imageName);

                object? result = cmd.ExecuteScalar();

                if (result != null && result != DBNull.Value)
                {
                    byte[] imageData = (byte[])result;

                    using var ms = new MemoryStream(imageData);
                    using var img = Image.FromStream(ms);

                    // کپی کامل برای آزاد شدن stream و جلوگیری از ObjectDisposed
                    return new Bitmap(img);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading image {imageName} from DB: {ex.Message}");
            }

            return null;
        }

        public List<ClientCommand> GetPendingCommands(
            string computerName,
            string windowsUsername)
        {
            var commands = new List<ClientCommand>();

            try
            {
                using var conn = new NpgsqlConnection(GetConnectionString());
                conn.Open();

                const string query = @"
                    SELECT id, command
                    FROM client_commands
                    WHERE status = 'pending'
                      AND (
                           computer_name = @computerName
                        OR windows_username = @windowsUsername
                      );";

                using var cmd = new NpgsqlCommand(query, conn);

                cmd.Parameters.AddWithValue(
                    "@computerName",
                    string.IsNullOrWhiteSpace(computerName)
                        ? (object)DBNull.Value
                        : computerName);

                cmd.Parameters.AddWithValue(
                    "@windowsUsername",
                    string.IsNullOrWhiteSpace(windowsUsername)
                        ? (object)DBNull.Value
                        : windowsUsername);

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

        public void MarkCommandAsOutdated(int commandId)
        {
            try
            {
                using var conn = new NpgsqlConnection(GetConnectionString());
                conn.Open();

                const string query = @"
                    UPDATE client_commands
                    SET status = 'outdated'
                    WHERE id = @id;";

                using var cmd = new NpgsqlCommand(query, conn);
                cmd.Parameters.AddWithValue("@id", commandId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] Error: {ex.Message}");
            }
        }

        public void MarkCommandAsExecuted(int commandId)
        {
            try
            {
                using var conn = new NpgsqlConnection(GetConnectionString());
                conn.Open();

                const string query = @"
                    UPDATE client_commands
                    SET status = 'executed'
                    WHERE id = @id;";

                using var cmd = new NpgsqlCommand(query, conn);
                cmd.Parameters.AddWithValue("@id", commandId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] Error: {ex.Message}");
            }
        }
    }

    public sealed class ClientCommand
    {
        public int Id { get; set; }
        public string Command { get; set; } = string.Empty;
    }
}
