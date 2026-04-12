#nullable disable
#pragma warning disable CS8618 
#pragma warning disable CS8602 
#pragma warning disable CS8600 
#pragma warning disable CS8604
#pragma warning disable SYSLIB0014 
#pragma warning disable CS0414

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nefarius.ViGEm.Client;
using System.Collections.Generic;

// NOTA EDUCATIVA: NUNCA colocar "using System.Windows.Forms" ou "using System.Drawing" aqui em cima num projeto WPF.

namespace emu2026
{
    public partial class MainWindow : Window
    {
        private FovOverlayWindow fovOverlay;
        public UserProfile activeConfig = new UserProfile();
        private Thread workerThread, inputThread;
        public volatile bool stopWorker = false;
        private bool isUiReady = false;
        public bool QuickScopeEnabled { get; set; } = false; // Vamos usar isto para ligar o Spam de Mira
        public int AimSpamDelay { get; set; } = 150;

        private bool isBinding = false;
        private System.Windows.Controls.Button currentBindButton = null;
        private bool isDragging = false;
        private System.Windows.Point dragStartPoint;
        private System.Windows.Controls.Button draggedButton;

        private string profilesDir;
        private string mainConfigFile;
        private DateTime licenseExpirationTime;
        private System.Windows.Threading.DispatcherTimer uiTimer;

        public ViGEmClient client;
        public Nefarius.ViGEm.Client.Targets.IXbox360Controller xbox;

        // Remove a linha que força os 30 dias no construtor vazio
        public MainWindow()
        {
            InitializeComponent();
            // Apenas para não crashar se for aberto sem login em modo de debug
            licenseExpirationTime = DateTime.UtcNow;
            SetupWindow();
        }

        // Este é o construtor correto que deve ser chamado após o Login
        public MainWindow(DateTime expirationDate)
        {
            InitializeComponent();
            licenseExpirationTime = expirationDate; // Aqui a data do Firebase é aplicada
            SetupWindow();
        }

        private void SetupWindow()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            profilesDir = Path.Combine(baseDir, "Profiles");
            mainConfigFile = Path.Combine(baseDir, "vortex_config.json");
            Directory.CreateDirectory(profilesDir);

            uiTimer = new System.Windows.Threading.DispatcherTimer();
            uiTimer.Interval = TimeSpan.FromSeconds(1);
            uiTimer.Tick += UiTimer_Tick;
            uiTimer.Start();

        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            TimeSpan timeRemaining = licenseExpirationTime - DateTime.UtcNow;
            if (timeRemaining.TotalSeconds <= 0)
            {
                uiTimer.Stop();
                if (lblTimer != null) { lblTimer.Text = "LICENSE EXPIRED!"; lblTimer.Foreground = new SolidColorBrush(System.Windows.Media.Colors.Red); }
                System.Windows.MessageBox.Show("Your license has expired.", "License Expired", MessageBoxButton.OK, MessageBoxImage.Warning);
                System.Windows.Application.Current.Shutdown();
            }
            else
            {
                if (lblTimer != null) lblTimer.Text = $"Expires in: {timeRemaining.Days}d {timeRemaining.Hours:D2}h {timeRemaining.Minutes:D2}m {timeRemaining.Seconds:D2}s";
            }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeApp();
        }

