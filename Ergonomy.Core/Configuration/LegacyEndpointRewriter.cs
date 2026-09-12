using System;

namespace Ergonomy.Configuration
{
    /// <summary>
    /// Rewrites retired Control API / Kafka addresses that may still be stored in
    /// machine-scope environment variables or an old PostgreSQL JSON row.
    /// Does not touch the Docker-internal broker <c>kafka:9092</c>.
    /// </summary>
    public static class LegacyEndpointRewriter
    {
        public const string LegacyApiOrigin = "http://172.17.214.38:8082";
        public const string CurrentApiOrigin = "https://siscoeye.sirjansteel.com";
        public const string LegacyKafkaBootstrap = "172.17.214.38:9092";
        public const string CurrentKafkaBootstrap = AgentEndpoints.KafkaBootstrap;

        /// <summary>
        /// Maps a Control API URL from the retired HTTP origin onto the HTTPS reverse proxy.
        /// </summary>
        public static string RewriteApiUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url ?? string.Empty;

            return url.Replace(LegacyApiOrigin, CurrentApiOrigin, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Maps the retired agent Kafka listener (:9092) onto the EXTERNAL listener (:9094).
        /// Leaves <c>kafka:9092</c> unchanged.
        /// </summary>
        public static string RewriteKafkaBootstrap(string? bootstrap)
        {
            if (string.IsNullOrWhiteSpace(bootstrap))
                return bootstrap ?? string.Empty;

            return bootstrap.Replace(LegacyKafkaBootstrap, CurrentKafkaBootstrap, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Applies endpoint rewrites in place. Returns true if any field changed.
        /// </summary>
        public static bool Rewrite(AppSettings settings)
        {
            if (settings == null)
                return false;

            bool changed = false;

            if (settings.API != null)
            {
                changed |= RewriteField(settings.API.Settings, RewriteApiUrl, v => settings.API.Settings = v);
                changed |= RewriteField(settings.API.LoadImages, RewriteApiUrl, v => settings.API.LoadImages = v);
                changed |= RewriteField(settings.API.Commands, RewriteApiUrl, v => settings.API.Commands = v);
            }

            if (settings.Kafka != null)
                changed |= RewriteField(settings.Kafka.BootstrapServers, RewriteKafkaBootstrap, v => settings.Kafka.BootstrapServers = v);

            if (settings.Update != null)
                changed |= RewriteField(settings.Update.DownloadUrl, RewriteApiUrl, v => settings.Update.DownloadUrl = v);

            return changed;
        }

        private static bool RewriteField(string? current, Func<string?, string> rewrite, Action<string> assign)
        {
            string next = rewrite(current);
            if (string.Equals(current ?? string.Empty, next, StringComparison.Ordinal))
                return false;
            assign(next);
            return true;
        }
    }
}
