using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ergonomy.Configuration;
using Ergonomy.Logging;
using Ergonomy.Observability;

namespace Ergonomy.Services
{
    /// <summary>
    /// Auto-update pipeline. Compares the running agent against a Control API
    /// (PostgreSQL-backed) manifest using semantic versioning with baseline 1.0.0,
    /// waits a deterministic per-machine jitter, downloads with transient-fault retries,
    /// verifies SHA256, then hands off to <c>apply_update.bat</c> so the running binary
    /// can be replaced after every file handle is disposed.
    ///
    /// Idempotent: a version already applied (marker file, matching staging hash, or
    /// current &gt;= latest) is skipped. Concurrent apply is serialized with a named mutex.
    /// </summary>
    public sealed class UpdateManager : WorkerBase
    {
        public const string BaselineVersion = "1.0.0";

        private const string MutexName = @"Global\Ergonomy.UpdateManager";
        private const int DefaultMaxJitterSeconds = 900;
        private const int DefaultRetryCount = 5;
        private const int DefaultCheckIntervalMinutes = 60;

        private readonly HttpClient _httpClient;
        private readonly ISettingsService _settingsService;
        private readonly MachineIdentity _identity;
        private readonly AgentMetrics _metrics;
        private readonly string _currentVersion;
        private readonly object _applySync = new();

        private string? _jitterSatisfiedForVersion;
        private string? _applyInFlightVersion;

        /// <summary>
        /// Raised on a background thread after the handoff script has been launched.
        /// The host must dispose file handles and exit so <c>apply_update.bat</c> can replace binaries.
        /// </summary>
        public Action? OnShutdownRequested { get; set; }

        /// <summary>
        /// مدیر به‌روزرسانی را به کلاینت HTTP، تنظیمات کنترل، هویت ماشین و متریک‌ها متصل می‌کند.
        /// </summary>
        public UpdateManager(
            ISettingsService settingsService,
            MachineIdentity identity,
            AgentMetrics metrics,
            ILogger<UpdateManager> logger)
            : base(logger)
        {
            // Dedicated client: the shared DI HttpClient is capped at 15s, which is too short
            // for package downloads. This singleton lives for the process lifetime.
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _currentVersion = ResolveCurrentVersion();
        }

        protected override string Name => nameof(UpdateManager);

        /// <summary>
        /// First tick runs immediately so a pending update is noticed at startup;
        /// the download itself is still gated by deterministic jitter.
        /// </summary>
        protected override bool ImmediateFirstRun => true;

        /// <summary>
        /// فاصله بررسی به‌روزرسانی را از تنظیمات مؤثر می‌خواند.
        /// </summary>
        protected override TimeSpan GetInterval()
        {
            int minutes = _settingsService.Current.Update?.CheckIntervalMinutes ?? DefaultCheckIntervalMinutes;
            if (minutes <= 0)
                minutes = DefaultCheckIntervalMinutes;
            return TimeSpan.FromMinutes(minutes);
        }

