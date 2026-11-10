# ClickHouse Kafka ingestion pipeline v5

Database: `Monitoring`  
Broker (ClickHouse Kafka engine): `kafka:9092`  
Topics: `app_logs`, `advanced_system_metrics_topic`, `user_activity_topic`  
Format: `JSONEachRow`  
Consumer groups: `clickhouse_applogs_v5`, `clickhouse_systemmetrics_v5`, `clickhouse_useractivities_v5`

Live `DESCRIBE` of the cluster was not available in this workspace. Destination schemas below are taken from `ClickHouse/Create*.sql` in this repository. Kafka JSON fields are taken from the agent producers (`MessageLogService`, `UserActivityPayload`, `AdvancedMetricsCollector`). Run `00_inspect.sql` on the live server before applying `pipeline_v5.sql`.

---

## A. Root causes

The failing statement:

```sql
CREATE MATERIALIZED VIEW Monitoring.MV_AppLogs_To_Target
TO Monitoring.AppLogs
AS
SELECT
ifNull(
parseDateTime64BestEffortOrNull(Timestamp, 7),
ifNullortOrNull(Timestamp, 7),
ifNullCollectedAt, 7),
now64(7)
)
) AS Timestamp,
...
FROM Monitoring.Kafka_AppLogs;
```

### Syntax errors

1. **`ifNullortOrNull(...)` is not a ClickHouse function.** This is a corrupted splice of `ifNull` + `parseDateTime64BestEffortOrNull`. The parser fails here.
2. **`ifNullCollectedAt` is not a function or identifier.** It looks like `ifNull(` concatenated with `CollectedAt` with the opening parenthesis lost.
3. **`ifNull` takes exactly two arguments:** `ifNull(x, default)`. The outer `ifNull(` is given a `parseDateTime64BestEffortOrNull` result, then another broken call, then `ifNullCollectedAt, 7)`, then `now64(7)` — that is 3+ arguments and nested garbage.
4. **Unbalanced parentheses.** Count of `(` vs `)` around the `Timestamp` expression does not match (extra `)` before `AS Timestamp`).
5. **`parseDateTimeBestEffortOrNull` does not take a scale argument.** Scale `7` is valid only on the `DateTime64` variants (`parseDateTime64BestEffortOrNull(s, 7)`). `parseDateTimeBestEffortOrNull(s)` or `(s, timezone)`.
6. **Materialized View SQL must not appear inside `CREATE TABLE`.** The statement above is a standalone MV (correct kind of statement) but earlier scripts mixed MV `SELECT` fragments into table DDL. That produces “expected ENGINE” / “unrecognized token” errors.

### Semantic errors

7. **`Monitoring.Kafka_AppLogs` missing** (`UNKNOWN_TABLE`). The MV was created before the Kafka engine table, or the Kafka table was created in `default` instead of `Monitoring`.
8. **Agent `app_logs` JSON has no `Timestamp` field.** Producer fields are `CollectedAt`, `CollectedAt_Shamsi`, `LogLevel`, `Message`, `WindowsUsername`, `WindowsSid`, `MachineName`, `ComputerName`, `WindowsUsername_RunAdmin`, `Category`. Using `toDateTime64(Timestamp, 3)` on a missing/empty column throws or yields epoch, and `toDateTime(CollectedAt)` on a bad string breaks the MV insert.
9. **`CreateKafkaSystemMetrics.sql` previously created destination `SystemMetrics ENGINE = ReplacingMergeTree`**, not `ENGINE = Kafka()`. There was no `Kafka_SystemMetrics` table. Topic was also wrong (`system_metrics` vs required `advanced_system_metrics_topic`).
10. **User-activity JSON has `CollectedAt_Shamsi`, destination has `Timestamp_Shamsi`.** A passthrough `Timestamp_Shamsi` from Kafka is empty. Map `CollectedAt_Shamsi AS Timestamp_Shamsi`.
11. **`SessionId` is a JSON string**, sometimes dashed (`Guid.ToString()`) and sometimes `N` format (`Guid.ToString("N")`). Declaring the Kafka column as `UUID` skips those rows via `kafka_skip_broken_messages`.
12. **Numeric metrics are sparse.** `EnabledMetrics` omits keys. Kafka `UInt64`/`Float64` columns then default to 0 or reject the row. Staging as `String` + `to*OrZero` in the MV is the compatible conversion.

---

## B. Inspection commands

### ClickHouse (read-only)

Run `ClickHouse/00_inspect.sql`, or:

