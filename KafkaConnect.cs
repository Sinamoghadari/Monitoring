using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Ergonomy.Configuration;
using Ergonomy.Database;

namespace Ergonomy.Database
{
    public sealed class KafkaConnect : IDisposable
    {
        private IProducer<string, string>? _producer;
        private KafkaSettings _settings;
        private readonly object _sync = new();
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
            : this(new KafkaSettings
            {
                BootstrapServers = bootstrapServers,
                UserActivityTopic = userActivityTopic ?? string.Empty,
                SystemMetricsTopic = systemMetricsTopic ?? string.Empty,
                AppLogsTopic = appLogsTopic ?? string.Empty
            })
        {
        }

        /// <summary>
        /// تولیدکننده کافکا را از مدل تنظیمات می‌سازد.
        /// </summary>
        public KafkaConnect(KafkaSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            KafkaSettings normalized = NormalizeOrThrow(settings);
            _settings = normalized;
            _producer = BuildProducer(normalized);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Kafka producer initialized.");
        }

        /// <summary>
        /// تنظیمات فعلی تولیدکننده را به‌صورت رونوشت برمی‌گرداند.
        /// </summary>
        public KafkaSettings CurrentSettings
        {
            get { lock (_sync) return _settings.Clone(); }
        }

        /// <summary>
        /// در صورت تغییر واقعی bootstrap یا تاپیک‌ها، تولیدکننده را با تنظیمات جدید بازسازی می‌کند.
        /// اگر ساخت تولیدکننده جدید شکست بخورد، تولیدکننده قبلی حفظ می‌شود (idempotent و fail-safe).
        /// </summary>
        /// <param name="settings">تنظیمات کافکا از Control API یا بوت‌استرپ.</param>
        /// <returns>اگر تولیدکننده واقعاً جایگزین شد true است.</returns>
        public bool Reconfigure(KafkaSettings? settings)
        {
            if (settings == null)
                return false;

            KafkaSettings normalized;
            try
            {
                normalized = NormalizeOrThrow(settings);
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ⚠️ Kafka reconfigure ignored: {ex.Message}");
                return false;
            }

            lock (_sync)
            {
                ThrowIfDisposed();

                if (_settings.EquivalentTo(normalized))
                    return false;

                IProducer<string, string> next;
                try
                {
                    next = BuildProducer(normalized);
                }
                catch (Exception)
                {
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] ❌ Kafka producer rebuild failed; keeping the existing producer.");
                    return false;
                }

                IProducer<string, string>? previous = _producer;
                _producer = next;
                _settings = normalized;

                DisposeProducer(previous);
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Kafka producer re-initialized.");
            return true;
        }

        /// <summary>
        /// به‌صورت ناهمگام یک رکورد فعالیت کاربر را با کلید پایدار messageId به تاپیک مربوطه در کافکا می‌فرستد.
        /// </summary>
        /// <param name="messageId">کلید پیام کافکا برای حذف تکرار.</param>
        /// <param name="activityData">بار فعالیت کاربر برای سریال‌سازی JSON.</param>
        /// <param name="cancellationToken">توکن لغو ارسال.</param>
        /// <returns>وظیفه‌ای که پس از تحویل به کافکا کامل می‌شود.</returns>
        public async Task SendUserActivityAsync(
            string messageId,
            UserActivityPayload activityData,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(activityData);

            await SendMessageAsync(
                Snapshot().UserActivityTopic,
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
                Snapshot().SystemMetricsTopic,
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
                Snapshot().AppLogsTopic,
                messageId,
                JsonSerializer.Serialize(logData),
                cancellationToken);
        }

