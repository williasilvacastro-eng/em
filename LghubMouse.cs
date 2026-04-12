using System;
using System.Runtime.InteropServices;

namespace emu2026
{
    // Tornamos a classe "public static" para podermos chamá-la de qualquer lado do emulador
    // sem precisarmos de criar cópias dela.
    public static class LghubMouse
    {
        [DllImport("ghub_mouse.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool mouse_open();

        [DllImport("ghub_mouse.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void mouse_close();

        [DllImport("ghub_mouse.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void mouse_move(int x, int y);

        private static bool _connected = false;
        private static long _lastPingTime = 0;
        private static double _residualX = 0.0;
        private static double _residualY = 0.0;

        public static bool Iniciar()
        {
            try
            {
                _connected = mouse_open();
                _residualX = 0.0;
                _residualY = 0.0;
                return _connected;
            }
            catch (Exception)
            {
                _connected = false;
                return false;
            }
        }

        public static void Mover(int deltaX, int deltaY)
        {
            try { mouse_move(deltaX, deltaY); }
            catch { }
        }

        /// <summary>
        /// Movimento sub-pixel com residual tracking para precisão máxima
        /// </summary>
        public static void MoverPreciso(double deltaX, double deltaY)
        {
            if (!_connected) return;
            try
            {
                _residualX += deltaX;
                _residualY += deltaY;
                int ix = (int)_residualX;
                int iy = (int)_residualY;
                if (ix != 0 || iy != 0)
                {
                    mouse_move(ix, iy);
                    _residualX -= ix;
                    _residualY -= iy;
                }
            }
            catch { }
        }

        /// <summary>
        /// Verifica se o driver Logitech ainda está conectado (com debounce de 500ms)
        /// </summary>
        public static bool EstaConectado()
        {
            if (!_connected) return false;
            long now = Environment.TickCount;
            if (now - _lastPingTime > 500)
            {
                _lastPingTime = now;
                try
                {
                    mouse_move(0, 0);
                }
                catch
                {
                    _connected = false;
                }
            }
            return _connected;
        }

        public static void Fechar()
        {
            try { mouse_close(); }
            catch { }
            _connected = false;
        }
    }
}