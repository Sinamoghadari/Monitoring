using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ergonomy.Hooks
{
    public class GlobalInputHook : IDisposable
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

        public event EventHandler KeyboardActivity;
        public event EventHandler MouseActivity;

        public GlobalInputHook()
        {
            _keyboardCallback = KeyboardHookCallback;
            _mouseCallback = MouseHookCallback;
        }

        public void Start()
        {
            _keyboardHookHandle = SetHook(WH_KEYBOARD_LL, _keyboardCallback);
            _mouseHookHandle = SetHook(WH_MOUSE_LL, _mouseCallback);
        }

        public void Stop()
        {
            UnhookWindowsHookEx(_keyboardHookHandle);
            UnhookWindowsHookEx(_mouseHookHandle);
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
                KeyboardActivity?.Invoke(this, EventArgs.Empty);
            }
            return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                switch ((int)wParam)
                {
                    case WM_LBUTTONDOWN:
                    case WM_RBUTTONDOWN:
                    case WM_MBUTTONDOWN:
                    case WM_MOUSEMOVE:
                    case WM_MOUSEWHEEL:
                        MouseActivity?.Invoke(this, EventArgs.Empty);
                        break;
                }
            }
            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Stop();
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
        #endregion
    }
}