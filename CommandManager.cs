using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SysTimer = System.Timers.Timer;
using ElapsedEventArgs = System.Timers.ElapsedEventArgs;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Ergonomy.Configuration;
using Ergonomy.Database;
using Ergonomy.Logging;

namespace Ergonomy
{
    public class RemoteCommand { public int Id { get; set; } public string Command { get; set; } = string.Empty; }

    /// <summary>Polls the command API. Machine-authoritative policy is checked at every execution boundary.</summary>
    public sealed class CommandManager : IDisposable
    {
        private readonly SysTimer _scheduleTimer;
        private readonly object _pollSync = new();
        private Task? _pollTask;
        private readonly string _windowsUsername;
        private readonly LocalDatabaseManager _localDbManager;
        private readonly ISettingsService _settingsService;
        private readonly ILogger<CommandManager> _logger;
        private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
        private readonly SemaphoreSlim _pollGate = new(1, 1);
        private AppSettings _appSettings;
        private string _lastExecutedSchedule = "";
        private bool _disposed;

        public Action<string, string>? OnLogRequired { get; set; }
        public Action? OnStopCollection { get; set; }
        public Action? OnStartCollection { get; set; }
        public Action? OnForceSync { get; set; }

        public CommandManager(AppSettings appSettings, string windowsUsername, LocalDatabaseManager localDbManager,
            ISettingsService settingsService, ILogger<CommandManager> logger)
        {
            _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
            _windowsUsername = windowsUsername;
            _localDbManager = localDbManager ?? throw new ArgumentNullException(nameof(localDbManager));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _scheduleTimer = new SysTimer(GetIntervalMilliseconds(_appSettings.CommandCheckIntervalSeconds));
            _scheduleTimer.AutoReset = true;
            _scheduleTimer.Elapsed += OnTimerElapsed;
        }

        private static double GetIntervalMilliseconds(double seconds) => (seconds > 0 ? seconds : 30) * 1000;
        public void Start() => _scheduleTimer.Start();
        public void Stop() => _scheduleTimer.Stop();

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // The timer callback only begins one tracked async operation; overlap is prevented by _pollGate.
            lock (_pollSync) _pollTask = PollOnceAsync();
        }

