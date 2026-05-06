using System;
using System.Timers;

namespace Ergonomy.Hooks
{
    public class ActivityMonitor : IDisposable
    {
        private readonly GlobalInputHook _globalInputHook;
        private readonly System.Timers.Timer _keyboardTimer;
        private readonly System.Timers.Timer _mouseTimer;
        private DateTime _lastKeyboardActivity;
        private DateTime _lastMouseActivity;
        private bool _isRunning = false;
        public TimeSpan TotalKeyboardActiveTime { get; private set; }
        public TimeSpan TotalMouseActiveTime { get; private set; }

        public ActivityMonitor(GlobalInputHook globalInputHook)
        {
            _globalInputHook = globalInputHook;
            _globalInputHook.KeyboardActivity += OnKeyboardActivity;
            _globalInputHook.MouseActivity += OnMouseActivity;

            _keyboardTimer = new System.Timers.Timer(1000);
            _keyboardTimer.Elapsed += OnKeyboardTimerElapsed;

            _mouseTimer = new System.Timers.Timer(1000);
            _mouseTimer.Elapsed += OnMouseTimerElapsed;
        }

        public void Start()
        {
            if (_isRunning) return; // جلوگیری از اجرای مجدد

            _lastKeyboardActivity = DateTime.UtcNow;
            _lastMouseActivity = DateTime.UtcNow;
            _keyboardTimer.Start();
            _mouseTimer.Start();
            _globalInputHook.Start();
            
            _isRunning = true;
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _globalInputHook.Stop();
            _keyboardTimer.Stop();
            _mouseTimer.Stop();
            
            _isRunning = false;
        }

        public void ResetTotalTimers()
        {
            TotalKeyboardActiveTime = TimeSpan.Zero;
            TotalMouseActiveTime = TimeSpan.Zero;
        }

        private void OnKeyboardActivity(object sender, EventArgs e)
        {
            _lastKeyboardActivity = DateTime.UtcNow;
        }

        private void OnMouseActivity(object sender, EventArgs e)
        {
            _lastMouseActivity = DateTime.UtcNow;
        }

        private void OnKeyboardTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (DateTime.UtcNow - _lastKeyboardActivity < TimeSpan.FromSeconds(1))
            {
                TotalKeyboardActiveTime = TotalKeyboardActiveTime.Add(TimeSpan.FromSeconds(1));
            }
        }

        private void OnMouseTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (DateTime.UtcNow - _lastMouseActivity < TimeSpan.FromSeconds(1))
            {
                TotalMouseActiveTime = TotalMouseActiveTime.Add(TimeSpan.FromSeconds(1));
            }
        }

        public void Dispose()
        {
            Stop();
            _globalInputHook.KeyboardActivity -= OnKeyboardActivity;
            _globalInputHook.MouseActivity -= OnMouseActivity;
            _keyboardTimer.Dispose();
            _mouseTimer.Dispose();
        }
    }
}
