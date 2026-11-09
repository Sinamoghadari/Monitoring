using System;
using System.Collections.Generic;

namespace Ergonomy.Core.Ipc
{
    /// <summary>Identity of the interactive Task process, sent once on connect.</summary>
    public sealed class TaskHelloPayload
    {
        public int ProcessId { get; set; }
        public int WindowsSessionId { get; set; }
        public string WindowsSid { get; set; } = string.Empty;
        public string WindowsUsername { get; set; } = string.Empty;
        public string MachineName { get; set; } = string.Empty;
        public string AgentVersion { get; set; } = string.Empty;
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Service acknowledgement of <see cref="IpcMessageTypes.Hello"/>.</summary>
    public sealed class HelloAckPayload
    {
        public bool Accepted { get; set; } = true;
        public string? RejectionReason { get; set; }
        public int ProtocolVersion { get; set; } = IpcConstants.ProtocolVersion;
        public string ServiceSessionId { get; set; } = string.Empty;
    }

    /// <summary>Periodic liveness ping from the Task process.</summary>
    public sealed class HeartbeatPayload
    {
        public DateTime SentUtc { get; set; } = DateTime.UtcNow;
        public bool HooksInstalled { get; set; }
        public bool CollectionEnabled { get; set; }
        public long WorkingSetBytes { get; set; }
    }

    /// <summary>
    /// Activity accumulated by the interactive process (hooks + ActivityMonitor live there,
    /// because low-level input hooks cannot observe an interactive desktop from session 0).
    /// The Service turns this into the persisted <c>user_activity</c> outbox record.
    /// </summary>
    public sealed class ActivityReportPayload
    {
        public string SessionId { get; set; } = string.Empty;
        public string WindowsSid { get; set; } = string.Empty;
        public string WindowsUsername { get; set; } = string.Empty;
        /// <summary>"Start" | "Update" | "Stop" - mirrors UserActivityPayload.StateType.</summary>
        public string StateType { get; set; } = "Update";
        public double KeyboardActiveSeconds { get; set; }
        public double MouseActiveSeconds { get; set; }
        public double TotalActiveSeconds { get; set; }
        public int SessionCloseCounter { get; set; }
        public int PrimaryAlarmCount { get; set; }
        public int SecondaryAlarmCount { get; set; }
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    }

    public enum AlarmKind
    {
        Primary = 0,
        Secondary = 1,
        Message = 2
    }

    /// <summary>Service -> Task request to render an alarm on the interactive desktop.</summary>
    public sealed class ShowAlarmPayload
    {
        public AlarmKind Kind { get; set; } = AlarmKind.Primary;
        public string? Title { get; set; }
        public string? Message { get; set; }
        /// <summary>Local path of the image already downloaded by the Service (never a URL).</summary>
        public string? ImagePath { get; set; }
        public int AutoCloseSeconds { get; set; }
        public int UnclosableSeconds { get; set; }
    }

    /// <summary>Task -> Service outcome of a <see cref="ShowAlarmPayload"/> request.</summary>
    public sealed class AlarmAckPayload
    {
        public AlarmKind Kind { get; set; }
        public bool Shown { get; set; }
        public string? Error { get; set; }
        public DateTime ShownUtc { get; set; } = DateTime.UtcNow;
        public double VisibleSeconds { get; set; }
    }

    /// <summary>
    /// The subset of settings the interactive process needs. The Service stays the single
    /// owner of the Settings API refresh; the Task process never calls the API itself.
    /// </summary>
    public sealed class SettingsSnapshotPayload
    {
        public bool AllowErgonomyCollection { get; set; }
        public int NotificationIntervalSeconds { get; set; }
        public int ActivityThresholdSeconds { get; set; }
        public int PrimaryAlarmAutoCloseSeconds { get; set; }
        public int SecondaryAlarmAutoCloseSeconds { get; set; }
        public int SecondaryAlarmUnclosableSeconds { get; set; }
        public int SessionCloseLimit { get; set; }
        public string? PrimaryAlarmImagePath { get; set; }
        public string? SecondaryAlarmImagePath { get; set; }
        public string Source { get; set; } = "Bootstrap";
        public DateTime IssuedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Service -> Task orderly shutdown request.</summary>
    public sealed class ShutdownRequestPayload
    {
        public string Reason { get; set; } = string.Empty;
        public int GraceSeconds { get; set; } = 5;
    }

    /// <summary>
    /// Task -> Service problem record. The Service writes it to the SQLite outbox and
    /// SyncEngine forwards it to app_logs. Only WARNING / ERROR are persisted.
    /// </summary>
    public sealed class ProblemLogPayload
    {
        public string Level { get; set; } = "ERROR";
        public string Message { get; set; } = string.Empty;
        public string Category { get; set; } = "Task";
    }

    /// <summary>Task -> Service notice that the process is exiting (best effort).</summary>
    public sealed class GoodbyePayload
    {
        public string Reason { get; set; } = string.Empty;
        public IDictionary<string, string>? Details { get; set; }
    }
}
