using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Logging
{
    /// <summary>
    /// Shared app_logs contract: LogLevel is one of INFORMATION / WARNING / ERROR,
    /// and WindowsUsername_RunAdmin is a Windows username with no elevation suffix.
    /// Applied before SQLite persistence and again before Kafka serialization so
    /// already-queued records are cleaned on the way out.
    /// </summary>
    public static class AppLogNormalizer
    {
        public const string Information = "INFORMATION";
        public const string Warning = "WARNING";
        public const string Error = "ERROR";

        public static readonly string[] AllowedLogLevels = { Information, Warning, Error };

        /// <summary>
        /// Maps producer aliases to the three Kafka/SQLite LogLevel values.
        /// Unknown, empty, DEBUG, and TRACE fall back to INFORMATION.
        /// </summary>
        public static string NormalizeLogLevel(string? level)
        {
            if (string.IsNullOrWhiteSpace(level))
                return Information;

            return level.Trim().ToUpperInvariant() switch
            {
                "INFORMATION" or "INFO" or "DEBUG" or "TRACE" or "VERBOSE" or "NOTICE" => Information,
                "WARNING" or "WARN" => Warning,
                "ERROR" or "ERR" or "FATAL" or "CRIT" or "CRITICAL" => Error,
                _ => Information
            };
        }

        /// <summary>
        /// Maps <see cref="LogLevel"/> from ILogger into the app_logs contract.
        /// </summary>
        public static string FromMicrosoftLogLevel(LogLevel level)
        {
            return level switch
            {
                LogLevel.Warning => Warning,
                LogLevel.Error or LogLevel.Critical => Error,
                _ => Information
            };
        }

        /// <summary>
        /// True only for WARNING / ERROR after normalization. INFORMATION (including
        /// DEBUG/TRACE aliases and unknown values) is not a problem record.
        /// </summary>
        public static bool IsProblemLevel(string? level)
        {
            string normalized = NormalizeLogLevel(level);
            return normalized == Warning || normalized == Error;
        }

        /// <summary>
        /// True for <see cref="LogLevel.Warning"/> and above. Healthy Information/Debug emit nothing.
        /// </summary>
        public static bool IsProblemLogLevel(LogLevel level)
            => level >= LogLevel.Warning;

        /// <summary>
        /// Returns only the Windows username. Strips <c>|Elevated=True</c> / <c>|Elevated=False</c>
        /// and any other <c>|Elevated=...</c> metadata. Does not invent a value from elevation flags.
        /// </summary>
        public static string NormalizeWindowsUsernameRunAdmin(string? value, string? fallbackUsername = null)
        {
            string raw = string.IsNullOrWhiteSpace(value) ? (fallbackUsername ?? string.Empty) : value;
            int elevated = raw.IndexOf("|Elevated=", StringComparison.OrdinalIgnoreCase);
            if (elevated >= 0)
                raw = raw[..elevated];

            int pipe = raw.IndexOf('|');
            if (pipe >= 0)
                raw = raw[..pipe];

            return raw.Trim();
        }

        /// <summary>
        /// Normalizes LogLevel and WindowsUsername_RunAdmin on a mutable payload dictionary
        /// (JsonElement dictionaries from the outbox, or metrics payloads).
        /// object? and object dictionary overloads cannot coexist: nullable annotation
        /// erasure makes them the same CLR signature (CS0111).
        /// </summary>
        public static void NormalizeDictionary(IDictionary<string, object> payload, bool normalizeLogLevel = true)
        {
            if (payload == null)
                return;

            var boxed = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in payload)
                boxed[kv.Key] = kv.Value;

            Apply(boxed, normalizeLogLevel);

            foreach (var kv in boxed)
            {
                if (kv.Value != null)
                    payload[kv.Key] = kv.Value;
            }
        }

        private static void Apply(Dictionary<string, object?> payload, bool normalizeLogLevel)
        {
            if (normalizeLogLevel)
                SetString(payload, "LogLevel", NormalizeLogLevel(GetString(payload, "LogLevel")));

            if (HasKey(payload, "WindowsUsername_RunAdmin") || !string.IsNullOrEmpty(GetString(payload, "WindowsUsername_RunAdmin")))
            {
                SetString(
                    payload,
                    "WindowsUsername_RunAdmin",
                    NormalizeWindowsUsernameRunAdmin(
                        GetString(payload, "WindowsUsername_RunAdmin"),
                        GetString(payload, "WindowsUsername")));
            }
        }

        /// <summary>
        /// Rewrites a JSON object payload in place (outbox row) so SQLite stores the contract values.
        /// </summary>
        public static string NormalizePayloadJson(string json, bool normalizeLogLevel = true)
        {
            if (string.IsNullOrWhiteSpace(json))
                return json;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return json;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return json;

                var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    payload[property.Name] = ToClr(property.Value);

                NormalizeDictionary(payload, normalizeLogLevel);
                return JsonSerializer.Serialize(payload);
            }
        }

        private static object? ToClr(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => element.GetRawText()
            };
        }

        private static bool HasKey(IDictionary<string, object?> payload, string key)
        {
            foreach (var kv in payload)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string? GetString(IDictionary<string, object?> payload, string key)
        {
            foreach (var kv in payload)
            {
                if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    continue;
                return ToStringValue(kv.Value);
            }

            return null;
        }

        private static void SetString(IDictionary<string, object?> payload, string key, string value)
        {
            string? existingKey = null;
            foreach (var kv in payload)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    existingKey = kv.Key;
                    break;
                }
            }

            payload[existingKey ?? key] = value;
        }

        private static string? ToStringValue(object? value)
        {
            if (value == null)
                return null;
            if (value is string s)
                return s;
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.String)
                    return element.GetString();
                if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
                    return null;
                return element.ToString();
            }

            return value.ToString();
        }
    }
}
