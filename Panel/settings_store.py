"""Single-row JSONB configuration store.

All reads and writes target one live row in app_configuration. Nested objects
(Update, API, Kafka, Images) are merged in PostgreSQL with jsonb || so Python
never load-merge-save the whole document. The live row is locked with
SELECT ... FOR UPDATE for the duration of the transaction.
"""

from __future__ import annotations

from typing import Any, Dict, Optional, Tuple

from psycopg2.extras import Json, RealDictCursor

SCHEMA_SQL = """
ALTER TABLE app_configuration
    ADD COLUMN IF NOT EXISTS row_version INTEGER NOT NULL DEFAULT 1;

ALTER TABLE app_configuration
    ALTER COLUMN updated_at SET DEFAULT NOW();
"""

# One-level deep merge: top-level scalars/arrays from the patch replace;
# nested JSON objects are shallow-merged (Update.Sha256 does not wipe ServiceName).
MERGE_SQL = """
WITH live AS (
    SELECT id
    FROM app_configuration
    ORDER BY id DESC
    LIMIT 1
    FOR UPDATE
)
UPDATE app_configuration AS c
SET
    settings_json = (
        SELECT COALESCE(jsonb_object_agg(key, value), '{}'::jsonb)
        FROM (
            SELECT COALESCE(e.key, p.key) AS key,
                   CASE
                       WHEN e.value IS NULL THEN p.value
                       WHEN p.value IS NULL THEN e.value
                       WHEN jsonb_typeof(e.value) = 'object'
                        AND jsonb_typeof(p.value) = 'object'
                       THEN e.value || p.value
                       ELSE p.value
                   END AS value
            FROM jsonb_each(c.settings_json) AS e(key, value)
            FULL OUTER JOIN jsonb_each(%(patch)s::jsonb) AS p(key, value)
                ON e.key = p.key
        ) merged
    ),
    updated_at = NOW(),
    row_version = c.row_version + 1
FROM live
WHERE c.id = live.id
  AND (%(expected_version)s IS NULL OR c.row_version = %(expected_version)s)
RETURNING c.id, c.settings_json, c.row_version, c.updated_at;
"""

REPLACE_SQL = """
WITH live AS (
    SELECT id
    FROM app_configuration
    ORDER BY id DESC
    LIMIT 1
    FOR UPDATE
)
UPDATE app_configuration AS c
SET
    settings_json = %(document)s::jsonb,
    updated_at = NOW(),
    row_version = c.row_version + 1
FROM live
WHERE c.id = live.id
  AND (%(expected_version)s IS NULL OR c.row_version = %(expected_version)s)
RETURNING c.id, c.settings_json, c.row_version, c.updated_at;
"""

COLLAPSE_SQL = """
DELETE FROM app_configuration
WHERE id NOT IN (
    SELECT id FROM (
        SELECT id FROM app_configuration ORDER BY id DESC LIMIT 1
    ) keep
);
"""

INSERT_SQL = """
INSERT INTO app_configuration (settings_json, row_version)
VALUES (%(document)s::jsonb, 1)
RETURNING id, settings_json, row_version, updated_at;
"""

GET_SQL = """
SELECT id, settings_json, row_version, updated_at
FROM app_configuration
ORDER BY id DESC
LIMIT 1;
"""


def ensure_schema(conn) -> None:
    with conn.cursor() as cur:
        cur.execute(SCHEMA_SQL)
    conn.commit()


def collapse_to_single_row(conn) -> None:
    with conn.cursor() as cur:
        cur.execute(COLLAPSE_SQL)


def get_live(conn) -> Optional[Tuple[int, Dict[str, Any], int]]:
    with conn.cursor(cursor_factory=RealDictCursor) as cur:
        cur.execute(GET_SQL)
        row = cur.fetchone()
    if row is None:
        return None
    document = row["settings_json"]
    if isinstance(document, str):
        import json
        document = json.loads(document)
    return int(row["id"]), document, int(row["row_version"])


def merge_live(
    conn,
    patch: Dict[str, Any],
    expected_version: Optional[int] = None,
) -> Tuple[Dict[str, Any], int]:
    if not patch:
        live = get_live(conn)
        if live is None:
            raise LookupError("app_configuration is empty")
        return live[1], live[2]

    with conn.cursor(cursor_factory=RealDictCursor) as cur:
        cur.execute(GET_SQL)
        existing = cur.fetchone()
        if existing is None:
            cur.execute(INSERT_SQL, {"document": Json(patch)})
            inserted = cur.fetchone()
            conn.commit()
            return _document(inserted), int(inserted["row_version"])

        cur.execute(
            MERGE_SQL,
            {"patch": Json(patch), "expected_version": expected_version},
        )
        updated = cur.fetchone()
        if updated is None:
            conn.rollback()
            if expected_version is not None:
                raise ConflictError(
                    f"row_version mismatch (expected {expected_version})"
                )
            raise LookupError("app_configuration live row disappeared")
        cur.execute(COLLAPSE_SQL)
    conn.commit()
    return _document(updated), int(updated["row_version"])


def replace_live(
    conn,
    document: Dict[str, Any],
    expected_version: Optional[int] = None,
) -> Tuple[Dict[str, Any], int]:
    with conn.cursor(cursor_factory=RealDictCursor) as cur:
        cur.execute(GET_SQL)
        existing = cur.fetchone()
        if existing is None:
            cur.execute(INSERT_SQL, {"document": Json(document)})
            inserted = cur.fetchone()
            conn.commit()
            return _document(inserted), int(inserted["row_version"])

        cur.execute(
            REPLACE_SQL,
            {"document": Json(document), "expected_version": expected_version},
        )
        updated = cur.fetchone()
        if updated is None:
            conn.rollback()
            if expected_version is not None:
                raise ConflictError(
                    f"row_version mismatch (expected {expected_version})"
                )
            raise LookupError("app_configuration live row disappeared")
        cur.execute(COLLAPSE_SQL)
    conn.commit()
    return _document(updated), int(updated["row_version"])


def _document(row) -> Dict[str, Any]:
    document = row["settings_json"]
    if isinstance(document, str):
        import json
        return json.loads(document)
    return document


class ConflictError(Exception):
    pass
