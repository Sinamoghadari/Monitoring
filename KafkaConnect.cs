using System;
using System.Text.Json;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;

public class KafkaConnect : IDisposable
{
    private readonly IProducer<Null, string> _producer;
    private readonly string _userActivityTopic;
    private readonly string _systemMetricsTopic;

    public KafkaConnect(IConfiguration configuration)
    {
        // اصلاح مسیر خواندن تنظیمات
        var bootstrapServers = configuration["AppSettings:Kafka:BootstrapServers"];
        _userActivityTopic = configuration["AppSettings:Kafka:UserActivityTopic"] ?? "user_activity_topic";
        _systemMetricsTopic = configuration["AppSettings:Kafka:SystemMetricsTopic"] ?? "advanced_system_metrics_topic";

        // بررسی خالی نبودن آدرس سرور برای جلوگیری از خطای مشابه
        if (string.IsNullOrEmpty(bootstrapServers))
        {
            throw new ArgumentNullException(nameof(bootstrapServers), "Kafka BootstrapServers is not configured properly in appsettings.json.");
        }

        var config = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            Acks = Acks.All,
            MessageSendMaxRetries = 3
        };

        _producer = new ProducerBuilder<Null, string>(config).Build();
    }


    // متد برای ارسال دیتای User Activity
    public async Task SendUserActivityAsync(object activityData)
    {
        string jsonMessage = JsonSerializer.Serialize(activityData);
        await SendMessageAsync(_userActivityTopic, jsonMessage);
    }

    // متد برای ارسال دیتای Advanced System Metrics
    public async Task SendSystemMetricsAsync(Dictionary<string, object> metricsData)
    {
        string jsonMessage = JsonSerializer.Serialize(metricsData);
        await SendMessageAsync(_systemMetricsTopic, jsonMessage);
    }

    public async Task SendAppLogAsync(object logData)
    {
        string jsonMessage = JsonSerializer.Serialize(logData);
        // فرض می‌کنیم تاپیک لاگ‌ها app_logs_topic نام دارد
        await SendMessageAsync("app_logs_topic", jsonMessage);
    }


    private async Task SendMessageAsync(string topic, string message)
    {
        try
        {
            var deliveryResult = await _producer.ProduceAsync(topic, new Message<Null, string> { Value = message });
            Console.WriteLine($"Delivered to: {deliveryResult.TopicPartitionOffset}");
        }
        catch (ProduceException<Null, string> e)
        {
            Console.WriteLine($"Delivery failed: {e.Error.Reason}");
        }
    }

    public void Dispose()
    {
        // Flush کردن پیام‌های باقی‌مانده قبل از بسته شدن برنامه
        _producer?.Flush(TimeSpan.FromSeconds(10));
        _producer?.Dispose();
    }
}