```sql
SELECT database, name, engine, total_rows, create_table_query
FROM system.tables
WHERE database = 'Monitoring'
ORDER BY engine, name;

SELECT table, position, name, type
FROM system.columns
WHERE database = 'Monitoring'
ORDER BY table, position;

SELECT name, engine, engine_full
FROM system.tables
WHERE database = 'Monitoring' AND engine = 'Kafka';

SELECT name, as_select
FROM system.tables
WHERE database = 'Monitoring' AND engine = 'MaterializedView';
```

Required output before treating `pipeline_v5.sql` as production-final:

- `create_table_query` for `AppLogs`, `SystemMetrics`, `UserActivities`
- confirmation that destination column names/types match the `CREATE TABLE IF NOT EXISTS` blocks
- if they differ, **stop** and paste `DESCRIBE TABLE Monitoring.<name>` — do not apply the MV until the SELECT list is aligned

### Kafka (from a broker/tools container on `my-network`)

```bash
# topics
kafka-topics.sh --bootstrap-server kafka:9092 --list

kafka-topics.sh --bootstrap-server kafka:9092 --describe --topic app_logs
kafka-topics.sh --bootstrap-server kafka:9092 --describe --topic advanced_system_metrics_topic
kafka-topics.sh --bootstrap-server kafka:9092 --describe --topic user_activity_topic

# sample messages (JSONEachRow expected)
kafka-console-consumer.sh --bootstrap-server kafka:9092 \
  --topic app_logs --from-beginning --max-messages 3 --timeout-ms 10000

kafka-console-consumer.sh --bootstrap-server kafka:9092 \
  --topic advanced_system_metrics_topic --from-beginning --max-messages 1 --timeout-ms 10000

kafka-console-consumer.sh --bootstrap-server kafka:9092 \
  --topic user_activity_topic --from-beginning --max-messages 3 --timeout-ms 10000

# consumer groups / lag (after v5 Kafka tables exist)
kafka-consumer-groups.sh --bootstrap-server kafka:9092 --list
kafka-consumer-groups.sh --bootstrap-server kafka:9092 --describe --group clickhouse_applogs_v5
kafka-consumer-groups.sh --bootstrap-server kafka:9092 --describe --group clickhouse_systemmetrics_v5
kafka-consumer-groups.sh --bootstrap-server kafka:9092 --describe --group clickhouse_useractivities_v5
```

**ASSUMPTION to verify from those samples:** payloads are JSON objects (not Avro / CSV / JSON arrays). If a sample is not a JSON object, do not use `JSONEachRow`.

---

## C. Safe migration plan

1. Run inspection (section B). Compare destination columns to the scripts. Do not continue if names/types diverge.
2. Destination MergeTree tables stay. `CREATE TABLE IF NOT EXISTS` only. **No `DROP` / `TRUNCATE` of `AppLogs`, `SystemMetrics`, `UserActivities`.**
3. **DESTRUCTIVE (ingestion path only):** `DROP VIEW` MVs, then `DROP TABLE` Kafka engine tables. This does not delete MergeTree rows. It does reset ClickHouse’s Kafka consumer to the new `*_v5` groups, which replay retained topic offsets.
4. `CREATE` Kafka engine tables (`kafka:9092`, exact topic names, `JSONEachRow`, skip 1000).
5. `CREATE` MVs whose SELECT output matches destination columns and types.
6. Run `01_validate.sql`. Confirm `system.kafka_consumers` is consuming and destination `max(timestamp)` moves forward.
7. Do **not** attach two MVs to the same destination at once (duplicate rows).

Optional shadow validation (no duplicates on production dest):

```sql
CREATE TABLE Monitoring.AppLogs_v5_shadow AS Monitoring.AppLogs;
-- then CREATE MV ... TO Monitoring.AppLogs_v5_shadow
-- compare counts; DROP VIEW shadow MV; DROP TABLE shadow; then cut over
```

### Full destination reset (only if you explicitly want empty tables)

**DESTRUCTIVE. Deletes all ClickHouse rows in these three tables. Kafka topic data is not deleted; `*_v5` groups will re-ingest whatever Kafka still retains.**

```sql
TRUNCATE TABLE Monitoring.AppLogs;
TRUNCATE TABLE Monitoring.SystemMetrics;
TRUNCATE TABLE Monitoring.UserActivities;
```

Do not run that unless historical CH rows are disposable.

---

## D. Correct SQL scripts

Executable file: `ClickHouse/pipeline_v5.sql`  
Per-object files: `CreateAppLogs.sql`, `CreateSystemMetrics.sql`, `CreateUserActivities.sql`, `CreateKafka*.sql`, `CreateMaterializedView*.sql`

