# ClickHouse DDL audit — Kafka engine / MV repair

Date: 2026-09-12  
Scope: `ClickHouse/*.sql` only. No live `CLICKHOUSE` statements were executed.

## 1. Inventory

| File | Statements | Classification | Cross-refs |
| --- | --- | --- | --- |
| `CreateKafkaAppLogs.sql` | DROP VIEW, DROP TABLE, CREATE TABLE | **KAFKA_ENGINE** `Kafka_AppLogs` | topic `app_logs` → MV `MV_AppLogs_To_Target` |
| `CreateKafkaUserActivities.sql` | DROP VIEW, DROP TABLE, CREATE TABLE | **KAFKA_ENGINE** `Kafka_UserActivities` | topic `user_activity_topic` → MV `MV_UserActivities_To_Target` |
| `CreateKafkaSystemMetrics.sql` | CREATE TABLE `SystemMetrics` `ReplacingMergeTree` | **TARGET (misnamed)** | Not a Kafka engine. Duplicate/alternate of `CreateSystemMetrics.sql` |
| `CreateAppLogs.sql` | CREATE TABLE | **TARGET** `AppLogs` MergeTree | Fed by `MV_AppLogs_To_Target` |
| `CreateUserActivities.sql` | CREATE TABLE | **TARGET** `UserActivities` MergeTree | Fed by `MV_UserActivities_To_Target` |
| `CreateSystemMetrics.sql` | CREATE TABLE | **TARGET** `SystemMetrics` MergeTree | Intended sink of `MV_SystemMetrics_To_Target` |
| `CreateMaterializedViewAppLoggs.sql` | DROP VIEW, CREATE MV | **MV** `Monitoring.MV_AppLogs_To_Target` | `Kafka_AppLogs` → `AppLogs` |
| `CreateMaterializedViewUserActivities.sql` | DROP VIEW, CREATE MV | **MV** `MV_UserActivities_To_Target` | `Kafka_UserActivities` → `UserActivities` |
| `CreateMaterializedViewSystemMetrics.sql` | DROP VIEW, CREATE MV | **MV** `MV_SystemMetrics_To_Target` | `Kafka_SystemMetrics` → `SystemMetrics` (**source table missing**) |

No `ALTER` / `TRUNCATE` statements exist in this directory.

Suggested apply order (targets first, never drop them):

1. `CreateAppLogs.sql`, `CreateUserActivities.sql`, `CreateSystemMetrics.sql`
2. `CreateKafkaAppLogs.sql`, `CreateKafkaUserActivities.sql`
3. Materialized-view scripts

## 2. Files changed

### `CreateKafkaAppLogs.sql`

- Added `DROP VIEW IF EXISTS` for `MV_AppLogs_To_Target` / `Monitoring.MV_AppLogs_To_Target` then `DROP TABLE IF EXISTS` Kafka table (dependency order).
- `kafka_broker_list` already `'kafka:9092'` — unchanged.
- Settings already had `kafka_format = 'JSONEachRow'`, `kafka_group_name = 'clickhouse_applogs_group'`, `kafka_skip_broken_messages = 1000` — kept (group name not renamed).
- Reformatted SETTINGS onto separate lines.

### `CreateKafkaUserActivities.sql`

- Added `DROP VIEW IF EXISTS MV_UserActivities_To_Target` then `DROP TABLE IF EXISTS Kafka_UserActivities`.
- `kafka_broker_list` already `'kafka:9092'` — unchanged.
- **Added** `kafka_skip_broken_messages = 1000` (was missing).
- Kept `kafka_group_name = 'clickhouse_group_users'` and `kafka_format = 'JSONEachRow'`.
- Preserved Persian comment on `Timestamp_Shamsi`.

### `CreateMaterializedViewAppLoggs.sql`

- Added `DROP VIEW IF EXISTS` (unqualified + `Monitoring.`) before create.
- Added `IF NOT EXISTS` on `CREATE MATERIALIZED VIEW`.
- SELECT list unchanged (schema mismatch flagged below).

### `CreateMaterializedViewUserActivities.sql`

- Added `DROP VIEW IF EXISTS MV_UserActivities_To_Target`.
- SELECT list and Persian comment unchanged.

### `CreateMaterializedViewSystemMetrics.sql`

- Added `DROP VIEW IF EXISTS MV_SystemMetrics_To_Target`.
- Removed developer note `-- ← فقط همین خط اضافه/تغییر می‌شود` (not schema intent).
- SELECT list unchanged (missing Kafka source flagged below).

### `CreateAppLogs.sql`

- `CREATE TABLE AppLogs` → `CREATE TABLE IF NOT EXISTS AppLogs`.
- Engine, columns, `ORDER BY` / `PARTITION BY` untouched.

