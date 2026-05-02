using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using System.Diagnostics.Eventing.Reader;

public class AdvancedMetricsCollector
{
    private readonly int _topProcessesCount;
    private readonly string _targetIp;
    private readonly HashSet<string> _enabledMetrics;
public AdvancedMetricsCollector(List<string> enabledMetrics, int topProcessesCount = 10, string targetIp = "172.17.214.1")
{
    _topProcessesCount = topProcessesCount;
    _targetIp = targetIp;

    // اگر لیست خالی بود، یک لیست خالی می‌سازیم
    if (enabledMetrics == null || !enabledMetrics.Any())
    {
        _enabledMetrics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return;
    }

    // نرمال‌سازی نام‌ها: حذف "_" و "-" و فاصله‌ها برای تطبیق دقیق
    var normalizedMetrics = enabledMetrics
        .Where(m => !string.IsNullOrWhiteSpace(m))
        .Select(m => m.Replace("_", "").Replace("-", "").Replace(" ", ""))
        .ToList();

    _enabledMetrics = new HashSet<string>(normalizedMetrics, StringComparer.OrdinalIgnoreCase);
}

private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
{
    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
};

public Dictionary<string, object> Collect()
{
    var EnabledMetrics = new Dictionary<string, object>();
    
    // شناسه‌های پایه
    EnabledMetrics["ComputerName"] = Environment.MachineName;

    // ویندوز و سیستم
    if (_enabledMetrics.Contains("WindowsSid")) EnabledMetrics["WindowsSid"] = WindowsIdentity.GetCurrent().User?.Value ?? "Unknown";
    if (_enabledMetrics.Contains("WindowsUsername")) EnabledMetrics["WindowsUsername"] = WindowsIdentity.GetCurrent().Name;
    if (_enabledMetrics.Contains("MotherboardSerial")) EnabledMetrics["MotherboardSerial"] = GetMotherboardSerial();

    // زمان و آپتایم
    if (_enabledMetrics.Contains("BootTime") || _enabledMetrics.Contains("SystemUptimeSeconds"))
    {
        var bootTime = GetBootTime();
        if (_enabledMetrics.Contains("BootTime")) EnabledMetrics["BootTime"] = bootTime.ToString("yyyy-MM-dd HH:mm:ss");
        if (_enabledMetrics.Contains("SystemUptimeSeconds")) EnabledMetrics["SystemUptimeSeconds"] = (long)(DateTime.Now - bootTime).TotalSeconds;
    }

    // امنیت و لاگین
    if (_enabledMetrics.Contains("FailedLoginAttempts")) EnabledMetrics["FailedLoginAttempts"] = GetFailedLoginsCount();
    if (_enabledMetrics.Contains("AntivirusStatus")) EnabledMetrics["AntivirusStatus"] = GetSecurityStatus("AntiVirusProduct");
    if (_enabledMetrics.Contains("FirewallStatus")) EnabledMetrics["FirewallStatus"] = GetSecurityStatus("FirewallProduct");
    
    // پردازش‌ها و هندل‌ها
    if (_enabledMetrics.Contains("ActiveProcesses")) EnabledMetrics["ActiveProcesses"] = Process.GetProcesses().Length;
    if (_enabledMetrics.Contains("ActiveThreads")) EnabledMetrics["ActiveThreads"] = GetTotalThreads();
    if (_enabledMetrics.Contains("OpenHandles")) EnabledMetrics["OpenHandles"] = GetTotalHandles();
    if (_enabledMetrics.Contains("UsbDevicesCount")) EnabledMetrics["UsbDevicesCount"] = GetUsbDevicesCount();

    // JSON های تو در تو
    if (_enabledMetrics.Contains("NetworkTrace")) EnabledMetrics["NetworkTraceJson"] = PerformNetworkTrace();
    if (_enabledMetrics.Contains("DiskModels")) EnabledMetrics["DiskModelsJson"] = GetDiskModels();
    if (_enabledMetrics.Contains("TopProcesses")) EnabledMetrics["TopProcessesJson"] = GetTopProcesses();

    // Reliability 
    if (_enabledMetrics.Contains("DiskHealthStatus")) EnabledMetrics["DiskHealthStatusJson"] = GetDiskHealthStatus();
    if (_enabledMetrics.Contains("CriticalSystemEvents")) EnabledMetrics["CriticalSystemEventsJson"] = GetCriticalSystemEvents();
    
    // داده‌های سخت‌افزاری
    bool needsHardware = _enabledMetrics.Contains("CpuUsagePercent") || 
                         _enabledMetrics.Contains("LogicalCores") || 
                         _enabledMetrics.Contains("PhysicalCores") || 
                         _enabledMetrics.Contains("CpuTemperature") || 
                         _enabledMetrics.Contains("TotalRamMb") || 
                         _enabledMetrics.Contains("UsedRamMb") || 
                         _enabledMetrics.Contains("FreeRamMb") || 
                         _enabledMetrics.Contains("StorageDetails") || 
                         _enabledMetrics.Contains("NetworkDetails");
    
    if (needsHardware)
    {
        CollectHardwareData(EnabledMetrics);
    }
    

    return EnabledMetrics;
}
    // 1. متد جمع آوری وضعیت سلامت دیسک (S.M.A.R.T)
    private string GetDiskHealthStatus()
    {
        var diskHealth = new List<object>();
        try
        {
            // در وضعیت سالم باید مقدار "OK" برگردد. 
            // مقادیر دیگر مانند "Pred Fail" نشان دهنده خرابی قریب الوقوع هستند.
            using (var searcher = new ManagementObjectSearcher("SELECT Model, Status FROM Win32_DiskDrive"))
            {
                foreach (var item in searcher.Get())
                {
                    diskHealth.Add(new {
                        Model = item["Model"]?.ToString()?.Trim(),
                        Status = item["Status"]?.ToString()?.Trim()
                    });
                }
            }
        }
        catch { }
        return JsonSerializer.Serialize(diskHealth, _jsonOptions);
    }

