using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using Microsoft.Data.Sqlite;

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
        
        // --- تغییرات مربوط به WindowsUsername و WindowsUsername_RunAdmin ---
         EnabledMetrics["WindowsUsername_RunAdmin"] = WindowsIdentity.GetCurrent().Name ?? "";

        if (_enabledMetrics.Contains("WindowsUsername")) 
        {
            // ابتدا تلاش برای یافتن کاربر explorer.exe، سپس WMI، و در نهایت بازگشت به کاربر فعلی پراسس
            EnabledMetrics["WindowsUsername"] = GetExplorerUser() ?? GetInteractiveWindowsUsername() ?? WindowsIdentity.GetCurrent().Name;
        }
        else 
        {
            // اگر در تنظیمات فعال نبود هم کلید آن را با مقدار خالی می‌فرستیم تا ساختار حفظ شود
            EnabledMetrics["WindowsUsername"] = "";
        }
        // ----------------------------------------------------------------------

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
        
        // فراخوانی تاریخچه کروم
        //TO DO بعدا اضافه میکنم
        // if (_enabledMetrics.Contains("ChromeHistory"))  EnabledMetrics["ChromeHistoryJson"] = GetAllBrowsersHistoryLast24Hours();

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

    // --- توابع جدید برای استخراج کاربر واقعی تعاملی ---
    private string GetInteractiveWindowsUsername()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    var username = obj["UserName"]?.ToString();
                    if (!string.IsNullOrEmpty(username)) return username;
                }
            }
        }
        catch { }
        return null;
    }

    private string GetExplorerUser()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Process WHERE Name='explorer.exe'"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    string[] argList = new string[] { string.Empty, string.Empty };
                    int returnVal = Convert.ToInt32(obj.InvokeMethod("GetOwner", argList));
                    if (returnVal == 0)
                    {
                        return $"{argList[1]}\\{argList[0]}";
                    }
                }
            }
        }
        catch { }
        return null;
    }
    // ---------------------------------------------------

    // 1. متد جمع آوری وضعیت سلامت دیسک (S.M.A.R.T)
    private string GetDiskHealthStatus()
    {
        var diskHealth = new List<object>();
        try
        {
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
            string query = "*[System[(EventID=41 or EventID=1001) and TimeCreated[timediff(@SystemTime) <= 86400000]]]";
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
        var cpuDict = new Dictionary<string, object>(); 
        
        var diskWmiInfo = GetDiskWmiInfo(); 

        Dictionary<string, object> advancedSmartData = null;
        Dictionary<string, object> diskPerfData = null;

        if (_enabledMetrics.Contains("StorageDetails"))
        {
            advancedSmartData = GetAdvancedSmartData();
            diskPerfData = GetDiskPerformanceMetrics();
        }

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
            using (var diskTime = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total"))
            using (var queueLength = new PerformanceCounter("PhysicalDisk", "Avg. Disk Queue Length", "_Total"))
            using (var diskLatency = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Transfer", "_Total"))
            {
                diskTime.NextValue();
                queueLength.NextValue();
                diskLatency.NextValue();

                System.Threading.Thread.Sleep(100);

                dict["PercentDiskTime"] = diskTime.NextValue();
                dict["AvgQueueLength"] = queueLength.NextValue();
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
            using (var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM MSStorageDriver_FailurePredictData"))
            {
                foreach (ManagementObject queryObj in searcher.Get())
                {
                    string instanceName = queryObj["InstanceName"].ToString();
                    byte[] vendorSpecific = (byte[])queryObj["VendorSpecific"];
                    var smartData = new Dictionary<string, object>();

                    for (int i = 2; i < 362; i += 12)
                    {
                        if (i + 11 < vendorSpecific.Length)
                        {
                            byte attributeId = vendorSpecific[i];

                            if (attributeId == 5 || attributeId == 197 || attributeId == 198)
                            {
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

    private string GetTopProcesses()
    {
        try
        {
            var allProcesses = Process.GetProcesses();

            var topByRam = allProcesses
                .OrderByDescending(p => p.WorkingSet64)
                .Take(_topProcessesCount)
                .Select(p => new { 
                    Name = p.ProcessName, 
                    RamUsageMB = Math.Round(p.WorkingSet64 / 1048576.0, 2)
                })
                .ToList();

            var topByCpu = allProcesses
                .Select(p => 
                {
                    double cpuSeconds = 0;
                    try { cpuSeconds = Math.Round(p.TotalProcessorTime.TotalSeconds, 2); } 
                    catch { }
                    
                    return new { ProcessName = p.ProcessName, CpuTotalSeconds = cpuSeconds };
                })
                .OrderByDescending(p => p.CpuTotalSeconds)
                .Take(_topProcessesCount)
                .Select(p => new {
                    Name = p.ProcessName,
                    CpuTotalSeconds = p.CpuTotalSeconds
                })
                .ToList();
                
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
                foreach (var item in searcher.Get())
                {
                    string serial = item["SerialNumber"]?.ToString()?.Trim();
                    if (IsValidSerial(serial)) return serial;
                }
            }

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

    private bool IsValidSerial(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return false;
        
        string lower = serial.ToLower();
        if (lower.Contains("default string") || lower.Contains("o.e.m")) return false;
        
        return true;
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
                        capacityGb = Math.Round(sizeBytes / 1073741824.0, 2); 
                    }

                    if (!string.IsNullOrEmpty(model)) dict[model] = (serial, capacityGb);
                }
            }
        }
        catch { }
        return dict;
    }

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

    private string GetAllBrowsersHistoryLast24Hours()
    {
        var historyList = new List<object>();

        try
        {
            var userProfiles = GetWindowsUserProfiles();

            foreach (var userProfile in userProfiles)
            {
                CollectChromiumHistory(
                    Path.Combine(userProfile, @"AppData\Local\Google\Chrome\User Data"),
                    "Chrome",
                    historyList
                );

                CollectChromiumHistory(
                    Path.Combine(userProfile, @"AppData\Local\Microsoft\Edge\User Data"),
                    "Edge",
                    historyList
                );

                CollectFirefoxHistory(
                    Path.Combine(userProfile, @"AppData\Roaming\Mozilla\Firefox\Profiles"),
                    "Firefox",
                    historyList
                );
            }
        }
        catch
        {
        }

        return JsonSerializer.Serialize(historyList, _jsonOptions);
    }


    private List<string> GetWindowsUserProfiles()
    {
        var result = new List<string>();

        try
        {
            string usersRoot = @"C:\Users";

            if (!Directory.Exists(usersRoot))
                return result;

            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Public",
                "Default",
                "Default User",
                "All Users"
            };

            foreach (var dir in Directory.GetDirectories(usersRoot))
            {
                string name = Path.GetFileName(dir);

                if (!ignored.Contains(name))
                {
                    result.Add(dir);
                }
            }
        }
        catch
        {
        }

        return result;
    }
    
    private void CollectChromiumHistory(
    string browserUserDataPath,
    string browserName,
    List<object> historyList)
    {
        try
        {
            if (!Directory.Exists(browserUserDataPath))
                return;

            var profileDirs = new List<string>();

            profileDirs.AddRange(
                Directory.GetDirectories(browserUserDataPath, "Default")
            );

            profileDirs.AddRange(
                Directory.GetDirectories(browserUserDataPath, "Profile *")
            );

            foreach (var profileDir in profileDirs)
            {
                string historyFile = Path.Combine(profileDir, "History");

                if (!File.Exists(historyFile))
                    continue;

                string tempDir = Path.Combine(
                    Path.GetTempPath(),
                    Guid.NewGuid().ToString()
                );

                Directory.CreateDirectory(tempDir);

                try
                {
                    string tempHistory = Path.Combine(tempDir, "History");

                    CopyIfExists(historyFile, tempHistory);
                    CopyIfExists(historyFile + "-wal", tempHistory + "-wal");
                    CopyIfExists(historyFile + "-shm", tempHistory + "-shm");

                    using (var connection = new SqliteConnection(
                        $"Data Source={tempHistory};Mode=ReadOnly;"))
                    {
                        connection.Open();

                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = @"
                        SELECT url, title
                        FROM urls
                        WHERE datetime(
                            last_visit_time / 1000000 - 11644473600,
                            'unixepoch',
                            'localtime'
                        ) >= datetime('now', '-1 day')
                        ORDER BY last_visit_time DESC";

                            using (var reader = command.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    historyList.Add(new
                                    {
                                        Browser = browserName,
                                        Url = reader.GetString(0),
                                        Title = !reader.IsDBNull(1)
                                            ? reader.GetString(1)
                                            : ""
                                    });
                                }
                            }
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }
    }

    private void CollectFirefoxHistory(
        string firefoxProfilesPath,
        string browserName,
        List<object> historyList)
    {
        try
        {
            if (!Directory.Exists(firefoxProfilesPath))
                return;

            foreach (var profileDir in Directory.GetDirectories(firefoxProfilesPath))
            {
                string placesFile = Path.Combine(profileDir, "places.sqlite");

                if (!File.Exists(placesFile))
                    continue;

                string tempDir = Path.Combine(
                    Path.GetTempPath(),
                    Guid.NewGuid().ToString()
                );

                Directory.CreateDirectory(tempDir);

                try
                {
                    string tempDb = Path.Combine(tempDir, "places.sqlite");

                    CopyIfExists(placesFile, tempDb);
                    CopyIfExists(placesFile + "-wal", tempDb + "-wal");
                    CopyIfExists(placesFile + "-shm", tempDb + "-shm");

                    using (var connection = new SqliteConnection(
                        $"Data Source={tempDb};Mode=ReadOnly;"))
                    {
                        connection.Open();

                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = @"
                        SELECT p.url, p.title
                        FROM moz_places p
                        JOIN moz_historyvisits h
                        ON h.place_id = p.id
                        WHERE datetime(
                            h.visit_date / 1000000,
                            'unixepoch',
                            'localtime'
                        ) >= datetime('now', '-1 day')
                        ORDER BY h.visit_date DESC";

                            using (var reader = command.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    historyList.Add(new
                                    {
                                        Browser = browserName,
                                        Url = reader.GetString(0),
                                        Title = !reader.IsDBNull(1)
                                            ? reader.GetString(1)
                                            : ""
                                    });
                                }
                            }
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }
    }
    

    private void CopyIfExists(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
            {
                File.Copy(source, destination, true);
            }
        }
        catch
        {
        }
    }



}

public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) { computer.Traverse(this); }
    public void VisitHardware(IHardware hardware) { hardware.Update(); foreach (IHardware subHardware in hardware.SubHardware) subHardware.Accept(this); }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
