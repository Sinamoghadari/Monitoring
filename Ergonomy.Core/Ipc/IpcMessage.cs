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

        public static IpcMessage Create<T>(string type, T payload, string? correlationId = null)
        {
            return new IpcMessage
            {
                Type = type,
                CorrelationId = correlationId,
                Payload = JsonSerializer.SerializeToElement(payload, IpcSerializer.Options)
            };
        }

        public static IpcMessage Create(string type, string? correlationId = null)
            => new IpcMessage { Type = type, CorrelationId = correlationId };

        /// <summary>Deserializes the payload, returning <c>default</c> when absent or malformed.</summary>
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
