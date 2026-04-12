using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace emu2026
{
    internal static class ProtectedStorage
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("emu2026:vortex:license");

        public static void SaveUserSecret(string path, string value)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(value);
            byte[] cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, cipherBytes);
        }

        public static string? LoadUserSecret(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                byte[] cipherBytes = File.ReadAllBytes(path);
                byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return null;
            }
        }

        public static void DeleteSecret(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch { }
        }
    }
}