        /// <summary>
        /// پیام JSON را با کلید مشخص به تاپیک کافکا تحویل می‌دهد و خطاهای تحویل یا لغو را دوباره پرتاب می‌کند.
        /// </summary>
        private async Task SendMessageAsync(
            string topic,
            string messageId,
            string message,
            CancellationToken cancellationToken)
        {
            IProducer<string, string> producer = SnapshotProducer();

            if (string.IsNullOrWhiteSpace(messageId))
            {
                throw new ArgumentException(
                    "MessageId is required as the Kafka message key.",
                    nameof(messageId));
            }

            try
            {
                await producer.ProduceAsync(
                    topic,
                    new Message<string, string>
                    {
                        Key = messageId,
                        Value = message
                    },
                    cancellationToken);

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Kafka message sent.");
            }
            catch (ObjectDisposedException)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ⚠️ Kafka producer was replaced during send.");
                throw new InvalidOperationException(
                    "Kafka producer was replaced during send.");
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
                    "Kafka send was cancelled.");

                throw;
            }
            catch (Exception)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ❌ Unexpected Kafka send error. " +
                    "Kafka send failed.");

                throw;
            }
        }

        /// <summary>
        /// نام تاپیک را اعتبارسنجی می‌کند و در صورت خالی بودن، نام متغیر محیطی موردنیاز را در استثنا ذکر می‌نماید.
        /// </summary>
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
        /// تنظیمات کافکا را نرمال و اعتبارسنجی می‌کند.
        /// </summary>
        private static KafkaSettings NormalizeOrThrow(KafkaSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.BootstrapServers))
            {
                throw new ArgumentException(
                    "Kafka BootstrapServers is not configured. " +
                    "Set ERGONOMY_KAFKA_BOOTSTRAP_SERVERS at Machine level.",
                    nameof(settings));
            }

            return new KafkaSettings
            {
                BootstrapServers = settings.BootstrapServers.Trim(),
                UserActivityTopic = RequireTopicName(
                    settings.UserActivityTopic, "ERGONOMY_KAFKA_USER_ACTIVITY_TOPIC"),
                SystemMetricsTopic = RequireTopicName(
                    settings.SystemMetricsTopic, "ERGONOMY_KAFKA_SYSTEM_METRICS_TOPIC"),
                AppLogsTopic = RequireTopicName(
                    settings.AppLogsTopic, "ERGONOMY_KAFKA_APP_LOGS_TOPIC")
            };
        }

        /// <summary>
        /// یک تولیدکننده idempotent با فشرده‌سازی Gzip می‌سازد.
        /// </summary>
        private static IProducer<string, string> BuildProducer(KafkaSettings settings)
        {
            var config = new ProducerConfig
            {
                BootstrapServers = settings.BootstrapServers,
                Acks = Acks.All,
                EnableIdempotence = true,
                MessageTimeoutMs = 30_000,
                LingerMs = 50,
                CompressionType = CompressionType.Gzip,
                LogConnectionClose = false
            };

            return new ProducerBuilder<string, string>(config)
                .SetErrorHandler((_, error) =>
                {
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] ❌ Kafka client error: " +
                        $"{error.Code}");
                })
                .Build();
        }

        private KafkaSettings Snapshot()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                return _settings;
            }
        }

        private IProducer<string, string> SnapshotProducer()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                return _producer ?? throw new ObjectDisposedException(nameof(KafkaConnect));
            }
        }

        /// <summary>
        /// اگر تولیدکننده آزاد شده باشد، ObjectDisposedException پرتاب می‌کند.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(KafkaConnect));
        }

        private static void DisposeProducer(IProducer<string, string>? producer)
        {
            if (producer == null)
                return;

            try
            {
                producer.Flush(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] Kafka producer flush failed.");
            }
            finally
            {
                try
                {
                    producer.Dispose();
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// بافر تولیدکننده کافکا را خالی کرده و منابع کلاینت را آزاد می‌کند.
        /// </summary>
        public void Dispose()
        {
            IProducer<string, string>? producer;
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                producer = _producer;
                _producer = null;
            }

            DisposeProducer(producer);
        }
    }
}
