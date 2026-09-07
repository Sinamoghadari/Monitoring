using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Ergonomy.Core.Ipc
{
    /// <summary>
    /// Creates the server side of the Ergonomy pipe with an explicit, least-privilege ACL.
    ///
    /// Only two classes of trustee get Allow:
    ///   - the Service process SID (LocalSystem when installed, or the interactive debug user)
    ///   - the SID of each currently logged-on interactive user (so Ergonomy.Task can connect)
    ///
    /// Authenticated Users and Administrators are intentionally not granted. Anonymous and
    /// NETWORK are explicitly denied. The accept loop recreates waiting instances periodically
    /// so a user who logs on after the Service starts is added on the next refresh.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class IpcSecurityFactory
    {
        /// <summary>
        /// Builds a pipe DACL that grants FullControl to the Service SID and ReadWrite to
        /// interactive user SIDs, and denies anonymous/network logons.
        /// </summary>
        public static PipeSecurity CreatePipeSecurity()
        {
            var security = new PipeSecurity();

            SecurityIdentifier serviceSid = GetServiceSid();
            security.AddAccessRule(new PipeAccessRule(
                serviceSid, PipeAccessRights.FullControl, AccessControlType.Allow));

            foreach (SecurityIdentifier userSid in EnumerateInteractiveUserSids())
            {
                if (userSid == serviceSid)
                    continue;

                security.AddAccessRule(new PipeAccessRule(
                    userSid,
                    PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                    AccessControlType.Allow));
            }

            var anonymous = new SecurityIdentifier(WellKnownSidType.AnonymousSid, null);
            var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
            security.AddAccessRule(new PipeAccessRule(
                anonymous, PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(
                network, PipeAccessRights.FullControl, AccessControlType.Deny));

            return security;
        }

        /// <summary>
        /// Creates a new asynchronous, ACL'd server instance of the pipe.
        /// <see cref="NamedPipeServerStreamAcl"/> is required: on .NET the
        /// <c>SetAccessControl</c> path throws UnauthorizedAccessException for a service-owned pipe.
        /// </summary>
        public static NamedPipeServerStream CreateServerStream(string pipeName, int maxInstances)
        {
            return NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                maxInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                IpcConstants.PipeBufferBytes,
                IpcConstants.PipeBufferBytes,
                CreatePipeSecurity());
        }

        /// <summary>
        /// SID of the process that hosts the pipe server (LocalSystem under SCM).
        /// </summary>
        public static SecurityIdentifier GetServiceSid()
        {
            try
            {
                SecurityIdentifier? current = WindowsIdentity.GetCurrent()?.User;
                if (current != null)
                    return current;
            }
            catch
            {
            }

            return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        }

        /// <summary>
        /// Interactive user SIDs currently logged on, plus the process user (covers --console).
        /// </summary>
        public static IReadOnlyList<SecurityIdentifier> EnumerateInteractiveUserSids()
        {
            var unique = new Dictionary<string, SecurityIdentifier>(StringComparer.OrdinalIgnoreCase);

            void Add(SecurityIdentifier? sid)
            {
                if (sid == null)
                    return;
                unique[sid.Value] = sid;
            }

            try { Add(WindowsIdentity.GetCurrent()?.User); }
            catch { }

            IntPtr sessions = IntPtr.Zero;
            try
            {
                if (!WTSEnumerateSessions(WtsCurrentServerHandle, 0, 1, out sessions, out int count)
                    || sessions == IntPtr.Zero
                    || count <= 0)
                {
                    return new List<SecurityIdentifier>(unique.Values);
                }

                int size = Marshal.SizeOf<WtsSessionInfo>();
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WtsSessionInfo>(sessions + (i * size));
                    if (info.SessionId == 0)
                        continue;
                    if (info.State != WtsConnectState.Active && info.State != WtsConnectState.Connected)
                        continue;

                    Add(SidFromSession(info.SessionId));
                }
            }
            catch
            {
            }
            finally
            {
                if (sessions != IntPtr.Zero)
                    WTSFreeMemory(sessions);
            }

            return new List<SecurityIdentifier>(unique.Values);
        }

        private static SecurityIdentifier? SidFromSession(int sessionId)
        {
            if (!WTSQueryUserToken(sessionId, out IntPtr token) || token == IntPtr.Zero)
                return null;

            try
            {
                using var identity = new WindowsIdentity(token);
                return identity.User;
            }
            catch
            {
                return null;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        private static readonly IntPtr WtsCurrentServerHandle = IntPtr.Zero;

        private enum WtsConnectState
        {
            Active = 0,
            Connected = 1,
            ConnectQuery = 2,
            Shadow = 3,
            Disconnected = 4,
            Idle = 5,
            Listen = 6,
            Reset = 7,
            Down = 8,
            Init = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WtsSessionInfo
        {
            public int SessionId;
            public IntPtr WinStationName;
            public WtsConnectState State;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnumerateSessions(
            IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(int sessionId, out IntPtr phToken);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
