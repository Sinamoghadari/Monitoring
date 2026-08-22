using System;
using System.Timers;

namespace Ergonomy.Hooks
{
    public sealed class ActivityMonitor : IDisposable
    {
        private readonly GlobalInputHook _globalInputHook;
        private readonly System.Timers.Timer _sampleTimer;
        private readonly object _stateLock = new object();
        private bool _isRunning;

        private readonly TimeSpan _sampleWindow = TimeSpan.FromMilliseconds(1000);

        private TimeSpan _totalKeyboardActiveTime;
        private TimeSpan _totalMouseActiveTime;
        private long _totalKeyboardEvents;
        private long _totalMouseMoveEvents;
        private long _totalMouseClickEvents;
        private long _totalMouseWheelEvents;
        private long _totalMouseMovementPixels;
        private long _totalMouseActivityScore;

        public TimeSpan TotalKeyboardActiveTime { get { lock (_stateLock) return _totalKeyboardActiveTime; } }
        public TimeSpan TotalMouseActiveTime { get { lock (_stateLock) return _totalMouseActiveTime; } }

        public long TotalKeyboardEvents { get { lock (_stateLock) return _totalKeyboardEvents; } }
        public long TotalMouseMoveEvents { get { lock (_stateLock) return _totalMouseMoveEvents; } }
        public long TotalMouseClickEvents { get { lock (_stateLock) return _totalMouseClickEvents; } }
        public long TotalMouseWheelEvents { get { lock (_stateLock) return _totalMouseWheelEvents; } }
        public long TotalMouseMovementPixels { get { lock (_stateLock) return _totalMouseMovementPixels; } }

        // یک شاخص ساده برای اینکه بفهمیم موس چقدر "واقعاً" استفاده شده
        public long TotalMouseActivityScore { get { lock (_stateLock) return _totalMouseActivityScore; } }

        public ActivityMonitor(GlobalInputHook globalInputHook)
        {
            _globalInputHook = globalInputHook ?? throw new ArgumentNullException(nameof(globalInputHook));

            _sampleTimer = new System.Timers.Timer(1000);
            _sampleTimer.AutoReset = true;
            _sampleTimer.Elapsed += OnSampleTimerElapsed;
        }

        private DateTime _lastSampleLogUtc = DateTime.MinValue;
        private bool _firstActivityLogged;

        public void Start()
        {
            lock (_stateLock)
            {
                if (_isRunning) return;

                _globalInputHook.Start();
                _sampleTimer.Start();

                _isRunning = true;

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Activity monitor sampling started. Interval={_sampleTimer.Interval}ms");
            }
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                if (!_isRunning) return;

                _sampleTimer.Stop();
                _globalInputHook.Stop();

                _isRunning = false;
            }
        }

        public void ResetTotals()
        {
            lock (_stateLock)
            {
                _totalKeyboardActiveTime = TimeSpan.Zero;
                _totalMouseActiveTime = TimeSpan.Zero;

                _totalKeyboardEvents = 0;
                _totalMouseMoveEvents = 0;
                _totalMouseClickEvents = 0;
                _totalMouseWheelEvents = 0;
                _totalMouseMovementPixels = 0;
                _totalMouseActivityScore = 0;
            }

            _globalInputHook.ResetCounters();
        }

        private int _samplingInProgress;
        private bool _disposed;

        private void OnSampleTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (Interlocked.Exchange(ref _samplingInProgress, 1) != 0)
                return;

            try
            {
                lock (_stateLock)
                {
                    if (!_isRunning)
                        return;
                }

                var snapshot = _globalInputHook.ConsumeSnapshot();

                bool anyActivity = false;

                lock (_stateLock)
                {
                    if (snapshot.HasKeyboardActivity)
                    {
                        _totalKeyboardActiveTime = _totalKeyboardActiveTime.Add(_sampleWindow);
                        _totalKeyboardEvents += snapshot.KeyboardEvents;
                        anyActivity = true;
                    }

                    if (snapshot.HasMouseActivity)
                    {
                        _totalMouseActiveTime = _totalMouseActiveTime.Add(_sampleWindow);
                        anyActivity = true;
                    }

                    _totalMouseMoveEvents += snapshot.MouseMoveEvents;
                    _totalMouseClickEvents += snapshot.MouseClickEvents;
                    _totalMouseWheelEvents += snapshot.MouseWheelEvents;
                    _totalMouseMovementPixels += snapshot.MouseMovementPixels;

                    // این فرمول ساده است ولی مفید:
                    // حرکت موس = وزن پایه
                    // کلیک = وزن بیشتر
                    // اسکرول = وزن متوسط
                    _totalMouseActivityScore += snapshot.MouseMovementPixels
                                               + (snapshot.MouseClickEvents * 50)
                                               + (snapshot.MouseWheelEvents * 25);
                }

                // Throttled observability: log on first detected input and then
                // at most once every 30 seconds (avoids per-mouse-move spam).
                if (anyActivity && !_firstActivityLogged)
                {
                    _firstActivityLogged = true;
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] First input detected. " +
                        $"keyboardEvents={snapshot.KeyboardEvents}, mouseMoves={snapshot.MouseMoveEvents}, " +
                        $"mouseClicks={snapshot.MouseClickEvents}, mouseWheel={snapshot.MouseWheelEvents}");
                }
                else if (anyActivity && DateTime.UtcNow - _lastSampleLogUtc >= TimeSpan.FromSeconds(30))
                {
                    _lastSampleLogUtc = DateTime.UtcNow;
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] [Ergonomy] Sample: " +
                        $"keyboardEvents={_totalKeyboardEvents}, mouseMoves={_totalMouseMoveEvents}, " +
                        $"mouseClicks={_totalMouseClickEvents}, mouseWheel={_totalMouseWheelEvents}, " +
                        $"keyboardActive={_totalKeyboardActiveTime.TotalSeconds:0.0}s, " +
                        $"mouseActive={_totalMouseActiveTime.TotalSeconds:0.0}s, " +
                        $"totalActive={(_totalKeyboardActiveTime + _totalMouseActiveTime).TotalSeconds:0.0}s");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ ActivityMonitor sample error: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _samplingInProgress, 0);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            Stop();
            _sampleTimer.Elapsed -= OnSampleTimerElapsed;
            _sampleTimer.Dispose();
            _globalInputHook.Dispose();
        }
    }
}
