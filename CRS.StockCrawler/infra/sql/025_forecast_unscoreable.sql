/*  Dauerhaft nicht bewertbare Prognosen kenntlich machen.

    Der Befund: Von 18.086 fälligen, unbewerteten Prognosen waren 13.988
    dauerhaft nicht bewertbar. Ihr Zielzeitpunkt fällt in ein geschlossenes
    Marktfenster — für eine Aktie etwa auf einen Samstag oder auf die Nacht.
    Dort kann nie eine neue Bar erscheinen, und die Bewertung verlangt eine
    NEUE Bar (sonst verglichen sich Kurse mit sich selbst und jede Prognose
    nahe null wäre ein Treffer).

    `get_due_forecasts` nimmt die ältesten 5.000 nach Zielzeitpunkt. Genau
    diese standen vorn und blockierten die 4.098 bewertbaren dahinter: Seit dem
    22.08.2026 16:09 wurde nichts mehr bewertet, und damit hat auch
    `Ensemble.UpdateWeights` aufgehört zu lernen. Kein Fehler, keine Meldung —
    nur ein Lauf, der jede Stunde dasselbe erfolglos versucht.

    Erkennbar sind sie eindeutig: Existiert für den Wert eine Bar NACH dem
    Zielzeitpunkt, ist die Reihe über diesen Punkt hinweg fortgeschritten. Ist
    die letzte Bar davor trotzdem nicht neuer als die Prognose, dann lag
    zwischen Prognose und Ziel keine Handelszeit — und es wird auch keine mehr
    kommen.                                                                  */
USE stockcrawler;
GO

IF COL_LENGTH('dbo.forecast', 'unscoreable_utc') IS NULL
  ALTER TABLE dbo.forecast ADD unscoreable_utc DATETIME2(0) NULL;
GO

IF COL_LENGTH('dbo.forecast', 'unscoreable_reason') IS NULL
  ALTER TABLE dbo.forecast ADD unscoreable_reason NVARCHAR(200) NULL;
GO

/*  Der Index, an dem die Warteschlange hängt. Ohne `unscoreable_utc` im
    Filter läuft die Abfrage weiterhin über die Zurückgestellten.            */
/*  Kein GEFILTERTER Index.

    Der erste Entwurf hatte `WHERE unscoreable_utc IS NULL` -- und damit
    verlangt SQL Server für JEDES Update auf dbo.forecast, dass
    QUOTED_IDENTIFIER eingeschaltet ist. Eine Prozedur, die ohne diese
    Einstellung angelegt wurde, scheitert dann mit Fehler 1934, und zwar erst
    zur Laufzeit und mit einer Meldung, die den Zusammenhang nicht nennt.
    Genau so ist der Bewertungslauf unmittelbar nach dem Einbau gescheitert.

    Ein gewöhnlicher Index über den Zielzeitpunkt tut es fast genauso gut und
    stellt keine Bedingung an die Sitzung, in der später jemand eine Prozedur
    anlegt.                                                                  */
IF IndexProperty(OBJECT_ID('dbo.forecast'), 'IX_forecast_due', 'IndexID') IS NULL
  CREATE INDEX IX_forecast_due ON dbo.forecast(target_ts_utc, unscoreable_utc)
    INCLUDE (asset_id, horizon_hours, made_at_utc, base_close,
             predicted_close, predicted_return, confidence, model_version);
GO

CREATE OR ALTER PROCEDURE dbo.get_due_forecasts
  @now_utc DATETIME2(0),
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
         f.model_version    AS ModelVersion
    FROM dbo.forecast f
    LEFT JOIN dbo.forecast_score s ON s.forecast_id = f.forecast_id
   WHERE s.forecast_id IS NULL
     AND f.target_ts_utc <= @now_utc
     AND f.unscoreable_utc IS NULL
     /* Der eigentliche Filter: Es muss eine Bar geben, die NACH der Prognose
        und spätestens zum Zielzeitpunkt liegt.

        Vorher stand diese Bedingung nur im Code -- die Abfrage holte die
        ältesten 5.000 und der Dienst warf sie danach weg. Am Wochenende sind
        das 5.000 von 5.000: Für eine Aktie liegt zwischen Freitag 20:05 und
        Freitag 21:05 keine Handelszeit, und bis Montag lässt sich nicht einmal
        entscheiden, dass es nie eine geben wird. Die bewertbaren Prognosen
        dahinter kamen nie an die Reihe.

        Dieselbe Lehre wie bei den Verknüpfungen der Kurvendiskussion: Ein
        Filter hinter der Mengenbegrenzung ist kein Filter, sondern ein
        Zufallsgenerator. */
     AND EXISTS (SELECT 1 FROM dbo.price_bar p
                  WHERE p.asset_id = f.asset_id
                    AND p.interval_code = CASE WHEN f.horizon_hours >= 96
                                               THEN '1d' ELSE '1h' END
                    AND p.ts_utc > f.made_at_utc
                    AND p.ts_utc <= f.target_ts_utc)
   ORDER BY f.target_ts_utc;
END
GO

/*  Einmalig aufräumen, was sich bereits angesammelt hat. Danach erledigt das
    der Bewertungslauf selbst.                                               */
CREATE OR ALTER PROCEDURE dbo.mark_unscoreable_forecasts
AS
BEGIN
  SET NOCOUNT ON;

  UPDATE f
     SET unscoreable_utc = SYSUTCDATETIME(),
         unscoreable_reason = N'Zielzeitpunkt in geschlossenem Marktfenster — '
                            + N'zwischen Prognose und Ziel lag keine Handelszeit.'
    FROM dbo.forecast f
    LEFT JOIN dbo.forecast_score s ON s.forecast_id = f.forecast_id
   CROSS APPLY (SELECT CASE WHEN f.horizon_hours >= 96 THEN '1d' ELSE '1h' END AS iv) c
   WHERE s.forecast_id IS NULL
     AND f.unscoreable_utc IS NULL
     AND f.target_ts_utc <= SYSUTCDATETIME()
     -- Die Reihe ist über den Zielzeitpunkt hinaus fortgeschritten …
     AND EXISTS (SELECT 1 FROM dbo.price_bar p
                  WHERE p.asset_id = f.asset_id AND p.interval_code = c.iv
                    AND p.ts_utc > f.target_ts_utc)
     -- … und die letzte Bar davor ist trotzdem nicht neuer als die Prognose.
     AND ISNULL((SELECT MAX(p.ts_utc) FROM dbo.price_bar p
                  WHERE p.asset_id = f.asset_id AND p.interval_code = c.iv
                    AND p.ts_utc <= f.target_ts_utc), '1900-01-01') <= f.made_at_utc;

  SELECT @@ROWCOUNT AS zurueckgestellt;
END
GO
