using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Ergonomy.Configuration;

namespace Ergonomy.Services
{
    /// <summary>
    /// AES-256-GCM protector for small artifacts under ProgramData\Ergonomy.
    /// Encrypted blobs start with the versioned magic <c>ERG1</c>. Plaintext SQLite
    /// databases and plaintext version markers are never treated as ciphertext.
    /// </summary>
    public sealed class SensitiveFileProtector
    {
        public const string DefaultPassword = "Sina_2118908";
        public const string Magic = "ERG1";
        private const int SaltSize = 16;
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private const int KeySize = 32;
        private const int Iterations = 100_000;
        private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3");

        private readonly ISettingsService _settings;
        private readonly object _sync = new();

        public SensitiveFileProtector(ISettingsService settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public string Password
        {
            get
            {
                try
                {
                    string? value = _settings.Current?.DirectoryPassword;
                    return string.IsNullOrWhiteSpace(value) ? DefaultPassword : value.Trim();
                }
                catch
                {
                    return DefaultPassword;
                }
            }
        }

        public enum FileKind
        {
            Missing,
            Empty,
            Sqlite,
            EncryptedErg1,
            PlainText,
            Unknown
        }

        public static FileKind DetectKind(byte[] raw)
        {
            if (raw == null || raw.Length == 0)
                return FileKind.Empty;
            if (IsSqlite(raw))
                return FileKind.Sqlite;
            if (IsEncrypted(raw))
                return FileKind.EncryptedErg1;
            if (LooksLikeUtf8Text(raw))
                return FileKind.PlainText;
            return FileKind.Unknown;
        }

        public static FileKind DetectKind(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return FileKind.Missing;
            try
            {
                return DetectKind(File.ReadAllBytes(path));
            }
            catch
            {
                return FileKind.Unknown;
            }
        }

        public void WriteAllText(string path, string contents)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            byte[] plain = Encoding.UTF8.GetBytes(contents ?? string.Empty);
            byte[] cipher = Encrypt(plain, Password);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, path, overwrite: true);
        }

        public string? ReadAllText(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length == 0)
                return string.Empty;

            FileKind kind = DetectKind(raw);
            if (kind == FileKind.EncryptedErg1)
            {
                try
                {
                    byte[] plain = Decrypt(raw, Password);
                    return Encoding.UTF8.GetString(plain);
                }
                catch (Exception ex)
                {
                    StartupLog.Error($"Failed to decrypt text file '{path}'. Leaving original untouched.", ex);
                    return null;
                }
            }

            if (kind == FileKind.Sqlite)
            {
                StartupLog.Warn($"Refusing to read SQLite file '{path}' as a version marker.");
                return null;
            }

