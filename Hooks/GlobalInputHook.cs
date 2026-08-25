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

        /// <summary>
        /// هوک ورودی سراسری را با delegatهای پایدار صفحه‌کلید و ماوس آماده می‌کند
        /// تا GC نتواند callbackهای Win32 را جمع‌آوری کند.
        /// </summary>
        public GlobalInputHook()
        {
            _keyboardCallback = KeyboardHookCallback;
            _mouseCallback = MouseHookCallback;
        }

        /// <summary>
        /// نخ اختصاصی Ergonomy-InputHook را می‌سازد، هوک‌های WH_KEYBOARD_LL و WH_MOUSE_LL را نصب کرده
        /// و تا آماده شدن حلقه پیام یا پایان مهلت ده ثانیه منتظر می‌ماند.
        /// </summary>
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

        /// <summary>
        /// پیام WM_QUIT را به نخ هوک می‌فرستد تا حلقه GetMessage خارج شود و سپس برای پایان نخ منتظر می‌ماند.
        /// </summary>
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

        /// <summary>
        /// نقطه ورود نخ هوک است: صف پیام می‌سازد، هوک‌ها را نصب می‌کند
        /// و حلقه بومی GetMessage را تا دریافت WM_QUIT نگه می‌دارد.
        /// </summary>
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

        /// <summary>
        /// کد خطای Win32 را به پیام قابل خواندن تبدیل می‌کند تا نصب ناموفق هوک قابل تشخیص باشد.
        /// </summary>
        /// <param name="errorCode">کد خطای GetLastWin32Error.</param>
        /// <returns>شرح متنی خطا.</returns>
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

        /// <summary>
        /// هوک‌های صفحه‌کلید و ماوس را روی همان نخی که نصب شده‌اند آزاد می‌کند.
        /// </summary>
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

        /// <summary>
        /// شمارنده‌های مسیر داغ را به‌صورت اتمی صفر کرده و اسنپ‌شات پنجره نمونه‌برداری را برمی‌گرداند.
        /// </summary>
        /// <returns>اسنپ‌شات رویدادهای صفحه‌کلید و ماوس از آخرین مصرف.</returns>
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

        /// <summary>
        /// شمارنده‌های اتمی و نقطه مرجع حرکت ماوس را برای شروع یک دوره اندازه‌گیری جدید صفر می‌کند.
        /// </summary>
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

        /// <summary>
        /// هوک سطح پایین ویندوز را برای کل نشست با ماژول فرایند جاری نصب می‌کند.
        /// </summary>
        /// <param name="hookId">شناسه هوک مانند WH_KEYBOARD_LL یا WH_MOUSE_LL.</param>
        /// <param name="callback">تابع callback که باید در نخ هوک زنده بماند.</param>
        /// <returns>دسته هوک نصب‌شده یا صفر در صورت شکست.</returns>
        private IntPtr SetHook(int hookId, HookCallback callback)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(hookId, callback, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        /// <summary>
        /// در مسیر داغ فقط کلیدهای پایین‌رفته را به‌صورت اتمی می‌شمارد و زنجیره هوک را ادامه می‌دهد.
        /// هیچ کار سنگینی در این callback انجام نمی‌شود تا تأخیر ورودی ایجاد نشود.
        /// </summary>
        /// <param name="nCode">کد هوک ویندوز.</param>
        /// <param name="wParam">نوع پیام صفحه‌کلید.</param>
        /// <param name="lParam">اشاره‌گر به ساختار داده هوک.</param>
        /// <returns>نتیجه CallNextHookEx برای ادامه زنجیره.</returns>
        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                Interlocked.Increment(ref _keyboardEvents);
            }

            return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        /// <summary>
        /// حرکت، کلیک و اسکرول ماوس را به‌صورت اتمی می‌شمارد و فاصله منهتن حرکت را محاسبه می‌کند.
        /// سپس زنجیره هوک را بدون مسدود کردن ورودی ادامه می‌دهد.
        /// </summary>
        /// <param name="nCode">کد هوک ویندوز.</param>
        /// <param name="wParam">نوع پیام ماوس.</param>
        /// <param name="lParam">اشاره‌گر به MSLLHOOKSTRUCT.</param>
        /// <returns>نتیجه CallNextHookEx برای ادامه زنجیره.</returns>
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

        /// <summary>
        /// هوک را متوقف کرده و رویداد شروع نصب را آزاد می‌کند.
        /// </summary>
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

        /// <summary>
        /// مختصات نقطه ماوس را در یک long بسته‌بندی می‌کند تا تبادل اتمی ممکن شود.
        /// </summary>
        /// <param name="x">مختصات افقی.</param>
        /// <param name="y">مختصات عمودی.</param>
        /// <returns>مقدار بسته‌بندی‌شده نقطه.</returns>
        private static long PackPoint(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

        /// <summary>
        /// مختصات X را از مقدار بسته‌بندی‌شده نقطه استخراج می‌کند.
        /// </summary>
        /// <param name="packed">مقدار بسته‌بندی‌شده.</param>
        /// <returns>مختصات افقی.</returns>
        private static int UnpackX(long packed)
        {
            return (int)(packed >> 32);
        }

        /// <summary>
        /// مختصات Y را از مقدار بسته‌بندی‌شده نقطه استخراج می‌کند.
        /// </summary>
        /// <param name="packed">مقدار بسته‌بندی‌شده.</param>
        /// <returns>مختصات عمودی.</returns>
        private static int UnpackY(long packed)
        {
            return unchecked((int)(packed & 0xFFFFFFFF));
        }

        #region PInvoke

        /// <summary>
        /// هوک سراسری ویندوز را برای صفحه‌کلید یا ماوس نصب می‌کند.
        /// </summary>
        /// <param name="idHook">شناسه نوع هوک.</param>
        /// <param name="lpfn">تابع callback هوک.</param>
        /// <param name="hMod">دسته ماژول فرایند جاری.</param>
        /// <param name="dwThreadId">شناسه نخ؛ صفر یعنی هوک سراسری.</param>
        /// <returns>دسته هوک نصب‌شده.</returns>
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookCallback lpfn, IntPtr hMod, uint dwThreadId);

        /// <summary>
        /// هوک نصب‌شده را از زنجیره هوک‌های ویندوز خارج می‌کند.
        /// </summary>
        /// <param name="hhk">دسته هوک.</param>
        /// <returns>نتیجه موفقیت آزادسازی.</returns>
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        /// <summary>
        /// رویداد را به هوک بعدی زنجیره تحویل می‌دهد تا ورودی سیستم مسدود نشود.
        /// </summary>
        /// <param name="hhk">دسته هوک جاری.</param>
        /// <param name="nCode">کد هوک.</param>
        /// <param name="wParam">پارامتر پیام.</param>
        /// <param name="lParam">اشاره‌گر داده پیام.</param>
        /// <returns>نتیجه هوک بعدی.</returns>
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// دسته ماژول اجرایی فرایند را برای نصب هوک سطح پایین می‌گیرد.
        /// </summary>
        /// <param name="lpModuleName">نام ماژول.</param>
        /// <returns>دسته ماژول.</returns>
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        /// <summary>
        /// شناسه نخ جاری ویندوز را برای ارسال WM_QUIT برمی‌گرداند.
        /// </summary>
        /// <returns>شناسه نخ.</returns>
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>
        /// پیامی مانند WM_QUIT را به صف پیام نخ هوک ارسال می‌کند.
        /// </summary>
        /// <param name="idThread">شناسه نخ مقصد.</param>
        /// <param name="Msg">کد پیام.</param>
        /// <param name="wParam">پارامتر اول.</param>
        /// <param name="lParam">پارامتر دوم.</param>
        /// <returns>نتیجه ارسال پیام.</returns>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// پیام بعدی را از صف پیام نخ هوک می‌خواند و حلقه بومی را زنده نگه می‌دارد.
        /// </summary>
        /// <param name="lpMsg">ساختار پیام خروجی.</param>
        /// <param name="hWnd">پنجره فیلتر؛ صفر یعنی همه.</param>
        /// <param name="wMsgFilterMin">حد پایین فیلتر.</param>
        /// <param name="wMsgFilterMax">حد بالای فیلتر.</param>
        /// <returns>نتیجه دریافت پیام.</returns>
        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        /// <summary>
        /// شتاب‌دهنده‌های صفحه‌کلید پیام را ترجمه می‌کند.
        /// </summary>
        /// <param name="lpMsg">پیام جاری.</param>
        /// <returns>نتیجه ترجمه.</returns>
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        /// <summary>
        /// پیام را به رویه پنجره مقصد ارسال می‌کند.
        /// </summary>
        /// <param name="lpMsg">پیام جاری.</param>
        /// <returns>نتیجه توزیع پیام.</returns>
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        /// <summary>
        /// بدون مسدود شدن، وجود پیام را بررسی می‌کند تا صف پیام نخ هوک ساخته شود.
        /// </summary>
        /// <param name="lpMsg">ساختار پیام خروجی.</param>
        /// <param name="hWnd">پنجره فیلتر.</param>
        /// <param name="wMsgFilterMin">حد پایین فیلتر.</param>
        /// <param name="wMsgFilterMax">حد بالای فیلتر.</param>
        /// <param name="wRemoveMsg">پرچم برداشتن پیام از صف.</param>
        /// <returns>اگر پیامی موجود باشد true است.</returns>
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
        /// <summary>
        /// اسنپ‌شات غیرقابل‌تغییر رویدادهای ورودی یک پنجره نمونه‌برداری را می‌سازد.
        /// </summary>
        /// <param name="keyboardEvents">تعداد رویدادهای صفحه‌کلید.</param>
        /// <param name="mouseMoveEvents">تعداد حرکت ماوس.</param>
        /// <param name="mouseClickEvents">تعداد کلیک ماوس.</param>
        /// <param name="mouseWheelEvents">تعداد اسکرول ماوس.</param>
        /// <param name="mouseMovementPixels">مجموع فاصله حرکت ماوس به پیکسل.</param>
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
