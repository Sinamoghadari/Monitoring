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
using System.Globalization;

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

        DateTime currentTime = DateTime.Now;

        EnabledMetrics["CollectedAt"] = currentTime.ToString("yyyy-MM-dd HH:mm:ss");

        PersianCalendar pc = new PersianCalendar();
        EnabledMetrics["CollectedAt_Shamsi"] = $"{pc.GetYear(currentTime):0000}/{pc.GetMonth(currentTime):00}/{pc.GetDayOfMonth(currentTime):00} {currentTime:HH:mm:ss}";
        
        EnabledMetrics["ComputerName"] = Environment.MachineName;

        if (_enabledMetrics.Contains("WindowsSid")) EnabledMetrics["WindowsSid"] = WindowsIdentity.GetCurrent().User?.Value ?? "Unknown";
        if (_enabledMetrics.Contains("WindowsUsername")) EnabledMetrics["WindowsUsername"] = WindowsIdentity.GetCurrent().Name;
        if (_enabledMetrics.Contains("MotherboardSerial")) EnabledMetrics["MotherboardSerial"] = GetMotherboardSerial();

        if (_enabledMetrics.Contains("BootTime") || _enabledMetrics.Contains("SystemUptimeSeconds"))
        {
            var bootTime = GetBootTime();
            if (_enabledMetrics.Contains("BootTime")) EnabledMetrics["BootTime"] = bootTime.ToString("yyyy-MM-dd HH:mm:ss");
            if (_enabledMetrics.Contains("SystemUptimeSeconds")) EnabledMetrics["SystemUptimeSeconds"] = (long)(DateTime.Now - bootTime).TotalSeconds;
        }

        if (_enabledMetrics.Contains("FailedLoginAttempts")) EnabledMetrics["FailedLoginAttempts"] = GetFailedLoginsCount();
        if (_enabledMetrics.Contains("AntivirusStatus")) EnabledMetrics["AntivirusStatus"] = GetSecurityStatus("AntiVirusProduct");
        if (_enabledMetrics.Contains("FirewallStatus")) EnabledMetrics["FirewallStatus"] = GetSecurityStatus("FirewallProduct");
        
        if (_enabledMetrics.Contains("ActiveProcesses")) EnabledMetrics["ActiveProcesses"] = Process.GetProcesses().Length;
        if (_enabledMetrics.Contains("ActiveThreads")) EnabledMetrics["ActiveThreads"] = GetTotalThreads();
        if (_enabledMetrics.Contains("OpenHandles")) EnabledMetrics["OpenHandles"] = GetTotalHandles();
        if (_enabledMetrics.Contains("UsbDevicesCount")) EnabledMetrics["UsbDevicesCount"] = GetUsbDevicesCount();

        if (_enabledMetrics.Contains("NetworkTrace")) EnabledMetrics["NetworkTraceJson"] = PerformNetworkTrace();
        if (_enabledMetrics.Contains("DiskModels")) EnabledMetrics["DiskModelsJson"] = GetDiskModels();
        if (_enabledMetrics.Contains("TopProcesses")) EnabledMetrics["TopProcessesJson"] = GetTopProcesses();
        if (_enabledMetrics.Contains("GpuDetails")) EnabledMetrics["GpuDetailsJson"] = GetGpuDetails();

        if (_enabledMetrics.Contains("DiskHealthStatus")) EnabledMetrics["DiskHealthStatusJson"] = GetDiskHealthStatus();
        if (_enabledMetrics.Contains("CriticalSystemEvents")) EnabledMetrics["CriticalSystemEventsJson"] = GetCriticalSystemEvents();
        
        // شرط آپدیت شده: حذف متریک‌های تکی CPU و جایگزینی با CPUJson
        bool needsHardware = _enabledMetrics.Contains("CPUJson") ||
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
        var cpuDict = new Dictionary<string, object>(); // دیکشنری جدید برای CPU
        
        var diskWmiInfo = GetDiskWmiInfo(); 

        Dictionary<string, object> advancedSmartData = null;
        Dictionary<string, object> diskPerfData = null;

        if (_enabledMetrics.Contains("StorageDetails"))
        {
            advancedSmartData = GetAdvancedSmartData();
            diskPerfData = GetDiskPerformanceMetrics();
        }

        // واکشی اطلاعات پایه پردازنده از WMI
        if (_enabledMetrics.Contains("CPUJson"))
        {
            var cpuWmiDetails = GetCpuWmiDetails();
            foreach (var kvp in cpuWmiDetails)
            {
                cpuDict[kvp.Key] = kvp.Value;
            }
        }

        foreach (IHardware hardware in computer.Hardware)
        {
            if (hardware.HardwareType == HardwareType.Cpu && _enabledMetrics.Contains("CPUJson"))
            {
                cpuDict["CpuModel"] = hardware.Name;
                
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Load && sensor.Name.Contains("Total"))
                        cpuDict["CpuUsagePercent"] = sensor.Value ?? 0;
                    else if (sensor.SensorType == SensorType.Temperature)
                    {
                        if (sensor.Name.Contains("Core Average") || 
                            sensor.Name.Contains("CPU Package") || 
                            sensor.Name.Contains("Tctl") || 
                            sensor.Name.Contains("Tdie"))
                        {
                            cpuDict["CpuTemperature"] = sensor.Value ?? 0;
                        }
                    }
                }
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
                string diskName = hardware.Name;

                if (diskWmiInfo.ContainsKey(diskName))
                {
                    driveDetails["SerialNumber"] = diskWmiInfo[diskName].Serial;
                    driveDetails["CapacityGB"] = diskWmiInfo[diskName].CapacityGb;
                }
                else
                {
                    driveDetails["SerialNumber"] = "Unknown";
                    driveDetails["CapacityGB"] = 0;
                }

                foreach (var sensor in hardware.Sensors) driveDetails[sensor.Name] = sensor.Value ?? 0;
                
                if (advancedSmartData != null)
                {
                    var smartMatch = advancedSmartData.FirstOrDefault(k => k.Key.IndexOf(diskName, StringComparison.OrdinalIgnoreCase) >= 0 || diskName.IndexOf(k.Key, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (smartMatch.Key != null) driveDetails["AdvancedSMART"] = smartMatch.Value;
                }

                storageDict[diskName] = driveDetails;
            }
            else if (hardware.HardwareType == HardwareType.Network && _enabledMetrics.Contains("NetworkDetails"))
            {
                string name = hardware.Name;
                bool isMainAdapter = (name.StartsWith("Ethernet") || name.StartsWith("Wi-Fi") || name.StartsWith("WiFi")) 
                                    && !name.Contains("-QoS", StringComparison.OrdinalIgnoreCase)
                                    && !name.Contains("-WFP", StringComparison.OrdinalIgnoreCase)
                                    && !name.Contains("Filter", StringComparison.OrdinalIgnoreCase)
                                    && !name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                                    && !name.Contains("WSL", StringComparison.OrdinalIgnoreCase)
                                    && !name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                                    && !name.Contains("Kaspersky", StringComparison.OrdinalIgnoreCase);

                if (isMainAdapter)
                {
                    var netDetails = new Dictionary<string, float>();
                    foreach (var sensor in hardware.Sensors) netDetails[sensor.Name] = sensor.Value ?? 0;
                    networkDict[name] = netDetails;
                }
            }
        }
        
        if (_enabledMetrics.Contains("StorageDetails") && diskPerfData != null && diskPerfData.Count > 0)
            storageDict["System_DiskPerformance"] = diskPerfData;

        // ذخیره‌سازی JSON ها
        if (_enabledMetrics.Contains("CPUJson") && cpuDict.Count > 0) metrics["CPUJson"] = JsonSerializer.Serialize(cpuDict, _jsonOptions);
        if (_enabledMetrics.Contains("StorageDetails")) metrics["StorageDetailsJson"] = JsonSerializer.Serialize(storageDict, _jsonOptions);
        if (_enabledMetrics.Contains("NetworkDetails")) metrics["NetworkDetailsJson"] = JsonSerializer.Serialize(networkDict, _jsonOptions);

        computer.Close();
    }
    
    private Dictionary<string, object> GetCpuWmiDetails()
    {
        var cpuWmi = new Dictionary<string, object>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT LoadPercentage, CurrentClockSpeed, MaxClockSpeed, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
            {
                foreach (var item in searcher.Get())
                {
                    cpuWmi["CPUUtilization"] = item["LoadPercentage"] != null ? Convert.ToDouble(item["LoadPercentage"]) : 0;
                    
                    // تبدیل مگاهرتز به گیگاهرتز ($ / 1000.0 $)
                    if (item["CurrentClockSpeed"] != null)
                        cpuWmi["CPUSpeed_GHz"] = Math.Round(Convert.ToDouble(item["CurrentClockSpeed"]) / 1000.0, 2);
                    
                    if (item["MaxClockSpeed"] != null)
                        cpuWmi["CPUBaseSpeed_GHz"] = Math.Round(Convert.ToDouble(item["MaxClockSpeed"]) / 1000.0, 2);

                    cpuWmi["CPUCores"] = item["NumberOfCores"] != null ? Convert.ToInt32(item["NumberOfCores"]) : 0;
                    cpuWmi["CPULogicalCores"] = item["NumberOfLogicalProcessors"] != null ? Convert.ToInt32(item["NumberOfLogicalProcessors"]) : 0;
                    break;
                }
            }
        }
        catch { }
        return cpuWmi;
    }

    private Dictionary<string, object> GetDiskPerformanceMetrics()
    {
        var dict = new Dictionary<string, object>();
        try
        {
            // استفاده از کلمه کلیدی using برای آزادسازی منابع
            using (var diskTime = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total"))
            using (var queueLength = new PerformanceCounter("PhysicalDisk", "Avg. Disk Queue Length", "_Total"))
            using (var diskLatency = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Transfer", "_Total"))
            {
                // فراخوانی اولیه برای مقداردهی
                diskTime.NextValue();
                queueLength.NextValue();
                diskLatency.NextValue();

                // وقفه کوتاه $100$ میلی‌ثانیه‌ای برای محاسبه نرخ تغییرات توسط ویندوز
                System.Threading.Thread.Sleep(100);

                dict["PercentDiskTime"] = diskTime.NextValue();
                dict["AvgQueueLength"] = queueLength.NextValue();
                // تبدیل ثانیه به میلی‌ثانیه با ضرب در $1000$
                dict["AvgDiskLatencyMs"] = diskLatency.NextValue() * 1000; 
            }
        }
        catch (System.Exception ex)
        {
            dict["Error"] = "Performance Counter Error: " + ex.Message;
        }

        return dict;
    }

    private Dictionary<string, object> GetAdvancedSmartData()
    {
        var dict = new Dictionary<string, object>();
        try
        {
            // نیازمند دسترسی ادمین
            using (var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM MSStorageDriver_FailurePredictData"))
            {
                foreach (ManagementObject queryObj in searcher.Get())
                {
                    string instanceName = queryObj["InstanceName"].ToString();
                    byte[] vendorSpecific = (byte[])queryObj["VendorSpecific"];
                    var smartData = new Dictionary<string, object>();

                    // دیتای SMART در آرایه بایتی از ایندکس $2$ شروع می‌شود و هر رکورد $12$ بایت است
                    for (int i = 2; i < 362; i += 12)
                    {
                        if (i + 11 < vendorSpecific.Length)
                        {
                            byte attributeId = vendorSpecific[i];

                            // بررسی شناسه‌های حیاتی: 0x05, 0xC5, 0xC6
                            if (attributeId == 5 || attributeId == 197 || attributeId == 198)
                            {
                                // استخراج مقدار خام (معمولاً بایت‌های $5$ تا $8$ شامل دیتای اصلی هستند)
                                int rawValue = vendorSpecific[i + 5] | 
                                            (vendorSpecific[i + 6] << 8) | 
                                            (vendorSpecific[i + 7] << 16) | 
                                            (vendorSpecific[i + 8] << 24);

                                if (attributeId == 5)
                                    smartData["ReallocatedSectorsCount"] = rawValue;
                                else if (attributeId == 197)
                                    smartData["CurrentPendingSectorCount"] = rawValue;
                                else if (attributeId == 198)
                                    smartData["UncorrectableSectorCount"] = rawValue;
                            }
                        }
                    }
                    
                    // در صورت وجود داده، آن را با کلید نام اینستنس دیسک ذخیره می‌کنیم
                    if (smartData.Count > 0)
                    {
                        dict[instanceName] = smartData;
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            dict["Error"] = "WMI SMART Error (Run as Admin): " + ex.Message;
        }

        return dict;
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
            // 1. تلاش برای دریافت از BaseBoard
            using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard"))
            {
                foreach (var item in searcher.Get())
                {
                    string serial = item["SerialNumber"]?.ToString()?.Trim();
                    if (IsValidSerial(serial)) return serial;
                }
            }

            // 2. تلاش برای دریافت از BIOS در صورت شکست مرحله اول
            using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS"))
            {
                foreach (var item in searcher.Get())
                {
                    string serial = item["SerialNumber"]?.ToString()?.Trim();
                    if (IsValidSerial(serial)) return serial;
                }
            }
        }
        catch { }
        
        return "Unknown";
    }

    // متد کمکی برای بررسی معتبر بودن سریال
    private bool IsValidSerial(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return false;
        
        string lower = serial.ToLower();
        if (lower.Contains("default string") || lower.Contains("o.e.m")) return false;
        
        return true;
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
        return DateTime.MinValue; 
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

        // جایگزین متد GetDiskSerialsViaWmi جهت دریافت سریال و ظرفیت دیسک
    private Dictionary<string, (string Serial, double CapacityGb)> GetDiskWmiInfo()
    {
        var dict = new Dictionary<string, (string, double)>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Model, SerialNumber, Size FROM Win32_DiskDrive"))
            {
                foreach (var item in searcher.Get())
                {
                    string model = item["Model"]?.ToString() ?? "";
                    string serial = item["SerialNumber"]?.ToString()?.Trim() ?? "";
                    
                    double capacityGb = 0;
                    if (ulong.TryParse(item["Size"]?.ToString(), out ulong sizeBytes))
                    {
                        // تبدیل بایت به گیگابایت
                        capacityGb = Math.Round(sizeBytes / 1073741824.0, 2); 
                    }

                    if (!string.IsNullOrEmpty(model)) dict[model] = (serial, capacityGb);
                }
            }
        }
        catch { }
        return dict;
    }

    // متد جدید برای دریافت اطلاعات کارت گرافیک (GPU)
    private string GetGpuDetails()
    {
        var gpus = new List<object>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController"))
            {
                foreach (var item in searcher.Get())
                {
                    string name = item["Name"]?.ToString()?.Trim() ?? "Unknown GPU";
                    
                    double ramMb = 0;
                    if (long.TryParse(item["AdapterRAM"]?.ToString(), out long ramBytes))
                    {
                        // تبدیل بایت به مگابایت
                        ramMb = Math.Round(ramBytes / 1048576.0, 2); 
                    }

                    gpus.Add(new {
                        GpuModel = name,
                        GpuMemoryMb = ramMb
                    });
                }
            }
        }
        catch { }
        return JsonSerializer.Serialize(gpus, _jsonOptions);
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
