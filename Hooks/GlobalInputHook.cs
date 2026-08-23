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

        private readonly HookCallback _keyboardCallback;
        private readonly HookCallback _mouseCallback;

        private IntPtr _keyboardHookHandle = IntPtr.Zero;
        private IntPtr _mouseHookHandle = IntPtr.Zero;

        private Thread? _hookThread;
        private uint _hookThreadId;
        private readonly object _stateLock = new object();
        private readonly ManualResetEventSlim _startCompletedEvent = new ManualResetEventSlim(false);
        private Exception? _startupException;
        private bool _isDisposed;

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
            lock (_stateLock)
            {
                if (_isDisposed)
                    throw new ObjectDisposedException(nameof(GlobalInputHook));

                if (_hookThread != null && _hookThread.IsAlive)
                    return;

                _startCompletedEvent.Reset();
                _startupException = null;

                _hookThread = new Thread(HookThreadEntryPoint)
                {
                    IsBackground = true,
                    Name = "Ergonomy-InputHook"
                };

                _hookThread.Start();
            }

            // Synchronously wait (with timeout) for hook thread to complete installation
            if (!_startCompletedEvent.Wait(TimeSpan.FromSeconds(10)))
            {
                // Hook thread failed to signal completion; do not report success.
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ [Ergonomy] Input hook installation timed out after 10s.");
                throw new InvalidOperationException("Timed out while waiting for input hook installation.");
            }

            lock (_stateLock)
            {
                if (_startupException != null)
                {
                    Exception ex = _startupException;
                    _startupException = null;
                    throw new InvalidOperationException($"Failed to install Win32 input hooks.", ex);
                }
            }
        }

        public void Stop()
        {
            Thread? threadToJoin = null;
            uint threadId = 0;

            lock (_stateLock)
            {
                if (_hookThread == null)
                    return;

                threadToJoin = _hookThread;
                threadId = _hookThreadId;
                _hookThread = null;
                _hookThreadId = 0;
            }

            if (threadId != 0)
            {
                // Post WM_QUIT to wake and exit GetMessage pump on dedicated hook thread
                const uint WM_QUIT = 0x0012;
                PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            if (threadToJoin != null && threadToJoin != Thread.CurrentThread)
            {
                if (!threadToJoin.Join(3000))
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Hook thread stop timed out.");
                }
            }
        }

        private void HookThreadEntryPoint()
        {
            try
            {
                _hookThreadId = GetCurrentThreadId();

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Input hook thread started. ThreadId={_hookThreadId}");

                // Force creation of message queue for this thread
                MSG msg;
                PeekMessage(out msg, IntPtr.Zero, 0, 0, 0);

                _keyboardHookHandle = SetHook(WH_KEYBOARD_LL, _keyboardCallback);
                if (_keyboardHookHandle == IntPtr.Zero)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        $"WH_KEYBOARD_LL hook failed with Win32 error code {errorCode} " +
                        $"({DescribeWin32Error(errorCode)}).");
                }

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Keyboard hook installed. Handle={_keyboardHookHandle}");

                _mouseHookHandle = SetHook(WH_MOUSE_LL, _mouseCallback);
                if (_mouseHookHandle == IntPtr.Zero)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    if (_keyboardHookHandle != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(_keyboardHookHandle);
                        _keyboardHookHandle = IntPtr.Zero;
                    }
                    throw new InvalidOperationException(
                        $"WH_MOUSE_LL hook failed with Win32 error code {errorCode} " +
                        $"({DescribeWin32Error(errorCode)}).");
                }

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Mouse hook installed. Handle={_mouseHookHandle}");

                // Reset baseline when starting
                _hasLastMousePoint = 0;
                _lastMousePointPacked = 0;

                // Signal success to Start() caller
                _startCompletedEvent.Set();

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Input hook message loop active.");

                // Native Win32 Message Loop
                int bRet;
                while ((bRet = GetMessage(out msg, IntPtr.Zero, 0, 0)) != 0)
                {
                    if (bRet == -1)
                        break;

                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Input hook message loop exited (WM_QUIT or error).");
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ [Ergonomy] Hook installation error on thread " +
                    $"{GetCurrentThreadId()}.");
                _startupException = ex;
                _startCompletedEvent.Set();
            }
            finally
            {
                CleanupHooksOnHookThread();
            }
        }

        private static string DescribeWin32Error(int errorCode)
        {
            try
            {
                return new System.ComponentModel.Win32Exception(errorCode).Message;
            }
            catch
            {
                return "Unknown Win32 error";
            }
        }

        private void CleanupHooksOnHookThread()
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
                        unsafe
                        {
                            MSLLHOOKSTRUCT* pInfo = (MSLLHOOKSTRUCT*)lParam;
                            int ptX = pInfo->pt.x;
                            int ptY = pInfo->pt.y;

                            long currentPacked = PackPoint(ptX, ptY);
                            long previousPacked = Interlocked.Exchange(ref _lastMousePointPacked, currentPacked);

                            // اولین نقطه فقط baseline است
                            if (Interlocked.Exchange(ref _hasLastMousePoint, 1) == 1)
                            {
                                int prevX = UnpackX(previousPacked);
                                int prevY = UnpackY(previousPacked);

                                int distance = Math.Abs(ptX - prevX) + Math.Abs(ptY - prevY);

                                if (distance > 0)
                                {
                                    Interlocked.Add(ref _mouseMovementPixels, distance);
                                }
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
            lock (_stateLock)
            {
                if (_isDisposed)
                    return;
                _isDisposed = true;
            }

            Stop();
            _startCompletedEvent.Dispose();
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

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
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
