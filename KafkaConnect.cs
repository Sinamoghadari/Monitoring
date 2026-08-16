using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;

namespace Ergonomy.Database
{
    /// <summary>
    /// Kafka producer for Ergonomy telemetry.
    ///
    /// All infrastructure configuration is injected through the constructor.
    /// This class has no dependency on IConfiguration, appsettings.json,
    /// Registry, or Environment Variables directly.
    /// </summary>
    public sealed class KafkaConnect : IDisposable
    {
        private readonly IProducer<Null, string> _producer;

        private readonly string _userActivityTopic;
        private readonly string _systemMetricsTopic;
        private readonly string _appLogsTopic;

        private bool _disposed;

        public KafkaConnect(
            string bootstrapServers,
            string? userActivityTopic = null,
            string? systemMetricsTopic = null,
            string? appLogsTopic = null)
        {
            if (string.IsNullOrWhiteSpace(bootstrapServers))
            {
                throw new ArgumentException(
                    "Kafka BootstrapServers is not configured. " +
                    "Set ERGONOMY_KAFKA_BOOTSTRAP_SERVERS at Machine level.",
                    nameof(bootstrapServers));
            }

            _userActivityTopic = RequireTopicName(
                userActivityTopic,
                "ERGONOMY_KAFKA_USER_ACTIVITY_TOPIC");

            _systemMetricsTopic = RequireTopicName(
                systemMetricsTopic,
                "ERGONOMY_KAFKA_SYSTEM_METRICS_TOPIC");

            _appLogsTopic = RequireTopicName(
                appLogsTopic,
                "ERGONOMY_KAFKA_APP_LOGS_TOPIC");

            var config = new ProducerConfig
            {
                BootstrapServers = bootstrapServers.Trim(),

                // Broker acknowledgement from all in-sync replicas.
                Acks = Acks.All,

                // Prevents producer-level duplicates during retry/reconnection.
                EnableIdempotence = true,

                // Delivery timeout for one message.
                MessageTimeoutMs = 30_000,

                // Small batching window; suitable for telemetry workloads.
                LingerMs = 50,

                // Reduces network bandwidth for JSON payloads.
                CompressionType = CompressionType.Gzip,

                // Allows Kafka client diagnostics through Console/Error events if needed.
                LogConnectionClose = false
            };

            _producer = new ProducerBuilder<Null, string>(config)
                .SetErrorHandler((_, error) =>
                {
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] ❌ Kafka client error: " +
                        $"{error.Code} | {error.Reason}");
                })
                .Build();

            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] 🔌 Kafka Producer initialized. " +
                $"BootstrapServers: {bootstrapServers.Trim()} | " +
                $"UserActivityTopic: {_userActivityTopic} | " +
                $"SystemMetricsTopic: {_systemMetricsTopic} | " +
                $"AppLogsTopic: {_appLogsTopic}");
        }

        public Task SendUserActivityAsync(
            object activityData,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(activityData);

            return SendMessageAsync(
                _userActivityTopic,
                JsonSerializer.Serialize(activityData),
                cancellationToken);
        }

        public Task SendSystemMetricsAsync(
            Dictionary<string, object> metricsData,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(metricsData);

            return SendMessageAsync(
                _systemMetricsTopic,
                JsonSerializer.Serialize(metricsData),
                cancellationToken);
        }

        public Task SendAppLogAsync(
            object logData,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(logData);

            return SendMessageAsync(
                _appLogsTopic,
                JsonSerializer.Serialize(logData),
                cancellationToken);
        }

        /// <summary>
        /// On failure, this method logs and rethrows the exception.
        /// The caller, especially SyncEngine, must not mark an SQLite outbox
        /// item as delivered unless this method completes successfully.
        /// </summary>
        private async Task SendMessageAsync(
            string topic,
            string message,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            try
            {
                DeliveryResult<Null, string> result = await _producer.ProduceAsync(
                    topic,
                    new Message<Null, string>
                    {
                        Value = message
                    },
                    cancellationToken);

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] 🚀 Kafka message sent: " +
                    $"{result.TopicPartitionOffset}");
            }
            catch (ProduceException<Null, string> ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ Kafka delivery failed. " +
                    $"Topic: {topic} | Code: {ex.Error.Code} | " +
                    $"Reason: {ex.Error.Reason}");

                throw;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ⚠️ Kafka send was cancelled. " +
                    $"Topic: {topic}");

                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ Unexpected Kafka send error. " +
                    $"Topic: {topic} | Message: {ex.Message}");

                throw;
            }
        }

        private static string RequireTopicName(
            string? topicName,
            string environmentVariableName)
        {
            if (string.IsNullOrWhiteSpace(topicName))
            {
                throw new ArgumentException(
                    $"Kafka topic is not configured. " +
                    $"Set {environmentVariableName} at Machine level.",
                    nameof(topicName));
            }

            return topicName.Trim();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(KafkaConnect));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                _producer.Flush(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ⚠️ Kafka producer flush failed: " +
                    $"{ex.Message}");
            }
            finally
            {
                _producer.Dispose();
            }
        }
    }
}
