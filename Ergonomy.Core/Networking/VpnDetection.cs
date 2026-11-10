using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace Ergonomy.Networking
{
    /// <summary>
    /// The limited set of remote-access states that the detector is allowed to trust.
    /// An active RAS connection is not considered a VPN unless its device type was explicitly
    /// classified as a VPN by the Windows RAS API.
    /// </summary>
    public enum RemoteAccessConnectionKind
    {
        None = 0,
        ExplicitVpn = 1,
        NonVpnOrUnknown = 2
    }

    /// <summary>
    /// Fail-closed result of VPN evaluation. Unknown and ambiguous evidence deliberately map to
    /// IsVpnActive=false; the state remains available for diagnostics and future telemetry policy.
    /// </summary>
    public enum VpnDetectionState
    {
        NotDetected = 0,
        ConfirmedVpn = 1,
        UnknownOrAmbiguous = 2
    }

    /// <summary>
    /// OS-independent snapshot used by the VPN policy. The Windows collector populates this from
    /// NetworkInterface, adapter metadata, route data, and RAS. Keeping the policy pure makes the
    /// hard exclusions unit-testable without requiring a Windows network stack in the test runner.
    /// </summary>
    public sealed class NetworkInterfaceSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string NetConnectionId { get; set; } = string.Empty;
        public string Manufacturer { get; set; } = string.Empty;
        public string PnpDeviceId { get; set; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;
        public NetworkInterfaceType InterfaceType { get; set; } = NetworkInterfaceType.Unknown;
        public OperationalStatus OperationalStatus { get; set; } = OperationalStatus.Unknown;
        public bool PhysicalAdapterKnown { get; set; }
        public bool IsPhysicalAdapter { get; set; }
        public bool HasUsableAddress { get; set; }
        public bool HasActiveRoute { get; set; }
        public bool HasGateway { get; set; }
        public bool HasDefaultRoute { get; set; }
        public RemoteAccessConnectionKind RemoteAccessConnection { get; set; }
    }

    /// <summary>
    /// Result of the conservative detector. ProxyConfigured is intentionally independent from
    /// IsVpnActive: a WinINet/WinHTTP proxy is not a VPN tunnel.
    /// </summary>
    public sealed class VpnDetectionResult
    {
        public VpnDetectionState State { get; }
        public bool IsVpnActive => State == VpnDetectionState.ConfirmedVpn;
        public bool ProxyConfigured { get; }
        public string Evidence { get; }

        public VpnDetectionResult(
            VpnDetectionState state,
            bool proxyConfigured = false,
            string evidence = "")
        {
            State = state;
            ProxyConfigured = proxyConfigured;
            Evidence = evidence ?? string.Empty;
        }
    }

    /// <summary>
    /// Conservative VPN policy. Deny rules are evaluated before provider signatures. A name,
    /// NetworkInterfaceType, gateway, route, or generic virtual-adapter marker is never enough
    /// by itself to accuse a client of VPN use.
    /// </summary>
    public static class VpnDetector
    {
        private static readonly string[] HardExcludedTokens =
        {
            "wsl",
            "vEthernet",
            "hyper-v",
            "hyperv",
            "default switch",
            "dockernat",
            "docker",
            "containernat",
            "container",
            "host-only",
            "vmware",
            "vmnet",
            "virtualbox",
            "vbox",
            "npcap loopback",
            "software loopback",
            "loopback",
            "teredo",
            "6to4",
            "isatap",
            "bluetooth"
        };

        /// <summary>
        /// Evaluates all supplied interfaces. Proxy configuration is carried only as separate
        /// diagnostic state and never participates in a positive VPN decision.
        /// </summary>
        public static VpnDetectionResult Evaluate(
            IEnumerable<NetworkInterfaceSnapshot>? interfaces,
            bool proxyConfigured = false)
        {
            if (interfaces == null)
                return new VpnDetectionResult(VpnDetectionState.NotDetected, proxyConfigured);

            bool sawAmbiguousCandidate = false;

            foreach (NetworkInterfaceSnapshot snapshot in interfaces)
            {
                if (snapshot == null || IsHardExcluded(snapshot))
                    continue;

                if (!LooksLikeCandidate(snapshot))
                    continue;

                if (!HasOperationalEvidence(snapshot))
                {
                    sawAmbiguousCandidate = true;
                    continue;
                }

                // ExplicitVpn is populated only after the Windows collector has verified an
                // explicit VPN RAS device type and associated it with this interface. Generic
                // dial-up/remote-access entries are NonVpnOrUnknown and cannot pass this branch.
                if (snapshot.RemoteAccessConnection == RemoteAccessConnectionKind.ExplicitVpn
                    && IsTunnelLike(snapshot))
                {
                    return new VpnDetectionResult(
                        VpnDetectionState.ConfirmedVpn,
                        proxyConfigured,
                        "explicit-vpn-ras-interface");
                }

                // Third-party VPN adapters require an exact provider signature, a known virtual
                // adapter, an operational state, an address, and an active route. Generic TAP,
                // Wintun, Tunnel, PPP, or Virtual names are intentionally insufficient.
                if (snapshot.PhysicalAdapterKnown
                    && !snapshot.IsPhysicalAdapter
                    && IsTunnelLike(snapshot)
                    && HasTrustedProviderSignature(snapshot))
                {
                    return new VpnDetectionResult(
                        VpnDetectionState.ConfirmedVpn,
                        proxyConfigured,
                        "trusted-vpn-provider-interface");
                }

                sawAmbiguousCandidate = true;
            }

            return new VpnDetectionResult(
                sawAmbiguousCandidate
                    ? VpnDetectionState.UnknownOrAmbiguous
                    : VpnDetectionState.NotDetected,
                proxyConfigured);
        }

        /// <summary>
        /// Hard exclusions always win over RAS or provider evidence. This is deliberately an
        /// explicit developer/virtualization deny list; a generic "Virtual" token is handled by
        /// the positive-evidence rules so trusted VPN adapters are not accidentally excluded.
        /// </summary>
        public static bool IsHardExcluded(NetworkInterfaceSnapshot snapshot)
        {
            if (snapshot == null)
                return true;

            if (snapshot.InterfaceType == NetworkInterfaceType.Loopback)
                return true;

            string metadata = string.Join(
                " ",
                snapshot.Name,
                snapshot.Description,
                snapshot.NetConnectionId,
                snapshot.Manufacturer,
                snapshot.PnpDeviceId,
                snapshot.ServiceName);

            return HardExcludedTokens.Any(token =>
                metadata.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool HasOperationalEvidence(NetworkInterfaceSnapshot snapshot)
        {
            return snapshot.OperationalStatus == OperationalStatus.Up
                && snapshot.HasUsableAddress
                && snapshot.HasActiveRoute;
        }

        private static bool LooksLikeCandidate(NetworkInterfaceSnapshot snapshot)
        {
            if (snapshot.RemoteAccessConnection == RemoteAccessConnectionKind.NonVpnOrUnknown)
                return false;

            return IsTunnelLike(snapshot)
                || HasTrustedProviderSignature(snapshot);
        }

        private static bool IsTunnelLike(NetworkInterfaceSnapshot snapshot)
        {
            return snapshot.InterfaceType == NetworkInterfaceType.Ppp
                || snapshot.InterfaceType == NetworkInterfaceType.Tunnel
                || snapshot.InterfaceType == NetworkInterfaceType.Unknown
                || (snapshot.PhysicalAdapterKnown && !snapshot.IsPhysicalAdapter);
        }

        private static bool HasTrustedProviderSignature(NetworkInterfaceSnapshot snapshot)
        {
            string metadata = string.Join(
                " ",
                snapshot.Name,
                snapshot.Description,
                snapshot.NetConnectionId,
                snapshot.Manufacturer,
                snapshot.PnpDeviceId,
                snapshot.ServiceName);

            return ContainsProviderPair(metadata, "cisco", "anyconnect", "secure client", "vpn")
                || ContainsProviderPair(metadata, "globalprotect", "palo alto")
                || ContainsProviderPair(metadata, "fortinet", "forticlient", "vpn")
                || ContainsProviderPair(metadata, "openvpn", "tap", "wintun", "vpn")
                || ContainsProvider(metadata, "wireguard")
                || ContainsProviderPair(metadata, "wintun", "openvpn", "wireguard")
                || ContainsProviderPair(metadata, "checkpoint", "check point", "vpn")
                || ContainsProviderPair(metadata, "pulse secure", "ivanti", "vpn")
                || ContainsProviderPair(metadata, "sonicwall", "netextender")
                || ContainsProviderPair(metadata, "tailscale", "wireguard");
        }

        private static bool ContainsProvider(string metadata, string provider)
        {
            return metadata.IndexOf(provider, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsProviderPair(string metadata, string provider, params string[] productTokens)
        {
            if (!ContainsProvider(metadata, provider))
                return false;

            return productTokens.Any(token =>
                metadata.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
