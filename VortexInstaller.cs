using System;
using System.Diagnostics;
using System.IO;

namespace VortexEmulator
{
    public class VortexInstaller
    {
        public static void VerificarInstalacao()
        {
            // Verifica se o driver ViGEmBus esta instalado
            try
            {
                Process p = new Process();
                p.StartInfo.FileName = "sc.exe";
                p.StartInfo.Arguments = "query ViGEmBus";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                p.WaitForExit();

                if (p.ExitCode != 0)
                {
                    // Driver nao encontrado
                    var result = System.Windows.MessageBox.Show(
                        "ViGEmBus driver was not found on your PC.\n\n" +
                        "Install it from:\n" +
                        "https://github.com/nefarius/ViGEmBus/releases\n\n" +
                        "Without ViGEmBus, the emulated controller will NOT work.\n\n" +
                        "Click OK to continue (AI will still work without driver).",
                        "ViGEmBus Not Found",
                        System.Windows.MessageBoxButton.OKCancel,
                        System.Windows.MessageBoxImage.Warning);

                    if (result != System.Windows.MessageBoxResult.OK)
                    {
                        System.Windows.Application.Current.Shutdown();
                    }
                }
            }
            catch (Exception)
            {
                // sc.exe nao encontrado ou erro
            }
        }
    }
}