using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Ergonomy.Networking;

/// <summary>
/// Collects the complete Windows network inventory used by the fail-closed VPN policy.
/// This collector intentionally runs before the presentation filter used by NetworkDetailsJson.
/// </summary>
internal static class WindowsNetworkInventoryCollector
{
    private sealed class AdapterMetadata
    {
        public string Guid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string NetConnectionId { get; set; } = string.Empty;
        public string Manufacturer { get; set; } = string.Empty;
        public string PnpDeviceId { get; set; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;
        public bool PhysicalAdapterKnown { get; set; }
        public bool IsPhysicalAdapter { get; set; }
    }

    private sealed class RasEvidence
    {
        public RemoteAccessConnectionKind Kind { get; set; }
        public HashSet<string> Identifiers { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<NetworkInterfaceSnapshot> Collect(out bool proxyConfigured)
    {
        proxyConfigured = DetectProxyConfiguration();

        if (!OperatingSystem.IsWindows())
            return Array.Empty<NetworkInterfaceSnapshot>();

        Dictionary<string, AdapterMetadata> adapterMetadata = ReadAdapterMetadata();
        (HashSet<int> activeRoutes, HashSet<int> defaultRoutes) = ReadActiveRoutes();
        List<RasEvidence> rasEvidence = ReadRasEvidence();
        var snapshots = new List<NetworkInterfaceSnapshot>();

        try
        {
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    IPInterfaceProperties properties = networkInterface.GetIPProperties();
                    int ipv4Index = TryGetInterfaceIndex(properties);
                    AdapterMetadata metadata = FindAdapterMetadata(networkInterface, adapterMetadata);

                    bool hasUsableAddress = properties.UnicastAddresses.Any(
                        address => IsUsableAddress(address.Address));
                    bool hasGateway = properties.GatewayAddresses.Any(
                        gateway => IsUsableAddress(gateway.Address));
                    bool hasActiveRoute = ipv4Index > 0 && activeRoutes.Contains(ipv4Index);
                    bool hasDefaultRoute = ipv4Index > 0 && defaultRoutes.Contains(ipv4Index);

                    RemoteAccessConnectionKind rasKind = MatchRasEvidence(
                        networkInterface,
                        metadata,
                        rasEvidence);

                    snapshots.Add(new NetworkInterfaceSnapshot
                    {
                        Name = networkInterface.Name ?? string.Empty,
                        Description = string.IsNullOrWhiteSpace(metadata.Description)
                            ? networkInterface.Description ?? string.Empty
                            : metadata.Description,
                        NetConnectionId = metadata.NetConnectionId,
                        Manufacturer = metadata.Manufacturer,
                        PnpDeviceId = metadata.PnpDeviceId,
                        ServiceName = metadata.ServiceName,
                        InterfaceType = networkInterface.NetworkInterfaceType,
                        OperationalStatus = networkInterface.OperationalStatus,
                        PhysicalAdapterKnown = metadata.PhysicalAdapterKnown,
                        IsPhysicalAdapter = metadata.IsPhysicalAdapter,
                        HasUsableAddress = hasUsableAddress,
                        HasActiveRoute = hasActiveRoute,
                        HasGateway = hasGateway,
                        HasDefaultRoute = hasDefaultRoute,
                        RemoteAccessConnection = rasKind
                    });
                }
                catch
                {
                    // One malformed or inaccessible adapter must not prevent the rest of the
                    // inventory from being evaluated. Missing evidence remains fail-closed.
                }
            }
        }
        catch
        {
            return Array.Empty<NetworkInterfaceSnapshot>();
        }

        return snapshots;
    }

    private static Dictionary<string, AdapterMetadata> ReadAdapterMetadata()
    {
        var result = new Dictionary<string, AdapterMetadata>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT GUID, Name, Description, NetConnectionID, Manufacturer, " +
                "PNPDeviceID, ServiceName, PhysicalAdapter FROM Win32_NetworkAdapter");

