using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;

namespace Ergonomy.Database
{
    public class KafkaConnect : IDisposable
    {
        private readonly IProducer<Null, string> _producer;
        private readonly string _userActivityTopic;
        private readonly string _systemMetricsTopic;

        public KafkaConnect(IConfiguration configuration)
        {
            // خواندن دقیق پارامترها از فایل تنظیمات ثابت (Bootstrap)
            var bootstrapServers = configuration["AppSettings:Kafka:BootstrapServers"];
            
            _userActivityTopic = configuration["AppSettings:Kafka:UserActivityTopic"] ?? "user_activity_topic";
            
            // اصلاح فالبک به تاپیک هدف ClickHouse شما یعنی advanced_system_metrics_topic
            _systemMetricsTopic = configuration["AppSettings:Kafka:SystemMetricsTopic"] ?? "advanced_system_metrics_topic";

            if (string.IsNullOrEmpty(bootstrapServers))
            {
                throw new ArgumentNullException(nameof(bootstrapServers), 
                    "Kafka BootstrapServers is not configured properly in appsettings.json.");
            }

            var config = new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                Acks = Acks.All,
                MessageSendMaxRetries = 3,
                // بهینه‌سازی توان عملیاتی برای سناریوی مانیتورینگ
                LingerMs = 50, 
                CompressionType = CompressionType.Gzip
            };

            _producer = new ProducerBuilder<Null, string>(config).Build();
            
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔌 Kafka Producer initialized. " +
                              $"Target Topic: {_systemMetricsTopic}");
        }

        public async Task SendUserActivityAsync(object activityData)
        {
            string jsonMessage = JsonSerializer.Serialize(activityData);
            await SendMessageAsync(_userActivityTopic, jsonMessage);
        }

        public async Task SendSystemMetricsAsync(Dictionary<string, object> metricsData)
        {
            string jsonMessage = JsonSerializer.Serialize(metricsData);
            await SendMessageAsync(_systemMetricsTopic, jsonMessage);
        }

        public async Task SendAppLogAsync(object logData)
        {
            string jsonMessage = JsonSerializer.Serialize(logData);
            await SendMessageAsync("app_logs_topic", jsonMessage);
        }

        private async Task SendMessageAsync(string topic, string message)
        {
            try
            {
                var deliveryResult = await _producer.ProduceAsync(topic, new Message<Null, string> { Value = message });
                // لاگ در سطح Debug یا Console برای تایید ارسال صحیح
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🚀 Sent to: {deliveryResult.TopicPartitionOffset}");
            }
            catch (ProduceException<Null, string> e)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Kafka delivery failed: {e.Error.Reason}");
            }
        }

        public void Dispose()
        {
            try
            {
                // تضمین ارسال کل بافر موجود در حافظه کلاینت به کارگزار کافکا قبل از خروج
                _producer?.Flush(TimeSpan.FromSeconds(10));
                _producer?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️ Error disposing Kafka producer: {ex.Message}");
            }
        }
    }
}
