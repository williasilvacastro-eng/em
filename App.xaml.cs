using System;
namespace emu2026
{
    public partial class App : System.Windows.Application
    {
        private SecurityRuntime? securityRuntime;

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                securityRuntime = SecurityRuntime.Initialize();
                var loginWindow = new LoginWindow();
                MainWindow = loginWindow;
                loginWindow.Show();
            }
            catch (SecurityException ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "Security Protection", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Stop);
                Shutdown();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to start secure runtime: " + ex.Message, "Startup Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                Shutdown();
            }
        }

        protected override void OnExit(System.Windows.ExitEventArgs e)
        {
            securityRuntime?.Dispose();
            base.OnExit(e);
        }
    }
}