        /// <summary>
        /// یک دور بررسی، دانلود و در صورت نیاز اعمال به‌روزرسانی را اجرا می‌کند.
        /// </summary>
        protected override async Task DoWorkAsync(CancellationToken ct)
        {
            AgentUpdateSettings? manifest = _settingsService.Current.Update;
            _metrics.IncrementCounter(
                "ergonomy_update_checks_total",
                "Number of agent auto-update checks.",
                1);

            if (manifest == null || !manifest.Enabled)
                return;

            if (string.IsNullOrWhiteSpace(manifest.LatestVersion)
                || string.IsNullOrWhiteSpace(manifest.DownloadUrl)
                || string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                Logger.LogDebug("Update manifest is incomplete; skipping.");
                return;
            }

            string latest = manifest.LatestVersion.Trim();
            if (!SemanticVersion.TryParse(latest, out SemanticVersion latestVersion))
            {
                Logger.LogWarning(
                    LogEvents.UpdateCheckId,
                    "Update latest version '{Version}' is not valid semver.",
                    latest);
                return;
            }

            if (latestVersion.CompareTo(SemanticVersion.Baseline) < 0)
            {
                Logger.LogWarning(
                    LogEvents.UpdateCheckId,
                    "Refusing update {Version} below baseline {Baseline}.",
                    latest, BaselineVersion);
                return;
            }

            if (!SemanticVersion.TryParse(_currentVersion, out SemanticVersion currentVersion))
                currentVersion = SemanticVersion.Baseline;

            if (latestVersion.CompareTo(currentVersion) <= 0)
            {
                WriteMarker(latest, alreadyCurrent: true);
                return;
            }

            if (IsAlreadyApplied(latest))
            {
                Logger.LogInformation(
                    LogEvents.UpdateCheckId,
                    "Update {Version} already applied (marker). Skipping.",
                    latest);
                return;
            }

            Logger.LogInformation(
                LogEvents.UpdateAvailableId,
                "Update available. Current={Current} Latest={Latest}",
                _currentVersion, latest);

            int maxJitter = manifest.MaxJitterSeconds > 0
                ? manifest.MaxJitterSeconds
                : DefaultMaxJitterSeconds;

            if (!string.Equals(_jitterSatisfiedForVersion, latest, StringComparison.OrdinalIgnoreCase))
            {
                TimeSpan jitter = ComputeDeterministicJitter(
                    _identity.MachineName + "|" + _identity.WindowsSid + "|" + latest,
                    maxJitter);
                Logger.LogInformation(
                    LogEvents.UpdateAvailableId,
                    "Waiting deterministic jitter {Seconds}s before downloading {Version}.",
                    (int)jitter.TotalSeconds, latest);
                if (jitter > TimeSpan.Zero)
                    await Task.Delay(jitter, ct).ConfigureAwait(false);
                _jitterSatisfiedForVersion = latest;
            }

            ct.ThrowIfCancellationRequested();

            bool launched = await ApplyUpdateAsync(manifest, latest, ct).ConfigureAwait(false);
            if (launched)
            {
                Logger.LogInformation(
                    LogEvents.UpdateAppliedId,
                    "Handoff script launched for {Version}; requesting process exit so file locks are released.",
                    latest);
                try
                {
                    OnShutdownRequested?.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Update shutdown callback failed; process may need a manual restart.");
                }
            }
        }

        /// <summary>
        /// دانلود، صحت‌سنجی SHA256 و راه‌اندازی اسکریپت جایگزینی را به‌صورت idempotent اجرا می‌کند.
        /// </summary>
        private async Task<bool> ApplyUpdateAsync(
            AgentUpdateSettings manifest,
            string version,
            CancellationToken ct)
        {
            lock (_applySync)
            {
                if (string.Equals(_applyInFlightVersion, version, StringComparison.OrdinalIgnoreCase))
                    return false;
                _applyInFlightVersion = version;
            }

            using var mutex = new Mutex(false, MutexName);
            bool acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    Logger.LogInformation(LogEvents.UpdateCheckId, "Another update apply is in progress.");
                    return false;
                }

                if (IsAlreadyApplied(version))
                    return false;

                if (!TryCreateStagingRoot(out string stagingRoot))
                    return false;

                string versionDir = Path.Combine(stagingRoot, version);
                Directory.CreateDirectory(versionDir);
                string packagePath = Path.Combine(versionDir, "package.bin");
                string expectedHash = NormalizeHash(manifest.Sha256);

                if (File.Exists(packagePath) && HashesEqual(expectedHash, await HashFileAsync(packagePath, ct).ConfigureAwait(false)))
                {
                    Logger.LogInformation(
                        LogEvents.UpdateCheckId,
                        "Staged payload for {Version} already present with matching SHA256. Reusing.",
                        version);
                }
                else
                {
                    bool downloaded = await DownloadWithRetriesAsync(
                        manifest.DownloadUrl.Trim(),
                        packagePath,
                        expectedHash,
                        manifest.DownloadRetryCount > 0 ? manifest.DownloadRetryCount : DefaultRetryCount,
                        ct).ConfigureAwait(false);

                    if (!downloaded)
                        return false;
                }

