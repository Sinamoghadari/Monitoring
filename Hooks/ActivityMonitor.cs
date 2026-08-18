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

        public void Start()
        {
            lock (_stateLock)
            {
                if (_isRunning) return;

                _globalInputHook.Start();
                _sampleTimer.Start();

                _isRunning = true;
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

                lock (_stateLock)
                {
                    if (snapshot.HasKeyboardActivity)
                    {
                        _totalKeyboardActiveTime = _totalKeyboardActiveTime.Add(_sampleWindow);
                        _totalKeyboardEvents += snapshot.KeyboardEvents;
                    }

                    if (snapshot.HasMouseActivity)
                    {
                        _totalMouseActiveTime = _totalMouseActiveTime.Add(_sampleWindow);
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
            Stop();
            _sampleTimer.Elapsed -= OnSampleTimerElapsed;
            _sampleTimer.Dispose();
        }
    }
}