        private async Task InitializeApp()
        {
            var dependencyResult = await DependencyInstaller.EnsureDependenciesAsync();
            if (dependencyResult.RelaunchStarted)
            {
                System.Windows.Application.Current.Shutdown();
                return;
            }

            if (!dependencyResult.Success)
            {
                System.Windows.MessageBox.Show(dependencyResult.Message, "Driver Installation", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown();
                return;
            }

            RefreshProfileList();
            LoadConfig();

            fovOverlay = new FovOverlayWindow();
            isUiReady = true;
            UpdateFovOverlay();

            try
            {
                client = new ViGEmClient();
                SetupController();

                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;
                RawInputAPI.TimeBeginPeriod(1);

                inputThread = new Thread(InputListenerWorker) { Priority = ThreadPriority.Highest, IsBackground = true };
                inputThread.SetApartmentState(ApartmentState.STA);
                inputThread.Start();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to initialize drivers.\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown();
            }
        }

        public void UpdateFovOverlay()
        {
            if (!isUiReady) return;
            if (fovOverlay != null) fovOverlay.Visibility = Visibility.Hidden;
        }

        // --- GESTÃO DA INTERFACE E NAVEGAÇÃO ---
        private void Window_MouseDown(object s, System.Windows.Input.MouseButtonEventArgs e) { if (e.ChangedButton == System.Windows.Input.MouseButton.Left) this.DragMove(); }
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) { Cleanup(); }
        private void Window_PreviewMouseDown(object s, MouseButtonEventArgs e)
        {
            if (!isBinding || currentBindButton == null) return;

            int keyCode = e.ChangedButton switch
            {
                MouseButton.Right => 0x02,
                MouseButton.Middle => 0x04,
                MouseButton.XButton1 => 0x05,
                MouseButton.XButton2 => 0x06,
                _ => 0
            };

            if (keyCode == 0) return;

            FinishBinding(keyCode);
            e.Handled = true;
        }
        private void Close_Click(object s, RoutedEventArgs e)
        {
            Cleanup();
            System.Windows.Application.Current.Shutdown();
        }
        private void Hide_Click(object s, RoutedEventArgs e) { this.WindowState = WindowState.Minimized; }
        private void BtnUpdateLog_Click(object s, RoutedEventArgs e) { new UpdateLogWindow { Owner = this }.ShowDialog(); }
        private void Save_Click(object s, RoutedEventArgs e) { UpdateConfigFromUI(); SaveCurrentConfig(); if (activeConfig.SoundsEnabled) System.Media.SystemSounds.Beep.Play(); System.Windows.MessageBox.Show("Configuration applied!", "Success", MessageBoxButton.OK, MessageBoxImage.Information); }

        private void Nav_Controller_Click(object s, RoutedEventArgs e) { if (MainTabs != null) MainTabs.SelectedIndex = 0; SetNav((System.Windows.Controls.Button)s); }
        private void Nav_Bindings_Click(object s, RoutedEventArgs e) { if (MainTabs != null) MainTabs.SelectedIndex = 1; SetNav((System.Windows.Controls.Button)s); }
        private void Nav_Settings_Click(object s, RoutedEventArgs e) { if (MainTabs != null) MainTabs.SelectedIndex = 2; SetNav((System.Windows.Controls.Button)s); }
        private void Nav_Macro_Click(object s, RoutedEventArgs e) { if (MainTabs != null) MainTabs.SelectedIndex = 3; SetNav((System.Windows.Controls.Button)s); }
        private void Nav_Profiles_Click(object s, RoutedEventArgs e) { if (MainTabs != null) MainTabs.SelectedIndex = 4; SetNav((System.Windows.Controls.Button)s); }
        private void Nav_Themes_Click(object s, RoutedEventArgs e) { if (MainTabs != null) MainTabs.SelectedIndex = 5; SetNav((System.Windows.Controls.Button)s); }
        private void Nav_IA_Click(object s, RoutedEventArgs e) { if (MainTabs != null) MainTabs.SelectedIndex = 6; SetNav((System.Windows.Controls.Button)s); }

        private void SetNav(System.Windows.Controls.Button b)
        {
            if (navController != null) navController.Tag = "";
            if (navBindings != null) navBindings.Tag = "";
            if (navSettings != null) navSettings.Tag = "";
            if (navMacros != null) navMacros.Tag = "";
            if (navProfiles != null) navProfiles.Tag = "";
            if (navThemes != null) navThemes.Tag = "";
            if (navIA != null) navIA.Tag = "";
            b.Tag = "Selected";
        }

        private bool isUpdatingUI = false;

