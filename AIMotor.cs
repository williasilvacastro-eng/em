#nullable disable
#pragma warning disable CS8618
#pragma warning disable CS8602
#pragma warning disable CS8600
#pragma warning disable CS8604
#pragma warning disable CS8605
#pragma warning disable CS0649
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Compunet.YoloV8;
using Microsoft.ML.OnnxRuntime;

namespace emu2026
{
    public partial class MainWindow
    {
        public void MotorInteligenciaArtificial()
        {
            AILog.Clear();
            AILog.Log("=== AI Motor Iniciado ===");
            string modelPath = ResolveModelPath(activeConfig.AI_ModelPath);
            while (string.IsNullOrEmpty(modelPath) && !stopWorker)
            {
                modelPath = ResolveModelPath(activeConfig.AI_ModelPath);
                Thread.Sleep(2000);
            }
            if (stopWorker) return;
            try
            {
                string finalModelPath = modelPath;
                bool usingInt8 = false;
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string optimizedDir = Path.Combine(baseDir, "models opmized");

                if (activeConfig.AI_Int8Enabled)
                {
                    string int8Name = Path.GetFileNameWithoutExtension(modelPath) + "_int8.onnx";
                    string int8Path = Path.Combine(optimizedDir, int8Name);
                    if (File.Exists(int8Path))
                    {
                        finalModelPath = int8Path;
                        usingInt8 = true;
                    }
                    else
                    {
                        AILog.Log("INT8 ativado mas modelo _int8.onnx nao encontrado. Usando FP32.");
                    }
                }

                AILog.Log("Carregando modelo: " + Path.GetFileName(finalModelPath) + "...");

                SessionOptions options = new SessionOptions();
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                options.EnableMemoryPattern = true;

                string provider = activeConfig.AI_GpuProvider;
                // Com Microsoft.ML.OnnxRuntime.DirectML, apenas DirectML está disponível.
                // Para usar TensorRT ou CUDA, instale o pacote Microsoft.ML.OnnxRuntime.Gpu e adicione
                // AppendExecutionProvider_TensorRT(0) ou AppendExecutionProvider_CUDA(0) respectivamente.
                switch (provider)
                {
                    case "TensorRT":
                        AILog.Log("GPU Provider: DirectML (TensorRT requer pacote separado, usando DirectML)");
                        options.AppendExecutionProvider_DML(0);
                        break;
                    case "CUDA":
                        AILog.Log("GPU Provider: DirectML (CUDA requer pacote separado, usando DirectML)");
                        options.AppendExecutionProvider_DML(0);
                        break;
                    default:
                        options.AppendExecutionProvider_DML(0);
                        AILog.Log("GPU Provider: DirectML (Universal)");
                        break;
                }
                AILog.Log("Precision: " + (usingInt8 ? "INT8" : "FP32"));

                YoloPredictorOptions predictorOptions = new YoloPredictorOptions { SessionOptions = options };
                using var predictor = new YoloPredictor(finalModelPath, predictorOptions);
                AILog.Log("Modelo carregado com sucesso!");
                int screenW = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
                int screenH = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
                int lastCaptureSize = 0;
                Bitmap reusableBmp = null;
                Graphics gDest = null;
                Stopwatch loopTimer = new Stopwatch();
                int fpsCounter = 0;
                long fpsLastTime = Environment.TickCount64;
                long lastLogTime = Environment.TickCount64;

                // Sticky Aim: rastrear mesmo alvo entre frames
                float[] currentTargetPos = null;
                float currentTargetW = 0, currentTargetH = 0;
                int framesLost = 0;
                const int maxLostFrames = 4;

                AILog.Log("MODO LOCK: detectou = trava no alvo");
                int initTargetFps = Math.Max(30, Math.Min(500, activeConfig.AI_TargetFps));
                AILog.Log("IA pronta — rodando a ~" + initTargetFps + " FPS");

                while (!stopWorker)
                {
                    loopTimer.Restart();
                    if (!activeConfig.AI_Enabled)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    long nowFrame = Environment.TickCount64;
                    int fovW = activeConfig.AI_FovWidth >= 50 ? activeConfig.AI_FovWidth : 320;
                    int fovH = activeConfig.AI_FovHeight >= 50 ? activeConfig.AI_FovHeight : 320;
                    int captureSize = Math.Max((int)(Math.Max(fovW, fovH) * 0.7), 200);
                    float centerX = captureSize / 2f;
                    float centerY = captureSize / 2f;
                    int captureX = (screenW / 2) - (captureSize / 2);
                    int captureY = (screenH / 2) - (captureSize / 2);

                    if (reusableBmp == null || captureSize != lastCaptureSize)
                    {
                        gDest?.Dispose();
                        reusableBmp?.Dispose();
                        reusableBmp = new Bitmap(captureSize, captureSize, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                        gDest = Graphics.FromImage(reusableBmp);
                        lastCaptureSize = captureSize;
                    }

                    gDest.CopyFromScreen(captureX, captureY, 0, 0, reusableBmp.Size, CopyPixelOperation.SourceCopy);
                    using var ms = new System.IO.MemoryStream();
                    reusableBmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                    var result = predictor.Detect(ms.ToArray());

                    float confThreshold = (float)(activeConfig.AI_Conf / 100.0);
                    if (currentTargetPos != null && framesLost < 2)
                        confThreshold = Math.Min(confThreshold, 0.25f);

                    // Encontrar alvo: offset offset RAW (distX, distY = pixels do centro ao alvo)
                    // SEM filtro, SEM Kalman, SEM EMA, SEM MovementPath
                    // O ControllerEngine cuida de todo o suavismo
                    float fovHalfW = fovW / 2f;
                    float fovHalfH = fovH / 2f;
                    float[] chosenDist = null;
                    float chosenW = 0, chosenH = 0;
                    float chosenConf = 0f;
                    double chosenScore = double.MaxValue;

                    foreach (var b in result)
                    {
                        if (b.Confidence < confThreshold) continue;
                        float w = b.Bounds.Width, h = b.Bounds.Height;
                        float area = w * h;
                        if (area < 80 || area > 100000) continue;

                        float bx = b.Bounds.X + (w / 2f);
                        float by = activeConfig.AITargetBone == 1 ? b.Bounds.Y + (h * 0.15f) : b.Bounds.Y + (h * 0.30f);
                        float distX = bx - centerX, distY = by - centerY;
                        if (Math.Abs(distX) > fovHalfW || Math.Abs(distY) > fovHalfH) continue;

                        float dist = (float)Math.Sqrt(distX * distX + distY * distY);

                        // STICKY AIM: preferir alvo atual se estiver proximo
                        bool isCurrentTarget = false;
                        if (currentTargetPos != null && framesLost < maxLostFrames)
                        {
                            float deltaToLast = (float)Math.Sqrt(
                                (distX - currentTargetPos[0]) * (distX - currentTargetPos[0]) +
                                (distY - currentTargetPos[1]) * (distY - currentTargetPos[1]));
                            float trackingR = Math.Max(currentTargetW, currentTargetH) * 1.5f + 40.0f;
                            if (deltaToLast < trackingR)
                            {
                                float areaRatio = area / (currentTargetW * currentTargetH > 1 ? currentTargetW * currentTargetH : 1);
                                if (areaRatio > 0.4 && areaRatio < 2.5) isCurrentTarget = true;
                            }
                        }

                        // Score: distancia (menor=melhor), confianca bonus, bonus de alvo atual
                        double score = dist * 0.7 + (1.0 - b.Confidence) * 300.0 * 0.3;
                        if (isCurrentTarget) score -= 20.0;

                        if (chosenDist == null || score < chosenScore)
                        {
                            chosenDist = new float[] { distX, distY };
                            chosenW = w; chosenH = h;
                            chosenConf = b.Confidence;
                            chosenScore = score;
                        }
                    }

                    if (chosenDist != null)
                    {
                        // OFFSETS CRUS — sem nenhum filtro
                        int rawX = (int)Math.Round(chosenDist[0]);
                        int rawY = (int)Math.Round(chosenDist[1]);
                        // LOCK: alvo no centro = trava
                        int locked = (Math.Abs(rawX) <= 3 && Math.Abs(rawY) <= 3) ? 1 : 0;

                        currentTargetPos = new float[] { chosenDist[0], chosenDist[1] };
                        currentTargetW = chosenW; currentTargetH = chosenH;
                        framesLost = 0;

                        lock (SharedState.aimLock)
                        {
                            if (SharedState.aimbotQueue.Count > 2) SharedState.aimbotQueue.Dequeue();
                            SharedState.aimbotQueue.Enqueue(new Vector2Data
                            {
                                X = rawX, Y = rawY, Trigger = locked,
                                Confidence = chosenConf, Timestamp = Environment.TickCount
                            });
                        }

                        // Vision debug
                        if (activeConfig.AI_ShowVision)
                        {
                            using (var gfx = Graphics.FromImage(reusableBmp))
                            {
                                gfx.DrawRectangle(new Pen(Color.Red, 1), centerX - fovW / 2f, centerY - fovH / 2f, fovW, fovH);
                                float tx = centerX + chosenDist[0];
                                float ty = centerY + chosenDist[1];
                                Pen pen = new Pen(locked == 1 ? Color.Cyan : (chosenConf > 0.7f ? Color.LimeGreen : Color.Yellow), 2);
                                gfx.DrawEllipse(pen, tx - 8, ty - 8, 16, 16);
                            }
                            string dbg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Debug", "vision_debug.bmp");
                            Directory.CreateDirectory(Path.GetDirectoryName(dbg));
                            reusableBmp.Save(dbg, System.Drawing.Imaging.ImageFormat.Bmp);
                        }
                    }
                    else
                    {
                        framesLost++;
                        if (framesLost > maxLostFrames + 2)
                        {
                            currentTargetPos = null;
                        }
                        lock (SharedState.aimLock)
                        {
                            SharedState.aimbotQueue.Clear();
                            SharedState.aimbotQueue.Enqueue(new Vector2Data
                                { X = 0, Y = 0, Trigger = 0, Confidence = 0f, Timestamp = Environment.TickCount });
                        }
                    }

                    // FPS counter
                    fpsCounter++;
                    if (nowFrame - fpsLastTime >= 1000)
                    {
                        AIFps = fpsCounter;
                        fpsCounter = 0;
                        fpsLastTime = nowFrame;
                        if (nowFrame - lastLogTime >= 5000)
                        {
                            AILog.Log(AIFps + " FPS | conf=" + activeConfig.AI_Conf + " | force=" + activeConfig.AI_AimForce + " | kp=" + activeConfig.AI_AimKp);
                            lastLogTime = nowFrame;
                        }
                    }

                    long elapsed = loopTimer.ElapsedMilliseconds;
                    int targetFps = Math.Max(30, Math.Min(500, activeConfig.AI_TargetFps));
                    int targetMs = 1000 / targetFps;
                    int sleep = targetMs - (int)elapsed;
                    if (sleep > 0) Thread.Sleep(sleep);
                }
                gDest?.Dispose();
                reusableBmp?.Dispose();
            }
            catch (Exception ex)
            {
                AILog.Log("ERRO FATAL: " + ex.Message);
                System.Windows.MessageBox.Show("ERRO FATAL NA IA:\n\n" + ex.Message, "CRASH",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
       }
        private string ResolveModelPath(string requested)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(requested))
            {
                // Normaliza separadores
                string normalized = requested.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

                // Se comeca com models/, resolve direto
                if (normalized.StartsWith("models" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    string fullPath = Path.Combine(baseDir, normalized);
                    if (File.Exists(fullPath)) return fullPath;
                }
                // Tenta caminho absoluto
                if (Path.IsPathRooted(normalized) && File.Exists(normalized)) return normalized;
                // Tenta no diretorio raiz
                string rootPath = Path.Combine(baseDir, Path.GetFileName(normalized));
                if (File.Exists(rootPath)) return rootPath;
                // Tenta na pasta models/
                string modelsPath = Path.Combine(baseDir, "models", Path.GetFileName(normalized));
                if (File.Exists(modelsPath)) return modelsPath;
            }
            // Busca em baseDir, depois models/, depois subpastas
            var files = Directory.GetFiles(baseDir, "*.onnx");
            if (files.Length > 0) return files[0];
            string modelsDir = Path.Combine(baseDir, "models");
            if (Directory.Exists(modelsDir))
            {
                var modelFiles = Directory.GetFiles(modelsDir, "*.onnx");
                if (modelFiles.Length > 0) return modelFiles[0];
            }
            return null;
        }
        public void ShowVisionDebug()
        {
            string dbg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Debug", "vision_debug.bmp");
            if (!File.Exists(dbg))
            {
                System.Windows.MessageBox.Show("Nenhuma imagem disponivel.\nAtive AI + Show Vision e aguarde.", "Vision Debug");
                return;
            }
            try
            {
                var win = new System.Windows.Window
                {
                    Title = "VORTEX AI Vision", Width = 520, Height = 560,
                    Background = System.Windows.Media.Brushes.Black,
                    WindowStyle = System.Windows.WindowStyle.ToolWindow,
                    Topmost = true, ShowInTaskbar = true
                };
                var img = new System.Windows.Controls.Image();
                var src = new System.Windows.Media.Imaging.BitmapImage();
                src.BeginInit(); src.UriSource = new Uri(dbg);
                src.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                src.EndInit();
                img.Source = src; img.Stretch = System.Windows.Media.Stretch.Uniform;
                win.Content = img; win.ShowDialog();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Erro: " + ex.Message, "Vision Debug");
            }
        }

        // CLASSES AUXILIARES (anteriormente em Classes.cs e CoreModels.cs)
        public struct Vector2Data
        {
            public int X;
            public int Y;
            public int Trigger;
            public long Timestamp;
            public float Confidence;
        }

        public static class SharedState
        {
            public static volatile int sharedDeltaX = 0;
            public static volatile int sharedDeltaY = 0;
            public static volatile bool rawLMB = false;
            public static volatile bool rawRMB = false;
            public static volatile bool EmulatorActive = false;
            public static System.Collections.Generic.Queue<Vector2Data> aimbotQueue = new System.Collections.Generic.Queue<Vector2Data>();
            public static object aimLock = new object();
            public static volatile int aimbotTrigger = 0;
            public static volatile int aimbotTimestamp = 0;

        }

        public static class AILog
        {
            static event Action<string> _updated;
            static System.Collections.Generic.List<string> _l = new System.Collections.Generic.List<string>();
            static object _k = new object();
            public static void Log(string m)
            {
                string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + m;
                lock (_k) { _l.Add(line); if (_l.Count > 200) _l.RemoveAt(0); }
                _updated?.Invoke(line);
            }
            public static void Clear()
            {
                lock (_k) _l.Clear();
                _updated?.Invoke("[CLEARED]");
            }
            public static string[] GetAll()
            {
                lock (_k) return _l.ToArray();
            }
            public static event Action<string> OnUpdated
            {
                add { _updated += value; }
                remove { _updated -= value; }
            }
        }

        public static int AIFps = 0;
    }

    public class UserProfile
    {
        public string Name { get; set; } = "Default";
        public double BaseSens { get; set; } = 3.0;
        public double YRatio { get; set; } = 2.0;
        public double CurveResponse { get; set; } = 1.5;
        public double Smoothing { get; set; } = 0.08;
        public double ParachuteMultiplier { get; set; } = 2.5;
        public string ThemeColor { get; set; } = "#FFC107";
        public bool AI_ShowVision { get; set; } = false;
        public bool TriggerbotEnabled { get; set; } = false;
        public int TriggerDelay { get; set; } = 150;
        public bool QuickScopeEnabled { get; set; } = false;
        public int AimSpamDelay { get; set; } = 150;
        public bool SmartRecoilEnabled { get; set; } = true;
        public bool RapidFireEnabled { get; set; } = false;
        public int RapidFireDelay { get; set; } = 50;
        public int RecoilStrength { get; set; } = 28;
        public bool AutoPingEnabled { get; set; } = false;
        public bool BunnyHopEnabled { get; set; } = false;
        public bool SoundsEnabled { get; set; } = true;
        public bool YYEnabled { get; set; } = false;
        public int YYDelay { get; set; } = 60;
        public int KeyYY { get; set; } = 0x05;
        public bool RotationalAssist { get; set; } = false;
        public int RotationalSpeed { get; set; } = 20;
        public int RotationalIntensity { get; set; } = 5;
        public bool AI_Enabled { get; set; } = false;
        public bool AI_DrawFov { get; set; } = false;
        public int AI_FovWidth { get; set; } = 300;
        public int AI_FovHeight { get; set; } = 150;
        public bool AI_InvertX { get; set; } = false;
        public bool AI_InvertY { get; set; } = true;
        public string AI_TargetIP { get; set; } = "127.0.0.1";
        public double ColorSpeed { get; set; } = 0.8;
        public int AITargetBone { get; set; } = 1;
        public double AI_Kp { get; set; } = 85.0;
        public double AI_Kd { get; set; } = 50.0;
        public double AI_AimKp { get; set; } = 1.0;
        public double AI_AimKi { get; set; } = 0.004;
        public double AI_FilterAlpha { get; set; } = 0.35;
        public double AI_IntegralClamp { get; set; } = 5000.0;
        public double AI_MicroDeadzone { get; set; } = 0.5;
        public string ip { get; set; } = string.Empty;
        public double AI_Conf { get; set; } = 45.0;
        public bool AI_RageMode { get; set; } = false;
        public double Humanizer { get; set; } = 2.0;
        public bool ColorBotEnabled { get; set; } = false;
        public bool UseHSV { get; set; } = true;
        public string TargetHex { get; set; } = "#FF0000";
        public int Fov { get; set; } = 100;
        public int AimOffsetY { get; set; } = 0;
        public int Density { get; set; } = 5;
        public double AI_AimKd { get; set; } = 0.15;
        public double AI_TargetPersistence { get; set; } = 40.0;
        public bool HybridMouseEnabled { get; set; } = true;
        public int AntiRecoilRamp { get; set; } = 100;
        public double AI_AimForce { get; set; } = 1.0;
        public string AI_ModelPath { get; set; } = "best.onnx";
        public string AI_GpuProvider { get; set; } = "DirectML";
        public bool AI_Int8Enabled { get; set; } = false;
        public int AI_TargetFps { get; set; } = 143;
        public int KeyEmulatorToggle { get; set; } = 0x24;
        public int KeyParachute { get; set; } = 0x12;
        public int KeyShoot { get; set; } = 0x01;
        public int KeyAim { get; set; } = 0x02;
        public int KeyLethal { get; set; } = 0x04;
        public int KeyTactical { get; set; } = 0x51;
        public int KeyJump { get; set; } = 0x20;
        public int KeyCrouch { get; set; } = 0x43;
        public int KeyReload { get; set; } = 0x52;
        public int KeySwap { get; set; } = 0x31;
        public int KeyMelee { get; set; } = 0x56;
        public int KeySprint { get; set; } = 0xA0;
        public int KeyPing { get; set; } = 0x5A;
        public int KeyBackpack { get; set; } = 0x09;
        public int KeyKillstreak { get; set; } = 0x33;
        public int KeyFireMode { get; set; } = 0x42;
        public int KeyMap { get; set; } = 0x4D;
        public int KeyMenu { get; set; } = 0x1B;
        public Dictionary<string, System.Windows.Point> LayoutPositions { get; set; } = new Dictionary<string, System.Windows.Point>();
    }

    public class FovOverlayWindow : Window
    {
        private System.Windows.Shapes.Rectangle fovRect;
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int GWL_EXSTYLE = -20;
        public FovOverlayWindow()
        {
            this.WindowStyle = WindowStyle.None;
            this.AllowsTransparency = true;
            this.Background = System.Windows.Media.Brushes.Transparent;
            this.Topmost = true;
            this.ShowInTaskbar = false;
            fovRect = new System.Windows.Shapes.Rectangle();
            fovRect.Stroke = System.Windows.Media.Brushes.Red;
            fovRect.StrokeThickness = 2;
            var canvas = new Canvas();
            canvas.Children.Add(fovRect);
            this.Content = canvas;
            this.Loaded += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
            };
        }
        public void UpdateFOV(int width, int height)
        {
            this.Dispatcher.Invoke(() =>
            {
                double screenW = SystemParameters.PrimaryScreenWidth;
                double screenH = SystemParameters.PrimaryScreenHeight;
                this.Width = screenW;
                this.Height = screenH;
                this.Left = 0;
                this.Top = 0;
                fovRect.Width = width;
                fovRect.Height = height;
                Canvas.SetLeft(fovRect, (screenW / 2) - (width / 2));
                Canvas.SetTop(fovRect, (screenH / 2) - (height / 2));
                this.Visibility = Visibility.Visible;
            });
        }
    }

