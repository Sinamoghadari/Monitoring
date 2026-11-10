using System;
using Microsoft.Data.Sqlite;
using Ergonomy.Core.Hosting;
using Ergonomy.Services;

namespace Ergonomy.Database
{
    /// <summary>
    /// Stores the last successfully accepted Control API settings in the same local SQLite
    /// database used by the outbox. The settings cache is deliberately separate from sync_queue:
    /// a missing or damaged cache must fall back to bootstrap defaults without destroying queued
    /// telemetry.
    /// </summary>
    public sealed class SettingsCacheStore
    {
        private const int BusyTimeoutMilliseconds = 5_000;
        private const string CacheKey = "current";
        private readonly SqliteOutboxConnectionProvider _connectionProvider;
        private readonly object _sync = new();

        public SettingsCacheStore(SqliteOutboxConnectionProvider connectionProvider)
        {
            _connectionProvider = connectionProvider
                ?? throw new ArgumentNullException(nameof(connectionProvider));
        }

        /// <summary>
        /// Reads the most recent settings JSON. The SQLite owner creates the table on a first
        /// run; a non-owner uses a read-only connection. Any database/schema/read failure
        /// intentionally selects bootstrap.
        /// </summary>
        public string? LoadSerialized()
        {
            lock (_sync)
            {
                try
                {
                    using SqliteConnection connection = OpenConnection();
                    if (RuntimeIsolation.MayWriteSqlite)
                        EnsureTable(connection);

                    using SqliteCommand command = connection.CreateCommand();
                    command.CommandText = @"
                        SELECT settings_json
                        FROM app_settings_cache
                        WHERE cache_key = $cache_key
                        LIMIT 1;";
                    command.Parameters.AddWithValue("$cache_key", CacheKey);

                    object? value = command.ExecuteScalar();
                    if (value == null || value == DBNull.Value)
                        return null;

                    return value.ToString();
                }
                catch (Exception ex)
                {
                    StartupLog.Error("Local settings cache could not be read; bootstrap settings will be used.", ex);
                    return null;
                }
            }
        }

        /// <summary>
        /// Replaces the single cached settings document after a successful API response.
        /// Returns false when SQLite cannot accept the document; callers can continue with the
        /// in-memory settings while retaining the existing cache for the next restart.
        /// </summary>
        public bool SaveSerialized(string settingsJson)
        {
            if (string.IsNullOrWhiteSpace(settingsJson))
                return false;

            if (!RuntimeIsolation.MayWriteSqlite)
                return false;

            lock (_sync)
            {
                try
                {
                    using SqliteConnection connection = OpenConnection();
                    EnsureTable(connection);

                    using SqliteCommand command = connection.CreateCommand();
                    command.CommandText = @"
                        INSERT INTO app_settings_cache
                            (cache_key, settings_json, updated_at)
                        VALUES
                            ($cache_key, $settings_json,
                             strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                        ON CONFLICT(cache_key) DO UPDATE SET
                            settings_json = excluded.settings_json,
                            updated_at = excluded.updated_at;";
                    command.Parameters.AddWithValue("$cache_key", CacheKey);
                    command.Parameters.AddWithValue("$settings_json", settingsJson);
                    command.ExecuteNonQuery();
                    return true;
                }
                catch (Exception ex)
                {
                    StartupLog.Error(
                        "Successful API settings could not be persisted to the local cache; " +
                        "the new settings will remain active in memory.",
                        ex);
                    return false;
                }
            }
        }

        private SqliteConnection OpenConnection()
        {
            string connectionString = RuntimeIsolation.MayWriteSqlite
                ? _connectionProvider.ConnectionString
                : _connectionProvider.ReadOnlyConnectionString;
            var connection = new SqliteConnection(connectionString);
            connection.Open();

            using SqliteCommand pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
            pragma.ExecuteNonQuery();
            return connection;
        }

        private static void EnsureTable(SqliteConnection connection)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS app_settings_cache (
                    cache_key TEXT PRIMARY KEY,
                    settings_json TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );";
            command.ExecuteNonQuery();
        }
    }
}
