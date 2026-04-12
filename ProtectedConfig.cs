using System;
using System.Text;

namespace emu2026
{
    internal static class ProtectedConfig
    {
        private static readonly string[] FirebaseUrlParts =
        {
            "aHR0cHM6Ly8=",
            "dm9ydGV4YXV0aC0zNGM4Yi0=",
            "ZGVmYXVsdC1ydGRi",
            "LmZpcmViYXNlaW8uY29t"
        };

        public static string FirebaseUrl => Decode(FirebaseUrlParts[0]) +
                                            Decode(FirebaseUrlParts[1]) +
                                            Decode(FirebaseUrlParts[2]) +
                                            Decode(FirebaseUrlParts[3]);

        private static string Decode(string base64)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
    }
}
