# Ergonomy — Two-Process Architecture Split

Status: **in progress (step 1 of 3 — skeleton + IPC layer + Generic Host / Windows Service lifetime landed).**

## Why two processes

The agent currently runs as one interactive WinForms process that does *everything*:
low-level input hooks, alarm UI, SQLite outbox, Kafka sync, Postgres/ClickHouse,
advanced metrics, remote commands and the Prometheus endpoint. That single process is
both a reliability and a security problem:

* everything dies (or is killed by the user) when the interactive session ends;
* privileged work (system power commands, hardware counters, LibreHardwareMonitor)
  shares a process with a user-owned message pump;
* a hung UI thread stalls telemetry delivery, and vice versa.

The split separates *desktop-bound* work from *machine-bound* work:

| Concern | Process | Rationale |
| --- | --- | --- |
| Low-level keyboard/mouse hooks, `ActivityMonitor` | **Ergonomy.Task** | `WH_KEYBOARD_LL` / `WH_MOUSE_LL` require a pumped, interactive desktop; session 0 cannot see them. |
| Alarm forms (Primary / Secondary / Message) | **Ergonomy.Task** | Only an interactive session can render UI to the user. |
| SQLite outbox, `SyncEngine`, Kafka, Postgres/ClickHouse | **Ergonomy.Service** | Must survive logoff; must not be user-terminable. |
| Advanced metrics, health checks, `MetricsEndpoint` | **Ergonomy.Service** | Machine scope + privileged counters. |
| Settings API refresh, permission evaluation, remote commands | **Ergonomy.Service** | Single owner of policy; the Task process only receives the resulting snapshot. |
| Shared DTOs, settings model, log events, IPC transport | **Ergonomy.Core** | Referenced by both processes (and, during migration, by the legacy project). |

## Projects

```
Ergonomy.sln
├── Ergonomy.csproj            legacy single-process app (migration source, still builds)
├── Ergonomy.Core/             library  — contracts + Named Pipe transport (no WinForms, no I/O deps)
│   ├── Configuration/         AppSettings, OutboxSettings          (moved)
│   ├── Contracts/             SyncRecord, UserActivityPayload      (moved)
│   ├── Logging/               ConsoleStructuredLogger, LogEvents   (moved)
│   └── Ipc/                   IpcConstants, IpcMessage, IpcContracts, IpcFraming,
│                              IpcSecurityFactory, IpcConnection,
│                              NamedPipeIpcServer, NamedPipeIpcClient
├── Ergonomy.Service/          exe (session 0) — hosts the pipe SERVER
└── Ergonomy.Task/             WinExe (user session) — hosts the pipe CLIENT
```

The legacy `Ergonomy.csproj` now references `Ergonomy.Core` and explicitly removes the three
new sub-folders from its default compile globs, so nothing is compiled twice and the existing
build keeps working while code is moved over.

## IPC: local Named Pipe only

**Hard constraint: no new TCP or UDP listening port may be introduced.** The only pre-existing
listener in the product is the Prometheus scrape endpoint (`Observability/MetricsEndpoint.cs`,
`HttpListener`), which belongs to the Service process and is *not* part of this channel.

* **Path:** `\\.\pipe\Ergonomy.Agent.v1` (version suffix — incompatible builds simply never pair).
* **Topology:** one duplex (`PipeDirection.InOut`) pipe. **Service = server**, up to 8 instances
  (one interactive Task process per logon session). **Task = client**, reconnects with
  exponential backoff (1s → 30s), so it may start before the Service and survives a Service restart.
* **Transmission:** byte mode + explicit 4-byte little-endian length prefix, max frame 256 KB.
  An explicit prefix (rather than message mode) keeps the protocol correct regardless of how the
  peer opened the pipe and makes a partial read impossible to misread as a complete message.
* **Envelope:** UTF-8 JSON `IpcMessage { Type, Id, CorrelationId, ProtocolVersion, TimestampUtc, payload }`.
  A message with a mismatched `ProtocolVersion` is dropped and logged; an unknown `Type` is logged
  and ignored (forward compatible — never fatal to the connection).

