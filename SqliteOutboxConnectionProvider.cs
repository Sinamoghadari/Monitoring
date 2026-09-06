using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Ergonomy.Services;

namespace Ergonomy.Database
{
    /// <summary>
    /// Single authoritative location and SQLCipher connection settings for the local outbox.
    /// The database is encrypted at rest by SQLCipher (TDE). Whole-file AES-GCM lock/unlock
    /// is no longer used. Legacy ERG1 or plaintext SQLite files are imported once.
    /// </summary>
    public sealed class SqliteOutboxConnectionProvider
    {
        static SqliteOutboxConnectionProvider()
        {
            try
            {
                SQLitePCL.Batteries_V2.Init();
            }
            catch (Exception ex)
            {
                StartupLog.Error("SQLCipher native provider failed to initialize.", ex);
            }
        }

        public string DatabasePath { get; }
        public string ConnectionString { get; }

        public SqliteOutboxConnectionProvider()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Ergonomy");
            Directory.CreateDirectory(directory);
            DatabasePath = Path.Combine(directory, "ergonomy_local.db");

            string passphrase;
            try
            {
                passphrase = SqlCipherKeyStore.GetPassphrase();
            }
            catch (Exception ex)
            {
                StartupLog.Error("SQLCipher key could not be loaded. Outbox open may fail.", ex);
                passphrase = string.Empty;
            }

            try
            {
                EnsureSqlCipherDatabase(DatabasePath, passphrase);
            }
            catch (Exception ex)
            {
                StartupLog.Error(
                    $"SQLCipher prepare failed for '{DatabasePath}'. Original file was not destroyed.",
                    ex);
            }

            ConnectionString = BuildConnectionString(DatabasePath, passphrase);
        }

        internal static string BuildConnectionString(string path, string passphrase, bool pooling = true)
        {
            return new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = pooling,
                Password = passphrase ?? string.Empty
            }.ToString();
        }

        private static void EnsureSqlCipherDatabase(string path, string passphrase)
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                CreateEmptyEncrypted(path, passphrase);
                return;
            }

            byte[] header = ReadPrefix(path, 32);
            if (LegacyErg1Migrator.IsErg1(header))
            {
                string plainTemp = path + ".plain.tmp";
                try
                {
                    if (!LegacyErg1Migrator.TryDecryptToSqliteFile(path, plainTemp))
                        return;
                    ImportPlaintextIntoSqlCipher(plainTemp, path, passphrase);
                }
                finally
                {
                    TryDelete(plainTemp);
                }

                return;
            }

            if (LegacyErg1Migrator.IsPlainSqlite(header))
            {
                ImportPlaintextIntoSqlCipher(path, path, passphrase);
                return;
            }

            if (!CanOpenEncrypted(path, passphrase))
            {
                StartupLog.Error(
                    $"Existing database '{path}' is not plaintext SQLite, not ERG1, and did not open with the SQLCipher key. File left untouched.");
            }
        }

        private static void CreateEmptyEncrypted(string path, string passphrase)
        {
            using var connection = new SqliteConnection(BuildConnectionString(path, passphrase, pooling: false));
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            cmd.ExecuteScalar();
            StartupLog.Info("Created a new SQLCipher-encrypted outbox database.");
        }

        private static bool CanOpenEncrypted(string path, string passphrase)
        {
            try
            {
                using var connection = new SqliteConnection(BuildConnectionString(path, passphrase, pooling: false));
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 1;";
                cmd.ExecuteScalar();
                return true;
            }
            catch (Exception ex)
            {
                StartupLog.Error("SQLCipher open of existing database failed.", ex);
                return false;
            }
        }

        /// <summary>
        /// Copies a plaintext SQLite file into a new SQLCipher database via sqlcipher_export,
        /// then atomically replaces the destination after a successful open-with-key check.
        /// </summary>
        private static void ImportPlaintextIntoSqlCipher(string plaintextPath, string destinationPath, string passphrase)
        {
            string encryptedTemp = destinationPath + ".sqlcipher.tmp";
            TryDelete(encryptedTemp);

            string attachPath = encryptedTemp.Replace("'", "''");
            string key = (passphrase ?? string.Empty).Replace("'", "''");

            // SQLCipher treats Password= as PRAGMA key. A plaintext source must be opened
            // without a key or the import will fail / corrupt the original.
            using (var plain = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = plaintextPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString()))
            {
                plain.Open();
                using (var checkpoint = plain.CreateCommand())
                {
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    try { checkpoint.ExecuteNonQuery(); }
                    catch { }
                }

                using (var attach = plain.CreateCommand())
                {
                    attach.CommandText = $"ATTACH DATABASE '{attachPath}' AS encrypted KEY '{key}';";
                    attach.ExecuteNonQuery();
                }

                using (var export = plain.CreateCommand())
                {
                    export.CommandText = "SELECT sqlcipher_export('encrypted');";
                    export.ExecuteScalar();
                }

                using (var detach = plain.CreateCommand())
                {
                    detach.CommandText = "DETACH DATABASE encrypted;";
                    detach.ExecuteNonQuery();
                }
            }

            try { SqliteConnection.ClearAllPools(); }
            catch { }

            if (!CanOpenEncrypted(encryptedTemp, passphrase))
            {
                StartupLog.Error("SQLCipher export verification failed. Original database was not replaced.");
                TryDelete(encryptedTemp);
                return;
            }

            string backup = destinationPath + ".pre-sqlcipher";
            File.Replace(encryptedTemp, destinationPath, backup);
            TryDelete(destinationPath + "-wal");
            TryDelete(destinationPath + "-shm");

            if (CanOpenEncrypted(destinationPath, passphrase))
            {
                TryDelete(backup);
                StartupLog.Info($"Imported local outbox into SQLCipher: {destinationPath}");
            }
            else
            {
                StartupLog.Error(
                    $"SQLCipher replacement at '{destinationPath}' did not reopen. Backup kept at '{backup}'.");
            }
        }

        private static byte[] ReadPrefix(string path, int count)
        {
            using var stream = File.OpenRead(path);
            byte[] buffer = new byte[count];
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == buffer.Length)
                return buffer;
            byte[] trimmed = new byte[read];
            Buffer.BlockCopy(buffer, 0, trimmed, 0, read);
            return trimmed;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