            return Encoding.UTF8.GetString(raw);
        }

        /// <summary>
        /// Migrates a legacy plaintext marker to ERG1. Never overwrites SQLite or failed ERG1 blobs.
        /// </summary>
        public void ProtectExistingText(string path)
        {
            if (!File.Exists(path))
                return;

            byte[] raw = File.ReadAllBytes(path);
            FileKind kind = DetectKind(raw);
            if (kind == FileKind.Empty || kind == FileKind.EncryptedErg1 || kind == FileKind.Sqlite)
                return;

            try
            {
                WriteAllText(path, Encoding.UTF8.GetString(raw));
                StartupLog.Info($"Migrated plaintext marker to ERG1: {path}");
            }
            catch (Exception ex)
            {
                StartupLog.Error($"Failed to migrate plaintext marker '{path}'. Original left intact.", ex);
            }
        }

        /// <summary>
        /// Prepares <paramref name="path"/> for SQLite. Plaintext SQLite is left alone.
        /// ERG1 blobs are decrypted to a temp file, validated as SQLite, then swapped in.
        /// Decrypt failures never destroy the original file.
        /// </summary>
        public bool UnlockDatabase(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return true;

            lock (_sync)
            {
                byte[] raw;
                try
                {
                    raw = File.ReadAllBytes(path);
                }
                catch (Exception ex)
                {
                    StartupLog.Error($"Could not read database '{path}'.", ex);
                    return false;
                }

                FileKind kind = DetectKind(raw);
                if (kind == FileKind.Empty || kind == FileKind.Sqlite)
                    return true;

                if (kind != FileKind.EncryptedErg1)
                {
                    StartupLog.Warn($"Database '{path}' is not SQLite and not ERG1 ({kind}). Leaving original in place.");
                    return true;
                }

                byte[] plain;
                try
                {
                    plain = Decrypt(raw, Password);
                }
                catch (Exception ex)
                {
                    StartupLog.Error(
                        $"ERG1 database decrypt failed for '{path}'. Original ciphertext was not overwritten.",
                        ex);
                    return TryRestorePlaintextBackup(path);
                }

                if (!IsSqlite(plain))
                {
                    StartupLog.Error(
                        $"Decrypted '{path}' is not a SQLite database. Original ERG1 file was not overwritten.");
                    return false;
                }

                string temp = path + ".plain.tmp";
                try
                {
                    File.WriteAllBytes(temp, plain);
                    string backup = path + ".erg1.bak";
                    File.Replace(temp, path, backup);
                    StartupLog.Info($"Decrypted ERG1 SQLite database into working file '{path}'.");
                    return true;
                }
                catch (Exception ex)
                {
                    StartupLog.Error($"Failed to install decrypted SQLite working copy for '{path}'.", ex);
                    TryDelete(temp);
                    return false;
                }
            }
        }

        /// <summary>
        /// Writes a verified ERG1 sidecar, then atomically replaces the working SQLite file
        /// with ciphertext. The pre-encrypt backup is kept until the next successful unlock.
        /// Live SQLite access requires UnlockDatabase on the next start.
        /// </summary>
        public bool LockDatabase(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return true;

            lock (_sync)
            {
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
                TryDelete(path + "-journal");

                byte[] raw;
                try
                {
                    raw = File.ReadAllBytes(path);
                }
                catch (Exception ex)
                {
                    StartupLog.Error($"Could not read database for encryption '{path}'.", ex);
                    return false;
                }

                FileKind kind = DetectKind(raw);
                if (kind == FileKind.EncryptedErg1 || kind == FileKind.Empty)
                    return true;

                if (kind != FileKind.Sqlite)
                {
                    StartupLog.Warn($"Refusing to encrypt non-SQLite file '{path}' ({kind}).");
                    return false;
                }

                byte[] cipher;
                try
                {
                    cipher = Encrypt(raw, Password);
                    byte[] roundTrip = Decrypt(cipher, Password);
                    if (roundTrip.Length != raw.Length || !IsSqlite(roundTrip))
                    {
                        StartupLog.Error($"Encrypted database round-trip validation failed for '{path}'.");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    StartupLog.Error($"Failed to encrypt database '{path}'. Plaintext working copy kept.", ex);
                    return false;
                }

                string sidecar = path + ".erg1.tmp";
                try
                {
                    File.WriteAllBytes(sidecar, cipher);
                    string backup = path + ".pre-encrypt";
                    File.Replace(sidecar, path, backup);
                    StartupLog.Info($"SQLite database replaced with verified ERG1 ciphertext: {path}");
                    return true;
                }
                catch (Exception ex)
                {
                    StartupLog.Error($"Failed to swap encrypted database into '{path}'. Plaintext kept.", ex);
                    TryDelete(sidecar);
                    return false;
                }
            }
        }

        public string Describe(string path)
        {
            FileKind kind = DetectKind(path);
            return $"{path} => {kind}";
        }

        private static bool IsEncrypted(byte[] raw)
        {
            if (raw.Length < 4 + SaltSize + NonceSize + TagSize)
                return false;
            return raw[0] == (byte)'E' && raw[1] == (byte)'R' && raw[2] == (byte)'G' && raw[3] == (byte)'1';
        }

        private static bool IsSqlite(byte[] raw)
        {
            if (raw.Length < SqliteHeader.Length)
                return false;
            for (int i = 0; i < SqliteHeader.Length; i++)
            {
                if (raw[i] != SqliteHeader[i])
                    return false;
            }

            return true;
        }

        private static bool LooksLikeUtf8Text(byte[] raw)
        {
            int inspect = Math.Min(raw.Length, 256);
            for (int i = 0; i < inspect; i++)
            {
                byte b = raw[i];
                if (b == 0)
                    return false;
                if (b < 9 || (b > 13 && b < 32))
                    return false;
            }

            return true;
        }

        private static byte[] Encrypt(byte[] plaintext, string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] key = DeriveKey(password, salt);
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagSize];

            using (var gcm = new AesGcm(key, TagSize))
            {
                gcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            byte[] output = new byte[4 + SaltSize + NonceSize + TagSize + ciphertext.Length];
            Encoding.ASCII.GetBytes(Magic).CopyTo(output, 0);
            Buffer.BlockCopy(salt, 0, output, 4, SaltSize);
            Buffer.BlockCopy(nonce, 0, output, 4 + SaltSize, NonceSize);
            Buffer.BlockCopy(tag, 0, output, 4 + SaltSize + NonceSize, TagSize);
            Buffer.BlockCopy(ciphertext, 0, output, 4 + SaltSize + NonceSize + TagSize, ciphertext.Length);
            CryptographicOperations.ZeroMemory(key);
            return output;
        }

        private static byte[] Decrypt(byte[] raw, string password)
        {
            int header = 4 + SaltSize + NonceSize + TagSize;
            if (raw.Length < header)
                throw new CryptographicException("Encrypted file is truncated.");

            byte[] salt = new byte[SaltSize];
            byte[] nonce = new byte[NonceSize];
            byte[] tag = new byte[TagSize];
            byte[] ciphertext = new byte[raw.Length - header];
            Buffer.BlockCopy(raw, 4, salt, 0, SaltSize);
            Buffer.BlockCopy(raw, 4 + SaltSize, nonce, 0, NonceSize);
            Buffer.BlockCopy(raw, 4 + SaltSize + NonceSize, tag, 0, TagSize);
            Buffer.BlockCopy(raw, header, ciphertext, 0, ciphertext.Length);

            byte[] key = DeriveKey(password, salt);
            byte[] plaintext = new byte[ciphertext.Length];
            using (var gcm = new AesGcm(key, TagSize))
            {
                gcm.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            CryptographicOperations.ZeroMemory(key);
            return plaintext;
        }

        private static byte[] DeriveKey(string password, byte[] salt)
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                password ?? DefaultPassword,
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                KeySize);
        }

        private bool TryRestorePlaintextBackup(string path)
        {
            string backupPath = path + ".pre-encrypt";
            try
            {
                if (!File.Exists(backupPath))
                    return false;

                byte[] backup = File.ReadAllBytes(backupPath);
                if (!IsSqlite(backup))
                {
                    StartupLog.Warn($"Backup at '{backupPath}' is not SQLite; leaving ERG1 original in place.");
                    return false;
                }

                string temp = path + ".restore.tmp";
                File.WriteAllBytes(temp, backup);
                File.Replace(temp, path, path + ".erg1.failed");
                StartupLog.Warn($"Restored plaintext SQLite backup over undecryptable ERG1 file: {path}");
                return true;
            }
            catch (Exception ex)
            {
                StartupLog.Error($"Failed to restore plaintext backup for '{path}'. Original ERG1 left in place.", ex);
                return false;
            }
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
