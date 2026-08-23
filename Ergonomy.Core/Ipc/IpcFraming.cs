using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ergonomy.Core.Ipc
{
    /// <summary>
    /// Length-prefixed framing (4-byte little-endian payload length + UTF-8 JSON body).
    ///
    /// An explicit length prefix is used instead of relying on <c>PipeTransmissionMode.Message</c>
    /// so the protocol stays correct even if a peer opens the pipe in byte mode, and so a
    /// partial read can never be mistaken for a complete message.
    /// </summary>
    public static class IpcFraming
    {
        public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            if (payload.Length > IpcConstants.MaxFrameBytes)
            {
                throw new IpcProtocolException(
                    $"Outbound frame of {payload.Length} bytes exceeds the {IpcConstants.MaxFrameBytes} byte limit.");
            }

            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Reads one frame. Returns <c>null</c> on a clean end-of-stream (peer disconnected).</summary>
        public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));

            byte[] header = new byte[4];
            if (!await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false))
            {
                return null;
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 0 || length > IpcConstants.MaxFrameBytes)
            {
                throw new IpcProtocolException(
                    $"Inbound frame length {length} is invalid (limit {IpcConstants.MaxFrameBytes}). Connection is desynchronised.");
            }

            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] body = new byte[length];
            if (!await ReadExactlyAsync(stream, body, ct).ConfigureAwait(false))
            {
                // Truncated frame: treat as disconnect rather than as data.
                return null;
            }

            return body;
        }

        private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int chunk = await stream.ReadAsync(buffer.Slice(read), ct).ConfigureAwait(false);
                if (chunk == 0)
                {
                    return false;
                }

                read += chunk;
            }

            return true;
        }
    }

    /// <summary>Raised when a peer violates the framing/envelope contract.</summary>
    public sealed class IpcProtocolException : Exception
    {
        public IpcProtocolException(string message) : base(message) { }
        public IpcProtocolException(string message, Exception inner) : base(message, inner) { }
    }
}
