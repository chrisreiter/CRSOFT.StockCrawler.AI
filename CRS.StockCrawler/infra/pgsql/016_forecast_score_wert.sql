-- 016 -- Wert und Horizont an der Bewertung selbst. Gegenstueck zu
-- infra/sql/047; die Begruendung und die gemessenen Zahlen stehen dort.
-- Idempotent.
SET search_path = dbo, public;

ALTER TABLE dbo.forecast_score ADD COLUMN IF NOT EXISTS "asset_id" integer;
ALTER TABLE dbo.forecast_score ADD COLUMN IF NOT EXISTS "horizon_hours" integer;

CREATE INDEX IF NOT EXISTS "IX_forecast_score_wert"
    ON dbo.forecast_score ("asset_id", "horizon_hours")
    INCLUDE ("abs_pct_error", "direction_correct");
