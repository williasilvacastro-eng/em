#nullable disable
#pragma warning disable CS8600
#pragma warning disable CS8601
#pragma warning disable CS8602
#pragma warning disable CS8604
#pragma warning disable CS0414

using System;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace emu2026
{
    public partial class LoginWindow : Window
    {
        private static readonly HttpClient client = new HttpClient();
        private const string KeyFilePath = "key.dat";

        private bool environmentReady;
        private bool environmentCheckInProgress;
        private bool autoLoginTriggered;

        public LoginWindow()
        {
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            bool ready = await EnsureEnvironmentReadyAsync();
            if (!ready || !IsLoaded)
            {
                return;
            }

            string savedKey = ProtectedStorage.LoadUserSecret(KeyFilePath)?.Trim();
            if (!string.IsNullOrEmpty(savedKey))
            {
                txtKey.Text = savedKey;
                autoLoginTriggered = true;
                BtnLogin_Click(null, null);
            }
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            if (environmentCheckInProgress)
            {
                return;
            }

            if (!environmentReady)
            {
                bool ready = await EnsureEnvironmentReadyAsync();
                if (!ready)
                {
                    return;
                }
            }

            string chaveCliente = txtKey.Text?.Trim();
            if (string.IsNullOrEmpty(chaveCliente))
            {
                System.Windows.MessageBox.Show("Please enter a valid License Key!", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string firebaseUrl = ProtectedConfig.FirebaseUrl;
                string urlDoBanco = $"{firebaseUrl}/licenses/{chaveCliente}.json";
                HttpResponseMessage resposta = await client.GetAsync(urlDoBanco);
                resposta.EnsureSuccessStatusCode();

                string corpoDaResposta = await resposta.Content.ReadAsStringAsync();
                if (corpoDaResposta == "null")
                {
                    System.Windows.MessageBox.Show("Invalid or non-existent Key!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ProtectedStorage.DeleteSecret(KeyFilePath);
                    return;
                }

                LicenseData dadosDaKey = JsonSerializer.Deserialize<LicenseData>(corpoDaResposta);
                string hwidAtual = WindowsIdentity.GetCurrent()?.User?.Value ?? "UNKNOWN_HWID";
                bool isLifetime = dadosDaKey.duration_days == 9999;

                if (string.IsNullOrEmpty(dadosDaKey.hwid))
                {
                    dadosDaKey.hwid = hwidAtual;
                    dadosDaKey.is_used = true;
                    dadosDaKey.activation_date = DateTime.Now.ToString("dd/MM/yyyy");

                    string jsonAtualizado = JsonSerializer.Serialize(dadosDaKey);
                    var conteudo = new StringContent(jsonAtualizado, Encoding.UTF8, "application/json");
                    await client.PutAsync(urlDoBanco, conteudo);

                    System.Windows.MessageBox.Show("Key successfully activated and linked to this PC!", "Activation Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (dadosDaKey.hwid != hwidAtual)
                {
                    System.Windows.MessageBox.Show("Access Denied: This Key is already linked to another computer!", "HWID Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ProtectedStorage.DeleteSecret(KeyFilePath);
                    return;
                }

                DateTime dataExpiracao = DateTime.MaxValue;
                if (!isLifetime)
                {
                    DateTime dataAtivacao = DateTime.ParseExact(dadosDaKey.activation_date, "dd/MM/yyyy", null);
                    dataExpiracao = dataAtivacao.AddDays(dadosDaKey.duration_days);

                    if (DateTime.Now > dataExpiracao)
                    {
                        System.Windows.MessageBox.Show($"Access Denied: This Key expired on {dataExpiracao:MM/dd/yyyy}.", "License Expired", MessageBoxButton.OK, MessageBoxImage.Warning);
                        ProtectedStorage.DeleteSecret(KeyFilePath);
                        return;
                    }
                }

                ProtectedStorage.SaveUserSecret(KeyFilePath, chaveCliente);

                MainWindow mainWindow = new MainWindow(dataExpiracao);
                mainWindow.Show();

                if (IsLoaded)
                {
                    Close();
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Error communicating with the database: " + ex.Message, "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<bool> EnsureEnvironmentReadyAsync()
        {
            if (environmentReady)
            {
                return true;
            }

            environmentCheckInProgress = true;
            SetLoadingState(true, "Checking ViGEmBus and Interception...");

            try
            {
                var result = await DependencyInstaller.EnsureDependenciesAsync(UpdateDependencyStatus);
                if (result.RelaunchStarted)
                {
                    System.Windows.Application.Current.Shutdown();
                    return false;
                }

                if (!result.Success)
                {
                    SetLoadingState(false, result.Message);
                    System.Windows.MessageBox.Show(result.Message, "Driver Installation", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                environmentReady = true;
                SetLoadingState(false, result.Message);

                if (result.InstalledAnyDependency)
                {
                    System.Windows.MessageBox.Show(result.Message, "Driver Installation", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return true;
            }
            catch (Exception ex)
            {
                SetLoadingState(false, "Failed to prepare emulator dependencies.");
                System.Windows.MessageBox.Show("Failed to prepare emulator dependencies: " + ex.Message, "Driver Installation", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                environmentCheckInProgress = false;
                btnLogin.IsEnabled = environmentReady;
            }
        }

        private void UpdateDependencyStatus(string status)
        {
            Dispatcher.Invoke(() =>
            {
                if (txtDependencyStatus != null)
                {
                    txtDependencyStatus.Text = status;
                }

                if (txtLoadingStatus != null)
                {
                    txtLoadingStatus.Text = status;
                }
            });
        }

        private void SetLoadingState(bool isLoading, string status)
        {
            UpdateDependencyStatus(status);

            LoginPanel.Visibility = isLoading ? Visibility.Collapsed : Visibility.Visible;
            LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;

            if (btnLogin != null)
            {
                btnLogin.IsEnabled = !isLoading && environmentReady;
            }

            if (!isLoading && autoLoginTriggered && !environmentReady)
            {
                autoLoginTriggered = false;
            }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        private void BtnUpdateLog_Click(object sender, RoutedEventArgs e)
        {
            var updateLogWindow = new UpdateLogWindow
            {
                Owner = this
            };
            updateLogWindow.ShowDialog();
        }

        private void TxtHWID_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                string hwid = WindowsIdentity.GetCurrent()?.User?.Value ?? "HWID_NOT_FOUND";
                System.Windows.Clipboard.SetText(hwid);
                System.Windows.MessageBox.Show("HWID copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }
    }

    public class LicenseData
    {
        public int duration_days { get; set; }
        public bool is_used { get; set; }
        public string type { get; set; } = "temporary";
        public string hwid { get; set; } = "";
        public string activation_date { get; set; } = "";
    }
}
