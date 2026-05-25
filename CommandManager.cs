using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net.Http;
using System.Text;
using SysTimer = System.Timers.Timer;

namespace Ergonomy 
{
    // کلاس کمکی برای نگهداری ساختار دستورات دریافتی از API
    public class RemoteCommand
    {
        public int Id { get; set; }
        public string Command { get; set; } = string.Empty;
    }

    public class CommandManager : IDisposable
    {
        private SysTimer _scheduleTimer;
        private string _lastExecutedSchedule = "";
        private string _windowsUsername;
        private static readonly HttpClient _httpClient = new HttpClient();
        
        
        private dynamic _appSettings; 

        // Callbacks
        public Action<string, string>? OnLogRequired { get; set; }
        public Action? OnStopCollection { get; set; }
        public Action? OnStartCollection { get; set; }
        public Action? OnForceSync { get; set; }

        public CommandManager(dynamic appSettings, string windowsUsername)
        {
            _appSettings = appSettings;
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

            if (!string.IsNullOrWhiteSpace((string)_appSettings.ScheduledRestartTime) && 
                currentSystemTime == (string)_appSettings.ScheduledRestartTime && 
                _lastExecutedSchedule != currentSystemTime)
            {
                _lastExecutedSchedule = currentSystemTime;
                OnLogRequired?.Invoke("WARNING", $"Executing SCHEDULED RESTART exactly at {currentSystemTime}");
                System.Diagnostics.Process.Start("shutdown", "/r /t 5");
            }

            if (!string.IsNullOrWhiteSpace((string)_appSettings.ScheduledShutdownTime) && 
                currentSystemTime == (string)_appSettings.ScheduledShutdownTime && 
                _lastExecutedSchedule != currentSystemTime)
            {
                _lastExecutedSchedule = currentSystemTime;
                OnLogRequired?.Invoke("WARNING", $"Executing SCHEDULED SHUTDOWN exactly at {currentSystemTime}");
                System.Diagnostics.Process.Start("shutdown", "/s /t 5");
            }

            Task.Run(async () => await CheckAndExecuteCommandsFromApi(Environment.MachineName, _windowsUsername));
        }

        private async Task CheckAndExecuteCommandsFromApi(string computerName, string windowsUsername) 
        {
            
            string? baseUrl = _appSettings?.API?.Commands;
            
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                OnLogRequired?.Invoke("ERROR", "CommandsApiUrl is not configured in AppSettings.");
                baseUrl = "http://127.0.0.1:8000/api/commands"; 
            }

            baseUrl = baseUrl.TrimEnd('/'); // جلوگیری از مشکلات مربوط به اسلش اضافی
            string apiUrl = $"{baseUrl}?computer={computerName}&user={windowsUsername}";

            try
            {
                HttpResponseMessage response = await _httpClient.GetAsync(apiUrl);
                if (!response.IsSuccessStatusCode) return;

                string jsonString = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var pendingCommands = JsonSerializer.Deserialize<List<RemoteCommand>>(jsonString, options);

                if (pendingCommands == null || pendingCommands.Count == 0) return;

                foreach (var cmd in pendingCommands)
                {
                    OnLogRequired?.Invoke("INFO", $"Received remote command batch (ID: {cmd.Id}): '{cmd.Command}'. Preparing to execute.");
                    
                    string jsonCommandToExecute = cmd.Command;

                    _ = Task.Run(async () => 
                    {
                        await Task.Delay(20000); 
                        bool success = false; 

                        try
                        {
                            using (JsonDocument doc = JsonDocument.Parse(jsonCommandToExecute))
                            {
                                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (JsonElement element in doc.RootElement.EnumerateArray())
                                    {
                                        if (element.ValueKind == JsonValueKind.String)
                                            ProcessCommand(element.GetString());
                                    }
                                }
                                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                                {
                                    foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                                    {
                                        string key = prop.Name.ToLower();
                                        string value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()! : prop.Value.GetRawText();

                                        if (key == "msg" || key == "message")
                                            ProcessCommand($"msg:{value}");
                                        else if (key == "command" || key == "action")
                                            ProcessCommand(value);
                                        else
                                            ProcessCommand(key); 
                                    }
                                }
                            }
                            success = true; 
                        }
                        catch (JsonException)
                        {
                            try
                            {
                                OnLogRequired?.Invoke("WARNING", $"Command ID {cmd.Id} is not valid JSON. Executing as a raw string.");
                                ProcessCommand(jsonCommandToExecute);
                                success = true; 
                            }
                            catch (Exception ex)
                            {
                                OnLogRequired?.Invoke("ERROR", $"Error processing command ID {cmd.Id} as raw string: {ex.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            OnLogRequired?.Invoke("ERROR", $"Error processing command ID {cmd.Id}: {ex.Message}");
                        }

                        if (success)
                        {
                            // استفاده از همان Base URL برای ارسال درخواست POST
                            string markExecutedUrl = $"{baseUrl}/{cmd.Id}/execute";
                            var postResponse = await _httpClient.PostAsync(markExecutedUrl, new StringContent("{}", Encoding.UTF8, "application/json"));
                            
                            if (postResponse.IsSuccessStatusCode)
                                OnLogRequired?.Invoke("INFO", $"Command ID {cmd.Id} successfully processed and marked as executed via API.");
                            else
                                OnLogRequired?.Invoke("WARNING", $"Command ID {cmd.Id} failed to be marked as executed in API.");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                OnLogRequired?.Invoke("ERROR", $"Error fetching commands from API: {ex.Message}");
            }
        }

        private void ProcessCommand(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;

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
