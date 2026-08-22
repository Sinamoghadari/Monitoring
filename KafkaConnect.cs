using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Ergonomy.Database;

namespace Ergonomy.Database
{
    public sealed class KafkaConnect : IDisposable
    {
        private readonly IProducer<string, string> _producer;

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
                userActivityTopic, "ERGONOMY_KAFKA_USER_ACTIVITY_TOPIC");

            _systemMetricsTopic = RequireTopicName(
                systemMetricsTopic, "ERGONOMY_KAFKA_SYSTEM_METRICS_TOPIC");

            _appLogsTopic = RequireTopicName(
                appLogsTopic, "ERGONOMY_KAFKA_APP_LOGS_TOPIC");

            var config = new ProducerConfig
            {
                BootstrapServers = bootstrapServers.Trim(),
                Acks = Acks.All,
                EnableIdempotence = true,
                MessageTimeoutMs = 30_000,
                LingerMs = 50,
                CompressionType = CompressionType.Gzip,
                LogConnectionClose = false
            };

            _producer = new ProducerBuilder<string, string>(config)
                .SetErrorHandler((_, error) =>
                {
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] ❌ Kafka client error: " +
                        $"{error.Code}");
                })
                .Build();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Kafka producer initialized.");
        }

        // در KafkaConnect.cs
        public async Task SendUserActivityAsync(
            string messageId,
            UserActivityPayload activityData, 
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(activityData);

            // اکنون Serialize دقیقاً بر اساس کلاس UserActivityPayload انجام می‌شود
            await SendMessageAsync(
                _userActivityTopic,
                messageId,
                JsonSerializer.Serialize(activityData),
                cancellationToken);
        }


        public Task SendSystemMetricsAsync(
            string messageId,
            Dictionary<string, object> metricsData,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(metricsData);

            return SendMessageAsync(
                _systemMetricsTopic,
                messageId,
                JsonSerializer.Serialize(metricsData),
                cancellationToken);
        }

        public Task SendAppLogAsync(
            string messageId,
            object logData,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(logData);

            return SendMessageAsync(
                _appLogsTopic,
                messageId,
                JsonSerializer.Serialize(logData),
                cancellationToken);
        }

        private async Task SendMessageAsync(
            string topic,
            string messageId,
            string message,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(messageId))
            {
                throw new ArgumentException(
                    "MessageId is required as the Kafka message key.",
                    nameof(messageId));
            }

            try
            {
                DeliveryResult<string, string> result = await _producer.ProduceAsync(
                    topic,
                    new Message<string, string>
                    {
                        Key = messageId,
                        Value = message
                    },
                    cancellationToken);

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Kafka message sent.");
            }
            catch (ProduceException<string, string> ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ Kafka delivery failed. " +
                    $"Kafka delivery failure. Code: {ex.Error.Code}");

                throw;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ⚠️ Kafka send was cancelled. " +
                    $"Kafka send was cancelled.");

                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ Unexpected Kafka send error. " +
                    $"Kafka send failed.");

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
                    $"[{DateTime.Now:HH:mm:ss}] Kafka producer flush failed.");
            }
            finally
            {
                _producer.Dispose();
            }
        }
    }
}
