namespace Ergonomy.Configuration
{
    /// <summary>
    /// Production Control API and Kafka endpoints used by the Windows agent.
    /// HTTP(S) traffic goes through Nginx at siscoeye.sirjansteel.com.
    /// Kafka is the EXTERNAL listener (raw TCP 9094), not nginx/443 and not the
    /// Docker-internal <c>kafka:9092</c> address.
    /// </summary>
    public static class AgentEndpoints
    {
        public const string ApiSettings = "https://siscoeye.sirjansteel.com/api/settings";
        public const string ApiImages = "https://siscoeye.sirjansteel.com/api/images";
        public const string ApiDomainClients = "https://siscoeye.sirjansteel.com/api/domain-clients";
        public const string ApiUpdatesReport = "https://siscoeye.sirjansteel.com/api/updates/report";
        public const string KafkaBootstrap = "172.17.214.38:9094";
    }
}
