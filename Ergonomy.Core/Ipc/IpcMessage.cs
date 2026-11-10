using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ergonomy.Core.Ipc
{
    /// <summary>Well-known <see cref="IpcMessage.Type"/> values.</summary>
    public static class IpcMessageTypes
    {
        // Task -> Service
        public const string Hello = "task.hello";
        public const string Heartbeat = "task.heartbeat";
        public const string ActivityReport = "task.activity";
        public const string AlarmAck = "task.alarm.ack";
        public const string ProblemLog = "task.problem";
        public const string Goodbye = "task.goodbye";

        // Service -> Task
        public const string HelloAck = "service.hello.ack";
        public const string ShowAlarm = "service.alarm.show";
        public const string SettingsSnapshot = "service.settings";
        public const string StopCollection = "service.collection.stop";
        public const string StartCollection = "service.collection.start";
        public const string ShutdownRequest = "service.shutdown";
    }

    /// <summary>
    /// Envelope for every frame on the pipe. The payload stays as raw JSON so the transport
    /// layer never needs to know the concrete contract types (forward compatible: an unknown
    /// message type is logged and ignored rather than killing the connection).
    /// </summary>
    public sealed class IpcMessage
    {
        public string Type { get; set; } = string.Empty;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string? CorrelationId { get; set; }
        public int ProtocolVersion { get; set; } = IpcConstants.ProtocolVersion;
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("payload")]
        public JsonElement? Payload { get; set; }

        /// <summary>
        /// یک پاکت IPC با نوع مشخص و payload سریال‌شده به JSON می‌سازد.
        /// </summary>
        /// <typeparam name="T">نوع قرارداد payload.</typeparam>
        /// <param name="type">نوع پیام در کاتالوگ IPC.</param>
        /// <param name="payload">بدنه قرارداد.</param>
        /// <param name="correlationId">شناسه همبستگی اختیاری برای پاسخ.</param>
        /// <returns>پاکت آماده ارسال روی Named Pipe.</returns>
        public static IpcMessage Create<T>(string type, T payload, string? correlationId = null)
        {
            return new IpcMessage
            {
                Type = type,
                CorrelationId = correlationId,
                Payload = JsonSerializer.SerializeToElement(payload, IpcSerializer.Options)
            };
        }

        /// <summary>
        /// یک پاکت IPC بدون بدنه برای پیام‌های کنترلی می‌سازد.
        /// </summary>
        /// <param name="type">نوع پیام.</param>
        /// <param name="correlationId">شناسه همبستگی اختیاری.</param>
        /// <returns>پاکت بدون payload.</returns>
        public static IpcMessage Create(string type, string? correlationId = null)
            => new IpcMessage { Type = type, CorrelationId = correlationId };

        /// <summary>
        /// payload خام JSON را به نوع قرارداد تبدیل می‌کند و در صورت نبود یا خرابی مقدار پیش‌فرض برمی‌گرداند.
        /// </summary>
        /// <typeparam name="T">نوع قرارداد مورد انتظار.</typeparam>
        /// <returns>نمونه بازسازی‌شده یا مقدار پیش‌فرض.</returns>
        public T? GetPayload<T>()
        {
            if (Payload is null || Payload.Value.ValueKind == JsonValueKind.Null ||
                Payload.Value.ValueKind == JsonValueKind.Undefined)
            {
                return default;
            }

            try
            {
                return Payload.Value.Deserialize<T>(IpcSerializer.Options);
            }
            catch (JsonException)
            {
                return default;
            }
        }
    }

    /// <summary>Shared JSON settings; both sides must use exactly these options.</summary>
    public static class IpcSerializer
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
    }
}
