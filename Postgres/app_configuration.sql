CREATE TABLE IF NOT EXISTS app_configuration (
    id SERIAL PRIMARY KEY,
    settings_json JSONB NOT NULL,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);


INSERT INTO app_configuration (settings_json)
VALUES (
'{
  "AllowErgonomyCollection": true,
  "AllowKafkaWrite": true,
  "AllowSqliteWrite": true,
  "NotificationIntervalSeconds": 7,
  "ActivityThresholdSeconds": 60,
  "PrimaryAlarmAutoCloseSeconds": 20,
  "SessionCloseLimit": 3,
  "SecondaryAlarmUnclosableSeconds": 60,
  "SecondaryAlarmAutoCloseSeconds": 10,
  "AdvancedMetricsIntervalMinutes": 5,
  "SyncEngineIntervalMinutes": 1,
  "TopProcessesCount": 10,
  "CheckIntervalMinutes": 60,
  "LoggingIntervalHours": 24,
  "SettingsCheckIntervalSeconds": 120,
  "EnabledMetrics": [
      "WindowsSid", "WindowsUsername", "BootTime", "SystemUptimeSeconds", 
      "FailedLoginAttempts", "AntivirusStatus", "FirewallStatus", "UsbDevicesCount", 
      "MotherboardSerial", "ActiveProcesses", "ActiveThreads", "OpenHandles", 
      "NetworkTrace", "DiskModels", "TopProcesses", "CpuUsagePercent", 
      "LogicalCores", "PhysicalCores", "CpuTemperature", "TotalRamMb", 
      "UsedRamMb", "FreeRamMb", "StorageDetails", "NetworkDetails" , "ComputerName"
    ],
  "NetworkTraceTargetIP": "172.17.214.1",
  "API": {
    "Settings": "https://siscoeye.sirjansteel.com/api/settings",
    "LoadImages": "https://siscoeye.sirjansteel.com/api/images"
  },
  "PermissionSqliteRetryIntervalHours": 1,
  "PermissionKafkaRetryIntervalHours": 1,
  "PermissionPostgresRetryIntervalHours": 1,
  "ConnectionFailureSleepMinutes": 10,
  "VersionCheckerMinute": 4800,
  "Kafka": {
    "BootstrapServers": "172.17.214.38:9094",
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
