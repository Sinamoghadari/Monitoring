using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ergonomy.Hooks
{
    public sealed class GlobalInputHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MOUSEWHEEL = 0x020A;

        private delegate IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam);

        private HookCallback _keyboardCallback;
        private HookCallback _mouseCallback;

        private IntPtr _keyboardHookHandle = IntPtr.Zero;
        private IntPtr _mouseHookHandle = IntPtr.Zero;

        // Counters collected in the hot path
        private long _keyboardEvents;
        private long _mouseMoveEvents;
        private long _mouseClickEvents;
        private long _mouseWheelEvents;
        private long _mouseMovementPixels;

        // Used only for mouse move distance calculation
        private long _lastMousePointPacked;
        private int _hasLastMousePoint;

        public GlobalInputHook()
        {
            _keyboardCallback = KeyboardHookCallback;
            _mouseCallback = MouseHookCallback;
        }

        public void Start()
        {
            if (_keyboardHookHandle != IntPtr.Zero || _mouseHookHandle != IntPtr.Zero)
                return;

            _keyboardHookHandle = SetHook(WH_KEYBOARD_LL, _keyboardCallback);
            _mouseHookHandle = SetHook(WH_MOUSE_LL, _mouseCallback);

            // Reset baseline when starting
            _hasLastMousePoint = 0;
            _lastMousePointPacked = 0;
        }

        public void Stop()
        {
            if (_keyboardHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHookHandle);
                _keyboardHookHandle = IntPtr.Zero;
            }

            if (_mouseHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }
        }

        public InputActivitySnapshot ConsumeSnapshot()
        {
            return new InputActivitySnapshot(
                keyboardEvents: Interlocked.Exchange(ref _keyboardEvents, 0),
                mouseMoveEvents: Interlocked.Exchange(ref _mouseMoveEvents, 0),
                mouseClickEvents: Interlocked.Exchange(ref _mouseClickEvents, 0),
                mouseWheelEvents: Interlocked.Exchange(ref _mouseWheelEvents, 0),
                mouseMovementPixels: Interlocked.Exchange(ref _mouseMovementPixels, 0)
            );
        }

        public void ResetCounters()
        {
            Interlocked.Exchange(ref _keyboardEvents, 0);
            Interlocked.Exchange(ref _mouseMoveEvents, 0);
            Interlocked.Exchange(ref _mouseClickEvents, 0);
            Interlocked.Exchange(ref _mouseWheelEvents, 0);
            Interlocked.Exchange(ref _mouseMovementPixels, 0);

            _hasLastMousePoint = 0;
            _lastMousePointPacked = 0;
        }

        private IntPtr SetHook(int hookId, HookCallback callback)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(hookId, callback, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                Interlocked.Increment(ref _keyboardEvents);
            }

            return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                switch ((int)wParam)
                {
                    case WM_MOUSEMOVE:
                    {
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                        long currentPacked = PackPoint(info.pt.x, info.pt.y);
                        long previousPacked = Interlocked.Exchange(ref _lastMousePointPacked, currentPacked);

                        // اولین نقطه فقط baseline است
                        if (Interlocked.Exchange(ref _hasLastMousePoint, 1) == 1)
                        {
                            int prevX = UnpackX(previousPacked);
                            int prevY = UnpackY(previousPacked);

                            // distance = |dx| + |dy|
                            // از sqrt استفاده نمی‌کنیم تا سبک‌تر باشد
                            int distance = Math.Abs(info.pt.x - prevX) + Math.Abs(info.pt.y - prevY);

                            if (distance > 0)
                            {
                                Interlocked.Add(ref _mouseMovementPixels, distance);
                            }
                        }

                        Interlocked.Increment(ref _mouseMoveEvents);
                        break;
                    }

                    case WM_LBUTTONDOWN:
                    case WM_RBUTTONDOWN:
                    case WM_MBUTTONDOWN:
                        Interlocked.Increment(ref _mouseClickEvents);
                        break;

                    case WM_MOUSEWHEEL:
                        Interlocked.Increment(ref _mouseWheelEvents);
                        break;
                }
            }

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Stop();
        }

        private static long PackPoint(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

        private static int UnpackX(long packed)
        {
            return (int)(packed >> 32);
        }

        private static int UnpackY(long packed)
        {
            return unchecked((int)(packed & 0xFFFFFFFF));
        }

        #region PInvoke

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookCallback lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public int mouseData;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        #endregion
    }

    public readonly struct InputActivitySnapshot
    {
        public InputActivitySnapshot(
            long keyboardEvents,
            long mouseMoveEvents,
            long mouseClickEvents,
            long mouseWheelEvents,
            long mouseMovementPixels)
        {
            KeyboardEvents = keyboardEvents;
            MouseMoveEvents = mouseMoveEvents;
            MouseClickEvents = mouseClickEvents;
            MouseWheelEvents = mouseWheelEvents;
            MouseMovementPixels = mouseMovementPixels;
        }

        public long KeyboardEvents { get; }
        public long MouseMoveEvents { get; }
        public long MouseClickEvents { get; }
        public long MouseWheelEvents { get; }
        public long MouseMovementPixels { get; }

        public bool HasKeyboardActivity => KeyboardEvents > 0;
        public bool HasMouseActivity => MouseMoveEvents > 0 || MouseClickEvents > 0 || MouseWheelEvents > 0;
    }
}
