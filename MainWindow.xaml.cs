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
using System.Net.Http;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using VortexEmulator;
using System.Collections.Generic;

// NOTA EDUCATIVA: NUNCA colocar "using System.Windows.Forms" ou "using System.Drawing" aqui em cima num projeto WPF.

namespace emu2026
{
    public partial class MainWindow : Window
    {
        private FovOverlayWindow fovOverlay;
        public UserProfile activeConfig = new UserProfile();
        private Thread workerThread, inputThread, aiThread;
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
            VortexInstaller.VerificarInstalacao();
            // Apenas para não crashar se for aberto sem login em modo de debug
            licenseExpirationTime = DateTime.UtcNow;
            SetupWindow();
        }

        // Este é o construtor correto que deve ser chamado após o Login
        public MainWindow(DateTime expirationDate)
        {
            InitializeComponent();
            VortexInstaller.VerificarInstalacao(); // Adiciona isto se necessário
            licenseExpirationTime = expirationDate.Kind == DateTimeKind.Utc
                ? expirationDate
                : DateTime.SpecifyKind(expirationDate, DateTimeKind.Utc);
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

            AILog.OnUpdated += OnAiLogUpdated;
        }

        private void OnAiLogUpdated(string line)
        {
            Dispatcher?.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (txtLogBox != null)
                    {
                        string[] existing = txtLogBox.Text.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        if (existing.Length > 150)
                        {
                            txtLogBox.Text = string.Join("\n", existing.Skip(100)) + "\n";
                        }
                        txtLogBox.Text += line + "\n";
                        LogScroller?.ScrollToEnd();
                    }
                    if (txtFpsDisplay != null)
                        txtFpsDisplay.Text = "FPS: " + AIFps.ToString();
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Background);
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
            if (!await CheckAndInstallDrivers()) { System.Windows.Application.Current.Shutdown(); return; }

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

