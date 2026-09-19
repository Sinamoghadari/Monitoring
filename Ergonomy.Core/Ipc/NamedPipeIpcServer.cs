using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Core.Ipc
{
    /// <summary>
    /// Named-pipe server hosted by the Ergonomy.Service process.
    ///
    /// Accepts multiple concurrent instances (one interactive Task process per logon session),
    /// pumps each connection on its own task, and exposes fire-and-forget send/broadcast to
    /// the connected interactive processes. No socket is ever opened.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class NamedPipeIpcServer : IDisposable
    {
        private readonly ILogger<NamedPipeIpcServer> _logger;
        private readonly string _pipeName;
        private readonly int _maxInstances;
        private readonly ConcurrentDictionary<string, IpcConnection> _connections = new();
        private CancellationTokenSource? _cts;
        private Task? _acceptLoop;
        private int _connectionSeq;
        private bool _disposed;

        /// <summary>
        /// سرور Named Pipe فرایند سرویس را با ظرفیت چندنمونه و لاگر می‌سازد.
        /// </summary>
        /// <param name="logger">ثبت‌کننده پذیرش، قطع و خطای پروتکل.</param>
        /// <param name="pipeName">نام پایپ نسخه‌شده.</param>
        /// <param name="maxInstances">حداکثر اتصال همزمان فرایندهای تعاملی.</param>
        public NamedPipeIpcServer(
            ILogger<NamedPipeIpcServer> logger,
            string? pipeName = null,
            int maxInstances = IpcConstants.MaxServerInstances)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pipeName = string.IsNullOrWhiteSpace(pipeName) ? IpcConstants.PipeName : pipeName!;
            _maxInstances = maxInstances;
        }

        /// <summary>Invoked for every inbound message. Exceptions are logged, never fatal.</summary>
        public Func<IpcConnection, IpcMessage, CancellationToken, Task>? MessageReceived { get; set; }

        public Action<IpcConnection>? ClientConnected { get; set; }
        public Action<IpcConnection>? ClientDisconnected { get; set; }

        public int ConnectedClients => _connections.Count;
        public string PipeName => _pipeName;

        /// <summary>
        /// حلقه پذیرش پس‌زمینه را شروع می‌کند تا فرایندهای Task بتوانند متصل شوند.
        /// </summary>
        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_acceptLoop != null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _logger.LogInformation("IPC server listening on named pipe. Pipe=\\\\.\\pipe\\{PipeName} MaxInstances={Max}",
                _pipeName, _maxInstances);
        }

        /// <summary>
        /// به‌صورت ناهمگام نمونه‌های سرور ACLدار را می‌سازد، منتظر اتصال می‌ماند
        /// و برای هر کلاینت یک پمپ جداگانه اجرا می‌کند.
        /// </summary>
        /// <param name="ct">توکن لغو حلقه پذیرش.</param>
        /// <returns>وظیفه‌ای که تا توقف سرور زنده است.</returns>
        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = IpcSecurityFactory.CreateServerStream(_pipeName, _maxInstances);
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    waitCts.CancelAfter(IpcConstants.AcceptAclRefresh);
                    try
                    {
                        await server.WaitForConnectionAsync(waitCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // No client connected before the ACL refresh window. Recreate the
                        // instance so a newly logged-on user SID is included in the DACL.
                        server.Dispose();
                        server = null;
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    break;
                }
                catch (IOException ex)
                {
                    server?.Dispose();
                    _logger.LogWarning(ex, "IPC accept failed; retrying.");
                    await DelayQuietAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    server?.Dispose();
                    _logger.LogError(ex, "IPC pipe could not be created (ACL/privilege problem). Pipe={PipeName}", _pipeName);
                    await DelayQuietAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex)
                {
                    server?.Dispose();
                    _logger.LogError(ex, "Unexpected IPC accept failure.");
                    await DelayQuietAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    continue;
                }

                if (server == null)
                    continue;

                string id = $"conn-{Interlocked.Increment(ref _connectionSeq)}";
                var connection = new IpcConnection(server, id);
                _connections[id] = connection;
                _logger.LogInformation("IPC client connected. Connection={ConnectionId} Clients={Count}", id, _connections.Count);

                try { ClientConnected?.Invoke(connection); }
                catch (Exception ex) { _logger.LogError(ex, "ClientConnected handler failed. Connection={ConnectionId}", id); }

                _ = Task.Run(() => PumpConnectionAsync(connection, ct), CancellationToken.None);
            }

            _logger.LogInformation("IPC accept loop stopped.");
        }

        /// <summary>
        /// پیام‌های یک اتصال را می‌خواند، hello را ثبت کرده و به router سرویس تحویل می‌دهد.
        /// </summary>
        /// <param name="connection">اتصال کلاینت تعاملی.</param>
        /// <param name="ct">توکن لغو پمپ.</param>
        /// <returns>وظیفه‌ای که تا قطع کلاینت ادامه دارد.</returns>
        private async Task PumpConnectionAsync(IpcConnection connection, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    IpcMessage? message = await connection.ReceiveAsync(ct).ConfigureAwait(false);
                    if (message is null)
                    {
                        break; // peer closed
                    }

                    if (message.ProtocolVersion != IpcConstants.ProtocolVersion)
                    {
                        _logger.LogWarning(
                            "Dropping IPC message with mismatched protocol version. Connection={ConnectionId} Type={Type} Version={Version}",
                            connection.Id, message.Type, message.ProtocolVersion);
                        continue;
                    }

                    if (message.Type == IpcMessageTypes.Hello)
                    {
                        connection.Peer = message.GetPayload<TaskHelloPayload>();
                        _logger.LogInformation(
                            "IPC hello received. Connection={ConnectionId} Pid={Pid} WinSession={Session} User={User}",
                            connection.Id, connection.Peer?.ProcessId, connection.Peer?.WindowsSessionId,
                            connection.Peer?.WindowsUsername);
                    }

                    Func<IpcConnection, IpcMessage, CancellationToken, Task>? handler = MessageReceived;
                    if (handler is null)
                    {
                        continue;
                    }

                    try
                    {
                        await handler(connection, message, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "IPC message handler failed. Connection={ConnectionId} Type={Type}",
                            connection.Id, message.Type);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (IpcProtocolException ex)
            {
                _logger.LogError(ex, "IPC protocol violation; dropping connection. Connection={ConnectionId}", connection.Id);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "IPC connection closed. Connection={ConnectionId}", connection.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IPC connection pump failed. Connection={ConnectionId}", connection.Id);
            }
            finally
            {
                _connections.TryRemove(connection.Id, out _);
                try { ClientDisconnected?.Invoke(connection); }
                catch (Exception ex) { _logger.LogError(ex, "ClientDisconnected handler failed."); }

                connection.Dispose();
                _logger.LogInformation("IPC client disconnected. Connection={ConnectionId} Clients={Count}",
                    connection.Id, _connections.Count);
            }
        }

        /// <summary>
        /// پیام را به همه فرایندهای تعاملی متصل پخش می‌کند و خطای هر اتصال را جداگانه می‌گیرد.
        /// </summary>
        /// <param name="message">پاکت پخش‌شونده.</param>
        /// <param name="ct">توکن لغو ارسال.</param>
        /// <returns>وظیفه‌ای که پس از تلاش برای همه اتصالات کامل می‌شود.</returns>
        public async Task BroadcastAsync(IpcMessage message, CancellationToken ct = default)
        {
            foreach (IpcConnection connection in _connections.Values.ToArray())
            {
                await SendSafeAsync(connection, message, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// پیام را به یک اتصال مشخص ارسال می‌کند بدون اینکه خطای نوشتن به فراخواننده نشت کند.
        /// </summary>
        /// <param name="connection">اتصال مقصد.</param>
        /// <param name="message">پاکت ارسالی.</param>
        /// <param name="ct">توکن لغو.</param>
        /// <returns>وظیفه ارسال امن.</returns>
        public Task SendAsync(IpcConnection connection, IpcMessage message, CancellationToken ct = default)
            => SendSafeAsync(connection, message, ct);

        /// <summary>
        /// ارسال روی یک اتصال را در try/catch می‌پیچد تا شکست یک کلاینت بقیه را متوقف نکند.
        /// </summary>
        /// <param name="connection">اتصال مقصد.</param>
        /// <param name="message">پاکت ارسالی.</param>
        /// <param name="ct">توکن لغو.</param>
        /// <returns>وظیفه ارسال.</returns>
        private async Task SendSafeAsync(IpcConnection connection, IpcMessage message, CancellationToken ct)
        {
            try
            {
                if (connection.IsConnected)
                {
                    await connection.SendAsync(message, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IPC send failed. Connection={ConnectionId} Type={Type}", connection.Id, message.Type);
            }
        }

        /// <summary>
        /// به‌صورت ناهمگام حلقه پذیرش را لغو کرده و همه اتصالات باز را می‌بندد.
        /// </summary>
        /// <returns>وظیفه‌ای که پس از توقف سرور کامل می‌شود.</returns>
        public async Task StopAsync()
        {
            if (_cts is null)
            {
                return;
            }

            try { _cts.Cancel(); } catch (ObjectDisposedException) { }

            foreach (IpcConnection connection in _connections.Values.ToArray())
            {
                connection.Dispose();
            }

            _connections.Clear();

            if (_acceptLoop != null)
            {
                try
                {
                    await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("IPC accept loop did not stop within the grace period.");
                }
                catch (Exception)
                {
                    // already faulted / cancelled
                }
            }

            _acceptLoop = null;
        }

        /// <summary>
        /// تأخیر کوتاه بین تلاش‌های پذیرش ناموفق را بدون پرتاب OperationCanceledException اعمال می‌کند.
        /// </summary>
        /// <param name="delay">مدت انتظار.</param>
        /// <param name="ct">توکن لغو.</param>
        /// <returns>وظیفه تأخیر.</returns>
        private static async Task DelayQuietAsync(TimeSpan delay, CancellationToken ct)
        {
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// سرور پایپ را متوقف کرده و توکن لغو را آزاد می‌کند.
        /// </summary>
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