            foreach (ManagementObject item in searcher.Get())
            {
                var metadata = new AdapterMetadata
                {
                    Guid = ReadString(item["GUID"]),
                    Name = ReadString(item["Name"]),
                    Description = ReadString(item["Description"]),
                    NetConnectionId = ReadString(item["NetConnectionID"]),
                    Manufacturer = ReadString(item["Manufacturer"]),
                    PnpDeviceId = ReadString(item["PNPDeviceID"]),
                    ServiceName = ReadString(item["ServiceName"])
                };

                object? physical = item["PhysicalAdapter"];
                if (physical != null && physical != DBNull.Value
                    && bool.TryParse(Convert.ToString(physical, CultureInfo.InvariantCulture), out bool isPhysical))
                {
                    metadata.PhysicalAdapterKnown = true;
                    metadata.IsPhysicalAdapter = isPhysical;
                }

                string key = NormalizeIdentifier(metadata.Guid);
                if (!string.IsNullOrWhiteSpace(key))
                    result[key] = metadata;
            }
        }
        catch
        {
            // WMI is supporting evidence only. If it is unavailable, provider-signature matches
            // cannot pass because PhysicalAdapterKnown remains false.
        }

        return result;
    }

    private static AdapterMetadata FindAdapterMetadata(
        NetworkInterface networkInterface,
        Dictionary<string, AdapterMetadata> metadata)
    {
        string id = NormalizeIdentifier(networkInterface.Id);
        if (!string.IsNullOrWhiteSpace(id) && metadata.TryGetValue(id, out AdapterMetadata exact))
            return exact;

        // GUIDs are normally available, but a fallback match keeps the collector useful on
        // systems where the WMI GUID field is missing. It is not used to make a positive VPN
        // decision unless the physical-adapter field was also obtained from WMI.
        foreach (AdapterMetadata candidate in metadata.Values)
        {
            if (EqualsIgnoreCase(candidate.Name, networkInterface.Name)
                || EqualsIgnoreCase(candidate.Description, networkInterface.Description))
            {
                return candidate;
            }
        }

        return new AdapterMetadata
        {
            Description = networkInterface.Description ?? string.Empty
        };
    }

    private static (HashSet<int> Active, HashSet<int> Default) ReadActiveRoutes()
    {
        var active = new HashSet<int>();
        var defaults = new HashSet<int>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT InterfaceIndex, Destination, Mask FROM Win32_IP4RouteTable");
            foreach (ManagementObject item in searcher.Get())
            {
                if (!TryReadInt(item["InterfaceIndex"], out int index) || index <= 0)
                    continue;

                active.Add(index);
                string destination = ReadString(item["Destination"]);
                string mask = ReadString(item["Mask"]);
                if (destination == "0.0.0.0" && (string.IsNullOrWhiteSpace(mask) || mask == "0.0.0.0"))
                    defaults.Add(index);
            }
        }
        catch
        {
            // No route evidence means no VPN positive. Do not infer routes from adapter names.
        }

        // IPv6 route data is optional because Win32_IP6RouteTable is absent on some Windows
        // versions. A successful IPv4 route inventory is still retained.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT InterfaceIndex, Destination FROM Win32_IP6RouteTable");
            foreach (ManagementObject item in searcher.Get())
            {
                if (!TryReadInt(item["InterfaceIndex"], out int index) || index <= 0)
                    continue;

                active.Add(index);
                string destination = ReadString(item["Destination"]);
                if (string.IsNullOrWhiteSpace(destination) || destination == "::")
                    defaults.Add(index);
            }
        }
        catch
        {
        }

        return (active, defaults);
    }

    private static List<RasEvidence> ReadRasEvidence()
    {
        var result = new List<RasEvidence>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_RASConnection");

            foreach (ManagementObject item in searcher.Get())
            {
                string deviceType = ReadString(item["DeviceType"]);
                var evidence = new RasEvidence
                {
                    Kind = IsExplicitVpnRasType(deviceType)
                        ? RemoteAccessConnectionKind.ExplicitVpn
                        : RemoteAccessConnectionKind.NonVpnOrUnknown
                };

                AddIdentifier(evidence.Identifiers, item["DeviceName"]);
                AddIdentifier(evidence.Identifiers, item["EntryName"]);
                AddIdentifier(evidence.Identifiers, item["ConnectionID"]);
                AddIdentifier(evidence.Identifiers, item["Name"]);
                AddIdentifier(evidence.Identifiers, item["ServiceName"]);
                result.Add(evidence);
            }
        }
        catch
        {
            // RAS is optional supporting evidence. Failure must never create a VPN positive.
        }

        return result;
    }

    private static RemoteAccessConnectionKind MatchRasEvidence(
        NetworkInterface networkInterface,
        AdapterMetadata metadata,
        IEnumerable<RasEvidence> evidence)
    {
        string[] identifiers =
        {
            networkInterface.Name ?? string.Empty,
            networkInterface.Description ?? string.Empty,
            metadata.Name,
            metadata.Description,
            metadata.NetConnectionId,
            metadata.ServiceName
        };

        foreach (RasEvidence item in evidence)
        {
            if (item.Identifiers.Any(rasIdentifier => identifiers.Any(interfaceIdentifier =>
                EqualsIgnoreCase(rasIdentifier, interfaceIdentifier))))
            {
                return item.Kind;
            }
        }

        return RemoteAccessConnectionKind.None;
    }

    private static bool IsExplicitVpnRasType(string deviceType)
    {
        string normalized = deviceType.Trim().Replace("_", string.Empty).Replace("-", string.Empty);
        return normalized.Equals("vpn", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("pptp", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("l2tp", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("sstp", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("ikev2", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("ikev", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DetectProxyConfiguration()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            string[] registryPaths =
            {
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings",
                @"Software\Policies\Microsoft\Windows\CurrentVersion\Internet Settings"
            };

            foreach (string path in registryPaths)
            {
                if (HasProxyValues(Registry.CurrentUser.OpenSubKey(path)))
                    return true;
                if (HasProxyValues(Registry.LocalMachine.OpenSubKey(path)))
                    return true;
            }
        }
        catch
        {
        }

        return DetectWinHttpProxy();
    }

    private static bool HasProxyValues(RegistryKey? key)
    {
        using (key)
        {
            if (key == null)
                return false;

            bool enabled = ReadRegistryBool(key.GetValue("ProxyEnable"));
            bool autoDetect = ReadRegistryBool(key.GetValue("AutoDetect"));
            string proxy = Convert.ToString(key.GetValue("ProxyServer"), CultureInfo.InvariantCulture) ?? string.Empty;
            string pac = Convert.ToString(key.GetValue("AutoConfigURL"), CultureInfo.InvariantCulture) ?? string.Empty;
            return enabled || autoDetect || !string.IsNullOrWhiteSpace(proxy) || !string.IsNullOrWhiteSpace(pac);
        }
    }

    private static bool DetectWinHttpProxy()
    {
        try
        {
            if (!WinHttpGetDefaultProxyConfiguration(out WINHTTP_PROXY_INFO info))
                return false;

            try
            {
                // WINHTTP_ACCESS_TYPE_NAMED_PROXY = 3; AUTO_DETECT = 4.
                return info.dwAccessType == 3 || info.dwAccessType == 4
                    || info.lpszProxy != IntPtr.Zero;
            }
            finally
            {
                if (info.lpszProxy != IntPtr.Zero)
                    GlobalFree(info.lpszProxy);
                if (info.lpszProxyBypass != IntPtr.Zero)
                    GlobalFree(info.lpszProxyBypass);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadRegistryBool(object? value)
    {
        if (value is int integer)
            return integer != 0;
        if (value is long longValue)
            return longValue != 0;
        return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out bool parsed) && parsed;
    }

    private static int TryGetInterfaceIndex(IPInterfaceProperties properties)
    {
        try
        {
            return properties.GetIPv4Properties()?.Index
                ?? properties.GetIPv6Properties()?.Index
                ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsUsableAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (bytes.Length == 4 && (bytes[0] == 169 && bytes[1] == 254 || bytes[0] >= 224))
                return false;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Link-local and multicast addresses do not provide usable tunnel reachability.
            if (bytes.Length == 16
                && ((bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) || bytes[0] == 0xff))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddIdentifier(HashSet<string> identifiers, object? value)
    {
        string text = ReadString(value);
        if (!string.IsNullOrWhiteSpace(text))
            identifiers.Add(text);
    }

    private static bool TryReadInt(object? value, out int result)
    {
        return int.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out result);
    }

    private static string ReadString(object? value)
    {
        return value == null || value == DBNull.Value
            ? string.Empty
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string NormalizeIdentifier(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Trim('{', '}');
    }

    private static bool EqualsIgnoreCase(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINHTTP_PROXY_INFO
    {
        public uint dwAccessType;
        public IntPtr lpszProxy;
        public IntPtr lpszProxyBypass;
    }

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetDefaultProxyConfiguration(out WINHTTP_PROXY_INFO proxyInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