                aiThread = new Thread(MotorInteligenciaArtificial) { Priority = ThreadPriority.Highest, IsBackground = true };
                aiThread.Start();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("O Motor de Inteligência Artificial foi iniciado com sucesso e está ativo nos bastidores!", "VORTEX AI - Sistema Online", MessageBoxButton.OK, MessageBoxImage.Information);
                System.Windows.MessageBox.Show($"Failed to initialize drivers.\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown();
            }
        }

        public void UpdateFovOverlay()
        {
            if (!isUiReady) return;
            if (activeConfig.AI_DrawFov) fovOverlay.UpdateFOV(activeConfig.AI_FovWidth, activeConfig.AI_FovHeight);
            else fovOverlay.Visibility = Visibility.Hidden;
        }

        private bool IsAdministrator() { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }

        private void RestartAsAdmin()
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = Process.GetCurrentProcess().MainModule.FileName, UseShellExecute = true, Verb = "runas" });
                System.Windows.Application.Current.Shutdown();
            }
            catch
            {
                System.Windows.MessageBox.Show("Administrator privileges required!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown();
            }
        }

        private async Task<bool> CheckAndInstallDrivers()
        {
            bool vigemMissing = false;
            try { var test = new ViGEmClient(); test.Dispose(); } catch (VigemBusNotFoundException) { vigemMissing = true; } catch { }
            if (vigemMissing)
            {
                var result = System.Windows.MessageBox.Show("ViGEmBus driver not found.\nWould you like to download and install it AUTOMATICALLY?", "Auto Install Required", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes) { await InstallViGEmBus(); try { var test = new ViGEmClient(); test.Dispose(); } catch { return false; } }
                else return false;
            }
            return true;
        }

        private async Task InstallViGEmBus()
        {
            string url = "https://github.com/nefarius/ViGEmBus/releases/latest/download/ViGEmBus_Setup_x64.msi";
            string tempFile = Path.Combine(Path.GetTempPath(), "ViGEmBus_Setup_x64.msi");
            try
            {
                using (var httpClient = new HttpClient())
                {
                    var response = await httpClient.GetAsync(url);
                    using (var fs = new FileStream(tempFile, FileMode.CreateNew))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }
                Process p = new Process();
                p.StartInfo.FileName = "msiexec";
                p.StartInfo.Arguments = $"/i \"{tempFile}\" /quiet /qn /norestart";
                p.StartInfo.UseShellExecute = true;
                p.StartInfo.Verb = "runas";
                p.Start();
                await p.WaitForExitAsync();
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
            catch { }
        }

        // --- GESTÃO DA INTERFACE E NAVEGAÇÃO ---
        private void Window_MouseDown(object s, System.Windows.Input.MouseButtonEventArgs e) { if (e.ChangedButton == System.Windows.Input.MouseButton.Left) this.DragMove(); }
        private void Close_Click(object s, RoutedEventArgs e) { Cleanup(); Environment.Exit(0); }
        private void Hide_Click(object s, RoutedEventArgs e) { this.WindowState = WindowState.Minimized; }
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

        // LOAD MODEL BUTTON
        private async void BtnLoadModel_Click(object s, RoutedEventArgs e)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string modelsDir = Path.Combine(baseDir, "models");
            Directory.CreateDirectory(modelsDir);

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                InitialDirectory = modelsDir,
                Filter = "ONNX Models (*.onnx)|*.onnx|PyTorch Models (*.pt)|*.pt|All Files (*.*)|*.*",
                Title = "Load AI Model (select from 'models/' folder or any location)"
            };
            if (dlg.ShowDialog() == true)
            {
                string selected = dlg.FileName;
                string ext = System.IO.Path.GetExtension(selected).ToLowerInvariant();

                try
                {
                    string destName;
                    string destFileFull;

                    if (ext == ".pt" || ext == ".engine")
                    {
                        destName = Path.GetFileName(selected).Replace(ext, ".onnx");
                        destFileFull = Path.Combine(modelsDir, destName);

                        System.Windows.MessageBox.Show(
                            "PyTorch model (.pt) selected.\nWill export to ONNX format automatically.\n\nIf export fails, run this in terminal:\n    ultralytics yolo export model=" + selected + " format=onnx\n\nThen place the .onnx file in the 'models/' folder.",
                            "Model Export Needed", MessageBoxButton.OK, MessageBoxImage.Information);

                        var psi = new ProcessStartInfo
                        {
                            FileName = "python",
                            Arguments = "-m ultralytics.yolo export model=\"" + selected + "\" format=onnx opset=11",
                            WorkingDirectory = Path.GetDirectoryName(selected),
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        var proc = Process.Start(psi);
                        if (proc != null)
                        {
                            proc.WaitForExit(600000);
                            if (proc.ExitCode == 0)
                            {
                                var dir = Path.GetDirectoryName(selected);
                                var onnxFiles = Directory.GetFiles(dir, "*.onnx");
                                if (onnxFiles.Length > 0)
                                {
                                    File.Copy(onnxFiles[0], destFileFull, true);
                                }
                            }
                        }
                    }
                    else
                    {
                        // Se ja esta na pasta models/, usa direto
                        if (Path.GetDirectoryName(selected).Equals(modelsDir, StringComparison.OrdinalIgnoreCase))
                        {
                            destName = "models/" + Path.GetFileName(selected);
                        }
                        else
                        {
                            // Copia para a pasta models/
                            destName = "models/" + Path.GetFileName(selected);
                            destFileFull = Path.Combine(modelsDir, Path.GetFileName(selected));
                            File.Copy(selected, destFileFull, true);
                        }
                    }

                    // Se nao foi setado pelo .pt export, define aqui
                    if (string.IsNullOrEmpty(destName))
                    {
                        destName = "models/" + Path.GetFileName(selected);
                    }

                    activeConfig.AI_ModelPath = destName;
                    if (txtModelPath != null) txtModelPath.Text = destName;
                    SaveCurrentConfig();
                    System.Windows.MessageBox.Show($"Model loaded: {Path.GetFileName(destName)}\nRestart AI to apply.", "Model Loaded", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error loading model:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnShowVision_Click(object s, RoutedEventArgs e)
        {
            if (activeConfig.AI_ShowVision)
            {
                ShowVisionDebug();
            }
            else
            {
                activeConfig.AI_ShowVision = true;
                if (chkMostrarCamera != null) chkMostrarCamera.IsChecked = true;
                SaveCurrentConfig();
                ShowVisionDebug();
            }
        }

        private void BtnClearLog_Click(object s, RoutedEventArgs e)
        {
            AILog.Clear();
            if (txtLogBox != null) txtLogBox.Text = "";
        }

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

                if (chkAIEnable != null) activeConfig.AI_Enabled = chkAIEnable.IsChecked == true;
                if (chkMostrarCamera != null) activeConfig.AI_ShowVision = chkMostrarCamera.IsChecked == true;
                if (chkDrawFov != null) activeConfig.AI_DrawFov = chkDrawFov.IsChecked == true;
                if (comboGpuProvider != null) activeConfig.AI_GpuProvider = GetGpuProviderName(comboGpuProvider.SelectedIndex);
                if (chkInt8 != null) activeConfig.AI_Int8Enabled = chkInt8.IsChecked == true;
                if (sldTargetFps != null) activeConfig.AI_TargetFps = (int)sldTargetFps.Value;
                if (comboOsso != null) activeConfig.AITargetBone = comboOsso.SelectedIndex;
                if (sldConfianca != null) activeConfig.AI_Conf = sldConfianca.Value;
                if (chkTriggerbot != null) activeConfig.TriggerbotEnabled = chkTriggerbot.IsChecked == true;
                if (chkQuickScope != null) activeConfig.QuickScopeEnabled = chkQuickScope.IsChecked == true;
                if (sldAimSpam != null) activeConfig.AimSpamDelay = (int)sldAimSpam.Value; // NOVO
                if (chkSmartRecoil != null) activeConfig.SmartRecoilEnabled = chkSmartRecoil.IsChecked == true;
                if (chkRageMode != null) activeConfig.AI_RageMode = chkRageMode.IsChecked == true;
                if (sldAISmooth != null) activeConfig.ColorSpeed = sldAISmooth.Value;
                if (sldAimKp != null) activeConfig.AI_AimKp = sldAimKp.Value;
                if (sldAimKi != null) activeConfig.AI_AimKi = sldAimKi.Value;
                if (sldAimKd != null) activeConfig.AI_AimKd = sldAimKd.Value;
                if (sldAimAlpha != null) activeConfig.AI_FilterAlpha = sldAimAlpha.Value;
                if (sldAimIClamp != null) activeConfig.AI_IntegralClamp = sldAimIClamp.Value;
                if (sldAimDead != null) activeConfig.AI_MicroDeadzone = sldAimDead.Value;
                if (sldFovW != null) activeConfig.AI_FovWidth = (int)sldFovW.Value;
                if (sldFovH != null) activeConfig.AI_FovHeight = (int)sldFovH.Value;

                if (sldAimForce != null) activeConfig.AI_AimForce = sldAimForce.Value;
                if (sldAimKd != null) activeConfig.AI_AimKd = sldAimKd.Value;

                if (txtModelPath != null && !string.IsNullOrEmpty(txtModelPath.Text)) activeConfig.AI_ModelPath = txtModelPath.Text.Trim();

                SaveCurrentConfig();
                UpdateFovOverlay();
            }
            catch { }
        }

        // Adiciona este botão temporário para descobrirmos o culpado!
        private void ChkAIEnable_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                // Tenta criar as opções da IA com DirectML
                var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
                sessionOptions.AppendExecutionProvider_DML(0); // Ativa a Placa Gráfica

                // Tenta carregar o teu ficheiro best.onnx
                var session = new Microsoft.ML.OnnxRuntime.InferenceSession("best.onnx", sessionOptions);

                System.Windows.MessageBox.Show("✅ SUCESSO! A IA carregou e a Placa Gráfica foi ativada!", "Diagnóstico", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                // Se falhar, VAI MOSTRAR O MOTIVO EXATO!
                System.Windows.MessageBox.Show("❌ ERRO NA IA:\n\n" + ex.Message, "Diagnóstico Crítico", MessageBoxButton.OK, MessageBoxImage.Error);

                // Desmarca a caixinha automaticamente porque falhou
                if (this.FindName("chkAIEnable") is System.Windows.Controls.CheckBox chk) chk.IsChecked = false;
            }
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
            SetSl(sldConfianca, txtConfianca, activeConfig.AI_Conf);
            SetSl(sldAimSpam, txtAimSpam, activeConfig.AimSpamDelay); // NOVO
            SetSl(sldAimKp, txtAimKp, activeConfig.AI_AimKp);
            SetSl(sldAimKi, txtAimKi, activeConfig.AI_AimKi);
            SetSl(sldAimKd, txtAimKd, activeConfig.AI_AimKd);
            SetSl(sldAimAlpha, txtAimAlpha, activeConfig.AI_FilterAlpha);
            SetSl(sldAimIClamp, txtAimIClamp, activeConfig.AI_IntegralClamp);
            SetSl(sldAimForce, txtAimForce, activeConfig.AI_AimForce);
            SetSl(sldAimDead, txtAimDead, activeConfig.AI_MicroDeadzone);
            SetSl(sldAimDead, txtAimDead, activeConfig.AI_MicroDeadzone);

            if (comboOsso != null) comboOsso.SelectedIndex = activeConfig.AITargetBone;
            if (comboGpuProvider != null) comboGpuProvider.SelectedIndex = GetGpuProviderIndex(activeConfig.AI_GpuProvider);
            if (chkInt8 != null) chkInt8.IsChecked = activeConfig.AI_Int8Enabled;
            if (sldTargetFps != null) sldTargetFps.Value = activeConfig.AI_TargetFps;
            if (txtGpuWarning != null) txtGpuWarning.Visibility = (activeConfig.AI_GpuProvider != "DirectML") ? Visibility.Visible : Visibility.Collapsed;
            if (chkTriggerbot != null) chkTriggerbot.IsChecked = activeConfig.TriggerbotEnabled;
            if (chkQuickScope != null) chkQuickScope.IsChecked = activeConfig.QuickScopeEnabled;
            if (sldAimSpam != null) activeConfig.AimSpamDelay = (int)sldAimSpam.Value; // NOVO
            if (chkAIEnable != null) chkAIEnable.IsChecked = activeConfig.AI_Enabled;
            if (chkDrawFov != null) chkDrawFov.IsChecked = activeConfig.AI_DrawFov;
            if (chkMostrarCamera != null) chkMostrarCamera.IsChecked = activeConfig.AI_ShowVision;
            if (chkSmartRecoil != null) chkSmartRecoil.IsChecked = activeConfig.SmartRecoilEnabled;
            if (chkRageMode != null) chkRageMode.IsChecked = activeConfig.AI_RageMode;
            if (sldFovW != null) sldFovW.Value = activeConfig.AI_FovWidth;
            if (sldFovH != null) sldFovH.Value = activeConfig.AI_FovHeight;
            if (txtModelPath != null) txtModelPath.Text = activeConfig.AI_ModelPath;
            if (txtCustomThemeHex != null) txtCustomThemeHex.Text = activeConfig.ThemeColor;
            ChangeTheme(activeConfig.ThemeColor);

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

        // GPU Provider helpers
        private string GetGpuProviderName(int idx) { return idx == 1 ? "TensorRT" : idx == 2 ? "CUDA" : "DirectML"; }
        private int GetGpuProviderIndex(string name) { return name == "TensorRT" ? 1 : name == "CUDA" ? 2 : 0; }

        private void ComboGpuProvider_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (!isUiReady || isUpdatingUI) return;
            UpdateConfigFromUI();
            if (txtGpuWarning != null)
                txtGpuWarning.Visibility = (activeConfig.AI_GpuProvider != "DirectML") ? Visibility.Visible : Visibility.Collapsed;
            RestartAIThread();
        }

        public void RestartAIThread()
        {
            bool wasActive = activeConfig.AI_Enabled;
            activeConfig.AI_Enabled = false;
            Thread.Sleep(500);
            if (aiThread != null && aiThread.IsAlive)
            {
                aiThread.Interrupt();
                aiThread.Join(1000);
            }
            aiThread = new Thread(MotorInteligenciaArtificial) { Priority = ThreadPriority.Highest, IsBackground = true };
            activeConfig.AI_Enabled = wasActive;
            aiThread.Start();
            SaveCurrentConfig();
        }

        private async void BtnConvertInt8_Click(object s, RoutedEventArgs e)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string modelName = activeConfig.AI_ModelPath;
            string modelPath = Path.IsPathRooted(modelName) ? modelName : Path.Combine(baseDir, modelName);

            // Tenta na pasta models/
            if (!File.Exists(modelPath))
            {
                modelPath = Path.Combine(baseDir, "models", modelName);
            }
            if (!File.Exists(modelPath))
            {
                System.Windows.MessageBox.Show($"Model file not found: {modelName}\n\nMake sure the model is loaded or placed in the 'models/' folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string pythonScript = Path.Combine(baseDir, "convert_int8.py");
            if (!File.Exists(pythonScript))
            {
                System.Windows.MessageBox.Show("convert_int8.py not found.\nPlease place it in the application directory.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string optimizedDir = Path.Combine(baseDir, "models opmized");
            Directory.CreateDirectory(optimizedDir);

            if (btnConvertInt8 != null) { btnConvertInt8.Content = "CONVERTING..."; btnConvertInt8.IsEnabled = false; }

            await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"\"{pythonScript}\" \"{modelPath}\" \"{optimizedDir}\"",
                        WorkingDirectory = baseDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        string error = proc.StandardError.ReadToEnd();
                        proc.WaitForExit(600000);

                        Dispatcher?.BeginInvoke(new Action(() =>
                        {
                            if (proc.ExitCode == 0)
                            {
                                string int8Name = Path.GetFileNameWithoutExtension(modelPath) + "_int8.onnx";
                                string int8Path = Path.Combine(optimizedDir, int8Name);
                                if (File.Exists(int8Path))
                                {
                                    AILog.Log("INT8 conversion successful: " + int8Name);
                                    System.Windows.MessageBox.Show($"Model converted to INT8!\nFile: models opmized/{int8Name}\n\nEnable INT8 checkbox to use it.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                                else
                                {
                                    System.Windows.MessageBox.Show("Conversion finished but INT8 file was not found.\nCheck the output:\n" + output, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                                }
                            }
                            else
                            {
                                System.Windows.MessageBox.Show("Conversion failed:\n" + error + "\n\nMake sure you have Python + onnxruntime installed.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        }));
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        System.Windows.MessageBox.Show($"Error: {ex.Message}\n\nMake sure Python is installed and on PATH.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }));
                }
                finally
                {
                    Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        if (btnConvertInt8 != null) { btnConvertInt8.Content = "CONVERT MODEL TO INT8"; btnConvertInt8.IsEnabled = true; }
                    }));
                }
            });
        }

        private async void Bind_Click(object s, RoutedEventArgs e) { if (chkEditMode != null && chkEditMode.IsChecked == true) return; if (isBinding) return; System.Windows.Controls.Button b = s as System.Windows.Controls.Button; currentBindButton = b; isBinding = true; b.Content = "..."; await Task.Delay(200); int k = await Task.Run(() => DetectKeyPress()); if (k != 0) { UpdateConfigByKey(b.Tag.ToString(), k); SaveCurrentConfig(); } isBinding = false; UpdateBindButtons(); }
        private void UpdateConfigByKey(string t, int k) { if (t == "KeyShoot") activeConfig.KeyShoot = k; else if (t == "KeyAim") activeConfig.KeyAim = k; else if (t == "KeyTactical") activeConfig.KeyTactical = k; else if (t == "KeyLethal") activeConfig.KeyLethal = k; else if (t == "KeyJump") activeConfig.KeyJump = k; else if (t == "KeyCrouch") activeConfig.KeyCrouch = k; else if (t == "KeyReload") activeConfig.KeyReload = k; else if (t == "KeySwap") activeConfig.KeySwap = k; else if (t == "KeyMelee") activeConfig.KeyMelee = k; else if (t == "KeySprint") activeConfig.KeySprint = k; else if (t == "KeyPing") activeConfig.KeyPing = k; else if (t == "KeyFireMode") activeConfig.KeyFireMode = k; else if (t == "KeyKillstreak") activeConfig.KeyKillstreak = k; else if (t == "KeyBackpack") activeConfig.KeyBackpack = k; else if (t == "KeyMap") activeConfig.KeyMap = k; else if (t == "KeyMenu") activeConfig.KeyMenu = k; else if (t == "KeyYY") activeConfig.KeyYY = k; else if (t == "KeyEmulatorToggle") activeConfig.KeyEmulatorToggle = k; else if (t == "KeyParachute") activeConfig.KeyParachute = k; }
        private void UpdateBindButtons() { if (btnLT != null) btnLT.Content = GetKeyName(activeConfig.KeyAim); if (btnRT != null) btnRT.Content = GetKeyName(activeConfig.KeyShoot); if (btnLB != null) btnLB.Content = GetKeyName(activeConfig.KeyTactical); if (btnRB != null) btnRB.Content = GetKeyName(activeConfig.KeyLethal); if (btnY != null) btnY.Content = GetKeyName(activeConfig.KeySwap); if (btnB != null) btnB.Content = GetKeyName(activeConfig.KeyCrouch); if (btnA != null) btnA.Content = GetKeyName(activeConfig.KeyJump); if (btnX != null) btnX.Content = GetKeyName(activeConfig.KeyReload); if (btnLS != null) btnLS.Content = GetKeyName(activeConfig.KeySprint); if (btnRS != null) btnRS.Content = GetKeyName(activeConfig.KeyMelee); if (btnUp != null) btnUp.Content = GetKeyName(activeConfig.KeyPing); if (btnLeft != null) btnLeft.Content = GetKeyName(activeConfig.KeyFireMode); if (btnRight != null) btnRight.Content = GetKeyName(activeConfig.KeyKillstreak); if (btnDown != null) btnDown.Content = GetKeyName(activeConfig.KeyBackpack); if (btnView != null) btnView.Content = GetKeyName(activeConfig.KeyMap); if (btnMenu != null) btnMenu.Content = GetKeyName(activeConfig.KeyMenu); }

        public void Cleanup() { stopWorker = true; RawInputAPI.TimeEndPeriod(1); if (xbox != null) xbox.Disconnect(); }

        private int DetectKeyPress() { long t = DateTimeOffset.Now.ToUnixTimeMilliseconds() + 5000; while (isBinding && DateTimeOffset.Now.ToUnixTimeMilliseconds() < t) { for (int i = 1; i < 255; i++) { if ((RawInputAPI.GetAsyncKeyState(i) & 0x8000) != 0) { if (i == 1 || i == 2) Thread.Sleep(50); return i; } } Thread.Sleep(10); } return 0; }
        private string GetKeyName(int k) { if (k == 0x01) return "L-Click"; if (k == 0x02) return "R-Click"; if (k == 0x04) return "M-Click"; if (k == 0xA0) return "L-Shift"; if (k == 0x20) return "Space"; if (k == 0x09) return "Tab"; if (k >= 0x30 && k <= 0x5A) return ((char)k).ToString(); return "K:" + k; }
    }
}