                string payloadDir = PreparePayloadDirectory(versionDir, packagePath);
                if (string.IsNullOrEmpty(payloadDir))
                    return false;

                string batPath = MaterializeHandoffScript(stagingRoot);
                if (string.IsNullOrEmpty(batPath))
                    return false;

                string targetDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string markerPath = GetMarkerPath();
                string serviceName = string.IsNullOrWhiteSpace(manifest.ServiceName)
                    ? "Ergonomy.Service"
                    : manifest.ServiceName.Trim();
                string restartExe = ResolveRestartExecutable();
                int pid = Environment.ProcessId;

                // Every FileStream / HttpContent must already be disposed here (using-blocks
                // above) so the handoff script can move/overwrite the install directory.
                var startInfo = new ProcessStartInfo
                {
                    FileName = batPath,
                    Arguments = string.Join(" ",
                        Quote(payloadDir),
                        Quote(targetDir),
                        pid.ToString(CultureInfo.InvariantCulture),
                        Quote(serviceName),
                        Quote(version),
                        Quote(markerPath),
                        Quote(restartExe)),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = stagingRoot
                };

                Process? handoff = Process.Start(startInfo);
                if (handoff == null)
                {
                    Logger.LogWarning(LogEvents.UpdateDownloadFailedId, "Failed to start apply_update.bat.");
                    return false;
                }

