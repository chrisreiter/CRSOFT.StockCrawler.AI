-- 017 -- Index auf dem Bewertungszeitpunkt. Gegenstueck zu infra/sql/048;
-- die Begruendung und die gemessenen Zahlen stehen dort. Idempotent.
SET search_path = dbo, public;

CREATE INDEX IF NOT EXISTS "IX_forecast_score_bewertet"
    ON dbo.forecast_score ("scored_at_utc")
    INCLUDE ("direction_correct", "abs_pct_error");
