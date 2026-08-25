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
        /// <summary>
        /// به‌صورت ناهمگام یک فریم طول‌پیشوند را روی استریم Named Pipe می‌نویسد و آن را flush می‌کند.
        /// </summary>
        /// <param name="stream">استریم پایپ.</param>
        /// <param name="payload">بدنه UTF-8 JSON.</param>
        /// <param name="ct">توکن لغو نوشتن.</param>
        /// <returns>وظیفه‌ای که پس از نوشتن کامل فریم تمام می‌شود.</returns>
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

        /// <summary>
        /// به‌صورت ناهمگام یک فریم کامل را می‌خواند. در قطع اتصال تمیز null برمی‌گرداند.
        /// </summary>
        /// <param name="stream">استریم پایپ.</param>
        /// <param name="ct">توکن لغو خواندن.</param>
        /// <returns>بدنه فریم یا null در صورت قطع همتا.</returns>
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

        /// <summary>
        /// بافر را دقیقاً به اندازه درخواستی پر می‌کند تا خواندن ناقص با پیام کامل اشتباه نشود.
        /// </summary>
        /// <param name="stream">استریم مبدأ.</param>
        /// <param name="buffer">بافر مقصد.</param>
        /// <param name="ct">توکن لغو.</param>
        /// <returns>اگر بافر کامل پر شد true و اگر استریم بسته شد false است.</returns>
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
        /// <summary>
        /// استثنای نقض قرارداد فریم یا پاکت IPC را می‌سازد.
        /// </summary>
        /// <param name="message">شرح نقض پروتکل.</param>
        public IpcProtocolException(string message) : base(message) { }

        /// <summary>
        /// استثنای نقض پروتکل را با استثنای درونی می‌سازد.
        /// </summary>
        /// <param name="message">شرح نقض پروتکل.</param>
        /// <param name="inner">استثنای علت اصلی.</param>
        public IpcProtocolException(string message, Exception inner) : base(message, inner) { }
    }
}