### ACL

Set at creation time via `NamedPipeServerStreamAcl.Create` (`SetAccessControl` on a
service-owned pipe throws `UnauthorizedAccessException` on .NET):

| Trustee | Rights |
| --- | --- |
| `LocalSystem` | FullControl |
| `BUILTIN\Administrators` | FullControl |
| `Authenticated Users` | `ReadWrite \| Synchronize` (no `CreateNewInstance` — a non-admin process can never spoof the server end) |
| `Anonymous Logon`, `NETWORK` | explicit **Deny** |

### Message catalogue

| Type | Direction | Payload |
| --- | --- | --- |
| `task.hello` | Task → Service | `TaskHelloPayload` (pid, Windows session id, SID, user, version) |
| `service.hello.ack` | Service → Task | `HelloAckPayload` |
| `task.heartbeat` | Task → Service | `HeartbeatPayload` (every 15s) |
| `task.activity` | Task → Service | `ActivityReportPayload` → becomes the `user_activity` outbox record |
| `service.alarm.show` | Service → Task | `ShowAlarmPayload` (kind, local image path, auto-close) |
| `task.alarm.ack` | Task → Service | `AlarmAckPayload` |
| `service.settings` | Service → Task | `SettingsSnapshotPayload` (pushed on connect and on every refresh) |
| `service.collection.start` / `.stop` | Service → Task | none |
| `service.shutdown` | Service → Task | `ShutdownRequestPayload` |
| `task.goodbye` | Task → Service | `GoodbyePayload` |

Images are **never** transferred over the pipe: the Service downloads them and sends a local path.

## Threading contract

* `Ergonomy.Task` keeps the WinForms STA message loop as the process pump. Pipe callbacks arrive
  on thread-pool threads and are marshalled to the UI thread through a hidden anchor `Control`
  (`TaskApplicationContext.MarshalToUi`) — the same lesson as the single-process fix: a WinForms
  timer or form touched from a pool thread silently never runs.
* `Ergonomy.Service` runs on the .NET Generic Host with `UseWindowsService`. Under the SCM,
  `WindowsServiceLifetime` handles start/stop/shutdown control codes; launched interactively,
  `ConsoleLifetime` takes over and Ctrl+C fires the same `ApplicationStopping` token. The Named
  Pipe server is started by `IpcHostedService.StartAsync` and stopped by `StopAsync` — no
  custom message pump or signal handler is needed.
* Sends never block a caller: `NamedPipeIpcClient.TrySendAsync` returns `false` when disconnected
  instead of waiting, and each connection serialises writes through a semaphore.

## Migration plan

**Step 1 — done (this change).** Three projects created and added to the solution; shared
contracts (`AppSettings`, `OutboxSettings`, `SyncRecord`/`UserActivityPayload`, `LogEvents`,
`ConsoleStructuredLogger`) moved into `Ergonomy.Core`; full Named Pipe transport, contracts,
ACL, server/client and both entry points implemented; `Ergonomy.Service` wired to Generic Host
with `UseWindowsService` (SCM + interactive console dual-mode); the legacy project keeps
building against `Ergonomy.Core`.

**Step 2 — move the desktop half.** `Hooks/GlobalInputHook.cs`, `Hooks/ActivityMonitor.cs`,
`AlarmManager.cs`, `UI/*` and the alarm-side of `ErgonomyManager.cs` move into `Ergonomy.Task`;
`TaskApplicationContext.ShowAlarm` replaces its TODO with the real forms and
`ReportActivityAsync` is called from the threshold path instead of writing to SQLite directly.

**Step 3 — move the machine half.** `LocalDatabaseManager`, `SyncEngine`, `KafkaConnect`,
`AdvancedMetricsCollector`, `HealthCheckService`, `CommandManager`, `SettingsService` and the
workers move into `Ergonomy.Service`, wired to `ServiceIpcHost.ActivityReceived` /
`SettingsSnapshotProvider`. `Ergonomy.csproj` is then deleted and `Ergonomy.sln` keeps three
projects.