Apply order: destinations (already in `pipeline_v5.sql`) → Kafka tables → MVs.

### Field mapping (agent JSON → destination)

| Pipeline | Kafka topic | JSON fields used | Destination |
| --- | --- | --- | --- |
| App logs | `app_logs` | `CollectedAt` (no `Timestamp` in producer), `CollectedAt_Shamsi`, `LogLevel`, `Message`, `WindowsUsername`, `MachineName`, `Category`. Extra JSON `WindowsSid`, `ComputerName`, `WindowsUsername_RunAdmin` ignored. | `Monitoring.AppLogs` |
| System metrics | `advanced_system_metrics_topic` | Always: `CollectedAt`, `CollectedAt_Shamsi`, `ComputerName`, `WindowsSid`, `WindowsUsername`, `WindowsUsername_RunAdmin`. Optional per `EnabledMetrics`: RAM/CPU/JSON blobs listed on dest. **Not produced:** `ChromeHistoryJson` (dest filled with `''`). **Not on dest:** `GpuDetailsJson` (ignored). | `Monitoring.SystemMetrics` |
| User activity | `user_activity_topic` | `SessionId`, `WindowsSid`, `WindowsUsername`, `StateType`, `*ActiveSeconds`, alarm counters, `Timestamp`, `CollectedAt`, `CollectedAt_Shamsi` → dest `Timestamp_Shamsi` | `Monitoring.UserActivities` |

Broker is `kafka:9092` on every Kafka engine table. Groups are the v5 names above.

---

## E. Validation commands

Run `ClickHouse/01_validate.sql`.

```sql
SELECT name, engine, engine_full
FROM system.tables
WHERE database = 'Monitoring'
  AND (name LIKE 'Kafka_%' OR name LIKE 'MV_%'
       OR name IN ('AppLogs', 'SystemMetrics', 'UserActivities'))
ORDER BY name;

SELECT 'AppLogs' AS tbl, count() AS rows, max(Timestamp) AS latest FROM Monitoring.AppLogs
UNION ALL
SELECT 'SystemMetrics', count(), max(CollectedAt) FROM Monitoring.SystemMetrics
UNION ALL
SELECT 'UserActivities', count(), max(Timestamp) FROM Monitoring.UserActivities;

SELECT * FROM system.kafka_consumers WHERE database = 'Monitoring';
```

Expect after a few minutes of producer traffic: `rows` increasing, `latest` near `now()`, `system.kafka_consumers` showing assignments.

Kafka lag:

```bash
kafka-consumer-groups.sh --bootstrap-server kafka:9092 --describe --group clickhouse_applogs_v5
```

ClickHouse server log (container): look for `StorageKafka` / `JSONEachRow` parse errors.

---

## F. Troubleshooting commands

```sql
-- MV insert exceptions
SELECT event_time, exception, query
FROM system.query_log
WHERE event_time > now() - INTERVAL 1 HOUR
  AND type IN ('ExceptionBeforeStart', 'ExceptionWhileProcessing')
  AND (
      positionCaseInsensitive(query, 'Kafka_') > 0
      OR positionCaseInsensitive(exception, 'Kafka') > 0
  )
ORDER BY event_time DESC
LIMIT 50;

-- Peek at Kafka staging (consumes with the table's group — do this sparingly)
SELECT * FROM Monitoring.Kafka_AppLogs LIMIT 5;
```

| Symptom | Likely cause | Action |
| --- | --- | --- |
| `UNKNOWN_TABLE Monitoring.Kafka_*` | MV created first, or wrong database | Run Kafka `CREATE` in `Monitoring`, then MV |
| MV created, dest rows stay 0 | Wrong topic, broker `9094`/IP, empty topic, JSON not objects | `engine_full`, `kafka-topics --describe`, console-consumer sample |
| Rows skipped | UUID/number parse on Kafka table | Keep Kafka columns `String`; conversions stay in MV; `kafka_skip_broken_messages = 1000` |
| Duplicate dest rows | Two MVs `TO` the same table | Drop the old MV |
| `system.kafka_consumers` missing | Older CH | Use Kafka `--describe --group` instead |
| Dest schema ≠ script | Live table created earlier | Paste `DESCRIBE`; do not force the MV |

Function fallbacks (CH 25.2 has the `*OrNull` / `*OrZero` functions used). If `parseDateTime64BestEffortOrNull` is missing:

```sql
-- alternative
toDateTime64OrNull(CollectedAt, 7)
```

If `toUUIDOrNull` is missing: `CAST(SessionId AS Nullable(UUID))` (fails the row on `N` format; prefer upgrading or the `substring` concat already in the MV).
