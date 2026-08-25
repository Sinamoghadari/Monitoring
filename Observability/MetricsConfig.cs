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

        /// <summary>
        /// پیکربندی نقطه متریک را با درگاه، محیط و شناسه عامل می‌سازد.
        /// </summary>
        /// <param name="port">درگاه HTTP اسکرپ.</param>
        /// <param name="environment">نام محیط استقرار.</param>
        /// <param name="agentId">شناسه پایدار عامل.</param>
        public MetricsConfig(int port, string environment, string agentId)
        {
            Port = port;
            Environment = environment;
            AgentId = agentId;
        }

        /// <summary>
        /// پیکربندی متریک را یک‌بار از متغیرهای محیطی سطح ماشین می‌خواند.
        /// </summary>
        /// <returns>نمونه پیکربندی آماده برای ترکیب ریشه.</returns>
        public static MetricsConfig FromEnvironment()
        {
            int port = 9100;
            string? portEnv = System.Environment.GetEnvironmentVariable(
                "ERGONOMY_METRICS_PORT", EnvironmentVariableTarget.Machine);
            if (int.TryParse(portEnv, out int parsed) && parsed > 0 && parsed < 65536)
                port = parsed;

            string environment = System.Environment.GetEnvironmentVariable(
                "ERGONOMY_ENVIRONMENT", EnvironmentVariableTarget.Machine)
                ?? "production";

            string agentId = System.Environment.GetEnvironmentVariable(
                "ERGONOMY_AGENT_ID", EnvironmentVariableTarget.Machine)
                ?? System.Environment.MachineName;

            return new MetricsConfig(port, environment, agentId);
        }
    }
}
