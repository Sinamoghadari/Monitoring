using System;

namespace Ergonomy.Observability
{
    /// <summary>
    /// Infrastructure configuration for the internal Prometheus scrape endpoint. Resolved once
    /// from the machine environment at the composition root so services never read environment
    /// variables directly. Infrastructure settings remain authoritative in the provider/composition
    /// root (they are not delegated to the Settings API).
    /// </summary>
    public sealed class MetricsConfig
    {
        public int Port { get; }
        public string Environment { get; }
        public string AgentId { get; }

        public MetricsConfig(int port, string environment, string agentId)
        {
            Port = port;
            Environment = environment;
            AgentId = agentId;
        }

        public static MetricsConfig FromEnvironment()
        {
            int port = 9100;
            string? portEnv = Environment.GetEnvironmentVariable(
                "ERGONOMY_METRICS_PORT", EnvironmentVariableTarget.Machine);
            if (int.TryParse(portEnv, out int parsed) && parsed > 0 && parsed < 65536)
                port = parsed;

            string environment = Environment.GetEnvironmentVariable(
                "ERGONOMY_ENVIRONMENT", EnvironmentVariableTarget.Machine)
                ?? "production";

            string agentId = Environment.GetEnvironmentVariable(
                "ERGONOMY_AGENT_ID", EnvironmentVariableTarget.Machine)
                ?? Environment.MachineName;

            return new MetricsConfig(port, environment, agentId);
        }
    }
}
