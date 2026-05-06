using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using SysTimer = System.Timers.Timer;

namespace Ergonomy 
{
    public class CommandManager : IDisposable
    {
        private SysTimer _scheduleTimer;
        private string _lastExecutedSchedule;
        private string _windowsUsername;
        
        // وابستگی‌ها
        private dynamic _appSettings; 
        private dynamic _dbManager; 

        // Callbacks
        public Action<string, string> OnLogRequired { get; set; }
        public Action OnStopCollection { get; set; }
        public Action OnStartCollection { get; set; }
        public Action OnForceSync { get; set; }

        public CommandManager(dynamic appSettings, dynamic dbManager, string windowsUsername)
        {
            _appSettings = appSettings;
            _dbManager = dbManager;
            _windowsUsername = windowsUsername;

            double intervalSec = _appSettings?.CommandCheckIntervalSeconds ?? 30;
            if (intervalSec <= 0) intervalSec = 30;

            _scheduleTimer = new SysTimer();
            _scheduleTimer.Interval = intervalSec * 1000; 
            _scheduleTimer.Elapsed += (s, e) => CheckScheduledTasks();
        }

        public void Start() => _scheduleTimer.Start();
        public void Stop() => _scheduleTimer.Stop();

        public void UpdateTimerInterval(double newIntervalSeconds)
        {
            if (newIntervalSeconds > 0 && _scheduleTimer != null)
            {
                _scheduleTimer.Interval = newIntervalSeconds * 1000;
            }
        }

        private void CheckScheduledTasks()
        {
            if (_appSettings == null) return;

            string currentSystemTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            if (!string.IsNullOrWhiteSpace(_appSettings.ScheduledRestartTime) && 
                currentSystemTime == _appSettings.ScheduledRestartTime && 
                _lastExecutedSchedule != currentSystemTime)
            {
                _lastExecutedSchedule = currentSystemTime;
                OnLogRequired?.Invoke("WARNING", $"Executing SCHEDULED RESTART exactly at {currentSystemTime}");
                System.Diagnostics.Process.Start("shutdown", "/r /t 5");
            }

            if (!string.IsNullOrWhiteSpace(_appSettings.ScheduledShutdownTime) && 
                currentSystemTime == _appSettings.ScheduledShutdownTime && 
                _lastExecutedSchedule != currentSystemTime)
            {
                _lastExecutedSchedule = currentSystemTime;
                OnLogRequired?.Invoke("WARNING", $"Executing SCHEDULED SHUTDOWN exactly at {currentSystemTime}");
                System.Diagnostics.Process.Start("shutdown", "/s /t 5");
            }

            CheckAndExecuteCommands(Environment.MachineName, _windowsUsername);
        }

        private void CheckAndExecuteCommands(string computerName, string windowsUsername) 
        {
            var pendingCommands = _dbManager?.GetPendingCommands(computerName, windowsUsername); 
            if (pendingCommands == null || pendingCommands.Count == 0) return;

            foreach (var cmd in pendingCommands)
            {
                OnLogRequired?.Invoke("INFO", $"Received remote command batch (ID: {cmd.Id}): '{cmd.Command}'. Preparing to execute.");
                
                string jsonCommandToExecute = cmd.Command;

                Task.Run(async () => 
                {
                    await Task.Delay(20000); 

                    bool success = false; // پرچم برای بررسی موفقیت‌آمیز بودن پردازش

                    try
                    {
                        using (JsonDocument doc = JsonDocument.Parse(jsonCommandToExecute))
                        {
                            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                                {
                                    if (element.ValueKind == JsonValueKind.String)
                                    {
                                        ProcessCommand(element.GetString());
                                    }
                                }
                            }
                            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                            {
                                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                                {
                                    string key = prop.Name.ToLower();
                                    string value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();

                                    if (key == "msg" || key == "message")
                                    {
                                        ProcessCommand($"msg:{value}");
                                    }
                                    else if (key == "command" || key == "action")
                                    {
                                        ProcessCommand(value);
                                    }
                                    else
                                    {
                                        ProcessCommand(key); 
                                    }
                                }
                            }
                        }
                        
                        // اگر کد به اینجا رسید، یعنی خواندن و پارس کردن JSON موفق بوده است
                        success = true; 
                    }
                    catch (JsonException)
                    {
                        try
                        {
                            // اگر JSON نبود (متن ساده بود)
                            OnLogRequired?.Invoke("WARNING", $"Command ID {cmd.Id} is not valid JSON. Executing as a raw string.");
                            ProcessCommand(jsonCommandToExecute);
                            success = true; // اجرای موفق به عنوان رشته متنی
                        }
                        catch (Exception ex)
                        {
                            OnLogRequired?.Invoke("ERROR", $"Error processing command ID {cmd.Id} as raw string: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        // در صورت بروز هرگونه خطای پیش‌بینی نشده در پردازش
                        OnLogRequired?.Invoke("ERROR", $"Error processing command ID {cmd.Id}: {ex.Message}");
                    }

                    // ثبت در دیتابیس فقط در صورت موفقیت‌آمیز بودن خواندن و پردازش انجام می‌شود
                    if (success)
                    {
                        _dbManager?.MarkCommandAsExecuted(cmd.Id);
                        OnLogRequired?.Invoke("INFO", $"Command ID {cmd.Id} successfully processed and marked as executed in database.");
                    }
                    else
                    {
                        OnLogRequired?.Invoke("WARNING", $"Command ID {cmd.Id} failed to process. Status in database remains unchanged.");
                    }
                });
            }
        }



        private void ProcessCommand(string command)
        {
            string lowerCmd = command.ToLower().Trim();

            if (lowerCmd.StartsWith("msg:"))
            {
                string message = command.Substring(4).Trim(); 
                OnLogRequired?.Invoke("INFO", $"Displaying custom message: {message}");

                System.Threading.Thread thread = new System.Threading.Thread(() =>
                {
                    var msgForm = new Ergonomy.UI.MessageAlarmForm(message);
                    Application.Run(msgForm); 
                });
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.Start();
                return;
            }

            switch (lowerCmd)
            {
                case "stop":
                    OnStopCollection?.Invoke();
                    OnLogRequired?.Invoke("INFO", "Application tracking PAUSED via remote command.");
                    break;
                case "start":
                    OnStartCollection?.Invoke();
                    OnLogRequired?.Invoke("INFO", "Application tracking RESUMED via remote command.");
                    break;
                case "os_restart": 
                    OnLogRequired?.Invoke("WARNING", "Windows is RESTARTING via remote command.");
                    OnForceSync?.Invoke(); 
                    System.Diagnostics.Process.Start("shutdown", "/r /t 5"); 
                    break;
                case "os_shutdown": 
                    OnLogRequired?.Invoke("WARNING", "Windows is SHUTTING DOWN via remote command.");
                    OnForceSync?.Invoke();
                    System.Diagnostics.Process.Start("shutdown", "/s /t 5"); 
                    break;
            }
        }

        public void Dispose()
        {
            _scheduleTimer?.Stop();
            _scheduleTimer?.Dispose();
        }
    }
}
