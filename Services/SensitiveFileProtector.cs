using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Ergonomy.Configuration;

namespace Ergonomy.Services
{
    /// <summary>
    /// AES-256-GCM protector for sensitive files under ProgramData\Ergonomy.
    /// Key material is <see cref="AppSettings.DirectoryPassword"/> (PBKDF2).
    /// Files are unreadable outside the agent: ciphertext starts with magic ERG1.
    /// </summary>
    public sealed class SensitiveFileProtector
    {
        public const string DefaultPassword = "Sina_2118908";
        private const string Magic = "ERG1";
        private const int SaltSize = 16;
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private const int KeySize = 32;
        private const int Iterations = 100_000;

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
                string? value = _settings.Current?.DirectoryPassword;
                return string.IsNullOrWhiteSpace(value) ? DefaultPassword : value.Trim();
            }
        }

        /// <summary>
        /// Writes <paramref name="contents"/> as AES-GCM ciphertext so the file is unusable as plain text.
        /// </summary>
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

        /// <summary>
        /// Reads an encrypted or legacy plaintext file. Returns null if the path is missing.
        /// </summary>
        public string? ReadAllText(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length == 0)
                return string.Empty;

            if (IsEncrypted(raw))
            {
                byte[] plain = Decrypt(raw, Password);
                return Encoding.UTF8.GetString(plain);
            }

            return Encoding.UTF8.GetString(raw);
        }

        /// <summary>
        /// Re-encrypts a legacy plaintext marker so it is no longer readable as a version string.
        /// </summary>
        public void ProtectExistingText(string path)
        {
            if (!File.Exists(path))
                return;

            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length == 0 || IsEncrypted(raw))
                return;

            WriteAllText(path, Encoding.UTF8.GetString(raw));
        }

        /// <summary>
        /// Decrypts <paramref name="path"/> in place when it is an ERG1 blob so SQLite can open it.
        /// </summary>
        public void UnlockDatabase(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            lock (_sync)
            {
                byte[] raw = File.ReadAllBytes(path);
                if (raw.Length == 0 || !IsEncrypted(raw))
                    return;

                byte[] plain = Decrypt(raw, Password);
                string temp = path + ".plain";
                File.WriteAllBytes(temp, plain);
                File.Move(temp, path, overwrite: true);
            }
        }

        /// <summary>
        /// Encrypts the SQLite database file at rest after connections are closed.
        /// Companion WAL/SHM files are deleted so ciphertext is the only durable copy.
        /// </summary>
        public void LockDatabase(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            lock (_sync)
            {
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
                TryDelete(path + "-journal");

                byte[] raw = File.ReadAllBytes(path);
                if (raw.Length == 0 || IsEncrypted(raw))
                    return;

                byte[] cipher = Encrypt(raw, Password);
                string temp = path + ".enc";
                File.WriteAllBytes(temp, cipher);
                File.Move(temp, path, overwrite: true);
            }
        }

        private static bool IsEncrypted(byte[] raw)
        {
            if (raw.Length < 4 + SaltSize + NonceSize + TagSize)
                return false;
            return raw[0] == (byte)'E' && raw[1] == (byte)'R' && raw[2] == (byte)'G' && raw[3] == (byte)'1';
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
