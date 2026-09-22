using Ergonomy.Configuration;
using Xunit;

namespace Ergonomy.Core.Tests
{
    public sealed class LegacyEndpointRewriterTests
    {
        [Fact]
        public void RewriteApiUrl_maps_retired_http_origin_to_https_domain()
        {
            Assert.Equal(
                "https://siscoeye.sirjansteel.com/api/settings",
                LegacyEndpointRewriter.RewriteApiUrl("http://172.17.214.38:8082/api/settings"));
            Assert.Equal(
                "https://siscoeye.sirjansteel.com/api/updates/report",
                LegacyEndpointRewriter.RewriteApiUrl("http://172.17.214.38:8082/api/updates/report"));
        }

        [Fact]
        public void RewriteApiUrl_leaves_current_https_and_empty_unchanged()
        {
            Assert.Equal(
                AgentEndpoints.ApiSettings,
                LegacyEndpointRewriter.RewriteApiUrl(AgentEndpoints.ApiSettings));
            Assert.Equal(string.Empty, LegacyEndpointRewriter.RewriteApiUrl(""));
        }

        [Fact]
        public void RewriteKafkaBootstrap_maps_retired_9092_listener_only()
        {
            Assert.Equal(
                "172.17.214.38:9094",
                LegacyEndpointRewriter.RewriteKafkaBootstrap("172.17.214.38:9092"));
            Assert.Equal(
                "172.17.214.38:9094",
                LegacyEndpointRewriter.RewriteKafkaBootstrap("172.17.214.38:9094"));
            Assert.Equal(
                "kafka:9092",
                LegacyEndpointRewriter.RewriteKafkaBootstrap("kafka:9092"));
        }

        [Fact]
        public void Rewrite_updates_api_kafka_and_update_fields()
        {
            var settings = new AppSettings
            {
                API = new ApiSettings
                {
                    Settings = "http://172.17.214.38:8082/api/settings",
                    LoadImages = "http://172.17.214.38:8082/api/images"
                },
                Kafka = new KafkaSettings { BootstrapServers = "172.17.214.38:9092" },
                Update = new AgentUpdateSettings
                {
                    DownloadUrl = "http://172.17.214.38:8082/api/updates/package_1.0.1.zip"
                }
            };

            Assert.True(LegacyEndpointRewriter.Rewrite(settings));
            Assert.Equal(AgentEndpoints.ApiSettings, settings.API.Settings);
            Assert.Equal(AgentEndpoints.ApiImages, settings.API.LoadImages);
            Assert.Equal(AgentEndpoints.KafkaBootstrap, settings.Kafka.BootstrapServers);
            Assert.Equal(
                "https://siscoeye.sirjansteel.com/api/updates/package_1.0.1.zip",
                settings.Update.DownloadUrl);
            Assert.False(LegacyEndpointRewriter.Rewrite(settings));
        }
    }
}
