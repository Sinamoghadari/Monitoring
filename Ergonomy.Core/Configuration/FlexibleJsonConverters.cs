using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ergonomy.Configuration
{
    /// <summary>
    /// Shared JSON options for Control API / PostgreSQL JSONB payloads.
    /// PostgreSQL and the FastAPI panel frequently emit numbers as strings (and the reverse),
    /// so deserialization must accept mixed token types without failing the whole refresh.
    /// </summary>
    public static class SettingsJson
    {
        /// <summary>
        /// گزینه‌های پایدار سریال‌سازی تنظیمات را برای خواندن پاسخ API و JSONB می‌سازد.
        /// </summary>
        public static JsonSerializerOptions CreateOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
                                 | JsonNumberHandling.AllowNamedFloatingPointLiterals,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                Converters =
                {
                    new FlexibleStringConverter(),
                    new FlexibleBoolConverter()
                }
            };
        }
    }

    /// <summary>
    /// Converts JSON string/number/bool/null tokens into a CLR string so Kafka bootstrap
    /// servers, topic names and API URLs survive mixed-type Control API payloads.
    /// </summary>
    public sealed class FlexibleStringConverter : JsonConverter<string>
    {
        /// <summary>
        /// توکن JSON را صرف‌نظر از نوع آن به رشته تبدیل می‌کند.
        /// </summary>
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out long integer))
                        return integer.ToString(CultureInfo.InvariantCulture);
                    if (reader.TryGetDouble(out double number))
                        return number.ToString(CultureInfo.InvariantCulture);
                    return reader.GetDecimal().ToString(CultureInfo.InvariantCulture);
                case JsonTokenType.True:
                    return "true";
                case JsonTokenType.False:
                    return "false";
                case JsonTokenType.Null:
                    return string.Empty;
                case JsonTokenType.StartArray:
                    // Control API / PostgreSQL JSONB often stores Kafka bootstrap as
                    // ["host:9092"] rather than a comma-separated string.
                    return ReadArrayAsCsv(ref reader);
                case JsonTokenType.StartObject:
                    reader.Skip();
                    return string.Empty;
                default:
                    throw new JsonException(
                        $"Cannot convert JSON token '{reader.TokenType}' to a string setting.");
            }
        }

        /// <summary>
        /// آرایه JSON را به رشته CSV تبدیل می‌کند (مثلاً bootstrap کافکا).
        /// </summary>
        private static string ReadArrayAsCsv(ref Utf8JsonReader reader)
        {
            var parts = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    break;

                switch (reader.TokenType)
                {
                    case JsonTokenType.String:
                        string? text = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                            parts.Add(text.Trim());
                        break;
                    case JsonTokenType.Number:
                        if (reader.TryGetInt64(out long integer))
                            parts.Add(integer.ToString(CultureInfo.InvariantCulture));
                        else if (reader.TryGetDouble(out double number))
                            parts.Add(number.ToString(CultureInfo.InvariantCulture));
                        break;
                    case JsonTokenType.True:
                        parts.Add("true");
                        break;
                    case JsonTokenType.False:
                        parts.Add("false");
                        break;
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        reader.Skip();
                        break;
                }
            }

            return string.Join(",", parts);
        }

        /// <summary>
        /// رشته تنظیمات را به‌صورت توکن رشته JSON می‌نویسد.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }

    /// <summary>
    /// Accepts JSON booleans, 0/1 numbers, and common string forms ("true"/"false"/"1"/"0"/"yes"/"no").
    /// </summary>
    public sealed class FlexibleBoolConverter : JsonConverter<bool>
    {
        /// <summary>
        /// توکن JSON را به مقدار بولی تفسیر می‌کند.
        /// </summary>
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    return true;
                case JsonTokenType.False:
                    return false;
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out long integer))
                        return integer != 0;
                    if (reader.TryGetDouble(out double number))
                        return Math.Abs(number) > double.Epsilon;
                    return false;
                case JsonTokenType.String:
                    return ParseBooleanString(reader.GetString());
                case JsonTokenType.Null:
                    return false;
                default:
                    throw new JsonException(
                        $"Cannot convert JSON token '{reader.TokenType}' to a boolean setting.");
            }
        }

        /// <summary>
        /// مقدار بولی را به‌صورت JSON true/false می‌نویسد.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        {
            writer.WriteBooleanValue(value);
        }

        /// <summary>
        /// رشته‌های متداول بولی را با فرهنگ ثابت تفسیر می‌کند.
        /// </summary>
        private static bool ParseBooleanString(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string trimmed = value.Trim();
            if (bool.TryParse(trimmed, out bool parsed))
                return parsed;

            if (trimmed == "1" || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
    }
}
