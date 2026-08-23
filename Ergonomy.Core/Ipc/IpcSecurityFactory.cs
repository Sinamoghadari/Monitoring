using System;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Ergonomy.Core.Ipc
{
    /// <summary>
    /// Creates the server side of the Ergonomy pipe with an explicit, least-privilege ACL.
    ///
    /// The Service runs as LocalSystem in session 0 and the Task process runs as the
    /// interactive user, so a default (creator-only) DACL would make the pipe unreachable.
    /// The DACL therefore grants:
    ///   - LocalSystem            : FullControl (the service itself)
    ///   - BUILTIN\Administrators : FullControl (support/diagnostics)
    ///   - Authenticated Users    : ReadWrite + Synchronize only
    /// Anonymous logon, NETWORK and remote callers are excluded: an authenticated local
    /// account is required, and the pipe is never exposed over a socket.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class IpcSecurityFactory
    {
        public static PipeSecurity CreatePipeSecurity()
        {
            var security = new PipeSecurity();

            var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            var anonymous = new SecurityIdentifier(WellKnownSidType.AnonymousSid, null);
            var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);

            security.AddAccessRule(new PipeAccessRule(
                localSystem, PipeAccessRights.FullControl, AccessControlType.Allow));

            security.AddAccessRule(new PipeAccessRule(
                administrators, PipeAccessRights.FullControl, AccessControlType.Allow));

            // Clients need Read + Write + Synchronize. CreateNewInstance is deliberately NOT
            // granted, so a non-admin process can never spoof the server end of the pipe.
            security.AddAccessRule(new PipeAccessRule(
                authenticatedUsers,
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                AccessControlType.Allow));

            // Defence in depth: explicit denies for anonymous and network logons.
            security.AddAccessRule(new PipeAccessRule(
                anonymous, PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(
                network, PipeAccessRights.FullControl, AccessControlType.Deny));

            return security;
        }

        /// <summary>
        /// Creates a new asynchronous, ACL'd server instance of the pipe.
        /// <see cref="NamedPipeServerStreamAcl"/> (.NET 6+) is required: on .NET the
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
    }
}
