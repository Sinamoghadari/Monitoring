using System.Text.Json;
using Ergonomy.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ergonomy.Core.Tests
{
    public sealed class AppLogNormalizerTests
    {
        [Theory]
        [InlineData("INFORMATION", "INFORMATION")]
        [InlineData("INFO", "INFORMATION")]
        [InlineData("info", "INFORMATION")]
        [InlineData(" Info ", "INFORMATION")]
        [InlineData("DEBUG", "INFORMATION")]
        [InlineData("TRACE", "INFORMATION")]
        [InlineData("WARNING", "WARNING")]
        [InlineData("WARN", "WARNING")]
        [InlineData("warn", "WARNING")]
        [InlineData("ERROR", "ERROR")]
        [InlineData("ERR", "ERROR")]
        [InlineData("CRITICAL", "ERROR")]
        [InlineData("FATAL", "ERROR")]
        [InlineData("CRIT", "ERROR")]
        [InlineData("fatal", "ERROR")]
        public void NormalizeLogLevel_maps_aliases(string input, string expected)
        {
            Assert.Equal(expected, AppLogNormalizer.NormalizeLogLevel(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("VERBOSE")]
        [InlineData("NOTICE")]
        [InlineData("banana")]
        [InlineData("unknown-level")]
        public void NormalizeLogLevel_unknown_falls_back_to_information(string? input)
        {
            Assert.Equal("INFORMATION", AppLogNormalizer.NormalizeLogLevel(input));
        }

        [Fact]
        public void FromMicrosoftLogLevel_maps_critical_to_error()
        {
            Assert.Equal("INFORMATION", AppLogNormalizer.FromMicrosoftLogLevel(LogLevel.Information));
            Assert.Equal("INFORMATION", AppLogNormalizer.FromMicrosoftLogLevel(LogLevel.Debug));
            Assert.Equal("WARNING", AppLogNormalizer.FromMicrosoftLogLevel(LogLevel.Warning));
            Assert.Equal("ERROR", AppLogNormalizer.FromMicrosoftLogLevel(LogLevel.Error));
            Assert.Equal("ERROR", AppLogNormalizer.FromMicrosoftLogLevel(LogLevel.Critical));
        }

        [Theory]
        [InlineData(@"SISCO\s.moghadarii|Elevated=False", @"SISCO\s.moghadarii")]
        [InlineData(@"SISCO\s.moghadarii|Elevated=True", @"SISCO\s.moghadarii")]
        [InlineData(@"SISCO\s.moghadarii|elevated=false", @"SISCO\s.moghadarii")]
        [InlineData(@"DOMAIN\user|Elevated=True", @"DOMAIN\user")]
        [InlineData(@"SISCO\s.moghadarii", @"SISCO\s.moghadarii")]
        [InlineData(@"  SISCO\s.moghadarii|Elevated=False  ", @"SISCO\s.moghadarii")]
        public void NormalizeWindowsUsernameRunAdmin_strips_elevation_suffix(string input, string expected)
        {
            Assert.Equal(expected, AppLogNormalizer.NormalizeWindowsUsernameRunAdmin(input));
        }

        [Fact]
        public void NormalizeWindowsUsernameRunAdmin_does_not_append_elevation()
        {
            string value = AppLogNormalizer.NormalizeWindowsUsernameRunAdmin(@"SISCO\s.moghadarii");
            Assert.False(value.Contains("|Elevated=", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(@"SISCO\s.moghadarii", value);
        }

        [Fact]
        public void NormalizeWindowsUsernameRunAdmin_falls_back_to_username_when_empty()
        {
            Assert.Equal(@"SISCO\s.moghadarii",
                AppLogNormalizer.NormalizeWindowsUsernameRunAdmin(null, @"SISCO\s.moghadarii"));
        }

        [Fact]
        public void NormalizePayloadJson_rewrites_queued_app_logs_before_kafka()
        {
            const string queued = """
                {
                  "CollectedAt": "2026-09-05T07:59:42Z",
                  "LogLevel": "INFO",
                  "Message": "UpdateManager started.",
                  "WindowsUsername": "SISCO\\s.moghadarii",
                  "WindowsUsername_RunAdmin": "SISCO\\s.moghadarii|Elevated=False",
                  "Category": "Update"
                }
                """;

            string normalized = AppLogNormalizer.NormalizePayloadJson(queued);
            using var doc = JsonDocument.Parse(normalized);
            Assert.Equal("INFORMATION", doc.RootElement.GetProperty("LogLevel").GetString());
            Assert.Equal(@"SISCO\s.moghadarii", doc.RootElement.GetProperty("WindowsUsername_RunAdmin").GetString());
            Assert.Equal("Update", doc.RootElement.GetProperty("Category").GetString());
        }

        [Fact]
        public void NormalizeDictionary_rewrites_json_element_payloads()
        {
            var payload = JsonSerializer.Deserialize<Dictionary<string, object>>("""
                {"LogLevel":"FATAL","WindowsUsername_RunAdmin":"SISCO\\s.moghadarii|Elevated=True"}
                """)!;

            AppLogNormalizer.NormalizeDictionary(payload);

            Assert.Equal("ERROR", payload["LogLevel"].ToString());
            Assert.Equal(@"SISCO\s.moghadarii", payload["WindowsUsername_RunAdmin"].ToString());
        }

        [Fact]
        public void AllowedLogLevels_are_exactly_the_three_contract_values()
        {
            Assert.Equal(new[] { "INFORMATION", "WARNING", "ERROR" }, AppLogNormalizer.AllowedLogLevels);
        }
    }
}
