using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Core.Ipc
{
    /// <summary>
    /// Named-pipe client used by the interactive Ergonomy.Task process.
    ///
    /// Owns a single background connect/receive loop with exponential backoff so the Task
    /// process survives a Service restart (and can start before the Service is ready).
    /// Sends are non-blocking for callers on the UI thread: they either go out on the current
    /// connection or are reported as failed - no UI thread ever waits on the pipe.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class NamedPipeIpcClient : IDisposable
    {
        private readonly ILogger<NamedPipeIpcClient> _logger;
        private readonly string _pipeName;
        private readonly string _serverName;
        private readonly object _sync = new();
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private IpcConnection? _connection;
        private bool _disposed;

        public NamedPipeIpcClient(
            ILogger<NamedPipeIpcClient> logger,
            string? pipeName = null,
            string? serverName = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pipeName = string.IsNullOrWhiteSpace(pipeName) ? IpcConstants.PipeName : pipeName!;
            _serverName = string.IsNullOrWhiteSpace(serverName) ? IpcConstants.ServerName : serverName!;
        }

        /// <summary>Invoked for every inbound message on a background thread.</summary>
        public Func<IpcMessage, CancellationToken, Task>? MessageReceived { get; set; }

        public Action? Connected { get; set; }
        public Action? Disconnected { get; set; }

        public bool IsConnected
        {
            get
            {
                lock (_sync)
                {
                    return _connection?.IsConnected == true;
                }
            }
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop != null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
            _logger.LogInformation("IPC client started. Pipe=\\\\{Server}\\pipe\\{PipeName}", _serverName, _pipeName);
        }

        private async Task RunAsync(CancellationToken ct)
        {
            TimeSpan delay = IpcConstants.ReconnectInitialDelay;

            while (!ct.IsCancellationRequested)
            {
                NamedPipeClientStream? pipe = null;
                try
                {
                    pipe = new NamedPipeClientStream(
                        _serverName,
                        _pipeName,
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                        TokenImpersonationLevel.None);

                    await pipe.ConnectAsync((int)IpcConstants.ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);

                    var connection = new IpcConnection(pipe, $"client-{Environment.ProcessId}");
                    lock (_sync) { _connection = connection; }

                    delay = IpcConstants.ReconnectInitialDelay;
                    _logger.LogInformation("IPC client connected to the service.");
                    try { Connected?.Invoke(); }
                    catch (Exception ex) { _logger.LogError(ex, "Connected handler failed."); }

                    await PumpAsync(connection, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    pipe?.Dispose();
                    break;
                }
                catch (TimeoutException)
                {
                    pipe?.Dispose();
                    _logger.LogDebug("IPC connect timed out; the service pipe is not available yet.");
                }
                catch (IOException ex)
                {
                    pipe?.Dispose();
                    _logger.LogDebug(ex, "IPC connection dropped.");
                }
                catch (UnauthorizedAccessException ex)
                {
                    pipe?.Dispose();
                    _logger.LogError(ex, "IPC connect denied by the pipe ACL. Pipe={PipeName}", _pipeName);
                }
                catch (Exception ex)
                {
                    pipe?.Dispose();
                    _logger.LogError(ex, "Unexpected IPC client failure.");
                }
                finally
                {
                    IpcConnection? previous;
                    lock (_sync)
                    {
                        previous = _connection;
                        _connection = null;
                    }

                    if (previous != null)
                    {
                        previous.Dispose();
                        try { Disconnected?.Invoke(); }
                        catch (Exception ex) { _logger.LogError(ex, "Disconnected handler failed."); }
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                double next = Math.Min(delay.TotalSeconds * 2, IpcConstants.ReconnectMaxDelay.TotalSeconds);
                delay = TimeSpan.FromSeconds(next);
            }

            _logger.LogInformation("IPC client loop stopped.");
        }

        private async Task PumpAsync(IpcConnection connection, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                IpcMessage? message = await connection.ReceiveAsync(ct).ConfigureAwait(false);
                if (message is null)
                {
                    return;
                }

                if (message.ProtocolVersion != IpcConstants.ProtocolVersion)
                {
                    _logger.LogWarning("Dropping IPC message with mismatched protocol version. Type={Type} Version={Version}",
                        message.Type, message.ProtocolVersion);
                    continue;
                }

                Func<IpcMessage, CancellationToken, Task>? handler = MessageReceived;
                if (handler is null)
                {
                    continue;
                }

                try
                {
                    await handler(message, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IPC message handler failed. Type={Type}", message.Type);
                }
            }
        }

        /// <summary>Sends a message. Returns false when currently disconnected (caller may buffer/drop).</summary>
        public async Task<bool> TrySendAsync(IpcMessage message, CancellationToken ct = default)
        {
            IpcConnection? connection;
            lock (_sync) { connection = _connection; }

            if (connection is null || !connection.IsConnected)
            {
                return false;
            }

            try
            {
                await connection.SendAsync(message, ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IPC send failed. Type={Type}", message.Type);
                return false;
            }
        }

        public async Task StopAsync()
        {
            if (_cts is null)
            {
                return;
            }

            try { _cts.Cancel(); } catch (ObjectDisposedException) { }

            IpcConnection? connection;
            lock (_sync)
            {
                connection = _connection;
                _connection = null;
            }

            connection?.Dispose();

            if (_loop != null)
            {
                try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { _logger.LogWarning("IPC client loop did not stop within the grace period."); }
                catch (Exception) { /* already finished/faulted */ }
            }

            _loop = null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try { StopAsync().GetAwaiter().GetResult(); } catch (Exception) { /* best effort */ }
            _cts?.Dispose();
            _cts = null;
        }
    }
}
