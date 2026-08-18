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

        private readonly TimeSpan _sampleWindow = TimeSpan.FromMilliseconds(5000);

        public TimeSpan TotalKeyboardActiveTime { get; private set; }
        public TimeSpan TotalMouseActiveTime { get; private set; }

        public long TotalKeyboardEvents { get; private set; }
        public long TotalMouseMoveEvents { get; private set; }
        public long TotalMouseClickEvents { get; private set; }
        public long TotalMouseWheelEvents { get; private set; }
        public long TotalMouseMovementPixels { get; private set; }

        // یک شاخص ساده برای اینکه بفهمیم موس چقدر "واقعاً" استفاده شده
        public long TotalMouseActivityScore { get; private set; }

        public ActivityMonitor(GlobalInputHook globalInputHook)
        {
            _globalInputHook = globalInputHook ?? throw new ArgumentNullException(nameof(globalInputHook));

            _sampleTimer = new System.Timers.Timer(5000);
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
                TotalKeyboardActiveTime = TimeSpan.Zero;
                TotalMouseActiveTime = TimeSpan.Zero;

                TotalKeyboardEvents = 0;
                TotalMouseMoveEvents = 0;
                TotalMouseClickEvents = 0;
                TotalMouseWheelEvents = 0;
                TotalMouseMovementPixels = 0;
                TotalMouseActivityScore = 0;
            }

            _globalInputHook.ResetCounters();
        }
        


        private void OnSampleTimerElapsed(object sender, ElapsedEventArgs e)
        {
            var snapshot = _globalInputHook.ConsumeSnapshot();

            lock (_stateLock)
            {
                if (snapshot.HasKeyboardActivity)
                {
                    TotalKeyboardActiveTime = TotalKeyboardActiveTime.Add(_sampleWindow);
                    TotalKeyboardEvents += snapshot.KeyboardEvents;
                }

                if (snapshot.HasMouseActivity)
                {
                    TotalMouseActiveTime = TotalMouseActiveTime.Add(_sampleWindow);
                }

                TotalMouseMoveEvents += snapshot.MouseMoveEvents;
                TotalMouseClickEvents += snapshot.MouseClickEvents;
                TotalMouseWheelEvents += snapshot.MouseWheelEvents;
                TotalMouseMovementPixels += snapshot.MouseMovementPixels;

                // این فرمول ساده است ولی مفید:
                // حرکت موس = وزن پایه
                // کلیک = وزن بیشتر
                // اسکرول = وزن متوسط
                TotalMouseActivityScore += snapshot.MouseMovementPixels
                                           + (snapshot.MouseClickEvents * 50)
                                           + (snapshot.MouseWheelEvents * 25);
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