Deployment (after step 3): `Ergonomy.Service` installed as a Windows service (LocalSystem,
auto-start, restart-on-failure); `Ergonomy.Task` started by a Task Scheduler logon trigger for
`INTERACTIVE`, single-instance per session via the `Local\Ergonomy.Task.SingleInstance.v1` mutex.

## Windows Service deployment (Ergonomy.Service)

`Ergonomy.Service` uses `Microsoft.Extensions.Hosting.WindowsServices` with
`UseWindowsService`. The same binary works in two modes:

| Mode | How to launch | Lifetime | Logging |
|---|---|---|---|
| **SCM** | `sc.exe start Ergonomy.Service` | `WindowsServiceLifetime` — handles SCM control codes | EventLog (auto) + any configured providers |
| **Interactive** | `Ergonomy.Service.exe` or `Ergonomy.Service.exe --console` | `ConsoleLifetime` — Ctrl+C / SIGTERM stops | Console + `ConsoleStructuredLogProvider` |

The mode is detected automatically: `Environment.UserInteractive == false` or the parent process
is `services.exe` → SCM mode. Pass `--console` to force interactive mode even when the binary
is installed as a service (useful for local debugging).

### sc.exe commands (run from an elevated command prompt)

```bat
:: ---- Install ----
:: Create the service (LocalSystem, auto-start, restart on failure).
sc.exe create Ergonomy.Service ^
    binPath= "C:\Program Files\Ergonomy\Ergonomy.Service.exe" ^
    start= auto ^
    DisplayName= "Ergonomy Agent Service" ^
    obj= LocalSystem

:: Describe the service.
sc.exe description Ergonomy.Service ^
    "Machine-bound agent: SQLite outbox, Kafka sync, advanced metrics, settings refresh, health checks. Hosts the Named Pipe server for the interactive Task process."

:: Configure restart-on-failure: restart after 10s, 30s, 60s; reset the failure counter after 1 day.
sc.exe failure Ergonomy.Service reset= 86400 actions= restart/10000/restart/30000/restart/60000

:: Grant the service account the right to log on as a service (usually already set for LocalSystem).
:: (Not required for LocalSystem; shown here for a custom gMSA account if needed.)
:: ntrights.exe +r SeServiceLogonRight -u "NT AUTHORITY\LocalService"

:: Start the service.
sc.exe start Ergonomy.Service

:: Verify status.
sc.exe query Ergonomy.Service

:: ---- Stop ----
sc.exe stop Ergonomy.Service

:: ---- Remove ----
:: Must be stopped first.
sc.exe delete Ergonomy.Service
```

### Updating the service binary

```bat
sc.exe stop Ergonomy.Service
:: Copy the new build output over the existing files.
xcopy /E /Y "C:\build\Ergonomy.Service\*" "C:\Program Files\Ergonomy\"
sc.exe start Ergonomy.Service
```

### Interactive debugging

```bat
:: Run the installed binary by hand (same code path, ConsoleLifetime, Ctrl+C to stop).
"C:\Program Files\Ergonomy\Ergonomy.Service.exe" --console

:: Or run from the build output during development.
dotnet run --project Ergonomy.Service -- --console
```

## Build

On a Windows/.NET 9 host:

```
dotnet restore Ergonomy.sln
dotnet build Ergonomy.sln -c Release -p:EnableWindowsTargeting=true
```

`Ergonomy.Core` and `Ergonomy.Task` add **no new NuGet dependency** beyond
`Microsoft.Extensions.DependencyInjection/Logging(.Abstractions) 9.0.0`. `Ergonomy.Service`
adds `Microsoft.Extensions.Hosting.WindowsServices 9.0.0` (which transitively brings
`Microsoft.Extensions.Hosting` and the EventLog provider). `NamedPipeServerStreamAcl` ships in
the shared framework for `net9.0-windows` (assembly `System.IO.Pipes.AccessControl.dll`) — no
package reference required.
