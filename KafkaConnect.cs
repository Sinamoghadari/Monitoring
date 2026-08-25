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

        /// <summary>
        /// تولیدکننده کافکا را با تأیید همه replicaها، ارسال idempotent و فشرده‌سازی Gzip می‌سازد.
        /// </summary>
        /// <param name="bootstrapServers">فهرست سرورهای بوت‌استرپ کافکا.</param>
        /// <param name="userActivityTopic">نام تاپیک فعالیت کاربر.</param>
        /// <param name="systemMetricsTopic">نام تاپیک متریک سیستم.</param>
        /// <param name="appLogsTopic">نام تاپیک لاگ برنامه.</param>
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

        /// <summary>
        /// به‌صورت ناهمگام یک رکورد فعالیت کاربر را با کلید پایدار messageId به تاپیک مربوطه در کافکا می‌فرستد.
        /// </summary>
        /// <param name="messageId">کلید پیام کافکا برای حذف تکرار.</param>
        /// <param name="activityData">بار فعالیت کاربر برای سریال‌سازی JSON.</param>
        /// <param name="cancellationToken">توکن لغو ارسال.</param>
        /// <returns>وظیفه‌ای که پس از تحویل به کافکا کامل می‌شود.</returns>
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


        /// <summary>
        /// به‌صورت ناهمگام متریک‌های پیشرفته سیستم را به تاپیک system_metrics در کافکا ارسال می‌کند.
        /// </summary>
        /// <param name="messageId">کلید پیام کافکا.</param>
        /// <param name="metricsData">دیکشنری متریک‌های جمع‌آوری‌شده.</param>
        /// <param name="cancellationToken">توکن لغو ارسال.</param>
        /// <returns>وظیفه ارسال پیام.</returns>
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

        /// <summary>
        /// به‌صورت ناهمگام یک رکورد تشخیصی برنامه را به تاپیک app_logs در کافکا ارسال می‌کند.
        /// </summary>
        /// <param name="messageId">کلید پیام کافکا.</param>
        /// <param name="logData">شیء لاگ برای سریال‌سازی.</param>
        /// <param name="cancellationToken">توکن لغو ارسال.</param>
        /// <returns>وظیفه ارسال پیام.</returns>
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

        /// <summary>
        /// پیام JSON را با کلید مشخص به تاپیک کافکا تحویل می‌دهد و خطاهای تحویل یا لغو را دوباره پرتاب می‌کند.
        /// </summary>
        /// <param name="topic">نام تاپیک مقصد.</param>
        /// <param name="messageId">کلید پیام برای ترتیب و حذف تکرار.</param>
        /// <param name="message">بدنه JSON پیام.</param>
        /// <param name="cancellationToken">توکن لغو عملیات شبکه.</param>
        /// <returns>وظیفه‌ای که پس از تأیید تحویل کامل می‌شود.</returns>
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

        /// <summary>
        /// نام تاپیک را اعتبارسنجی می‌کند و در صورت خالی بودن، نام متغیر محیطی موردنیاز را در استثنا ذکر می‌نماید.
        /// </summary>
        /// <param name="topicName">نام تاپیک پیکربندی‌شده.</param>
        /// <param name="environmentVariableName">نام متغیر محیطی مرتبط برای پیام خطا.</param>
        /// <returns>نام تاپیک پیراسته‌شده.</returns>
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

        /// <summary>
        /// اگر تولیدکننده آزاد شده باشد، ObjectDisposedException پرتاب می‌کند.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(KafkaConnect));
        }

        /// <summary>
        /// بافر تولیدکننده کافکا را خالی کرده و منابع کلاینت را آزاد می‌کند.
        /// </summary>
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
