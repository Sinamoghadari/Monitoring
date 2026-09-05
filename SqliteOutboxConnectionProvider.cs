using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Ergonomy.Services;

namespace Ergonomy.Database
{
    /// <summary>Single authoritative location and connection settings for the local outbox.</summary>
    public sealed class SqliteOutboxConnectionProvider
    {
        public string DatabasePath { get; }
        public string ConnectionString { get; }

        /// <summary>
        /// مسیر پایگاه محلی را در ProgramData ایجاد کرده و رشته اتصال اشتراکی SQLite را آماده می‌کند.
        /// If the file was encrypted at rest with DirectoryPassword it is decrypted first.
        /// </summary>
        public SqliteOutboxConnectionProvider()
            : this(protector: null)
        {
        }

        public SqliteOutboxConnectionProvider(SensitiveFileProtector? protector)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Ergonomy");
            Directory.CreateDirectory(directory);
            DatabasePath = Path.Combine(directory, "ergonomy_local.db");
            protector?.UnlockDatabase(DatabasePath);
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true
            }.ToString();
        }
    }
}
