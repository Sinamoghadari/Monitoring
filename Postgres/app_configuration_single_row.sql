-- Canonical single-row configuration.
-- Extra rows from historical INSERT-on-POST deploys are removed after the
-- live (highest id) document is kept. Future writes UPDATE that row.

ALTER TABLE app_configuration
    ADD COLUMN IF NOT EXISTS row_version INTEGER NOT NULL DEFAULT 1;

ALTER TABLE app_configuration
    ALTER COLUMN updated_at SET DEFAULT NOW();

DELETE FROM app_configuration
WHERE id NOT IN (
    SELECT id FROM (
        SELECT id FROM app_configuration ORDER BY id DESC LIMIT 1
    ) keep
);
