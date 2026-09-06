using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Ergonomy.Services
{
    /// <summary>
    /// Machine-scoped SQLCipher passphrase. The raw key is random and stored only as a
    /// DPAPI (LocalMachine) blob under ProgramData — not DirectoryPassword and not plaintext.
    /// </summary>
    public static class SqlCipherKeyStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Ergonomy.SqlCipher.v1");

        public static string KeyFilePath => Path.Combine(StartupLog.RootDirectory, "sqlcipher.key");

        /// <summary>
        /// Returns a hex passphrase suitable for SQLCipher KEY / connection-string Password.
        /// </summary>
        public static string GetPassphrase()
        {
            StartupLog.EnsureDirectories();
            string path = KeyFilePath;

            if (File.Exists(path))
            {
                try
                {
                    byte[] wrapped = File.ReadAllBytes(path);
                    byte[] plain = ProtectedData.Unprotect(wrapped, Entropy, DataProtectionScope.LocalMachine);
                    if (plain.Length >= 16)
                        return Convert.ToHexString(plain);
                    StartupLog.Error("SQLCipher key file decoded to an unexpectedly short key.");
                }
                catch (Exception ex)
                {
                    StartupLog.Error(
                        "Could not unprotect the SQLCipher key file. The existing database may be unreadable on this machine.",
                        ex);
                    throw;
                }
            }

            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] protectedBytes = ProtectedData.Protect(key, Entropy, DataProtectionScope.LocalMachine);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, path, overwrite: true);
            StartupLog.Info("Created a new DPAPI-protected SQLCipher key.");
            return Convert.ToHexString(key);
        }
    }
}
