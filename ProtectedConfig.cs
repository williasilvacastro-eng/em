using System;
using System.Text;

namespace emu2026
{
    internal static class ProtectedConfig
    {
        private static readonly string[] SupabaseFunctionUrlParts =
        {
            "aHR0cHM6Ly8=",
            "bm9ncnlobGh5cmdrZG1obHZ5cGY=",
            "LnN1cGFiYXNlLmNv",
            "L2Z1bmN0aW9ucy92MS92YWxpZGF0ZS1saWNlbnNl"
        };

        private static readonly string[] SupabaseAnonKeyParts =
        {
            "ZXlKaGJHY2lPaUpJVXpJMU5pSXNJblI1Y0NJNklrcFhWQ0o5LmV5SnBjM01pT2lKemRYQmhZbUZ6WlNJc0luSmxaaUk2SW01dlozSjVhR3hvZVhKbmEyUnRhR3gyZVhCbUlpd2ljbTlzWlNJNkltRnViMjRpTENKcFlYUWlPakUzTnpZek5qYzRPRElzSW1WNGNDSTZNakE1TVRrME16ZzRNbjAuRkUtbEFQR0NxT3ZQdU9TNmEyd1UtNUkybzJsemNQWUp3UmVucVhkaU9RZw=="
        };

        public static string SupabaseFunctionUrl => Decode(SupabaseFunctionUrlParts[0]) +
                                                    Decode(SupabaseFunctionUrlParts[1]) +
                                                    Decode(SupabaseFunctionUrlParts[2]) +
                                                    Decode(SupabaseFunctionUrlParts[3]);

        public static string SupabaseAnonKey => Decode(SupabaseAnonKeyParts[0]);

        private static string Decode(string base64)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
    }
}