                _metrics.IncrementCounter(
                    "ergonomy_update_applied_total",
                    "Number of update handoffs launched.",
                    1);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(LogEvents.UpdateDownloadFailedId, ex, "Update apply failed for {Version}.", version);
                return false;
            }
            finally
            {
                if (acquired)
                {
                    try { mutex.ReleaseMutex(); }
                    catch (ApplicationException) { }
                }

                lock (_applySync)
                {
                    if (string.Equals(_applyInFlightVersion, version, StringComparison.OrdinalIgnoreCase))
                        _applyInFlightVersion = null;
                }
            }
        }

        /// <summary>
        /// بسته را با تلاش مجدد برای خطاهای گذرای شبکه دانلود می‌کند و SHA256 را قبل از جایگزینی اتمی می‌سنجد.
        /// </summary>
        private async Task<bool> DownloadWithRetriesAsync(
            string url,
            string destinationPath,
            string expectedHash,
            int retryCount,
            CancellationToken ct)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Logger.LogWarning(
                    LogEvents.UpdateDownloadFailedId,
                    "Update download URL is not http/https. Refusing.");
                return false;
            }

            string partialPath = destinationPath + ".partial";
            Exception? lastError = null;

            for (int attempt = 1; attempt <= retryCount; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    TryDelete(partialPath);

                    using HttpResponseMessage response = await _httpClient.GetAsync(
                        uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        if (!IsTransientStatus(response.StatusCode))
                        {
                            Logger.LogWarning(
                                LogEvents.UpdateDownloadFailedId,
                                "Update download returned permanent status {StatusCode}.",
                                (int)response.StatusCode);
                            return false;
                        }

                        throw new HttpRequestException(
                            $"Transient HTTP {(int)response.StatusCode} while downloading update.");
                    }

                    await using (Stream httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await using (var fileStream = new FileStream(
                        partialPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 81920,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await httpStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                        await fileStream.FlushAsync(ct).ConfigureAwait(false);
                    }

                    string actualHash = await HashFileAsync(partialPath, ct).ConfigureAwait(false);
                    if (!HashesEqual(expectedHash, actualHash))
                    {
                        Logger.LogWarning(
                            LogEvents.UpdateIntegrityFailedId,
                            "Update SHA256 mismatch. Expected={Expected} Actual={Actual}",
                            expectedHash, actualHash);
                        TryDelete(partialPath);
                        _metrics.IncrementCounter(
                            "ergonomy_update_integrity_failures_total",
                            "SHA256 mismatches while downloading agent updates.",
                            1);
                        return false;
                    }

                    TryDelete(destinationPath);
                    File.Move(partialPath, destinationPath, overwrite: true);

                    _metrics.IncrementCounter(
                        "ergonomy_update_downloads_total",
                        "Successful update package downloads.",
                        1);
                    Logger.LogInformation(
                        LogEvents.UpdateAppliedId,
                        "Update package downloaded and SHA256 verified.");
                    return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDelete(partialPath);
                    throw;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    lastError = ex;
                    TryDelete(partialPath);
                    int delaySeconds = Math.Min(30, (int)Math.Pow(2, attempt - 1));
                    Logger.LogWarning(
                        LogEvents.UpdateDownloadFailedId,
                        "Transient update download failure (attempt {Attempt}/{Retries}); retrying in {Seconds}s.",
                        attempt, retryCount, delaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    TryDelete(partialPath);
                    Logger.LogWarning(LogEvents.UpdateDownloadFailedId, ex, "Update download failed permanently.");
                    return false;
                }
            }

            Logger.LogWarning(
                LogEvents.UpdateDownloadFailedId,
                lastError,
                "Update download exhausted retries.");
            return false;
        }

        /// <summary>
        /// بسته دانلودشده را به پوشه قابل کپی تبدیل می‌کند: فایل zip استخراج می‌شود
        /// و فایل تکی به‌نام Ergonomy.exe قرار می‌گیرد تا robocopy باینری را جایگزین کند.
        /// </summary>
        private string PreparePayloadDirectory(string versionDir, string packagePath)
        {
            try
            {
                string contentDir = Path.Combine(versionDir, "content");
                if (Directory.Exists(contentDir))
                    Directory.Delete(contentDir, recursive: true);
                Directory.CreateDirectory(contentDir);

                if (IsZipArchive(packagePath))
                {
                    ZipFile.ExtractToDirectory(packagePath, contentDir, overwriteFiles: true);
                }
                else
                {
                    string destExe = Path.Combine(contentDir, "Ergonomy.exe");
                    File.Copy(packagePath, destExe, overwrite: true);
                }

                return contentDir;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to prepare update payload directory.");
                return string.Empty;
            }
        }

        private static bool IsZipArchive(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                Span<byte> magic = stackalloc byte[4];
                int read = stream.Read(magic);
                return read >= 2 && magic[0] == (byte)'P' && magic[1] == (byte)'K';
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// اسکریپت جایگزینی را در پوشه staging می‌نویسد تا بعد از خروج فرایند در دسترس بماند.
        /// </summary>
        private string MaterializeHandoffScript(string stagingRoot)
        {
            string destination = Path.Combine(stagingRoot, "apply_update.bat");
            string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "apply_update.bat");
            try
            {
                if (File.Exists(bundled))
                {
                    File.Copy(bundled, destination, overwrite: true);
                }
                else if (!File.Exists(destination))
                {
                    File.WriteAllText(destination, EmbeddedHandoffScript, Encoding.ASCII);
                }

                return destination;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to materialize apply_update.bat.");
                return string.Empty;
            }
        }

        /// <summary>
        /// نسخه در حال اجرا را از اسمبلی می‌خواند و در صورت نبود به خط پایه ۱.۰.۰ برمی‌گردد.
        /// </summary>
        internal static string ResolveCurrentVersion()
        {
            try
            {
                var assembly = typeof(UpdateManager).Assembly;
                string? informational = assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    is System.Reflection.AssemblyInformationalVersionAttribute[] attrs
                    && attrs.Length > 0
                    ? attrs[0].InformationalVersion
                    : null;

                if (!string.IsNullOrWhiteSpace(informational))
                {
                    string version = informational.Split('+', 2)[0].Trim();
                    if (SemanticVersion.TryParse(version, out _))
                        return version;
                }

                Version? asm = assembly.GetName().Version;
                if (asm != null && !(asm.Major == 0 && asm.Minor == 0 && asm.Build == 0))
                    return $"{asm.Major}.{asm.Minor}.{Math.Max(asm.Build, 0)}";
            }
            catch
            {
            }

            return BaselineVersion;
        }

        /// <summary>
        /// تأخیر قطعی برای جلوگیری از هجوم همزمان همه عامل‌ها هنگام انتشار نسخه جدید.
        /// </summary>
        internal static TimeSpan ComputeDeterministicJitter(string key, int maxSeconds)
        {
            if (maxSeconds <= 0)
                return TimeSpan.Zero;

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            uint value = BitConverter.ToUInt32(hash, 0);
            int seconds = (int)(value % ((uint)maxSeconds + 1));
            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// اگر نشانگر نسخه اعمال‌شده با نسخه هدف یکی باشد، به‌روزرسانی تکراری نیست.
        /// </summary>
        private static bool IsAlreadyApplied(string version)
        {
            try
            {
                string marker = GetMarkerPath();
                if (!File.Exists(marker))
                    return false;
                string applied = File.ReadAllText(marker).Trim();
                return string.Equals(applied, version, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// نشانگر نسخه را برای مسیر idempotent می‌نویسد وقتی عامل از قبل روی نسخه هدف است.
        /// </summary>
        private static void WriteMarker(string version, bool alreadyCurrent)
        {
            if (!alreadyCurrent)
                return;
            try
            {
                string marker = GetMarkerPath();
                string? dir = Path.GetDirectoryName(marker);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                if (!IsAlreadyApplied(version))
                    File.WriteAllText(marker, version + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static string GetMarkerPath()
        {
            return Path.Combine(GetDataRoot(), "applied_version");
        }

        private static bool TryCreateStagingRoot(out string stagingRoot)
        {
            stagingRoot = Path.Combine(GetDataRoot(), "staging");
            try
            {
                Directory.CreateDirectory(stagingRoot);
                return true;
            }
            catch
            {
                stagingRoot = Path.Combine(Path.GetTempPath(), "Ergonomy", "updates", "staging");
                try
                {
                    Directory.CreateDirectory(stagingRoot);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static string GetDataRoot()
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(programData))
                return Path.Combine(programData, "Ergonomy", "updates");
            return Path.Combine(Path.GetTempPath(), "Ergonomy", "updates");
        }

        private static string ResolveRestartExecutable()
        {
            try
            {
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return path;
            }
            catch
            {
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ergonomy.exe");
        }

        private static async Task<string> HashFileAsync(string path, CancellationToken ct)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash);
        }

        private static string NormalizeHash(string hash)
        {
            return hash.Trim().Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        }

        private static bool HashesEqual(string expected, string actual)
        {
            return string.Equals(NormalizeHash(expected), NormalizeHash(actual), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTransient(Exception ex)
        {
            if (ex is HttpRequestException or IOException or SocketException or TimeoutException)
                return true;
            if (ex is TaskCanceledException { CancellationToken.IsCancellationRequested: false })
                return true;
            return ex.InnerException != null && IsTransient(ex.InnerException);
        }

        private static bool IsTransientStatus(HttpStatusCode status)
        {
            int code = (int)status;
            return code == 408 || code == 429 || code >= 500;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private const string EmbeddedHandoffScript =
            "@echo off\r\n" +
            "setlocal EnableExtensions EnableDelayedExpansion\r\n" +
            "set \"SOURCE=%~1\"\r\n" +
            "set \"TARGET=%~2\"\r\n" +
            "set \"PID=%~3\"\r\n" +
            "set \"SERVICE=%~4\"\r\n" +
            "set \"VERSION=%~5\"\r\n" +
            "set \"MARKER=%~6\"\r\n" +
            "set \"RESTART_EXE=%~7\"\r\n" +
            "if not exist \"%SOURCE%\\\" exit /b 2\r\n" +
            "if exist \"%MARKER%\" (\r\n" +
            "  set /p APPLIED=<\"%MARKER%\"\r\n" +
            "  if /I \"!APPLIED!\"==\"%VERSION%\" goto :restart\r\n" +
            ")\r\n" +
            ":waitpid\r\n" +
            "if \"%PID%\"==\"\" goto :copy\r\n" +
            "if \"%PID%\"==\"0\" goto :copy\r\n" +
            "tasklist /FI \"PID eq %PID%\" 2>nul | findstr /R /C:\" %PID% \" >nul\r\n" +
            "if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto :waitpid )\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            ":copy\r\n" +
            "if not exist \"%TARGET%\\\" mkdir \"%TARGET%\" >nul 2>&1\r\n" +
            "set /a ATTEMPT=0\r\n" +
            ":copylock\r\n" +
            "set /a ATTEMPT+=1\r\n" +
            "robocopy \"%SOURCE%\" \"%TARGET%\" /E /IS /IT /R:3 /W:2 /NFL /NDL /NJH /NJS /NP\r\n" +
            "if %ERRORLEVEL% GEQ 8 (\r\n" +
            "  if %ATTEMPT% GEQ 15 exit /b 3\r\n" +
            "  timeout /t 2 /nobreak >nul & goto :copylock\r\n" +
            ")\r\n" +
            "if not \"%MARKER%\"==\"\" ( >\"%MARKER%\" echo %VERSION% )\r\n" +
            ":restart\r\n" +
            "if not \"%SERVICE%\"==\"\" (\r\n" +
            "  sc query \"%SERVICE%\" >nul 2>&1\r\n" +
            "  if not errorlevel 1 ( net stop \"%SERVICE%\" /y >nul 2>&1 & net start \"%SERVICE%\" & if not errorlevel 1 goto :eof )\r\n" +
            ")\r\n" +
            "if not \"%RESTART_EXE%\"==\"\" if exist \"%RESTART_EXE%\" start \"\" \"%RESTART_EXE%\"\r\n";
    }

    /// <summary>
    /// Minimal MAJOR.MINOR.PATCH comparator used by <see cref="UpdateManager"/>.
    /// Pre-release labels sort below the matching release (1.2.0-beta &lt; 1.2.0).
    /// </summary>
    internal readonly struct SemanticVersion : IComparable<SemanticVersion>
    {
        public static readonly SemanticVersion Baseline = new(1, 0, 0, string.Empty);

        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        public string PreRelease { get; }

        public SemanticVersion(int major, int minor, int patch, string preRelease)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            PreRelease = preRelease ?? string.Empty;
        }

        public static bool TryParse(string? value, out SemanticVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string trimmed = value.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[1..];

            int plus = trimmed.IndexOf('+');
            if (plus >= 0)
                trimmed = trimmed[..plus];

            string core = trimmed;
            string pre = string.Empty;
            int dash = trimmed.IndexOf('-');
            if (dash >= 0)
            {
                core = trimmed[..dash];
                pre = trimmed[(dash + 1)..];
            }

            string[] parts = core.Split('.');
            if (parts.Length < 1 || parts.Length > 3)
                return false;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) || major < 0)
                return false;
            int minor = 0;
            int patch = 0;
            if (parts.Length > 1)
            {
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor) || minor < 0)
                    return false;
            }
            if (parts.Length > 2)
            {
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out patch) || patch < 0)
                    return false;
            }

            version = new SemanticVersion(major, minor, patch, pre);
            return true;
        }

        public int CompareTo(SemanticVersion other)
        {
            int cmp = Major.CompareTo(other.Major);
            if (cmp != 0) return cmp;
            cmp = Minor.CompareTo(other.Minor);
            if (cmp != 0) return cmp;
            cmp = Patch.CompareTo(other.Patch);
            if (cmp != 0) return cmp;

            bool thisRelease = string.IsNullOrEmpty(PreRelease);
            bool otherRelease = string.IsNullOrEmpty(other.PreRelease);
            if (thisRelease && otherRelease) return 0;
            if (thisRelease) return 1;
            if (otherRelease) return -1;
            return string.Compare(PreRelease, other.PreRelease, StringComparison.Ordinal);
        }
    }
}
