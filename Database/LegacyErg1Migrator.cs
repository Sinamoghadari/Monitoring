using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Ergonomy.Services;

namespace Ergonomy.Database
{
    /// <summary>
    /// One-time reader for the retired whole-file AES-GCM (ERG1) format.
    /// Used only to import a legacy outbox into SQLCipher; not used at runtime afterwards.
    /// </summary>
    internal static class LegacyErg1Migrator
    {
        private const int SaltSize = 16;
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private const int KeySize = 32;
        private const int Iterations = 100_000;
        private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3");

        public static bool IsErg1(byte[] raw)
        {
            if (raw == null || raw.Length < 4 + SaltSize + NonceSize + TagSize)
                return false;
            return raw[0] == (byte)'E' && raw[1] == (byte)'R' && raw[2] == (byte)'G' && raw[3] == (byte)'1';
        }

        public static bool IsPlainSqlite(byte[] raw)
        {
            if (raw == null || raw.Length < SqliteHeader.Length)
                return false;
            for (int i = 0; i < SqliteHeader.Length; i++)
            {
                if (raw[i] != SqliteHeader[i])
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Decrypts an ERG1 blob to a temp SQLite file. Tries the machine env var (if still set)
        /// then the historical default. Never overwrites <paramref name="sourcePath"/>.
        /// </summary>
        public static bool TryDecryptToSqliteFile(string sourcePath, string destinationPath)
        {
            byte[] raw;
            try
            {
                raw = File.ReadAllBytes(sourcePath);
            }
            catch (Exception ex)
            {
                StartupLog.Error($"Could not read legacy ERG1 file '{sourcePath}'.", ex);
                return false;
            }

            if (!IsErg1(raw))
                return false;

            foreach (string password in CandidatePasswords())
            {
                try
                {
                    byte[] plain = Decrypt(raw, password);
                    if (!IsPlainSqlite(plain))
                        continue;
                    File.WriteAllBytes(destinationPath, plain);
                    StartupLog.Info($"Decrypted legacy ERG1 database to a temporary SQLite file for SQLCipher import.");
                    return true;
                }
                catch (CryptographicException)
                {
                }
                catch (Exception ex)
                {
                    StartupLog.Error("Legacy ERG1 decrypt attempt failed.", ex);
                }
            }

            StartupLog.Error(
                $"Could not decrypt legacy ERG1 file '{sourcePath}'. Original was not overwritten.");
            return false;
        }

        private static string[] CandidatePasswords()
        {
            string? fromEnv = null;
            try
            {
                fromEnv = Environment.GetEnvironmentVariable(
                    "ERGONOMY_DIRECTORY_PASSWORD",
                    EnvironmentVariableTarget.Machine);
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(fromEnv))
            {
                try { fromEnv = Environment.GetEnvironmentVariable("ERGONOMY_DIRECTORY_PASSWORD"); }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(fromEnv))
                return new[] { fromEnv.Trim(), "Sina_2118908" };
            return new[] { "Sina_2118908" };
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

            byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                KeySize);
            byte[] plaintext = new byte[ciphertext.Length];
            using (var gcm = new AesGcm(key, TagSize))
            {
                gcm.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            CryptographicOperations.ZeroMemory(key);
            return plaintext;
        }
    }
}
