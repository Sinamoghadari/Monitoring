using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using Ergonomy.Networking;
using Xunit;

namespace Ergonomy.Core.Tests
{
    public sealed class VpnDetectionTests
    {
        [Theory]
        [InlineData("WSL", "WSL virtual ethernet")]
        [InlineData("vEthernet (Default Switch)", "Hyper-V virtual ethernet")]
        [InlineData("Hyper-V Virtual Ethernet Adapter", "Hyper-V")]
        [InlineData("DockerNAT", "Docker container NAT")]
        [InlineData("Docker Desktop", "Docker")]
        [InlineData("VMware Network Adapter VMnet8", "VMware")]
        [InlineData("VirtualBox Host-Only Ethernet Adapter", "VirtualBox host-only")]
        [InlineData("Npcap Loopback Adapter", "Npcap loopback")]
        [InlineData("Teredo Tunneling Pseudo-Interface", "Teredo")]
        [InlineData("6to4 Adapter", "6to4")]
        [InlineData("isatap.example", "ISATAP")]
        [InlineData("Bluetooth Network Connection", "Bluetooth")]
        public void Hard_exclusions_win_over_explicit_and_provider_evidence(string name, string description)
        {
            NetworkInterfaceSnapshot snapshot = EligibleInterface(name, description);
            snapshot.RemoteAccessConnection = RemoteAccessConnectionKind.ExplicitVpn;
            snapshot.Manufacturer = "WireGuard";
            snapshot.Description = description + " WireGuard Tunnel";

            VpnDetectionResult result = VpnDetector.Evaluate(new[] { snapshot });

            Assert.False(result.IsVpnActive);
            Assert.NotEqual(VpnDetectionState.ConfirmedVpn, result.State);
            Assert.True(VpnDetector.IsHardExcluded(snapshot));
        }

        [Fact]
        public void Loopback_interface_type_is_hard_excluded_even_without_a_loopback_name()
        {
            NetworkInterfaceSnapshot snapshot = EligibleInterface("lo0", "WireGuard Tunnel");
            snapshot.InterfaceType = NetworkInterfaceType.Loopback;
            snapshot.Manufacturer = "WireGuard";
            snapshot.RemoteAccessConnection = RemoteAccessConnectionKind.ExplicitVpn;

            VpnDetectionResult result = VpnDetector.Evaluate(new[] { snapshot });

            Assert.False(result.IsVpnActive);
            Assert.True(VpnDetector.IsHardExcluded(snapshot));
        }

        [Fact]
        public void Generic_virtual_adapter_is_unknown_not_vpn()
        {
            NetworkInterfaceSnapshot snapshot = EligibleInterface(
                "Generic Virtual Adapter",
                "Generic virtual network adapter");
            snapshot.PhysicalAdapterKnown = true;
            snapshot.IsPhysicalAdapter = false;
            snapshot.InterfaceType = NetworkInterfaceType.Tunnel;

            VpnDetectionResult result = VpnDetector.Evaluate(new[] { snapshot });

            Assert.False(result.IsVpnActive);
            Assert.Equal(VpnDetectionState.UnknownOrAmbiguous, result.State);
        }

        [Fact]
        public void Generic_tunnel_and_ppp_are_not_vpn_without_positive_evidence()
        {
            NetworkInterfaceSnapshot tunnel = EligibleInterface("Unknown Tunnel", "TAP-like tunnel");
            tunnel.InterfaceType = NetworkInterfaceType.Tunnel;
            tunnel.PhysicalAdapterKnown = true;
            tunnel.IsPhysicalAdapter = false;

            NetworkInterfaceSnapshot ppp = EligibleInterface("Generic PPP", "Remote access PPP");
            ppp.InterfaceType = NetworkInterfaceType.Ppp;

            VpnDetectionResult result = VpnDetector.Evaluate(new[] { tunnel, ppp });

            Assert.False(result.IsVpnActive);
            Assert.Equal(VpnDetectionState.UnknownOrAmbiguous, result.State);
        }

