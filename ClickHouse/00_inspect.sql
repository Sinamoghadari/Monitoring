-- =============================================================================
-- READ-ONLY inspection. Run these first. Safe: no DROP / TRUNCATE / INSERT.
-- =============================================================================

SELECT
    database,
    name,
    engine,
    total_rows,
    total_bytes,
    create_table_query
FROM system.tables
WHERE database = 'Monitoring'
ORDER BY engine, name;

SELECT
    table,
    position,
    name,
    type,
    default_kind
FROM system.columns
WHERE database = 'Monitoring'
ORDER BY table, position;

SELECT
    name,
    engine,
    as_select
FROM system.tables
WHERE database = 'Monitoring'
  AND engine = 'MaterializedView';

SELECT
    name,
    engine,
    engine_full
FROM system.tables
WHERE database = 'Monitoring'
  AND engine = 'Kafka';
