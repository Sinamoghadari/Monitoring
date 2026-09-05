CREATE TABLE IF NOT EXISTS app_configuration (
    id SERIAL PRIMARY KEY,
    settings_json JSONB NOT NULL,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);


INSERT INTO app_configuration (settings_json)
VALUES (
'{
  "NotificationIntervalSeconds": 5,
  "ActivityThresholdSeconds": 5,
  "PrimaryAlarmAutoCloseSeconds": 7,
  "SessionCloseLimit": 3,
  "SecondaryAlarmUnclosableSeconds": 10,
  "SecondaryAlarmAutoCloseSeconds": 7,
  "LoggingIntervalHours": 1,
  "AdvancedMetricsIntervalMinutes": 0.1,
  "SyncEngineIntervalMinutes": 1,
  "TopProcessesCount": 10,
  "EnabledMetrics": [
      "WindowsSid", "WindowsUsername", "BootTime", "SystemUptimeSeconds", 
      "FailedLoginAttempts", "AntivirusStatus", "FirewallStatus", "UsbDevicesCount", 
      "MotherboardSerial", "ActiveProcesses", "ActiveThreads", "OpenHandles", 
      "NetworkTrace", "DiskModels", "TopProcesses", "CpuUsagePercent", 
      "LogicalCores", "PhysicalCores", "CpuTemperature", "TotalRamMb", 
      "UsedRamMb", "FreeRamMb", "StorageDetails", "NetworkDetails" , "ComputerName"
    ],
  "NetworkTraceTargetIP": "172.17.214.1",
  "AllowSqliteWrite": true,
  "AllowKafkaWrite": true,
  "PermissionSqliteRetryIntervalHours": 0.1,
  "PermissionKafkaRetryIntervalHours": 0.1,
  "ConnectionFailureSleepMinutes": 5,
  "HeartbeatIntervalMinutes": 1,
  "VersionCheckerMinute": 60,
  "DirectoryPassword": "Sina_2118908",
  "Kafka": {
    "BootstrapServers": "172.17.214.38:9092",
    "UserActivityTopic": "user_activity",
    "SystemMetricsTopic": "system_metrics",
    "AppLogsTopic": "app_logs"
  },
  "Update": {
    "Enabled": false,
    "LatestVersion": "1.0.0",
    "DownloadUrl": "",
    "Sha256": "",
    "ServiceName": "Ergonomy.Service",
    "CheckIntervalMinutes": 60,
    "MaxJitterSeconds": 900,
    "DownloadRetryCount": 5
  },
  "Images": {
    "PrimaryAlarmImagePath": "Assets/primary_alarm.png",
    "SecondaryAlarmImagePath": "Assets/secondary_alarm.png"
  }
}'
);
