#nullable disable
#pragma warning disable CS8618 
#pragma warning disable CS8602 
#pragma warning disable CS8600 
#pragma warning disable CS8604
#pragma warning disable CS8605

using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace emu2026
{
    public partial class MainWindow
    {
        private const double OutputMultiplier = 80.0;
        private const short GameDeadzone = 2800;
        private static long lastToggleTime = 0;
        private static volatile bool macroYState = false;
        private static bool autoPingActive = false;
        private static long autoPingStartTime = 0;
        private static long lastDropAllMoneyTime = 0;
        private static DropAllMoneyMacroState dropAllMoneyMacroState = DropAllMoneyMacroState.Idle;
        private static long dropAllMoneyNextStepAt = 0;
        private const int DropAllMoneyButtonHoldMs = 80;
        private System.Windows.Forms.Form inputMessageWindow;

        private enum DropAllMoneyMacroState
        {
            Idle,
            OpeningBackpackPress,
            OpeningBackpackRelease,
            DroppingMoneyPress,
            DroppingMoneyRelease,
            ClosingBackpackPress,
            ClosingBackpackRelease
        }

        public void SetupController()
        {
            stopWorker = true;
            if (workerThread != null && workerThread.IsAlive) workerThread.Join(500);
            if (xbox != null) { try { xbox.Disconnect(); } catch { } xbox = null; }
            stopWorker = false;
            xbox = client.CreateXbox360Controller();
            xbox.Connect();
            workerThread = new Thread(NuclearWorkerXbox) { Priority = ThreadPriority.Highest, IsBackground = true };
            workerThread.Start();
        }

        static double ApplyCurve(double v, double s, double c) { double sign = v >= 0 ? 1.0 : -1.0; v = Math.Abs(v); v = Math.Pow(v, c) * s; return sign * v; }
        private bool IsKey(int k) { return (RawInputAPI.GetAsyncKeyState(k) & 0x8000) != 0; }
        private bool IsBindPressed(int bindCode)
        {
            return bindCode switch
            {
                0x01 => SharedState.rawLMB || IsKey(bindCode),
                0x02 => SharedState.rawRMB || IsKey(bindCode),
                0x04 => SharedState.rawMMB || IsKey(bindCode),
                0x05 => SharedState.rawX1 || IsKey(bindCode),
                0x06 => SharedState.rawX2 || IsKey(bindCode),
                _ => IsKey(bindCode)
            };
        }
        static void LockMouse(int cx, int cy) { RawInputAPI.RECT r = new RawInputAPI.RECT(); r.Left = cx; r.Top = cy; r.Right = cx + 1; r.Bottom = cy + 1; RawInputAPI.ClipCursor(ref r); }
        static void UnlockMouse() { RawInputAPI.ClipCursor(IntPtr.Zero); }

        private void StartDropAllMoneyMacro(long now)
        {
            if (dropAllMoneyMacroState != DropAllMoneyMacroState.Idle || activeConfig.KeyDropAllMoney == 0)
            {
                return;
            }

            dropAllMoneyMacroState = DropAllMoneyMacroState.OpeningBackpackPress;
            dropAllMoneyNextStepAt = now;
            lastDropAllMoneyTime = now;
        }

        private void UpdateDropAllMoneyMacro(long now, ref bool jump, ref bool crouch, ref bool reload, ref bool swap, ref bool tact, ref bool leth, ref bool sprint, ref bool melee, ref bool ping, ref bool firemode, ref bool killstreak, ref bool backpack, ref bool map, ref bool menu)
        {
            if (dropAllMoneyMacroState == DropAllMoneyMacroState.Idle || now < dropAllMoneyNextStepAt)
            {
                return;
            }

            switch (dropAllMoneyMacroState)
            {
                case DropAllMoneyMacroState.OpeningBackpackPress:
                    backpack = true;
                    dropAllMoneyMacroState = DropAllMoneyMacroState.OpeningBackpackRelease;
                    dropAllMoneyNextStepAt = now + DropAllMoneyButtonHoldMs;
                    break;

                case DropAllMoneyMacroState.OpeningBackpackRelease:
                    dropAllMoneyMacroState = DropAllMoneyMacroState.DroppingMoneyPress;
                    dropAllMoneyNextStepAt = now + Math.Max(50, activeConfig.DropAllMoneyOpenDelayMs);
                    break;

                case DropAllMoneyMacroState.DroppingMoneyPress:
                    swap = true;
                    dropAllMoneyMacroState = DropAllMoneyMacroState.DroppingMoneyRelease;
                    dropAllMoneyNextStepAt = now + DropAllMoneyButtonHoldMs;
                    break;

                case DropAllMoneyMacroState.DroppingMoneyRelease:
                    dropAllMoneyMacroState = DropAllMoneyMacroState.ClosingBackpackPress;
                    dropAllMoneyNextStepAt = now + Math.Max(50, activeConfig.DropAllMoneyActionDelayMs);
                    break;

                case DropAllMoneyMacroState.ClosingBackpackPress:
                    crouch = true;
                    dropAllMoneyMacroState = DropAllMoneyMacroState.ClosingBackpackRelease;
                    dropAllMoneyNextStepAt = now + DropAllMoneyButtonHoldMs;
                    break;

                case DropAllMoneyMacroState.ClosingBackpackRelease:
                    dropAllMoneyMacroState = DropAllMoneyMacroState.Idle;
                    dropAllMoneyNextStepAt = now + Math.Max(50, activeConfig.DropAllMoneyCloseDelayMs);
                    break;
            }
        }

        private void InputListenerWorker()
        {
            var w = new InputMessageWindow();
            inputMessageWindow = w;
            RegisterRawInput(w.Handle);
            System.Windows.Forms.Application.Run(w);
        }

        static void RegisterRawInput(IntPtr h)
        {
            RawInputAPI.RAWINPUTDEVICE[] r = new RawInputAPI.RAWINPUTDEVICE[1];
            r[0].usUsagePage = 0x01;
            r[0].usUsage = 0x02;
            r[0].dwFlags = 0x00000100;
            r[0].hwndTarget = h;
            RawInputAPI.RegisterRawInputDevices(r, 1, (uint)Marshal.SizeOf(typeof(RawInputAPI.RAWINPUTDEVICE)));
        }

        class InputMessageWindow : System.Windows.Forms.Form
        {
            protected override void SetVisibleCore(bool v) { base.SetVisibleCore(false); }
            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                const int WM_INPUT = 0x00FF;
                if (m.Msg == WM_INPUT && SharedState.EmulatorActive)
                {
                    int pcbSize = 0;
                    RawInputAPI.GetRawInputData(m.LParam, 0x10000003, IntPtr.Zero, ref pcbSize, Marshal.SizeOf(typeof(RawInputAPI.RAWINPUTHEADER)));

                    if (pcbSize > 0)
                    {
                        IntPtr pData = Marshal.AllocHGlobal(pcbSize);
                        if (RawInputAPI.GetRawInputData(m.LParam, 0x10000003, pData, ref pcbSize, Marshal.SizeOf(typeof(RawInputAPI.RAWINPUTHEADER))) == pcbSize)
                        {
                            RawInputAPI.RAWINPUT raw = (RawInputAPI.RAWINPUT)Marshal.PtrToStructure(pData, typeof(RawInputAPI.RAWINPUT));
                            if (raw.header.dwType == 0)
                            {
                                Interlocked.Add(ref SharedState.sharedDeltaX, raw.mouse.lLastX);
                                Interlocked.Add(ref SharedState.sharedDeltaY, raw.mouse.lLastY);

                                if ((raw.mouse.usButtonFlags & 0x0001) != 0) SharedState.rawLMB = true;
                                if ((raw.mouse.usButtonFlags & 0x0002) != 0) SharedState.rawLMB = false;
                                if ((raw.mouse.usButtonFlags & 0x0004) != 0) SharedState.rawRMB = true;
                                if ((raw.mouse.usButtonFlags & 0x0008) != 0) SharedState.rawRMB = false;
                                if ((raw.mouse.usButtonFlags & 0x0010) != 0) SharedState.rawMMB = true;
                                if ((raw.mouse.usButtonFlags & 0x0020) != 0) SharedState.rawMMB = false;
                                if ((raw.mouse.usButtonFlags & 0x0080) != 0)
                                {
                                    if (raw.mouse.usButtonData == 1) SharedState.rawX1 = true;
                                    if (raw.mouse.usButtonData == 2) SharedState.rawX2 = true;
                                }
                                if ((raw.mouse.usButtonFlags & 0x0100) != 0)
                                {
                                    if (raw.mouse.usButtonData == 1) SharedState.rawX1 = false;
                                    if (raw.mouse.usButtonData == 2) SharedState.rawX2 = false;
                                }
                            }
                        }
                        Marshal.FreeHGlobal(pData);
                    }
                }
                base.WndProc(ref m);
            }
        }

        public void NuclearWorkerXbox()
        {
            float dpiX, dpiY;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { dpiX = g.DpiX / 96f; dpiY = g.DpiY / 96f; }

            var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            int cx = (int)(bounds.Width * dpiX) / 2;
            int cy = (int)(bounds.Height * dpiY) / 2;

            double smoothX = 0, smoothY = 0, residualX = 0, residualY = 0;

            long lastShotTime = 0;
            bool rapidFireToggle = false;
            bool wasShooting = false;
            long burstStartTime = 0;
            long targetEnterTime = 0;

            Random humanizerRandom = new Random();
            Stopwatch hzTimer = Stopwatch.StartNew();
            Stopwatch stopwatch = Stopwatch.StartNew();
            double targetMs = 1.0;

            int currentAimbotX = 0;
            int currentAimbotY = 0;
            // Aimbot smoothing / precision state
            double aiFilterX = 0.0;
            double aiFilterY = 0.0;
            double aiIntegralX = 0.0;
            double aiIntegralY = 0.0;
            double aiDerivX = 0.0;
            double aiDerivY = 0.0;
            double lastErrorX = 0.0;
            double lastErrorY = 0.0;

            bool lghubInitialized = false;
            bool lghubAvailable = false;
            long recoilStartTime = 0;

            while (!stopWorker)
            {
                if ((RawInputAPI.GetAsyncKeyState(activeConfig.KeyEmulatorToggle) & 0x8000) != 0)
                {
                    if (stopwatch.ElapsedMilliseconds - lastToggleTime > 300)
                    {
                        SharedState.EmulatorActive = !SharedState.EmulatorActive;
                        if (SharedState.EmulatorActive)
                        {
                            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { dpiX = g.DpiX / 96f; dpiY = g.DpiY / 96f; }
                            bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                            cx = (int)(bounds.Width * dpiX) / 2;
                            cy = (int)(bounds.Height * dpiY) / 2;
                            smoothX = 0; smoothY = 0;
                            residualX = 0; residualY = 0;
                            Interlocked.Exchange(ref SharedState.sharedDeltaX, 0);
                            Interlocked.Exchange(ref SharedState.sharedDeltaY, 0);
                            LockMouse(cx, cy);
                            System.Windows.Forms.Cursor.Hide(); // Esconde o cursor completamente
                            if (activeConfig.SoundsEnabled) System.Media.SystemSounds.Asterisk.Play();
                        }
                        else
                        {
                            System.Windows.Forms.Cursor.Show(); // Mostra o cursor ao destravar
                            UnlockMouse();
                            if (xbox != null) xbox.ResetReport();
                            if (activeConfig.SoundsEnabled) System.Media.SystemSounds.Exclamation.Play();
                        }
                        lastToggleTime = stopwatch.ElapsedMilliseconds;
                    }
                }

                if (!SharedState.EmulatorActive) { Thread.SpinWait(10); continue; }

                RawInputAPI.SetCursorPos(cx, cy);
                if (stopwatch.ElapsedMilliseconds % 100 == 0) LockMouse(cx, cy);

                // MOUSE → CONTROLE: lê o movimento do mouse e converte para analógico
                // O cursor fica escondido (Cursor.Hide no lock), então não aparece tremor
                int dx = Interlocked.Exchange(ref SharedState.sharedDeltaX, 0);
                int dy = Interlocked.Exchange(ref SharedState.sharedDeltaY, 0);
                double s = activeConfig.Smoothing;
                if (s > 0) { smoothX = (smoothX * (1.0 - s)) + (dx * s); smoothY = (smoothY * (1.0 - s)) + (dy * s); }
                else { smoothX = dx; smoothY = dy; }

                double currentSensMod = IsKey(activeConfig.KeyParachute) ? activeConfig.ParachuteMultiplier : 1.0;
                double valX = ApplyCurve(smoothX, (activeConfig.BaseSens * currentSensMod), activeConfig.CurveResponse);
                double valY = ApplyCurve(smoothY, (activeConfig.BaseSens * currentSensMod), activeConfig.CurveResponse) * activeConfig.YRatio;
                double rawOutputX = valX * OutputMultiplier;
                double rawOutputY = valY * OutputMultiplier;

                double magnitude = Math.Sqrt(rawOutputX * rawOutputX + rawOutputY * rawOutputY);
                double finalX = 0, finalY = 0;
                if (magnitude > 200)
                {
                    double normX = rawOutputX / magnitude;
                    double normY = rawOutputY / magnitude;
                    double targetMag = magnitude + GameDeadzone;
                    finalX = normX * targetMag;
                    finalY = normY * targetMag;
                }
                finalX += residualX;
                finalY += residualY;
                int tX = (int)finalX;
                int tY = (int)finalY;
                residualX = finalX - tX;
                residualY = finalY - tY;

                // 1. LEITURA DOS BOTÕES FÍSICOS
                long now = stopwatch.ElapsedMilliseconds;

                bool aim = IsBindPressed(activeConfig.KeyAim);
                bool sht = IsBindPressed(activeConfig.KeyShoot);
                bool tact = IsBindPressed(activeConfig.KeyTactical);
                bool jump = IsBindPressed(activeConfig.KeyJump);
                bool crouch = IsBindPressed(activeConfig.KeyCrouch);
                bool reload = IsBindPressed(activeConfig.KeyReload);
                bool swap = IsBindPressed(activeConfig.KeySwap);
                bool leth = IsBindPressed(activeConfig.KeyLethal);
                bool sprint = IsBindPressed(activeConfig.KeySprint);
                bool melee = IsBindPressed(activeConfig.KeyMelee);
                bool firemode = IsBindPressed(activeConfig.KeyFireMode);
                bool killstreak = IsBindPressed(activeConfig.KeyKillstreak);
                bool backpack = IsBindPressed(activeConfig.KeyBackpack);
                bool map = IsBindPressed(activeConfig.KeyMap);
                bool menu = IsBindPressed(activeConfig.KeyMenu);
                bool dropAllMoneyPressed = IsBindPressed(activeConfig.KeyDropAllMoney);

                if (dropAllMoneyPressed && now - lastDropAllMoneyTime > 700)
                {
                    StartDropAllMoneyMacro(now);
                }

                // --- SOLUÇÃO: DECLARAR VARIÁVEIS DA IA ANTES DE SEREM USADAS ---
                int aiX = 0;
                int aiY = 0;
                bool aiAim = false;
                bool aiShoot = false;
                // ---------------------------------------------------------------

                // 2. MACRO: RAPID FIRE
                bool trig = sht;
                if (trig && activeConfig.RapidFireEnabled)
                {
                    if (stopwatch.ElapsedMilliseconds - lastShotTime > activeConfig.RapidFireDelay)
                    {
                        rapidFireToggle = !rapidFireToggle;
                        lastShotTime = stopwatch.ElapsedMilliseconds;
                    }
                    trig = rapidFireToggle;
                }

                // 3. ESTADO DE DISPARO (Agora a variável aiShoot já existe!)
                bool currentShoot = trig || aiShoot;
                if (currentShoot && !wasShooting) burstStartTime = stopwatch.ElapsedMilliseconds;

                // 4. MACRO: AUTO PING — mantem o ping por 200ms ao começar a atirar
                bool ping = IsBindPressed(activeConfig.KeyPing);
                if (activeConfig.AutoPingEnabled)
                {
                    if (currentShoot && !wasShooting)
                    {
                        autoPingActive = true;
                        autoPingStartTime = stopwatch.ElapsedMilliseconds;
                    }
                    if (autoPingActive && stopwatch.ElapsedMilliseconds - autoPingStartTime < 200)
                    {
                        ping = true;
                    }
                    else
                    {
                        autoPingActive = false;
                    }
                }
                wasShooting = currentShoot;

                // Smart Recoil: quando ativo e o jogador esta mirando, nao aplica recoil manual
                // A IA ja compensa automaticamente atraves do tracking do alvo
                bool applyRecoil = true;
                if (activeConfig.SmartRecoilEnabled && aim)
                {
                    applyRecoil = false;
                }

                // 5. MACRO: YY (Spam de Troca de Arma)
                if (activeConfig.YYEnabled && activeConfig.KeyYY != 0 && IsBindPressed(activeConfig.KeyYY))
                {
                    macroYState = (stopwatch.ElapsedMilliseconds % activeConfig.YYDelay) < (activeConfig.YYDelay / 2);
                }
                else
                {
                    macroYState = false;
                }

                UpdateDropAllMoneyMacro(now, ref jump, ref crouch, ref reload, ref swap, ref tact, ref leth, ref sprint, ref melee, ref ping, ref firemode, ref killstreak, ref backpack, ref map, ref menu);

                // --- LEITURA DA FILA DO AIMBOT (Comunicação com a IA) ---
                lock (SharedState.aimLock)
                {
                    while (SharedState.aimbotQueue.Count > 1) SharedState.aimbotQueue.Dequeue();

                    if (SharedState.aimbotQueue.Count > 0)
                    {
                        var d = SharedState.aimbotQueue.Peek();
                        if (unchecked(Environment.TickCount - d.Timestamp) < 500)
                        {
                            currentAimbotX = d.X;
                            currentAimbotY = d.Y;
                            SharedState.aimbotTrigger = d.Trigger;
                            SharedState.aimbotTimestamp = (int)d.Timestamp;
                        }
                        SharedState.aimbotQueue.Dequeue();
                    }
                }

                if (unchecked(Environment.TickCount - SharedState.aimbotTimestamp) < 600)
                {
                    double errorX = currentAimbotX;
                    double errorY = currentAimbotY;
                    double distance = Math.Sqrt(errorX * errorX + errorY * errorY);

                    // PIXEL LOCK: alvo no centro = TRAVA, para qualquer correcao
                    if (distance <= 3.0)
                    {
                        aiX = 0;
                        aiY = 0;
                        // Reseta filtros para evitar acumulo
                        aiFilterX = 0.0; aiFilterY = 0.0;
                        aiIntegralX = 0.0; aiIntegralY = 0.0;
                    }
                    else
                    {
                        // MODO LOCK DIRETO: offset * forca
                        // Sem filtro, sem integral, sem deadzone
                        // A forca vem de AI_AimForce (slider da UI, default 1.0 = 1x)
                        double force = Math.Max(1.0, activeConfig.AI_AimForce);
                        // AI_AimKp * 2 para mais ganho base
                        double kp = Math.Max(1.0, activeConfig.AI_AimKp * 2.0);

                        // EMA LEVE so para evitar jitter de 1px (alpha alto = quase direto)
                        double alpha = 0.85;
                        aiFilterX += (errorX - aiFilterX) * alpha;
                        aiFilterY += (errorY - aiFilterY) * alpha;

                        double outX = kp * aiFilterX * force;
                        double outY = kp * aiFilterY * force;

                        if (activeConfig.AI_RageMode) { outX *= 8.0; outY *= 8.0; }

                        aiX = (int)Math.Clamp(outX, -32767, 32767);
                        aiY = (int)Math.Clamp(outY, -32767, 32767);
                    }

                    if (activeConfig.TriggerbotEnabled && SharedState.aimbotTrigger == 1)
                    {
                        if (targetEnterTime == 0) targetEnterTime = stopwatch.ElapsedMilliseconds;
                        long requiredDelay = activeConfig.QuickScopeEnabled ? 0 : activeConfig.TriggerDelay;
                        if (stopwatch.ElapsedMilliseconds - targetEnterTime >= requiredDelay)
                        {
                            aiShoot = true;
                            if (activeConfig.QuickScopeEnabled) aiAim = true;
                        }
                    }
                    else { targetEnterTime = 0; }
                }
                else
                {
                    aiX = 0; aiY = 0;
                    currentAimbotX = 0; currentAimbotY = 0;
                    targetEnterTime = 0;
                    aiDerivX = 0.0; aiDerivY = 0.0;
                    lastErrorX = 0.0; lastErrorY = 0.0;
                }

                // 6. MACRO: ANTI-RECOIL
                int recoilValue = 0;
                if (currentShoot && applyRecoil)
                {
                    if (recoilStartTime == 0) recoilStartTime = stopwatch.ElapsedMilliseconds;
                    long shootDuration = stopwatch.ElapsedMilliseconds - recoilStartTime;
                    int rampMs = activeConfig.AntiRecoilRamp > 0 ? activeConfig.AntiRecoilRamp : 100;
                    double rampFactor = Math.Min(1.0, (double)shootDuration / rampMs);
                    double forcaRaw = 2000.0 + (activeConfig.RecoilStrength * 300.0);
                    recoilValue = (int)(forcaRaw * rampFactor);
                }
                else
                {
                    recoilStartTime = 0;
                }

                bool isAimingFisico = aim || IsKey(0x02);
                bool isShootingFisico = sht || IsKey(0x01);
                bool hasAiCorrection = (aiX != 0 || aiY != 0);

                if (isAimingFisico || isShootingFisico || aiShoot)
                {
                    // MODO HÍBRIDO: usa Logitech para precisão direta quando disponível
                    if (activeConfig.HybridMouseEnabled && !lghubInitialized)
                    {
                        lghubInitialized = true;
                        lghubAvailable = LghubMouse.Iniciar();
                    }

                    if (activeConfig.HybridMouseEnabled && lghubAvailable && LghubMouse.EstaConectado() && hasAiCorrection)
                    {
                        // Envia correção da IA direto ao mouse via ghub (sub-pixel)
                        // Converte stick value (-32768..32767) para delta de pixel proporcional
                        double mouseDeltaX = aiX / 100.0;
                        double mouseDeltaY = aiY / 100.0;
                        LghubMouse.MoverPreciso(mouseDeltaX, mouseDeltaY);
                        // Não soma ao stick quando usamos mouse direto
                    }
                    else
                    {
                        // Fallback: Xbox virtual stick (código original)
                        tX += aiX;
                        tY += aiY;
                    }
                }

                // Inicializamos o Analógico Esquerdo (Movimento / WASD)
                short lX = 0;
                short lY = 0;

                // 7. MACRO: ROTATIONAL ASSIST (Aplicado no Analógico Esquerdo / Boneco)
                // SO ativa se o jogador NAO estiver a mover (WASD inativo)
                bool playerMoving = IsBindPressed(0x57) || IsBindPressed(0x41) || IsBindPressed(0x53) || IsBindPressed(0x44); // W, A, S, D
                if (activeConfig.RotationalAssist && isAimingFisico && !playerMoving)
                {
                    double rotTime = stopwatch.ElapsedMilliseconds;
                    double rotSpeed = Math.Max(10.0, activeConfig.RotationalSpeed);
                    double rotIntensity = 4000.0 + (activeConfig.RotationalIntensity * 500.0);
                    lX = (short)(Math.Sin(rotTime / rotSpeed) * rotIntensity);
                    lY = (short)(Math.Cos(rotTime / rotSpeed) * rotIntensity);
                }
                else if (playerMoving)
                {
                    // Se o jogador esta a mover, NAO aplica rotacao mas mantem aim assist
                    // O aim assist no Right Stick continua a funcionar normalmente
                    lX = 0;
                    lY = 0;
                }

                // Apply recoil AFTER mouse→stick conversion, BEFORE final clamp
                // -recoilValue = puxa pra BAIXO (contrario do recoil que puxa pra cima)
                tY += recoilValue;

                // Garante que o Analógico Direito não ultrapassa os limites físicos do comando
                short sX = (short)Math.Clamp(tX, -32767, 32767);
                short sY = (short)Math.Clamp(tY, -32767, 32767);

                if (activeConfig.BunnyHopEnabled && jump) { jump = (stopwatch.ElapsedMilliseconds % 100) < 50; }

                // 8. MACRO: AIM SPAM (QuickScope)
                bool finalAim = aim || aiAim;
                bool finalShoot = currentShoot;

                if (activeConfig.QuickScopeEnabled && finalShoot)
                {
                    int spamDelay = Math.Max(100, activeConfig.AimSpamDelay);
                    int halfDelay = spamDelay / 2;
                    long aimCycle = stopwatch.ElapsedMilliseconds % spamDelay;
                    finalAim = (aimCycle < halfDelay);
                }

                // 9. ENVIO DE INSTRUÇÕES PARA O COMANDO VIRTUAL
                if (xbox != null)
                {
                    xbox.SetSliderValue(Xbox360Slider.LeftTrigger, (byte)(finalAim ? 255 : 0));
                    xbox.SetSliderValue(Xbox360Slider.RightTrigger, (byte)(finalShoot ? 255 : 0));
                    xbox.SetButtonState(Xbox360Button.A, jump);
                    xbox.SetButtonState(Xbox360Button.B, crouch);
                    xbox.SetButtonState(Xbox360Button.X, reload);
                    xbox.SetButtonState(Xbox360Button.Y, swap || macroYState);
                    xbox.SetButtonState(Xbox360Button.LeftShoulder, tact);
                    xbox.SetButtonState(Xbox360Button.RightShoulder, leth);
                    xbox.SetButtonState(Xbox360Button.LeftThumb, sprint);
                    xbox.SetButtonState(Xbox360Button.RightThumb, melee);
                    xbox.SetButtonState(Xbox360Button.Up, ping);
                    xbox.SetButtonState(Xbox360Button.Left, firemode);
                    xbox.SetButtonState(Xbox360Button.Right, killstreak);
                    xbox.SetButtonState(Xbox360Button.Down, backpack);
                    xbox.SetButtonState(Xbox360Button.Back, map);
                    xbox.SetButtonState(Xbox360Button.Start, menu);

                    // Eixos Finais!
                    xbox.SetAxisValue(Xbox360Axis.RightThumbX, sX);
                    xbox.SetAxisValue(Xbox360Axis.RightThumbY, (short)-sY); // O '-sY' inverte a lógica nativa do comando

                    // Envia os micro-passos do boneco gerados pelo Rotational Assist
                    xbox.SetAxisValue(Xbox360Axis.LeftThumbX, lX);
                    xbox.SetAxisValue(Xbox360Axis.LeftThumbY, lY);

                    xbox.SubmitReport();
                }

                while (hzTimer.Elapsed.TotalMilliseconds < targetMs) { Thread.SpinWait(10); }
                hzTimer.Restart();
            }
        }
    }
}
