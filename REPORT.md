# Ergonomy — Investigation & Fix Report

## Repository and Compile Truth

**Active project file:** `Ergonomy.csproj` (single-project solution `Ergonomy.sln`).
Target framework: `net9.0-windows`, `OutputType=Exe`, `UseWindowsForms=true`,
`Nullable=enable`, `AllowUnsafeBlocks=true`.

**Active compiled ergonomics source files:**
- `Hooks/GlobalInputHook.cs` (low-level WH_KEYBOARD_LL / WH_MOUSE_LL hooks on
  dedicated `Ergonomy-InputHook` thread)
- `Hooks/ActivityMonitor.cs` (1s sampling / accumulation)
- `ErgonomyManager.cs` (lifecycle, payload, threshold/notification)
- `AlarmManager.cs` (alarm counters, image selection, SessionCloseLimit)
- `UI/PrimaryAlarmForm.cs`, `UI/SecondaryAlarmForm.cs`, `UI/MessageAlarmForm.cs`
- `MainApplicationContext.cs` (host, permission evaluation, timers, sync)

**Duplicate / legacy findings:** `UI/MessageAlarmForm.cs` is live but only used
by `CommandManager` (separate STA thread + own `Application.Run`); it is not part
of the ergonomics alarm path. No other colliding/duplicate source files exist.

**Baseline build:** I could not run a baseline build. This sandbox has **no .NET
SDK**, **no NuGet package cache**, and **no outbound network** (verified: `which
dotnet` empty, `~/.nuget/packages` empty, HTTPS/HTTP to nuget/dotnet/debian all
fail). The committed `obj/project.assets.json` was generated on a Windows machine
(`C:\Users\s.moghadarii\.nuget\packages`). **Exact build blocker listed here** —
the commands below are the correct ones to run on a Windows/NET9 host:
- `dotnet build --no-restore -p:EnableWindowsTargeting=true`
- `dotnet build -c Release --no-restore -p:EnableWindowsTargeting=true`

## Root Cause

**Primary root cause of "Permission True but ergonomics inactive":**

`MainApplicationContext` runs the initial settings refresh synchronously before
the WinForms message loop starts:

```
MainApplicationContext() line 140
    UpdateSettingsFromApiAsync(...).GetAwaiter().GetResult();
```

At this point `SynchronizationContext.Current` is `null`. This is *proven* by the
fact that the blocking `.GetAwaiter().GetResult()` does **not** deadlock (it would
deadlock if a `WindowsFormsSynchronizationContext` were installed, because the
continuation would be posted to a not-yet-running UI message pump). Because the
context is null, the continuation of `UpdateSettingsFromApiAsync` — and therefore
`ReconfigureRuntimeBasedOnSettings()` → `EvaluateErgonomyPermission()` →
`ErgonomyManager.Start()` — executes on a **thread-pool thread**.

`ErgonomyManager.Start()` (old code) created the notification feedback as a
**`System.Windows.Forms.Timer`** on that thread-pool thread. A WinForms `Timer`
never raises `Tick` without a UI message pump, so the threshold was **never
evaluated** → no alarm, no image, no `Update` state. Only the one-time
`LogSessionState("Start")` payload was produced (delivered to Kafka with zeros,
which is *valid* at session start).

**Source references:**
- `MainApplicationContext.cs`: constructor L140 (blocking settings refresh),
  L207+ (`UpdateSettingsFromApiAsync`), L149/L284 (permission evaluation).
- `ErgonomyManager.cs` (pre-fix): `_notificationTimer = new System.Windows.Forms.Timer()`
  and `.Tick += OnNotificationTimerTick` (the "tick never fires" path).
- The `Start` payload path is `ErgonomyManager.LogSessionState("Start")` — this
  is the only record before interaction, hence the zero Start message.

**Root cause of the earlier severe lag:** handled by the prior dedicated-thread
hook design (callbacks only do atomic `Interlocked` increments + `CallNextHookEx`).
This is preserved; my fix does **not** move any work into the hook callback.

## Changes Applied

