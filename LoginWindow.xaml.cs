#nullable disable
#pragma warning disable CS8600
#pragma warning disable CS8601
#pragma warning disable CS8602
#pragma warning disable CS8604
#pragma warning disable CS0414

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Principal;
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
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public LoginWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (File.Exists(KeyFilePath))
            {
                string savedKey = File.ReadAllText(KeyFilePath).Trim();
                if (!string.IsNullOrEmpty(savedKey))
                {
                    txtKey.Text = savedKey;
                    BtnLogin_Click(null, null);
                }
            }
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            string chaveCliente = txtKey.Text?.Trim();

            if (string.IsNullOrEmpty(chaveCliente))
            {
                System.Windows.MessageBox.Show("Please enter a valid License Key!", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string hwidAtual = WindowsIdentity.GetCurrent()?.User?.Value ?? "UNKNOWN_HWID";
                LicenseValidationResponse dadosDaKey = await ValidarLicencaAsync(chaveCliente, hwidAtual);

                if (dadosDaKey == null || !dadosDaKey.valid)
                {
                    MostrarErroLicenca(dadosDaKey);
                    if (File.Exists(KeyFilePath))
                    {
                        File.Delete(KeyFilePath);
                    }

                    return;
                }

                DateTime dataExpiracao = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
                if (!string.IsNullOrWhiteSpace(dadosDaKey.expires_at))
                {
                    if (!DateTimeOffset.TryParse(
                        dadosDaKey.expires_at,
                        null,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var expirationOffset))
                    {
                        System.Windows.MessageBox.Show("Access Denied: Invalid expiration date on license.", "License Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        if (File.Exists(KeyFilePath))
                        {
                            File.Delete(KeyFilePath);
                        }

                        return;
                    }

                    dataExpiracao = expirationOffset.UtcDateTime;
                }

                if (dadosDaKey.first_activation)
                {
                    System.Windows.MessageBox.Show("Key successfully activated and linked to this PC!", "Activation Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                File.WriteAllText(KeyFilePath, chaveCliente);

                MainWindow mainWindow = new MainWindow(dataExpiracao);
                mainWindow.Show();

                if (IsLoaded)
                {
                    Close();
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Error communicating with Supabase: " + ex.Message, "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private void TxtHWID_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                string hwid = WindowsIdentity.GetCurrent()?.User?.Value ?? "HWID_NOT_FOUND";
                System.Windows.Clipboard.SetText(hwid);
                System.Windows.MessageBox.Show("HWID copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch
            {
            }
        }

        private static async Task<LicenseValidationResponse> ValidarLicencaAsync(string chaveCliente, string hwidAtual)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ProtectedConfig.SupabaseFunctionUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (!string.IsNullOrWhiteSpace(ProtectedConfig.SupabaseAnonKey) &&
                !string.Equals(ProtectedConfig.SupabaseAnonKey, "SUA_ANON_KEY_AQUI", StringComparison.Ordinal))
            {
                request.Headers.Add("apikey", ProtectedConfig.SupabaseAnonKey);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ProtectedConfig.SupabaseAnonKey);
            }

            request.Content = JsonContent.Create(new LicenseValidationRequest
            {
                license_key = chaveCliente,
                hwid = hwidAtual
            });

            HttpResponseMessage resposta = await client.SendAsync(request);
            string corpo = await resposta.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(corpo))
            {
                resposta.EnsureSuccessStatusCode();
                return null;
            }

            LicenseValidationResponse resultado = JsonSerializer.Deserialize<LicenseValidationResponse>(corpo, JsonOptions);

            if (!resposta.IsSuccessStatusCode && resultado == null)
            {
                resposta.EnsureSuccessStatusCode();
            }

            return resultado;
        }

        private static void MostrarErroLicenca(LicenseValidationResponse resposta)
        {
            string erro = resposta?.error ?? string.Empty;
            string status = resposta?.status ?? string.Empty;

            if (string.Equals(erro, "hwid_mismatch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "hwid_mismatch", StringComparison.OrdinalIgnoreCase))
            {
                System.Windows.MessageBox.Show("Access Denied: This Key is already linked to another computer!", "HWID Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.Equals(erro, "expired_key", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase))
            {
                System.Windows.MessageBox.Show("Access Denied: This Key has expired.", "License Expired", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.Equals(erro, "inactive_key", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "inactive", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "revoked", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "invalid", StringComparison.OrdinalIgnoreCase))
            {
                System.Windows.MessageBox.Show("Access Denied: This Key is not active.", "License Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            System.Windows.MessageBox.Show("Invalid or non-existent Key!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public class LicenseValidationRequest
    {
        public string license_key { get; set; } = "";
        public string hwid { get; set; } = "";
    }

    public class LicenseValidationResponse
    {
        public bool valid { get; set; }
        public string status { get; set; } = "";
        public string expires_at { get; set; } = "";
        public string error { get; set; } = "";
        public bool first_activation { get; set; }
        public string user_id { get; set; } = "";
    }
}
