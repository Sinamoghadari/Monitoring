using System;

namespace Ergonomy.Core.Ipc
{
    /// <summary>
    /// Well-known constants of the Ergonomy inter-process channel.
    ///
    /// TRANSPORT POLICY: the two Ergonomy processes communicate exclusively over a local
    /// Windows Named Pipe. No TCP or UDP listening socket may ever be introduced for IPC;
    /// the only pre-existing HTTP listener in the product is the Prometheus scrape endpoint
    /// (<c>Ergonomy.Observability.MetricsEndpoint</c>), which is owned by the Service process
    /// and is NOT part of this channel.
    /// </summary>
    public static class IpcConstants
    {
        /// <summary>Wire-protocol version. Bump on any breaking envelope/payload change.</summary>
        public const int ProtocolVersion = 1;

        /// <summary>
        /// Base pipe name (the OS path becomes <c>\\.\pipe\Ergonomy.Agent.v1</c>).
        /// Versioned so a Service and a Task of different builds simply never connect
        /// instead of exchanging incompatible frames.
        /// </summary>
        public const string PipeName = "Ergonomy.Agent.v1";

        /// <summary>Local machine only. A remote server name is never used by design.</summary>
        public const string ServerName = ".";

        /// <summary>Maximum concurrent pipe instances (one interactive Task process per session).</summary>
        public const int MaxServerInstances = 8;

        /// <summary>Hard cap for a single frame (guards against a hostile/desynchronised peer).</summary>
        public const int MaxFrameBytes = 256 * 1024;

        /// <summary>Pipe kernel buffer sizes.</summary>
        public const int PipeBufferBytes = 64 * 1024;

        /// <summary>Client reconnect backoff bounds.</summary>
        public static readonly TimeSpan ReconnectInitialDelay = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan ReconnectMaxDelay = TimeSpan.FromSeconds(30);

        /// <summary>How often the Task process proves liveness to the Service.</summary>
        public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

        /// <summary>Connection attempt timeout used by the client.</summary>
        public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How often an unconnected server instance is recreated so the pipe ACL can pick up
        /// a newly logged-on interactive user SID without granting Authenticated Users.
        /// </summary>
        public static readonly TimeSpan AcceptAclRefresh = TimeSpan.FromSeconds(30);
    }
}