    public static class NativeMethods
    {
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int v);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool ClipCursor(ref RECT lpRect);
        [DllImport("user32.dll")] public static extern bool ClipCursor(IntPtr lpRect);
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] public static extern uint TimeBeginPeriod(uint p);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] public static extern uint TimeEndPeriod(uint p);
        [DllImport("user32.dll")] public static extern bool RegisterRawInputDevices([MarshalAs(UnmanagedType.LPArray)] RAWINPUTDEVICE[] p, uint n, uint s);
        [DllImport("user32.dll")] public static extern int GetRawInputData(IntPtr h, uint c, IntPtr p, ref uint s, int hS);
        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget;
        }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    }

    public static class Interception
    {
        [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr interception_create_context();
        [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern void interception_set_filter(IntPtr c, InterceptionPredicate p, ushort f);
        [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int interception_wait(IntPtr c);
        [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int interception_receive(IntPtr c, int d, ref Stroke s, uint n);
        [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int interception_send(IntPtr c, int d, ref Stroke s, uint n);
        [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int interception_is_mouse(int d); public delegate int InterceptionPredicate(int d);
        public const ushort INTERCEPTION_FILTER_MOUSE_ALL = 0xFFFF;
        [StructLayout(LayoutKind.Sequential)]
        public struct MouseStroke
        {
            public ushort state; public ushort flags; public short rolling;
            public int x; public int y; public uint information;
        }
        [StructLayout(LayoutKind.Explicit)]
        public struct Stroke
        {
            [FieldOffset(0)] public MouseStroke mouse;
            [FieldOffset(0)] public byte data;
        }
    }

    public class AIConfig
    {
        public string ip { get; set; } = "127.0.0.1";
        public int port { get; set; } = 9999;
        public int fov_w { get; set; } = 300;
        public int fov_h { get; set; } = 150;
        public int target_bone { get; set; } = 1;
        public bool show_vision { get; set; }
    }
}
