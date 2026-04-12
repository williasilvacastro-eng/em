using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Principal;
using System.Threading.Tasks;

namespace emu2026
{
    internal sealed class DependencyBootstrapResult
    {
        public bool Success { get; init; }
        public bool RelaunchStarted { get; init; }
        public bool InstalledAnyDependency { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    internal static class DependencyInstaller
    {
        private const string ViGEmBusUrl = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe";
        private const string InterceptionUrl = "https://www.dropbox.com/scl/fi/v3ha0m8jq5rlh87kz7f75/install-interception.zip?rlkey=1qntwut8cpyhqtkirynvddguo&st=dadtc8ld&dl=1";

        public static async Task<DependencyBootstrapResult> EnsureDependenciesAsync(Action<string>? reportStatus = null)
        {
            reportStatus ??= _ => { };

            bool vigemInstalled = IsServiceInstalled("ViGEmBus");
            bool interceptionInstalled = IsInterceptionInstalled();

            if (vigemInstalled && interceptionInstalled)
            {
                reportStatus("ViGEmBus e Interception ja estao prontos.");
                return new DependencyBootstrapResult { Success = true, Message = "Dependencias prontas." };
            }

            if (!IsAdministrator())
            {
                reportStatus("Solicitando permissao de administrador para instalar os drivers...");
                if (TryRelaunchAsAdministrator())
                {
                    return new DependencyBootstrapResult
                    {
                        Success = false,
                        RelaunchStarted = true,
                        Message = "O aplicativo foi reaberto como administrador para concluir a instalacao."
                    };
                }

                return new DependencyBootstrapResult
                {
                    Success = false,
                    Message = "Nao foi possivel obter permissao de administrador para instalar os drivers."
                };
            }

            bool installedAny = false;

            if (!vigemInstalled)
            {
                reportStatus("Instalando ViGEmBus...");
                await InstallViGEmBusAsync();
                vigemInstalled = IsServiceInstalled("ViGEmBus");
                installedAny |= vigemInstalled;
            }

            if (!interceptionInstalled)
            {
                reportStatus("Instalando Interception...");
                await InstallInterceptionAsync();
                interceptionInstalled = IsInterceptionInstalled();
                installedAny |= interceptionInstalled;
            }

            if (!vigemInstalled || !interceptionInstalled)
            {
                string missing = BuildMissingDependenciesMessage(vigemInstalled, interceptionInstalled);
                reportStatus(missing);
                return new DependencyBootstrapResult
                {
                    Success = false,
                    InstalledAnyDependency = installedAny,
                    Message = missing
                };
            }

            string message = installedAny
                ? "Drivers instalados com sucesso. Se o emulador nao responder de imediato, reinicie o Windows uma vez."
                : "Dependencias prontas.";

            reportStatus(message);

            return new DependencyBootstrapResult
            {
                Success = true,
                InstalledAnyDependency = installedAny,
                Message = message
            };
        }

        public static bool IsInterceptionInstalled()
        {
            return IsUpperFilterDriverPresent("keyboard") && IsUpperFilterDriverPresent("mouse");
        }

        private static bool IsAdministrator()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool TryRelaunchAsAdministrator()
        {
            try
            {
                string executablePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    return false;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = true,
                    Verb = "runas"
                });

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsServiceInstalled(string serviceName)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"query {serviceName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                process?.WaitForExit();
                return process != null && process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUpperFilterDriverPresent(string serviceName)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"qc {serviceName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process == null)
                {
                    return false;
                }

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                return process.ExitCode == 0 &&
                       output.IndexOf("Upper Filter Driver", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static async Task InstallViGEmBusAsync()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "ViGEmBus_1.22.0_setup.exe");

            try
            {
                await DownloadFileAsync(ViGEmBusUrl, tempFile);
                await RunElevatedProcessAsync(tempFile, "/quiet /norestart");
            }
            finally
            {
                TryDeleteFile(tempFile);
            }
        }

        private static async Task InstallInterceptionAsync()
        {
            string tempZip = Path.Combine(Path.GetTempPath(), "install-interception.zip");
            string extractDir = Path.Combine(Path.GetTempPath(), "install-interception");

            try
            {
                await DownloadFileAsync(InterceptionUrl, tempZip);

                if (Directory.Exists(extractDir))
                {
                    Directory.Delete(extractDir, true);
                }

                ZipFile.ExtractToDirectory(tempZip, extractDir);

                string installerPath = Path.Combine(extractDir, "install-interception.exe");
                if (!File.Exists(installerPath))
                {
                    throw new FileNotFoundException("install-interception.exe nao encontrado dentro do pacote.", installerPath);
                }

                await RunElevatedProcessAsync(installerPath, "/install");
            }
            finally
            {
                TryDeleteFile(tempZip);
                TryDeleteDirectory(extractDir);
            }
        }

        private static async Task DownloadFileAsync(string url, string destinationPath)
        {
            using var httpClient = new HttpClient();
            using var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fileStream);
        }

        private static async Task RunElevatedProcessAsync(string fileName, string arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas"
                }
            };

            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"A instalacao falhou com codigo {process.ExitCode}.");
            }
        }

        private static string BuildMissingDependenciesMessage(bool vigemInstalled, bool interceptionInstalled)
        {
            if (!vigemInstalled && !interceptionInstalled)
            {
                return "Nao foi possivel instalar ViGEmBus e Interception automaticamente.";
            }

            if (!vigemInstalled)
            {
                return "Nao foi possivel instalar o ViGEmBus automaticamente.";
            }

            return "Nao foi possivel instalar o Interception automaticamente.";
        }

        private static void TryDeleteFile(string path)
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

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch { }
        }
    }
}
