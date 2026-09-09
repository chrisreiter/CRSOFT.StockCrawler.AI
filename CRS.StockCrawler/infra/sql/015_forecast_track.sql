/*  Durchgehender Prognoseverlauf aus dem Walk-Forward.

    Bewusst eine eigene, flache Tabelle statt forecast + forecast_score:

    - Es geht um Millionen Zeilen. Über die Identity-Spalte von dbo.forecast
      ließe sich das nicht per SqlBulkCopy schreiben, weil die vergebenen
      Schlüssel für die Bewertung zurückgelesen werden müssten.
    - Für die Darstellung im Chart braucht es die Teilmodell-Beiträge nicht.
    - dbo.forecast bleibt damit dem Livebetrieb vorbehalten und klein.       */
USE stockcrawler;
GO

IF OBJECT_ID('dbo.forecast_track') IS NULL
CREATE TABLE dbo.forecast_track (
  asset_id          INT           NOT NULL,
  horizon_hours     INT           NOT NULL,
  interval_code     VARCHAR(3)    NOT NULL,
  target_ts_utc     DATETIME2(0)  NOT NULL,
  made_at_utc       DATETIME2(0)  NOT NULL,
  base_close        DECIMAL(19,8) NOT NULL,
  predicted_close   DECIMAL(19,8) NOT NULL,
  actual_close      DECIMAL(19,8) NULL,
  abs_pct_error     FLOAT         NULL,
  direction_correct BIT           NULL,
  confidence        FLOAT         NOT NULL CONSTRAINT DF_ft_conf DEFAULT(0.5),
  run_label         NVARCHAR(64)  NOT NULL,
  CONSTRAINT PK_forecast_track PRIMARY KEY (asset_id, horizon_hours, interval_code, target_ts_utc)
);
GO

/* Für den Chart-Zugriff: ein Wert, ein Horizont, ein Zeitfenster. */
IF IndexProperty(OBJECT_ID('dbo.forecast_track'),'IX_ft_lookup','IndexID') IS NULL
  CREATE INDEX IX_ft_lookup ON dbo.forecast_track(asset_id, horizon_hours, target_ts_utc)
    INCLUDE(predicted_close, actual_close, abs_pct_error, direction_correct);
GO
