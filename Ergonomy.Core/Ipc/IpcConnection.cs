using System;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ergonomy.Core.Ipc
{
    /// <summary>
    /// One live pipe connection. Writes are serialised through a semaphore so several
    /// producers (timer threads, UI thread, workers) can send concurrently without
    /// interleaving frames. Reads are performed by a single owning loop.
    /// </summary>
    public sealed class IpcConnection : IDisposable
    {
        private readonly PipeStream _stream;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _disposed;

        public IpcConnection(PipeStream stream, string id)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Id = id;
            ConnectedUtc = DateTime.UtcNow;
        }

        /// <summary>Stable id for logging/correlation (not a security boundary).</summary>
        public string Id { get; }

        public DateTime ConnectedUtc { get; }

        /// <summary>Identity announced by the peer via <see cref="IpcMessageTypes.Hello"/>, if any.</summary>
        public TaskHelloPayload? Peer { get; internal set; }

        public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _stream.IsConnected;

        public async Task SendAsync(IpcMessage message, CancellationToken ct = default)
        {
            if (message is null) throw new ArgumentNullException(nameof(message));

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, IpcSerializer.Options);

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await IpcFraming.WriteFrameAsync(_stream, payload, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>Reads the next message, or <c>null</c> when the peer disconnected.</summary>
        public async Task<IpcMessage?> ReceiveAsync(CancellationToken ct = default)
        {
            byte[]? frame = await IpcFraming.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
            if (frame is null)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<IpcMessage>(frame, IpcSerializer.Options)
                       ?? throw new IpcProtocolException("Received an empty IPC envelope.");
            }
            catch (JsonException ex)
            {
                throw new IpcProtocolException(
                    $"Malformed IPC envelope ({Encoding.UTF8.GetString(frame, 0, Math.Min(frame.Length, 128))}).", ex);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (_stream is NamedPipeServerStream server && server.IsConnected)
                {
                    server.Disconnect();
                }
            }
            catch (Exception)
            {
                // Disconnect on an already-broken pipe is not actionable.
            }

            try { _stream.Dispose(); } catch (Exception) { /* best effort */ }
            _writeLock.Dispose();
        }
    }
}
