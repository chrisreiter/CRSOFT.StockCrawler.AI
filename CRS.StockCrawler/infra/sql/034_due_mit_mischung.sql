/*  `get_due_forecasts` liefert jetzt auch die gemischte Zahl.

    Ohne sie steht `CombinedClose` beim Bewerten auf NULL, und die Mischung würde nie
    bewertet — stillschweigend. Die Prognose sähe vollständig aus, die Auswertung
    „bringt das Mischen etwas?" bliebe für immer leer, und man suchte den Fehler in der
    Auswertung.

    Die Prozedur ist ansonsten wörtlich die aus 025, samt ihrem Filter gegen den
    Kopfstau: Es muss eine Bar geben, die NACH der Prognose und spätestens zum
    Zielzeitpunkt liegt.
*/

CREATE OR ALTER PROCEDURE dbo.get_due_forecasts
    @now_utc  DATETIME2(0),
    @max_rows INT = 5000
AS
BEGIN
  SET NOCOUNT ON;
  SELECT TOP (@max_rows)
         f.forecast_id      AS ForecastId,
         f.asset_id         AS AssetId,
         f.horizon_hours    AS HorizonHours,
         f.made_at_utc      AS MadeAtUtc,
         f.target_ts_utc    AS TargetTsUtc,
         f.base_close       AS BaseClose,
         f.predicted_close  AS PredictedClose,
         f.predicted_return AS PredictedReturn,
         f.confidence       AS Confidence,
         f.model_version    AS ModelVersion,
         f.combined_close   AS CombinedClose,
         f.combined_return  AS CombinedReturn,
         f.pillar_mix       AS PillarMix
    FROM dbo.forecast f
    LEFT JOIN dbo.forecast_score s ON s.forecast_id = f.forecast_id
   WHERE s.forecast_id IS NULL
     AND f.target_ts_utc <= @now_utc
     AND f.unscoreable_utc IS NULL
     AND EXISTS (SELECT 1
                   FROM dbo.price_bar b
                  WHERE b.asset_id = f.asset_id
                    AND b.interval_code = CASE WHEN f.horizon_hours >= 96 THEN '1d' ELSE '1h' END
                    AND b.ts_utc > f.made_at_utc
                    AND b.ts_utc <= f.target_ts_utc)
   ORDER BY f.target_ts_utc;
END
GO