        [Theory]
        [InlineData("Cisco AnyConnect Secure Mobility Client", "Cisco")]
        [InlineData("WireGuard Tunnel", "WireGuard")]
        [InlineData("OpenVPN Wintun Adapter", "OpenVPN")]
        [InlineData("Fortinet SSL VPN Virtual Ethernet Adapter", "Fortinet")]
        [InlineData("GlobalProtect Virtual Ethernet Adapter", "Palo Alto Networks GlobalProtect")]
        public void Trusted_provider_requires_active_address_and_route(string description, string manufacturer)
        {
            NetworkInterfaceSnapshot snapshot = EligibleInterface("Virtual VPN Adapter", description);
            snapshot.Manufacturer = manufacturer;
            snapshot.InterfaceType = NetworkInterfaceType.Tunnel;
            snapshot.PhysicalAdapterKnown = true;
            snapshot.IsPhysicalAdapter = false;

            VpnDetectionResult result = VpnDetector.Evaluate(new[] { snapshot });

            Assert.True(result.IsVpnActive);
            Assert.Equal(VpnDetectionState.ConfirmedVpn, result.State);
        }

        [Theory]
        [InlineData("down", true, true)]
        [InlineData("no usable address", false, true)]
        [InlineData("no active route", true, false)]
        public void Trusted_provider_is_fail_closed_when_operational_evidence_is_incomplete(
            string _, bool hasAddress, bool hasRoute)
        {
            NetworkInterfaceSnapshot snapshot = EligibleInterface(
                "WireGuard Tunnel",
                "WireGuard Tunnel");
            snapshot.Manufacturer = "WireGuard";
            snapshot.InterfaceType = NetworkInterfaceType.Tunnel;
            snapshot.PhysicalAdapterKnown = true;
            snapshot.IsPhysicalAdapter = false;
            snapshot.HasUsableAddress = hasAddress;
            snapshot.HasActiveRoute = hasRoute;
            if (_ == "down")
                snapshot.OperationalStatus = OperationalStatus.Down;

            VpnDetectionResult result = VpnDetector.Evaluate(new[] { snapshot });

            Assert.False(result.IsVpnActive);
            Assert.NotEqual(VpnDetectionState.ConfirmedVpn, result.State);
        }

        [Fact]
        public void Explicit_vpn_ras_is_positive_without_provider_name()
        {
            NetworkInterfaceSnapshot snapshot = EligibleInterface(
                "Work VPN",
                "WAN Miniport (IKEv2)");
            snapshot.InterfaceType = NetworkInterfaceType.Ppp;
            snapshot.RemoteAccessConnection = RemoteAccessConnectionKind.ExplicitVpn;
            snapshot.PhysicalAdapterKnown = true;
            snapshot.IsPhysicalAdapter = false;

            VpnDetectionResult result = VpnDetector.Evaluate(new[] { snapshot });

            Assert.True(result.IsVpnActive);
            Assert.Equal("explicit-vpn-ras-interface", result.Evidence);
        }

        [Fact]
        public void Generic_active_ras_is_not_a_vpn()
        {
            NetworkInterfaceSnapshot snapshot = EligibleInterface(
                "Corporate Dial-up",
                "Generic Remote Access Service");
            snapshot.InterfaceType = NetworkInterfaceType.Ppp;
            snapshot.RemoteAccessConnection = RemoteAccessConnectionKind.NonVpnOrUnknown;

            VpnDetectionResult result = VpnDetector.Evaluate(new[] { snapshot });

            Assert.False(result.IsVpnActive);
            Assert.NotEqual(VpnDetectionState.ConfirmedVpn, result.State);
        }

