using System;
using System.Runtime.InteropServices;

namespace emu2026
{
    public static class RawInputAPI
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        public static extern uint TimeBeginPeriod(uint uMilliseconds);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        public static extern uint TimeEndPeriod(uint uMilliseconds);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        public static extern bool ClipCursor(ref RECT lpRect);
        [DllImport("user32.dll")]
        public static extern bool ClipCursor(IntPtr lpRect);
        [DllImport("user32.dll")]
        public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
        [DllImport("user32.dll")]
        public static extern int GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref int pcbSize, int cbSizeHeader);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        public const uint INPUT_KEYBOARD = 1;
        public const uint INPUT_MOUSE = 0;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_SCANCODE = 0x0008;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint MAPVK_VK_TO_VSC = 0;

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_XDOWN = 0x0080;
        public const uint MOUSEEVENTF_XUP = 0x0100;

        public static void PressBind(int bindCode)
        {
            switch (bindCode)
            {
                case 0x01:
                    SendMouseClick(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, 0);
                    return;
                case 0x02:
                    SendMouseClick(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, 0);
                    return;
                case 0x04:
                    SendMouseClick(MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, 0);
                    return;
                case 0x05:
                    SendMouseClick(MOUSEEVENTF_XDOWN, MOUSEEVENTF_XUP, 1);
                    return;
                case 0x06:
                    SendMouseClick(MOUSEEVENTF_XDOWN, MOUSEEVENTF_XUP, 2);
                    return;
                default:
                    PressKey(bindCode);
                    return;
            }
        }

        public static void KeyDownBind(int bindCode)
        {
            switch (bindCode)
            {
                case 0x01:
                    SendMouseEvent(MOUSEEVENTF_LEFTDOWN, 0);
                    return;
                case 0x02:
                    SendMouseEvent(MOUSEEVENTF_RIGHTDOWN, 0);
                    return;
                case 0x04:
                    SendMouseEvent(MOUSEEVENTF_MIDDLEDOWN, 0);
                    return;
                case 0x05:
                    SendMouseEvent(MOUSEEVENTF_XDOWN, 1);
                    return;
                case 0x06:
                    SendMouseEvent(MOUSEEVENTF_XDOWN, 2);
                    return;
                default:
                    SendKeyboardEvent(bindCode, false);
                    return;
            }
        }

        public static void KeyUpBind(int bindCode)
        {
            switch (bindCode)
            {
                case 0x01:
                    SendMouseEvent(MOUSEEVENTF_LEFTUP, 0);
                    return;
                case 0x02:
                    SendMouseEvent(MOUSEEVENTF_RIGHTUP, 0);
                    return;
                case 0x04:
                    SendMouseEvent(MOUSEEVENTF_MIDDLEUP, 0);
                    return;
                case 0x05:
                    SendMouseEvent(MOUSEEVENTF_XUP, 1);
                    return;
                case 0x06:
                    SendMouseEvent(MOUSEEVENTF_XUP, 2);
                    return;
                default:
                    SendKeyboardEvent(bindCode, true);
                    return;
            }
        }

        public static void PressKey(int virtualKey)
        {
            SendKeyboardEvent(virtualKey, false);
            SendKeyboardEvent(virtualKey, true);
        }

        private static void SendKeyboardEvent(int virtualKey, bool keyUp)
        {
            ushort scanCode = (ushort)MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC);
            uint keyFlags = KEYEVENTF_SCANCODE;
            if (IsExtendedKey(virtualKey))
            {
                keyFlags |= KEYEVENTF_EXTENDEDKEY;
            }
            if (keyUp)
            {
                keyFlags |= KEYEVENTF_KEYUP;
            }

            INPUT[] inputs = new INPUT[1];
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = scanCode, dwFlags = keyFlags } }
            };

            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void SendMouseClick(uint downFlag, uint upFlag, uint mouseData)
        {
            SendMouseEvent(downFlag, mouseData);
            SendMouseEvent(upFlag, mouseData);
        }

        private static void SendMouseEvent(uint flags, uint mouseData)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0] = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = mouseData } }
            };

            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static bool IsExtendedKey(int virtualKey)
        {
            return virtualKey == 0x21 || virtualKey == 0x22 || virtualKey == 0x23 || virtualKey == 0x24 ||
                   virtualKey == 0x25 || virtualKey == 0x26 || virtualKey == 0x27 || virtualKey == 0x28 ||
                   virtualKey == 0x2D || virtualKey == 0x2E || virtualKey == 0xA3 || virtualKey == 0xA5;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }
        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }
        [StructLayout(LayoutKind.Sequential)]
        public struct RAWMOUSE { public ushort usFlags; public ushort usButtonFlags; public ushort usButtonData; public uint ulRawButtons; public int lLastX; public int lLastY; public uint ulExtraInformation; }
        [StructLayout(LayoutKind.Explicit)]
        public struct RAWINPUT { [FieldOffset(0)] public RAWINPUTHEADER header; [FieldOffset(24)] public RAWMOUSE mouse; }
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion U; }
        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
    }
}