- `ErgonomyManager.cs` — **core fix.** Notification feedback now uses a
  thread-safe `System.Timers.Timer` (`_notificationTimer`, L23, L112) so it ticks
  regardless of which thread `Start()` runs on. `OnNotificationTimerElapsed`
  (L164) evaluates the threshold and marshals the actual alarm/UI work to the UI
  thread via `_uiAnchor.BeginInvoke((Action)HandleThresholdReached)` (L189).
  `HandleThresholdReached` (L208) runs on the UI thread, calls
  `ShowPrimaryAlarm()`, `LogSessionState("Update")`, then `ResetTotals()`.
  Added `UpdateSettings` (L62) to refresh the manager's settings reference after
  API refresh; added lifecycle/observability logs; `LogSessionState` now queues
  the SQLite write off the UI thread.
- `MainApplicationContext.cs` — creates a hidden `_uiAnchor` `Control` (L34, L73)
  used to marshal UI work; resets `SynchronizationContext` to null (L84) so the
  blocking startup refresh does not deadlock (`Application.Run` reinstalls it);
  passes `_uiAnchor` into `ErgonomyManager` (L106); calls
  `_ergonomyManager.UpdateSettings(_appSettings)` + `SettingsSourceIsApi` (L275/
  L277); richer `EvaluateErgonomyPermission` logs (L585+); disposes the anchor.
- `AlarmManager.cs` — made thread-safe with a lock; added `UpdateSettings`;
  logs image-selected vs no-image fallback; avoids an orphaned `IsAlarmActive=true`
  when the session-close limit is already reached.
- `Hooks/GlobalInputHook.cs` — added lifecycle logs (hook thread started,
  keyboard/mouse hook handles, message-loop active/exited), a startup-wait
  timeout, and actionable Win32 error output.
- `Hooks/ActivityMonitor.cs` — added `Activity monitor sampling started` log and
  throttled sample logs (first input detected + max one sample every 30s).
- `Logging/DataLogger.cs` — added `UpdateSettings` so the hourly Excel logger
  follows settings changes.
- `knowledge.txt` — created (repository root), as required.

## Configuration Flow Verified

- `EnvironmentSettingsProvider.Load()` reads machine env vars → `AppSettings`
  (bootstrap). `MainApplicationContext.PreserveEnvironmentInfrastructureSettings`
  copies `API.*` and `Kafka.*` back from bootstrap into any API-refreshed
  settings (so the API **cannot** override endpoints/topics).
- `UpdateSettingsFromApiAsync` replaces `_appSettings` only when the API JSON
  differs; then `ReconfigureRuntimeBasedOnSettings` → `EvaluateErgonomyPermission`
  starts/stops the manager.
- **Effective values (documented):** `AllowErgonomyCollection=true`,
  `NotificationIntervalSeconds=5`, `ActivityThresholdSeconds=5`
  (`LogEffectiveSettings` prints these + `Source=API` vs `Bootstrap/Environment`).
- **Topic name resolution:** Kafka topics always come from bootstrap env
  (`PreserveEnvironmentInfrastructureSettings`). `ERGONOMY_KAFKA_APP_LOGS_TOPIC`
  maps to `AppLogsTopic` with default `"app_logs"`. The runtime log showed
  `app_logs`, which means that machine env var was **not** set → the default was
  used. The prompt's listed value `app_logs_topic` was never applied on that
  machine. This is a precedence/fallback fact, not a code defect; **not changed**
  because switching topics could break the existing ClickHouse pipeline.

## Runtime Execution Chain (evidence per stage)

1. **permission** → `[Ergonomy Status] Permission: True` + `[Ergonomy] Ergonomy
   permission is true; ensuring manager is started.`
2. **manager start** → `[Ergonomy] ErgonomyManager created.` +
   `[Ergonomy] Manager started. Notification timer started ...`
3. **hooks** → `[Ergonomy] Input hook thread started. ThreadId=...` +
   `[Ergonomy] Keyboard hook installed. Handle=...` +
   `[Ergonomy] Mouse hook installed. Handle=...` +
   `[Ergonomy] Input hook message loop active.`
4. **snapshot** → hook callbacks only do atomic `Interlocked` increments
   (`GlobalInputHook.KeyboardHookCallback`/`MouseHookCallback`); `ConsumeSnapshot`
   uses `Interlocked.Exchange`.
5. **monitor** → `[Ergonomy] Activity monitor sampling started. Interval=1000ms`;
   throttled `[Ergonomy] First input detected. ...` / `[Ergonomy] Sample: ...`
6. **threshold** → `[Ergonomy] Threshold reached (N.Ns >= 5s). Posting alarm to
   UI thread.`
