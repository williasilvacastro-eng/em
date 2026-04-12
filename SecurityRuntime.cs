using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Threading;

namespace emu2026
{
    internal sealed class SecurityException : Exception
    {
        public SecurityException(string message) : base(message) { }
    }

    internal sealed class SecurityRuntime : IDisposable
    {
        private readonly Dictionary<string, string> baselineHashes;
        private readonly DispatcherTimer watchdogTimer;
        private readonly string baseDirectory;
        private bool disposed;

        private static readonly string[] SuspiciousProcessFragments =
        {
            "dnspy", "ilspy", "x64dbg", "x32dbg", "ida", "ida64", "ghidra",
            "ollydbg", "megadumper", "processhacker", "httpdebugger", "fiddler",
            "wireshark", "de4dot", "charles", "dbg", "cheatengine"
        };

        private SecurityRuntime(Dictionary<string, string> hashes, string appBaseDirectory)
        {
            baselineHashes = hashes;
            baseDirectory = appBaseDirectory;
            watchdogTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            watchdogTimer.Tick += WatchdogTimer_Tick;
            watchdogTimer.Start();
        }

        public static SecurityRuntime Initialize()
        {
            EnsureSafeStartupEnvironment();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var hashes = CaptureFileHashes(baseDir);
            return new SecurityRuntime(hashes, baseDir);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            watchdogTimer.Stop();
            watchdogTimer.Tick -= WatchdogTimer_Tick;
        }

        private void WatchdogTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                EnsureSafeRuntimeEnvironment();
            }
            catch (SecurityException)
            {
                Environment.FailFast("Security policy violation detected.");
            }
        }

        private static void EnsureSafeStartupEnvironment()
        {
            if (Debugger.IsAttached || NativeMethods.IsDebuggerPresent())
            {
                throw new SecurityException("Debugger detected. This build cannot run under analysis tools.");
            }

            if (NativeMethods.CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, out bool remoteDebuggerPresent) && remoteDebuggerPresent)
            {
                throw new SecurityException("Remote debugger detected.");
            }

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COR_ENABLE_PROFILING")) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING")))
            {
                throw new SecurityException("CLR profiling environment detected.");
            }

            if (IsSuspiciousProcessRunning())
            {
                throw new SecurityException("Analysis tool detected in the current session.");
            }
        }

        private void EnsureSafeRuntimeEnvironment()
        {
            if (Debugger.IsAttached || NativeMethods.IsDebuggerPresent())
            {
                throw new SecurityException("Debugger attached during runtime.");
            }

            if (IsSuspiciousProcessRunning())
            {
                throw new SecurityException("Suspicious analysis process detected.");
            }

            VerifyApplicationDirectory();
            VerifyFileHashes();
        }

        private void VerifyApplicationDirectory()
        {
            string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(currentExe))
            {
                throw new SecurityException("Unable to validate executable path.");
            }

            string normalizedExe = Path.GetFullPath(currentExe);
            string normalizedBase = Path.GetFullPath(baseDirectory);

            if (!normalizedExe.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException("Executable path is outside the expected application directory.");
            }
        }

        private void VerifyFileHashes()
        {
            foreach (var pair in baselineHashes)
            {
                if (!File.Exists(pair.Key))
                {
                    throw new SecurityException("Critical file was removed after startup.");
                }

                string currentHash = ComputeSha256(pair.Key);
                if (!string.Equals(currentHash, pair.Value, StringComparison.Ordinal))
                {
                    throw new SecurityException("Critical file was modified during runtime.");
                }
            }
        }

        private static bool IsSuspiciousProcessRunning()
        {
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    string processName = string.Empty;

                    try
                    {
                        processName = process.ProcessName ?? string.Empty;
                    }
                    catch
                    {
                        continue;
                    }

                    string lowered = processName.ToLowerInvariant();
                    if (SuspiciousProcessFragments.Any(fragment => lowered.Contains(fragment)))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Se não der para enumerar, preferimos não derrubar o app.
            }

            return false;
        }

        private static Dictionary<string, string> CaptureFileHashes(string baseDirectory)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(baseDirectory, "*.*", SearchOption.TopDirectoryOnly))
            {
                string extension = Path.GetExtension(file);
                if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result[file] = ComputeSha256(file);
            }

            return result;
        }

        private static string ComputeSha256(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll")]
            internal static extern bool IsDebuggerPresent();

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, out bool isDebuggerPresent);
        }
    }
}