        // CORREÇÃO: Resolução da ambiguidade do ColorConverter especificando System.Windows.Media
        private void ChangeTheme(string hexColor)
        {
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hexColor);
                var newBrush = new LinearGradientBrush(color, (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF555555"), new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
                this.Resources["AccentColor"] = newBrush;
                activeConfig.ThemeColor = hexColor;
            }
            catch { }
        }

        private void ThemeBtn_Click(object s, RoutedEventArgs e) { if (s is System.Windows.Controls.Button b && b.Tag != null) { string hex = b.Tag.ToString(); isUpdatingUI = true; if (txtCustomThemeHex != null) txtCustomThemeHex.Text = hex; isUpdatingUI = false; ChangeTheme(hex); SaveCurrentConfig(); } }
        private void CustomThemeHex_Changed(object s, TextChangedEventArgs e) { if (isUpdatingUI || !isUiReady) return; if (txtCustomThemeHex != null && (txtCustomThemeHex.Text.Length == 7 || txtCustomThemeHex.Text.Length == 9)) { ChangeTheme(txtCustomThemeHex.Text); SaveCurrentConfig(); } }

        private void Slider_Changed(object s, RoutedPropertyChangedEventArgs<double> e) { if (!isUiReady || isUpdatingUI) return; isUpdatingUI = true; try { if (s is Slider slider && slider.Name.StartsWith("sld")) { string txtName = "txt" + slider.Name.Substring(3); if (this.FindName(txtName) is System.Windows.Controls.TextBox txt) { txt.Text = slider.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); } } UpdateConfigFromUI(); } finally { isUpdatingUI = false; } }
        private void Setting_Changed(object s, TextChangedEventArgs e) { if (!isUiReady || isUpdatingUI) return; isUpdatingUI = true; try { if (s is System.Windows.Controls.TextBox txt && txt.Name.StartsWith("txt")) { string sldName = "sld" + txt.Name.Substring(3); if (this.FindName(sldName) is Slider sld) { if (double.TryParse(txt.Text.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val)) { sld.Value = val; } } } UpdateConfigFromUI(); } finally { isUpdatingUI = false; } }
        private void Setting_Changed(object s, RoutedEventArgs e) { if (!isUiReady) return; UpdateConfigFromUI(); }

        private void UpdateConfigFromUI()
        {
            if (!isUiReady) return;
            try
            {
                if (sldSens != null) activeConfig.BaseSens = sldSens.Value;
                if (sldYRatio != null) activeConfig.YRatio = sldYRatio.Value;
                if (sldCurve != null) activeConfig.CurveResponse = sldCurve.Value;
                if (sldSmooth != null) activeConfig.Smoothing = sldSmooth.Value;
                if (sldParaMult != null) activeConfig.ParachuteMultiplier = sldParaMult.Value;
                if (chkRapidFire != null) activeConfig.RapidFireEnabled = chkRapidFire.IsChecked == true;
                if (sldRapidDelay != null) activeConfig.RapidFireDelay = (int)sldRapidDelay.Value;
                if (sldRecoil != null) activeConfig.RecoilStrength = (int)sldRecoil.Value;
                if (chkAutoPing != null) activeConfig.AutoPingEnabled = chkAutoPing.IsChecked == true;
                if (chkSounds != null) activeConfig.SoundsEnabled = chkSounds.IsChecked == true;
                if (chkRotational != null) activeConfig.RotationalAssist = chkRotational.IsChecked == true;
                if (sldRotTime != null) activeConfig.RotationalSpeed = (int)sldRotTime.Value;
                if (sldRotStr != null) activeConfig.RotationalIntensity = (int)sldRotStr.Value;
                if (sldDropMoneyOpenDelay != null) activeConfig.DropAllMoneyOpenDelayMs = (int)sldDropMoneyOpenDelay.Value;
                if (sldDropMoneyActionDelay != null) activeConfig.DropAllMoneyActionDelayMs = (int)sldDropMoneyActionDelay.Value;
                if (sldDropMoneyCloseDelay != null) activeConfig.DropAllMoneyCloseDelayMs = (int)sldDropMoneyCloseDelay.Value;

                activeConfig.AI_Enabled = false;
                activeConfig.AI_ShowVision = false;
                activeConfig.AI_DrawFov = false;
                activeConfig.TriggerbotEnabled = false;
                activeConfig.QuickScopeEnabled = false;
                activeConfig.SmartRecoilEnabled = false;
                activeConfig.AI_RageMode = false;

                SaveCurrentConfig();
                UpdateFovOverlay();
            }
            catch { }
        }

        private void SaveCurrentConfig() { try { string j = JsonSerializer.Serialize(activeConfig); File.WriteAllText(mainConfigFile, j); } catch { } }

        private void ApplyConfigToUI()
        {
            isUiReady = false;

            // CORREÇÃO: Usar o caminho completo do TextBox do WPF
            void SetSl(Slider sl, System.Windows.Controls.TextBox tx, double val) { if (sl != null) sl.Value = val; if (tx != null) tx.Text = val.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); }

            SetSl(sldSens, txtSens, activeConfig.BaseSens);
            SetSl(sldYRatio, txtYRatio, activeConfig.YRatio);
            SetSl(sldCurve, txtCurve, activeConfig.CurveResponse);
            SetSl(sldSmooth, txtSmooth, activeConfig.Smoothing);
            SetSl(sldParaMult, txtParaMult, activeConfig.ParachuteMultiplier);
            SetSl(sldRapidDelay, txtRapidDelay, activeConfig.RapidFireDelay);
            SetSl(sldRecoil, txtRecoil, activeConfig.RecoilStrength);
            SetSl(sldRotTime, txtRotTime, activeConfig.RotationalSpeed);
            SetSl(sldRotStr, txtRotStr, activeConfig.RotationalIntensity);
            SetSl(sldDropMoneyOpenDelay, txtDropMoneyOpenDelay, activeConfig.DropAllMoneyOpenDelayMs);
            SetSl(sldDropMoneyActionDelay, txtDropMoneyActionDelay, activeConfig.DropAllMoneyActionDelayMs);
            SetSl(sldDropMoneyCloseDelay, txtDropMoneyCloseDelay, activeConfig.DropAllMoneyCloseDelayMs);
            if (txtCustomThemeHex != null) txtCustomThemeHex.Text = activeConfig.ThemeColor;
            ChangeTheme(activeConfig.ThemeColor);

            activeConfig.AI_Enabled = false;
            activeConfig.AI_ShowVision = false;
            activeConfig.AI_DrawFov = false;
            activeConfig.TriggerbotEnabled = false;
            activeConfig.QuickScopeEnabled = false;
            activeConfig.SmartRecoilEnabled = false;
            activeConfig.AI_RageMode = false;

            isUiReady = true;
            UpdateBindButtons();
        }

        private void LoadConfig() { if (File.Exists(mainConfigFile)) { try { isUiReady = false; activeConfig = JsonSerializer.Deserialize<UserProfile>(File.ReadAllText(mainConfigFile)); ApplyConfigToUI(); } catch { activeConfig = new UserProfile(); } finally { isUiReady = true; } } }
        private void LoadProfile(string n) { string p = Path.Combine(profilesDir, n + ".json"); if (File.Exists(p)) { try { isUiReady = false; activeConfig = JsonSerializer.Deserialize<UserProfile>(File.ReadAllText(p)); ApplyConfigToUI(); SaveCurrentConfig(); } catch { } finally { isUiReady = true; } } }
        private void RefreshProfileList() { if (lstProfiles != null) { lstProfiles.Items.Clear(); var files = Directory.GetFiles(profilesDir, "*.json"); foreach (var f in files) lstProfiles.Items.Add(Path.GetFileNameWithoutExtension(f)); } }

        // CORREÇÕES DE REFERÊNCIA AO WPF PARA EVENTOS DE MOUSE E BOTÕES
        private void Btn_DragStart(object s, System.Windows.Input.MouseButtonEventArgs e) { if (chkEditMode != null && chkEditMode.IsChecked == true) { isDragging = true; draggedButton = s as System.Windows.Controls.Button; dragStartPoint = e.GetPosition(OverlayCanvas); draggedButton?.CaptureMouse(); e.Handled = true; } }
        private void Btn_DragMove(object s, System.Windows.Input.MouseEventArgs e) { if (isDragging && draggedButton != null) { System.Windows.Point p = e.GetPosition(OverlayCanvas); Canvas.SetLeft(draggedButton, Canvas.GetLeft(draggedButton) + p.X - dragStartPoint.X); Canvas.SetTop(draggedButton, Canvas.GetTop(draggedButton) + p.Y - dragStartPoint.Y); dragStartPoint = p; } }
        private void Btn_DragEnd(object s, MouseButtonEventArgs e) { if (isDragging) { isDragging = false; if (draggedButton != null) { draggedButton.ReleaseMouseCapture(); draggedButton = null; } } }

        private void SaveProfile(string profileName) { try { string p = Path.Combine(profilesDir, profileName + ".json"); File.WriteAllText(p, JsonSerializer.Serialize(activeConfig)); } catch { } }
        private void BtnNewProfile_Click(object s, RoutedEventArgs e) { string n = Microsoft.VisualBasic.Interaction.InputBox("Profile Name:", "New Profile", "Custom"); if (!string.IsNullOrWhiteSpace(n)) { activeConfig.Name = n; SaveProfile(n); SaveCurrentConfig(); RefreshProfileList(); } }
        private void BtnSaveProfile_Click(object s, RoutedEventArgs e) { UpdateConfigFromUI(); SaveProfile(activeConfig.Name); SaveCurrentConfig(); System.Windows.MessageBox.Show($"Profile '{activeConfig.Name}' saved!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information); RefreshProfileList(); }
        private void LstProfiles_SelectionChanged(object s, SelectionChangedEventArgs e) { if (lstProfiles != null && lstProfiles.SelectedItem != null) LoadProfile(lstProfiles.SelectedItem.ToString()); }

        private async void Bind_Click(object s, RoutedEventArgs e)
        {
            if (chkEditMode != null && chkEditMode.IsChecked == true) return;
            if (isBinding) return;

            System.Windows.Controls.Button b = s as System.Windows.Controls.Button;
            currentBindButton = b;
            isBinding = true;
            b.Content = "...";

            await Task.Delay(220);
            int k = await Task.Run(() => DetectKeyPress());
            if (k != 0) FinishBinding(k);
            else CancelBinding();
        }

        private void FinishBinding(int keyCode)
        {
            if (currentBindButton == null) return;

            UpdateConfigByKey(currentBindButton.Tag.ToString(), keyCode);
            SaveCurrentConfig();
            isBinding = false;
            currentBindButton = null;
            UpdateBindButtons();
        }

        private void CancelBinding()
        {
            isBinding = false;
            currentBindButton = null;
            UpdateBindButtons();
        }

        private void UpdateConfigByKey(string t, int k) { if (t == "KeyShoot") activeConfig.KeyShoot = k; else if (t == "KeyAim") activeConfig.KeyAim = k; else if (t == "KeyTactical") activeConfig.KeyTactical = k; else if (t == "KeyLethal") activeConfig.KeyLethal = k; else if (t == "KeyJump") activeConfig.KeyJump = k; else if (t == "KeyCrouch") activeConfig.KeyCrouch = k; else if (t == "KeyReload") activeConfig.KeyReload = k; else if (t == "KeySwap") activeConfig.KeySwap = k; else if (t == "KeyMelee") activeConfig.KeyMelee = k; else if (t == "KeySprint") activeConfig.KeySprint = k; else if (t == "KeyPing") activeConfig.KeyPing = k; else if (t == "KeyFireMode") activeConfig.KeyFireMode = k; else if (t == "KeyKillstreak") activeConfig.KeyKillstreak = k; else if (t == "KeyBackpack") activeConfig.KeyBackpack = k; else if (t == "KeyMap") activeConfig.KeyMap = k; else if (t == "KeyMenu") activeConfig.KeyMenu = k; else if (t == "KeyYY") activeConfig.KeyYY = k; else if (t == "KeyEmulatorToggle") activeConfig.KeyEmulatorToggle = k; else if (t == "KeyParachute") activeConfig.KeyParachute = k; else if (t == "KeyDropAllMoney") activeConfig.KeyDropAllMoney = k; }
        private void UpdateBindButtons() { if (btnLT != null) btnLT.Content = GetKeyName(activeConfig.KeyAim); if (btnRT != null) btnRT.Content = GetKeyName(activeConfig.KeyShoot); if (btnLB != null) btnLB.Content = GetKeyName(activeConfig.KeyTactical); if (btnRB != null) btnRB.Content = GetKeyName(activeConfig.KeyLethal); if (btnY != null) btnY.Content = GetKeyName(activeConfig.KeySwap); if (btnB != null) btnB.Content = GetKeyName(activeConfig.KeyCrouch); if (btnA != null) btnA.Content = GetKeyName(activeConfig.KeyJump); if (btnX != null) btnX.Content = GetKeyName(activeConfig.KeyReload); if (btnLS != null) btnLS.Content = GetKeyName(activeConfig.KeySprint); if (btnRS != null) btnRS.Content = GetKeyName(activeConfig.KeyMelee); if (btnUp != null) btnUp.Content = GetKeyName(activeConfig.KeyPing); if (btnLeft != null) btnLeft.Content = GetKeyName(activeConfig.KeyFireMode); if (btnRight != null) btnRight.Content = GetKeyName(activeConfig.KeyKillstreak); if (btnDown != null) btnDown.Content = GetKeyName(activeConfig.KeyBackpack); if (btnView != null) btnView.Content = GetKeyName(activeConfig.KeyMap); if (btnMenu != null) btnMenu.Content = GetKeyName(activeConfig.KeyMenu); if (lstParachute != null) lstParachute.Content = GetKeyName(activeConfig.KeyParachute); if (lstEmuToggle != null) lstEmuToggle.Content = GetKeyName(activeConfig.KeyEmulatorToggle); if (lstRT != null) lstRT.Content = GetKeyName(activeConfig.KeyShoot); if (lstLT != null) lstLT.Content = GetKeyName(activeConfig.KeyAim); if (lstLB != null) lstLB.Content = GetKeyName(activeConfig.KeyTactical); if (lstRB != null) lstRB.Content = GetKeyName(activeConfig.KeyLethal); if (lstA != null) lstA.Content = GetKeyName(activeConfig.KeyJump); if (lstB != null) lstB.Content = GetKeyName(activeConfig.KeyCrouch); if (lstX != null) lstX.Content = GetKeyName(activeConfig.KeyReload); if (lstY != null) lstY.Content = GetKeyName(activeConfig.KeySwap); if (lstLS != null) lstLS.Content = GetKeyName(activeConfig.KeySprint); if (lstRS != null) lstRS.Content = GetKeyName(activeConfig.KeyMelee); if (lstYY != null) lstYY.Content = GetKeyName(activeConfig.KeyYY); if (lstDropMoney != null) lstDropMoney.Content = GetKeyName(activeConfig.KeyDropAllMoney); }

        public void Cleanup()
        {
            stopWorker = true;

            try { uiTimer?.Stop(); } catch { }
            try { fovOverlay?.Close(); } catch { }

            try
            {
                if (xbox != null)
                {
                    xbox.ResetReport();
                    xbox.Disconnect();
                }
            }
            catch { }

            try { client?.Dispose(); } catch { }

            try
            {
                if (inputMessageWindow != null && !inputMessageWindow.IsDisposed)
                {
                    inputMessageWindow.BeginInvoke(new Action(() =>
                    {
                        try { inputMessageWindow.Close(); } catch { }
                        try { System.Windows.Forms.Application.ExitThread(); } catch { }
                    }));
                }
                else
                {
                    try { System.Windows.Forms.Application.ExitThread(); } catch { }
                }
            }
            catch { }

            try
            {
                if (workerThread != null && workerThread.IsAlive) workerThread.Join(500);
                if (inputThread != null && inputThread.IsAlive) inputThread.Join(500);
            }
            catch { }

            try { RawInputAPI.TimeEndPeriod(1); } catch { }
        }

        private int DetectKeyPress() { long t = DateTimeOffset.Now.ToUnixTimeMilliseconds() + 5000; while (isBinding && DateTimeOffset.Now.ToUnixTimeMilliseconds() < t) { for (int i = 1; i < 255; i++) { if ((RawInputAPI.GetAsyncKeyState(i) & 0x8000) != 0) { if (i == 1 || i == 2) Thread.Sleep(50); return i; } } Thread.Sleep(10); } return 0; }
        private string GetKeyName(int k) { if (k == 0) return "..."; if (k == 0x01) return "L-Click"; if (k == 0x02) return "R-Click"; if (k == 0x04) return "M-Click"; if (k == 0x05) return "Mouse4"; if (k == 0x06) return "Mouse5"; if (k == 0xA0) return "L-Shift"; if (k == 0xA1) return "R-Shift"; if (k == 0x20) return "Space"; if (k == 0x09) return "Tab"; if (k == 0x24) return "Home"; if (k == 0x1B) return "Esc"; if (k >= 0x30 && k <= 0x5A) return ((char)k).ToString(); return "K:" + k; }

        // INPUT LISTENER WORKER - Handles key presses for bindings
        private void InputListenerWorker()
        {
            while (!stopWorker)
            {
                try
                {
                    // Check for Drop All Money key press
                    if (activeConfig.KeyDropAllMoney != 0 &&
                        (RawInputAPI.GetAsyncKeyState(activeConfig.KeyDropAllMoney) & 0x8000) != 0)
                    {
                        ExecuteDropAllMoneySequence();
                        // Prevent rapid-fire triggering
                        Thread.Sleep(300);
                    }

                    // Add other key listeners here as needed for future features
                    // Example: if (activeConfig.SomeOtherKey != 0 && ...) { ... }

                    Thread.Sleep(5); // Reduce CPU usage
                }
                catch (Exception ex)
                {
                    // Log error but keep thread alive
                    System.Diagnostics.Debug.WriteLine($"InputListener error: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }

        // DROP ALL MONEY SEQUENCE - Executes the Warzone money drop combo
        private void ExecuteDropAllMoneySequence()
        {
            try
            {
                // 1. Open backpack (Tab key typically)
                RawInputAPI.PressBind(activeConfig.KeyBackpack); // Usually Tab
                Thread.Sleep(activeConfig.DropAllMoneyOpenDelayMs);

                // 2. Select first slot (typically D-Pad Down or equivalent)
                RawInputAPI.PressBind(0x28); // VK_DOWN (D-Pad Down)
                Thread.Sleep(50);

                // 3. Drop all money (Y button typically)
                RawInputAPI.PressBind(activeConfig.KeySwap); // Usually Y
                Thread.Sleep(activeConfig.DropAllMoneyActionDelayMs);

                // 4. Confirm drop (B button typically)
                RawInputAPI.PressBind(activeConfig.KeyCrouch); // Usually B
                Thread.Sleep(activeConfig.DropAllMoneyCloseDelayMs);

                // 5. Close backpack (Tab again)
                RawInputAPI.PressBind(activeConfig.KeyBackpack); // Tab to close
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Drop All Money error: {ex.Message}");
            }
        }
    }
}