7. **UI dispatch** → `_uiAnchor.BeginInvoke(HandleThresholdReached)` runs on the
   WinForms UI thread.
8. **alarm UI** → `[Ergonomy] Image selected ...` / `No alarm image available...
   showing alarm without image.` then `[Ergonomy] Primary alarm shown on UI thread.`
9. **outbox** → `LogSessionState("Update")` queues a `user_activity` payload to
   the SQLite outbox off the UI thread.
10. **Kafka / ClickHouse** → `SyncEngine` → `KafkaConnect.SendUserActivityAsync`
    → topic `user_activity_topic` → ClickHouse (previously verified path).

## Payload Semantics

- A `StateType="Start"` record is emitted the moment the session starts and **may
  legitimately have zero activity** because no input has been observed yet.
- The record that proves real activity is the **`StateType="Update"`** payload,
  emitted by `HandleThresholdReached` when `TotalKeyboard+TotalMouseActiveTime`
  reaches `ActivityThresholdSeconds`. It carries the accumulated values captured
  *before* `ResetTotals()`.
- **Field mapping verified (no mismatch):** `UserActivityPayload.KeyboardActiveSeconds`
  ← `ActivityMonitor.TotalKeyboardActiveTime.TotalSeconds`; `MouseActiveSeconds` ←
  `TotalMouseActiveTime.TotalSeconds`; `TotalActiveSeconds` = sum; `SessionCloseCounter`
  / `PrimaryAlarmCount` / `SecondaryAlarmCount` ← `AlarmManager` getters.
- `ResetTotals()` is called **only** after the alarm request and `Update` payload
  capture, so no reset erases data before it is consumed.

## Validation

- Build: **not executed in this sandbox** — see "Exact build blocker" above
  (no .NET SDK / NuGet cache / network). The provided commands must be run on a
  Windows/.NET 9 host. I do **not** claim a successful build here.
- Static validation performed: thorough source read of every modified file;
  brace/paren balance check (all six files balanced); method/type existence and
  signature review; cross-thread UI analysis.
- Tests: none exist; I did **not** add a test project because it could not be
  compiled/run in this sandbox and would introduce risk without verification.
- Runtime checks: **not performed** (requires a real Windows interactive desktop;
  see checklist below).

## Remaining Risks

- `DataLogger` writes an hourly Excel file; if the destination path is
  unwritable the log is silently skipped (pre-existing, unrelated to alarm path).
- `MessageAlarmForm` / `CommandManager` scheduled-shutdown paths are legacy and
  unchanged.
- If the UI-anchor `Control` cannot be created (rare), `BeginInvoke` is skipped
  and the alarm would attempt to show in the timer thread's context (logged as an
  error, not a crash). The normal path uses the UI thread.
- Repo `Environment_set.txt` sample is inconsistent with the production machine
  (false vs true, omits app-logs topic); documented, not modified.

## Manual Verification Checklist (Windows interactive desktop)

1. Set env `ERGONOMY_ALLOW_ERGONOMY_COLLECTION=true` (and the rest per the prompt),
   restart the app/service.
2. Confirm `[Ergonomy] Effective settings: Allow=True, NotificationIntervalSeconds=5,
   ActivityThresholdSeconds=5, Source=...`.
3. Confirm both `Keyboard hook installed` / `Mouse hook installed` and
   `Input hook message loop active`.
4. Confirm `Activity monitor sampling started. Interval=1000ms`.
5. Move/type for 10–20s → confirm throttled `First input detected` / `Sample:` lines
   show **non-zero** `keyboardEvents`/`mouseMoves`/`keyboardActive`/`mouseActive`.
6. Confirm `[Ergonomy] Threshold reached (N.Ns >= 5s). Posting alarm to UI thread.`
7. Confirm `Primary alarm shown on UI thread` and that an API-loaded image is
   displayed (or `No alarm image available` fallback is logged).
8. Confirm the UI stays responsive and the cursor stays smooth (no lag).
9. Confirm a subsequent `user_activity_topic` record has
   `StateType="Update"` with **non-zero** `KeyboardActiveSeconds`/`MouseActiveSeconds`.
10. Inspect the SQLite outbox record before sync; verify delivery to Kafka and
    ClickHouse.
11. Change API settings `AllowErgonomyCollection` true→false → confirm hooks/timers
    stop (`Manager stopped because: AllowErgonomyCollection is false`); then
    false→true → confirm it resumes **without** restarting the app.