    // 2. متد بررسی رویدادهای بحرانی (Crash و Shutdown ناگهانی در 24 ساعت گذشته)
    private string GetCriticalSystemEvents()
    {
        var eventStats = new Dictionary<string, int> {
            { "unexpected_shutdowns_id41", 0 },
            { "bsod_bugcheck_id1001", 0 }
        };
        
        try
        {
            // کوئری برای Event ID 41 (Kernel-Power) و 1001 (BugCheck) در 24 ساعت گذشته
            string query = "*[System[(EventID=41 or EventID=1001) and TimeCreated[timediff(@SystemTime) <= 86400000]]]";
            
            // بلوک using از اینجا حذف شد
            var logQuery = new EventLogQuery("System", PathType.LogName, query);
            
            using (var logReader = new EventLogReader(logQuery))
            {
                EventRecord ev;
                while ((ev = logReader.ReadEvent()) != null)
                {
                    using (ev)
                    {
                        if (ev.Id == 41) eventStats["unexpected_shutdowns_id41"]++;
                        if (ev.Id == 1001) eventStats["bsod_bugcheck_id1001"]++;
                    }
                }
            }
        }
        catch { }
        return JsonSerializer.Serialize(eventStats, _jsonOptions);
    }

    private void CollectHardwareData(Dictionary<string, object> metrics)
    {
        Computer computer = new Computer
        {
            IsCpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsMotherboardEnabled = true
        };

        computer.Open();
        computer.Accept(new UpdateVisitor());

        var storageDict = new Dictionary<string, object>();
        var networkDict = new Dictionary<string, object>();
        var diskSerials = GetDiskSerialsViaWmi();

        foreach (IHardware hardware in computer.Hardware)
        {
            if (hardware.HardwareType == HardwareType.Cpu)
            {
                foreach (var sensor in hardware.Sensors)
                {
                    if (_enabledMetrics.Contains("CpuUsagePercent") && sensor.SensorType == SensorType.Load && sensor.Name.Contains("Total"))
                        metrics["CpuUsagePercent"] = sensor.Value ?? 0;
                    else if (_enabledMetrics.Contains("CpuTemperature") && sensor.SensorType == SensorType.Temperature && sensor.Name.Contains("Core Average"))
                        metrics["CpuTemperature"] = sensor.Value ?? 0;
                }
                if (_enabledMetrics.Contains("LogicalCores")) metrics["LogicalCores"] = Environment.ProcessorCount;
                if (_enabledMetrics.Contains("PhysicalCores")) metrics["PhysicalCores"] = GetPhysicalCoresViaWmi();
            }

            else if (hardware.HardwareType == HardwareType.Memory)
            {
                float usedRam = 0, freeRam = 0;
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Data && sensor.Name == "Memory Used")
                        usedRam = (sensor.Value ?? 0) * 1024;
                    else if (sensor.SensorType == SensorType.Data && sensor.Name == "Memory Available")
                        freeRam = (sensor.Value ?? 0) * 1024;
                }

                if (_enabledMetrics.Contains("UsedRamMb")) metrics["UsedRamMb"] = usedRam;
                if (_enabledMetrics.Contains("FreeRamMb")) metrics["FreeRamMb"] = freeRam;
                if (_enabledMetrics.Contains("TotalRamMb")) metrics["TotalRamMb"] = usedRam + freeRam;
            }
            else if (hardware.HardwareType == HardwareType.Storage && _enabledMetrics.Contains("StorageDetails"))
            {
                var driveDetails = new Dictionary<string, object>();
                driveDetails["SerialNumber"] = diskSerials.ContainsKey(hardware.Name) ? diskSerials[hardware.Name] : "Unknown";

                foreach (var sensor in hardware.Sensors) driveDetails[sensor.Name] = sensor.Value ?? 0;
                storageDict[hardware.Name] = driveDetails;
            }
            else if (hardware.HardwareType == HardwareType.Network && _enabledMetrics.Contains("NetworkDetails"))
            {
                var netDetails = new Dictionary<string, float>();
                foreach (var sensor in hardware.Sensors) netDetails[sensor.Name] = sensor.Value ?? 0;
                networkDict[hardware.Name] = netDetails;
            }
        }
        
        if (_enabledMetrics.Contains("StorageDetails")) metrics["StorageDetailsJson"] = JsonSerializer.Serialize(storageDict, _jsonOptions);
        if (_enabledMetrics.Contains("NetworkDetails")) metrics["NetworkDetailsJson"] = JsonSerializer.Serialize(networkDict, _jsonOptions);

        computer.Close();
    }

    // --- توابع کمکی ---

    // دریافت لیست برنامه‌های پرمصرف جداگانه برای RAM و CPU
    private string GetTopProcesses()
    {
        try
        {
            var allProcesses = Process.GetProcesses();

            // 1. استخراج پرمصرف‌ترین‌ها از نظر RAM
            var topByRam = allProcesses
                .OrderByDescending(p => p.WorkingSet64)
                .Take(_topProcessesCount)
                .Select(p => new { 
                    Name = p.ProcessName, 
                    RamUsageMB = Math.Round(p.WorkingSet64 / 1048576.0, 2)
                })
                .ToList();

            // 2. استخراج پرمصرف‌ترین‌ها از نظر CPU (Total Processor Time)
            var topByCpu = allProcesses
                .Select(p => 
                {
                    double cpuSeconds = 0;
                    try { cpuSeconds = Math.Round(p.TotalProcessorTime.TotalSeconds, 2); } 
                    catch { } // چشم‌پوشی از پردازش‌های سیستمی محافظت‌شده
                    
                    return new { ProcessName = p.ProcessName, CpuTotalSeconds = cpuSeconds };
                })
                .OrderByDescending(p => p.CpuTotalSeconds)
                .Take(_topProcessesCount)
                .Select(p => new {
                    Name = p.ProcessName,
                    CpuTotalSeconds = p.CpuTotalSeconds
                })
                .ToList();
                
            // ترکیب هر دو در یک آبجکت
            var combinedResult = new 
            {
                TopByRam = topByRam,
                TopByCpu = topByCpu
            };

            return JsonSerializer.Serialize(combinedResult, _jsonOptions);
        }
        catch 
        {
            return "{}";
        }
    }

    // دریافت لیست مدل و برند هارد دیسک‌ها
    private string GetDiskModels()
    {
        var models = new List<string>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_DiskDrive"))
            {
                foreach (var item in searcher.Get())
                {
                    string model = item["Model"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(model))
                        models.Add(model);
                }
            }
        }
        catch { }
        return JsonSerializer.Serialize(models, _jsonOptions);
    }

    private string GetMotherboardSerial()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard"))
            {
                foreach (var item in searcher.Get()) return item["SerialNumber"]?.ToString()?.Trim() ?? "Unknown";
            }
        }
        catch { }
        return "Unknown";
    }

    private Dictionary<string, string> GetDiskSerialsViaWmi()
    {
        var dict = new Dictionary<string, string>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Model, SerialNumber FROM Win32_DiskDrive"))
            {
                foreach (var item in searcher.Get())
                {
                    string model = item["Model"]?.ToString() ?? "";
                    string serial = item["SerialNumber"]?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(model)) dict[model] = serial;
                }
            }
        }
        catch { }
        return dict;
    }

    private string PerformNetworkTrace()
    {
        var hops = new List<object>();
        try
        {
            using (Ping pingSender = new Ping())

            
            {
                PingOptions options = new PingOptions(1, true);
                byte[] buffer = new byte[32]; 
                int timeout = 1000; 

                // ردیابی تا حداکثر 10 گام (Hop)
                for (int ttl = 1; ttl <= 10; ttl++)
                {
                    options.Ttl = ttl;
                    PingReply reply = pingSender.Send(_targetIp, timeout, buffer, options); 
                    
                    hops.Add(new { 
                        Hop = ttl, 
                        IP = reply.Address?.ToString() ?? "*", 
                        TimeMs = reply.RoundtripTime, 
                        Status = reply.Status.ToString() 
                    });

                    if (reply.Status == IPStatus.Success) break;
                }
            }
        }
        catch (Exception ex)
        {
            hops.Add(new { Error = ex.Message });
        }
        return JsonSerializer.Serialize(hops, _jsonOptions);
    }

    private int GetPhysicalCoresViaWmi()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("Select NumberOfCores from Win32_Processor"))
            {
                foreach (var item in searcher.Get()) return Convert.ToInt32(item["NumberOfCores"]);
            }
        }
        catch { }
        return Environment.ProcessorCount / 2;
    }

    private string GetSecurityStatus(string productType)
    {
        try
        {
            string scope = @"root\SecurityCenter2";
            string query = $"SELECT * FROM {productType}";
            using (var searcher = new ManagementObjectSearcher(scope, query))
            {
                foreach (var item in searcher.Get()) return item["displayName"]?.ToString() ?? "Unknown";
            }
        }
        catch { }
        return "Not Found / No Permission";
    }

    // دریافت تعداد لاگین‌های ناموفق در 2 ساعت گذشته
    private int GetFailedLoginsCount()
    {
        try
        {
            int count = 0;
            EventLog log = new EventLog("Security");
            DateTime twoHoursAgo = DateTime.Now.AddHours(-2);
            
            foreach (EventLogEntry entry in log.Entries)
            {
                if (entry.InstanceId == 4625 && entry.TimeGenerated >= twoHoursAgo)
                    count++;
            }
            return count;
        }
        catch { return -1; }
    }

    private int GetUsbDevicesCount()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"Select * From Win32_USBControllerDevice"))
            {
                return searcher.Get().Count;
            }
        }
        catch { return 0; }
    }

    private DateTime GetBootTime()
    {
        try
        {
            long tickCount = Environment.TickCount64; 
            return DateTime.Now - TimeSpan.FromMilliseconds(tickCount);
    }
        catch
    {
        return DateTime.MinValue; // در صورت خطا مقدار پیش‌فرض برگردانید نه زمان حال
    }
    }

    private int GetTotalThreads()
    {
        int threads = 0;
        foreach (var p in Process.GetProcesses()) threads += p.Threads.Count;
        return threads;
    }

    private int GetTotalHandles()
    {
        int handles = 0;
        foreach (var p in Process.GetProcesses()) handles += p.HandleCount;
        return handles;
    }
}

// کلاس کمکی برای پیمایش قطعات سخت‌افزاری در LibreHardwareMonitor
public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) { computer.Traverse(this); }
    public void VisitHardware(IHardware hardware) { hardware.Update(); foreach (IHardware subHardware in hardware.SubHardware) subHardware.Accept(this); }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