        private async Task PollOnceAsync()
        {
            if (!await _pollGate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                CheckScheduledTasks();
                await CheckAndExecuteCommandsFromApi().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.RemoteCommandFailureId, ex, "Remote command poll failed. Operation={Operation}", "poll");
            }
            finally { _pollGate.Release(); }
        }

        private void CheckScheduledTasks()
        {
            AppSettings settings = _settingsService.Current;
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(settings.ScheduledRestartTime) && now == settings.ScheduledRestartTime && _lastExecutedSchedule != now)
            {
                _lastExecutedSchedule = now;
                ExecuteSystemPower("scheduled-restart", "/r /t 5");
            }
            if (!string.IsNullOrWhiteSpace(settings.ScheduledShutdownTime) && now == settings.ScheduledShutdownTime && _lastExecutedSchedule != now)
            {
                _lastExecutedSchedule = now;
                ExecuteSystemPower("scheduled-shutdown", "/s /t 5");
            }
        }

        private async Task CheckAndExecuteCommandsFromApi()
        {
            // Do not poll or log command content when remote commands are disabled.
            if (!IsRemoteEnabled()) { DenyRemote("remote", "RemoteCommandsEnabled is false"); return; }
            string? baseUrl = _settingsService.Current.API?.Commands;
            if (string.IsNullOrWhiteSpace(baseUrl)) return;
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(baseUrl.TrimEnd('/') + "?computer=" + Uri.EscapeDataString(Environment.MachineName) + "&user=" + Uri.EscapeDataString(_windowsUsername)).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return;
                var pending = JsonSerializer.Deserialize<List<RemoteCommand>>(await response.Content.ReadAsStringAsync().ConfigureAwait(false), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (pending == null) return;
                foreach (RemoteCommand command in pending)
                    await ExecuteDelayedAsync(command, baseUrl.TrimEnd('/')).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.RemoteCommandFailureId, ex, "Remote command retrieval failed. Operation={Operation}", "retrieve");
            }
        }

        private async Task ExecuteDelayedAsync(RemoteCommand command, string baseUrl)
        {
            await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            // Re-check after delay and immediately before every command action.
            if (!IsRemoteEnabled()) { DenyRemote("remote", "RemoteCommandsEnabled is false"); return; }
            bool processed = ProcessCommand(command.Command);
            if (!processed) return;
            try
            {
                using var response = await _httpClient.PostAsync(baseUrl + "/" + command.Id + "/execute", new StringContent("{}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
                _logger.LogInformation(LogEvents.RemoteCommandAllowedId, "Remote command processed. Category={Category}, Marked={Marked}", "supported", response.IsSuccessStatusCode);
            }
            catch (Exception ex) { _logger.LogWarning(LogEvents.RemoteCommandFailureId, ex, "Remote command acknowledgement failed. Operation={Operation}", "acknowledge"); }
        }

        private bool ProcessCommand(string? command)
        {
            if (!IsRemoteEnabled()) { DenyRemote("remote", "RemoteCommandsEnabled is false"); return false; }
            if (string.IsNullOrWhiteSpace(command)) return false;
            string lower = command.Trim().ToLowerInvariant();
            if (lower.StartsWith("msg:"))
            {
                // The content is intentionally not logged. It is UI data, not operational telemetry.
                string message = command[4..].Trim();
                var thread = new Thread(() => Application.Run(new Ergonomy.UI.MessageAlarmForm(message)));
                thread.SetApartmentState(ApartmentState.STA); thread.Start();
                _logger.LogInformation(LogEvents.RemoteCommandAllowedId, "Remote command allowed. Category={Category}", "message");
                return true;
            }
            switch (lower)
            {
                case "stop": OnStopCollection?.Invoke(); _logger.LogInformation(LogEvents.RemoteCommandAllowedId, "Remote command allowed. Category={Category}", "collection-stop"); return true;
                case "start": OnStartCollection?.Invoke(); _logger.LogInformation(LogEvents.RemoteCommandAllowedId, "Remote command allowed. Category={Category}", "collection-start"); return true;
                default: DenyRemote("unsupported", "Command category is not allowlisted"); return false;
            }
        }

        private bool IsRemoteEnabled() => _settingsService.Current.RemoteCommandsEnabled;
        private bool IsSystemPowerEnabled() => _settingsService.Current.SystemPowerCommandsEnabled;
        private void DenyRemote(string category, string reason) => _logger.LogWarning(LogEvents.RemoteCommandDeniedId,
            "Remote command denied. Category={Category}, Reason={Reason}, RemoteCommandsEnabled={Enabled}", category, reason, IsRemoteEnabled());
        private void ExecuteSystemPower(string category, string arguments)
        {
            // This check is deliberately adjacent to the only power-process invocation.
            if (!IsSystemPowerEnabled()) { DenySystemPower(category, "SystemPowerCommandsEnabled is false"); return; }
            _logger.LogWarning(LogEvents.RemoteCommandAllowedId, "System power command allowed. Category={Category}", category);
            System.Diagnostics.Process.Start("shutdown", arguments);
        }
        private void DenySystemPower(string category, string reason) => _logger.LogWarning(LogEvents.SystemPowerCommandDeniedId,
            "System power command denied. Category={Category}, Reason={Reason}, SystemPowerCommandsEnabled={Enabled}", category, reason, IsSystemPowerEnabled());

        public void UpdateSettings(AppSettings settings)
        {
            _appSettings = settings ?? throw new ArgumentNullException(nameof(settings));
            _scheduleTimer.Interval = GetIntervalMilliseconds(settings.CommandCheckIntervalSeconds);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _scheduleTimer.Stop();
            Task? poll; lock (_pollSync) poll = _pollTask;
            try { poll?.Wait(TimeSpan.FromSeconds(25)); } catch { }
            _scheduleTimer.Dispose(); _httpClient.Dispose(); _pollGate.Dispose();
        }
    }
}
