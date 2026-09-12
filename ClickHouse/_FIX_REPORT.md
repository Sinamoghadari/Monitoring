# ClickHouse DDL audit — Kafka engine / MV repair

Date: 2026-09-12 (pass 2)  
Scope: `ClickHouse/*.sql` only. No live statements were executed against ClickHouse.

## 1. Inventory

| File | Classification | Object | Feeds / source |
| --- | --- | --- | --- |
| `CreateAppLogs.sql` | TARGET MergeTree | `Monitoring.AppLogs` | sink of `MV_AppLogs_To_Target` |
| `CreateUserActivities.sql` | TARGET MergeTree | `Monitoring.UserActivities` | sink of `MV_UserActivities_To_Target` |
| `CreateSystemMetrics.sql` | TARGET MergeTree | `Monitoring.SystemMetrics` | sink of `MV_SystemMetrics_To_Target` |
| `CreateKafkaAppLogs.sql` | KAFKA_ENGINE | `Monitoring.Kafka_AppLogs` | topic `app_logs` |
| `CreateKafkaUserActivities.sql` | KAFKA_ENGINE | `Monitoring.Kafka_UserActivities` | topic `user_activity_topic` |
| `CreateKafkaSystemMetrics.sql` | KAFKA_ENGINE | `Monitoring.Kafka_SystemMetrics` | topic `system_metrics` |
| `CreateMaterializedViewAppLoggs.sql` | MV | `Monitoring.MV_AppLogs_To_Target` | `Kafka_AppLogs` → `AppLogs` |
| `CreateMaterializedViewUserActivities.sql` | MV | `Monitoring.MV_UserActivities_To_Target` | `Kafka_UserActivities` → `UserActivities` |
| `CreateMaterializedViewSystemMetrics.sql` | MV | `Monitoring.MV_SystemMetrics_To_Target` | `Kafka_SystemMetrics` → `SystemMetrics` |

No `ALTER` / `TRUNCATE` / target `DROP` statements.

Apply order:

1. Target scripts (`CreateAppLogs.sql`, `CreateUserActivities.sql`, `CreateSystemMetrics.sql`)
2. Kafka engine scripts
3. Materialized-view scripts

## 2. Files changed (this pass)

### `CreateKafkaSystemMetrics.sql` — **rewritten**

Previous content was TARGET `SystemMetrics ENGINE = ReplacingMergeTree()` (wrong file). Replaced with the missing Kafka engine table the MV already referenced:

- `CREATE TABLE IF NOT EXISTS Monitoring.Kafka_SystemMetrics`
- `ENGINE = Kafka()`
- `kafka_broker_list = 'kafka:9092'`
- `kafka_topic_list = 'system_metrics'` (agent / `Postgres/app_configuration.sql` default)
- `kafka_group_name = 'clickhouse_systemmetrics_group'` (new; no prior group existed)
- `kafka_format = 'JSONEachRow'`
- `kafka_skip_broken_messages = 1000`
- Columns = MV source fields (JSON types). `MessageId` is **not** a table column; Kafka message key remains virtual `_key`.
- DROP order: MV then Kafka table. **Does not drop** `SystemMetrics`.

### `CreateKafkaAppLogs.sql`

- Qualify as `Monitoring.Kafka_AppLogs` (fixes `UNKNOWN_TABLE Monitoring.Kafka_AppLogs`).
- Added `CollectedAt String` so the live MV `toDateTime(CollectedAt)` / `coalesce(..., CollectedAt)` resolves.
- Kept `Timestamp String` for the coalesce fallback.
- `CREATE DATABASE IF NOT EXISTS Monitoring`.
- Group name unchanged: `clickhouse_applogs_group`.

### `CreateKafkaUserActivities.sql`

- Qualify as `Monitoring.Kafka_UserActivities`.
- `CREATE DATABASE IF NOT EXISTS Monitoring`.
- Settings unchanged (`kafka:9092`, `user_activity_topic`, `clickhouse_group_users`, skip 1000).

### Materialized views

- All created as `Monitoring.MV_*` reading `Monitoring.Kafka_*` writing `Monitoring.*`.
- SystemMetrics MV: dropped `_key AS MessageId` and `parseDateTime64BestEffortOrZero(BootTime)` so the SELECT matches TARGET `CreateSystemMetrics.sql` (no `MessageId`, `BootTime String`).
- AppLogs MV SELECT unchanged (now valid against Kafka table that includes `CollectedAt`).

### Target scripts

- Prefixed `Monitoring.<table>` and `CREATE DATABASE IF NOT EXISTS Monitoring`.
- Columns, engines, `ORDER BY` / `PARTITION BY` unchanged. No drops.

## 3. Files unchanged (schema)

Target column lists / engines were not redesigned.

## 4. Flagged risks

| Severity | File | Issue |
| --- | --- | --- |
| Medium | `CreateKafkaUserActivities.sql` | `kafka_topic_list = 'user_activity_topic'` but agent default / PG config is `user_activity`. Existing CH topic name kept (do not rename). |
| Medium | `CreateKafkaUserActivities.sql` `SessionId UUID` | Non-UUID JSON values are skipped (`kafka_skip_broken_messages = 1000`). |
| Medium | Live cluster | If an earlier run created unqualified `default.Kafka_AppLogs` or `default.SystemMetrics`, those objects are **not** dropped. Create the `Monitoring.*` objects from these scripts. If live `SystemMetrics` still has `MessageId` from the old ReplacingMergeTree script, MV insert still works (extra target columns get defaults). |
| Low | Kafka scripts DROP VIEW | Re-run Kafka script then re-run the matching MV script. |

## 5. Assertions

| Check | Result |
| --- | --- |
| Zero `'9094'` in Kafka SETTINGS | **PASS** |
| Every Kafka table has broker, topic, group, format, skip 1000 | **PASS** (`Kafka_AppLogs`, `Kafka_UserActivities`, `Kafka_SystemMetrics`) |
| Broker is `kafka:9092` | **PASS** |
| MV sources exist by name | **PASS** (`Monitoring.Kafka_AppLogs`, `Monitoring.Kafka_UserActivities`, `Monitoring.Kafka_SystemMetrics`) |
| MV SELECT columns present on Kafka source | **PASS** (AppLogs now includes `CollectedAt`; SystemMetrics MV no longer selects missing `MessageId`) |
| No target DROP/TRUNCATE | **PASS** |

## 6. Live error mapping

`UNKNOWN_TABLE Monitoring.Kafka_AppLogs` while creating `MV_AppLogs_To_Target`: run `CreateKafkaAppLogs.sql` **before** the MV (creates `Monitoring.Kafka_AppLogs`). Then run `CreateMaterializedViewAppLoggs.sql`.

`Kafka_SystemMetrics` missing: run rewritten `CreateKafkaSystemMetrics.sql`, then `CreateMaterializedViewSystemMetrics.sql`.