## 3. Files unchanged and why

| File | Why |
| --- | --- |
| `CreateUserActivities.sql` | TARGET MergeTree; already `IF NOT EXISTS`; no Kafka settings; column/`ORDER BY` changes are out of scope. |
| `CreateSystemMetrics.sql` | TARGET MergeTree; already `IF NOT EXISTS`; must not drop or alter. |
| `CreateKafkaSystemMetrics.sql` | **Not a Kafka engine table.** It creates TARGET `SystemMetrics` (`ReplacingMergeTree`, includes `MessageId`). Converting it to `ENGINE = Kafka()` would invent `kafka_topic_list` / `kafka_group_name` (forbidden) and would replace a target definition. Left unmodified. |

Zero occurrences of `kafka:9094` or `172.17.214.38:9094` were present before or after the edit.

## 4. Flagged risks

| Severity | File | Issue |
| --- | --- | --- |
| **High** | `CreateKafkaSystemMetrics.sql` (entire file) vs `CreateMaterializedViewSystemMetrics.sql:34` | MV reads `FROM Kafka_SystemMetrics` but **no script creates** `Kafka_SystemMetrics`. The file named `CreateKafkaSystemMetrics.sql` actually defines TARGET `SystemMetrics ENGINE = ReplacingMergeTree()`. Topic name is not specified anywhere in these scripts, so the Kafka table was **not invented**. System-metrics ingestion via MV cannot start until a real Kafka engine table is added in a follow-up (with an agreed topic, e.g. agent `system_metrics` vs Control API `advanced_system_metrics_topic`). |
| **High** | `CreateSystemMetrics.sql` vs `CreateKafkaSystemMetrics.sql` | Two competing TARGETs named `SystemMetrics`: MergeTree without `MessageId` vs ReplacingMergeTree with `MessageId`. `CREATE IF NOT EXISTS` means whichever runs first wins. MV `SELECT _key AS MessageId` does not match `CreateSystemMetrics.sql` (no `MessageId` column). |
| **High** | `CreateMaterializedViewAppLoggs.sql:8-12` vs `CreateKafkaAppLogs.sql` | MV selects `CollectedAt` (`toDateTime64(CollectedAt, 3)` / `toDateTime(CollectedAt)`). Kafka table **has no `CollectedAt` column** (only `Timestamp String`). Agent `app_logs` JSON *does* send `CollectedAt`. Out of scope to add columns; MV will fail or yield default/NULL for that expression until the Kafka table is aligned. |
| **Medium** | `CreateKafkaUserActivities.sql` `SessionId UUID` | Agent JSON typically sends a hex GUID string. ClickHouse JSONEachRow can parse UUID strings; non-UUID values become skip-broken (now 1000). |
| **Medium** | `CreateMaterializedViewSystemMetrics.sql` `parseDateTime64BestEffortOrZero(BootTime, 7) AS BootTime` | Both TARGET definitions store `BootTime` as **String**. Inserting DateTime64 into String may coerce or fail depending on CH version. |
| **Low** | `CreateMaterializedViewAppLoggs.sql` | Filename typo `AppLoggs`. MV is created in database `Monitoring.` while other objects are unqualified. |
| **Low** | Kafka scripts DROP VIEW | Re-running a Kafka script alone drops the MV until the matching MV script is re-applied. Run Kafka scripts then MV scripts. |

No target-table `DROP`/`TRUNCATE` was present; none were added.

## 5. Final assertions

| Check | Result |
| --- | --- |
| Zero `'9094'` inside any Kafka engine SETTINGS block | **PASS** (zero `'9094'` anywhere under `ClickHouse/`) |
| Every **actual** `ENGINE = Kafka()` table has `kafka_broker_list`, `kafka_topic_list`, `kafka_group_name`, `kafka_format` | **PASS** (`Kafka_AppLogs`, `Kafka_UserActivities`; both `kafka:9092` + `JSONEachRow` + skip 1000) |
| `kafka_skip_broken_messages = 1000` on every Kafka engine table | **PASS** |
| Kafka group names unchanged | **PASS** (`clickhouse_applogs_group`, `clickhouse_group_users`) |
| MV SELECT columns exist on Kafka source (name presence) | **FAIL** — `MV_AppLogs_To_Target` references `CollectedAt` absent from `Kafka_AppLogs`; `MV_SystemMetrics_To_Target` references table `Kafka_SystemMetrics` which has **no CREATE** |
| No target table dropped/truncated | **PASS** |

Remaining assertion failures are schema gaps that require a new Kafka table / column additions — out of this repair’s scope.