        [Fact]
        public void Explicit_vpn_ras_on_a_physical_non_tunnel_interface_is_not_enough()
        {
            NetworkInterfaceSnapshot snapshot = EligibleInterface("Ethernet", "Ethernet adapter");
            snapshot.InterfaceType = NetworkInterfaceType.Ethernet;
            snapshot.RemoteAccessConnection = RemoteAccessConnectionKind.ExplicitVpn;
            snapshot.PhysicalAdapterKnown = true;
            snapshot.IsPhysicalAdapter = true;

            VpnDetectionResult result = VpnDetector.Evaluate(new[] { snapshot });

            Assert.False(result.IsVpnActive);
        }

        [Fact]
        public void Proxy_only_configuration_stays_separate_from_vpn_status()
        {
            NetworkInterfaceSnapshot physical = EligibleInterface("Ethernet", "Intel Ethernet Controller");
            physical.InterfaceType = NetworkInterfaceType.Ethernet;
            physical.PhysicalAdapterKnown = true;
            physical.IsPhysicalAdapter = true;

            VpnDetectionResult result = VpnDetector.Evaluate(new[] { physical }, proxyConfigured: true);

            Assert.True(result.ProxyConfigured);
            Assert.False(result.IsVpnActive);
            Assert.NotEqual(VpnDetectionState.ConfirmedVpn, result.State);
        }

        [Fact]
        public void Null_or_empty_inventory_is_fail_closed()
        {
            Assert.False(VpnDetector.Evaluate(null).IsVpnActive);
            Assert.False(VpnDetector.Evaluate(Enumerable.Empty<NetworkInterfaceSnapshot>()).IsVpnActive);
        }

        [Fact]
        public void Filtered_out_display_adapter_is_still_considered_from_raw_inventory()
        {
            // The presentation filter in AdvancedMetricsCollector excludes names containing
            // "Virtual". The detector must receive the complete list before that filter.
            NetworkInterfaceSnapshot rawVpn = EligibleInterface(
                "Virtual WireGuard Adapter",
                "WireGuard Tunnel");
            rawVpn.Manufacturer = "WireGuard";
            rawVpn.InterfaceType = NetworkInterfaceType.Tunnel;
            rawVpn.PhysicalAdapterKnown = true;
            rawVpn.IsPhysicalAdapter = false;

            List<NetworkInterfaceSnapshot> rawInventory = new() { rawVpn };
            List<NetworkInterfaceSnapshot> displayInventory = rawInventory
                .Where(item => !item.Name.Contains("Virtual", System.StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.Empty(displayInventory);
            Assert.True(VpnDetector.Evaluate(rawInventory).IsVpnActive);
            Assert.False(VpnDetector.Evaluate(displayInventory).IsVpnActive);
        }

        [Fact]
        public void Every_hard_excluded_adapter_remains_false_even_with_proxy_enabled()
        {
            NetworkInterfaceSnapshot snapshot = EligibleInterface(
                "vEthernet (WSL)",
                "WireGuard Tunnel");
            snapshot.Manufacturer = "WireGuard";
            snapshot.InterfaceType = NetworkInterfaceType.Tunnel;
            snapshot.RemoteAccessConnection = RemoteAccessConnectionKind.ExplicitVpn;

            VpnDetectionResult result = VpnDetector.Evaluate(new[] { snapshot }, proxyConfigured: true);

            Assert.False(result.IsVpnActive);
            Assert.True(result.ProxyConfigured);
        }

        private static NetworkInterfaceSnapshot EligibleInterface(string name, string description)
        {
            return new NetworkInterfaceSnapshot
            {
                Name = name,
                Description = description,
                InterfaceType = NetworkInterfaceType.Tunnel,
                OperationalStatus = OperationalStatus.Up,
                PhysicalAdapterKnown = true,
                IsPhysicalAdapter = false,
                HasUsableAddress = true,
                HasActiveRoute = true,
                HasGateway = true,
                HasDefaultRoute = true,
                RemoteAccessConnection = RemoteAccessConnectionKind.None
            };
        }
    }
}
