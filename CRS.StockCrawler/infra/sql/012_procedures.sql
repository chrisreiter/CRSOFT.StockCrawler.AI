/*  Upsert-Prozeduren. Alles idempotent über MERGE auf den PKs.            */
USE stockcrawler;
GO

/* ------------------------------------------------- Table-Typ für Bars */
IF TYPE_ID('dbo.PriceBarList') IS NULL
CREATE TYPE dbo.PriceBarList AS TABLE (
  ts_utc    DATETIME2(0)  NOT NULL,
  [open]    DECIMAL(19,8) NULL,
  [high]    DECIMAL(19,8) NULL,
  [low]     DECIMAL(19,8) NULL,
  [close]   DECIMAL(19,8) NOT NULL,
  adj_close DECIMAL(19,8) NULL,
  volume    DECIMAL(38,8) NULL,
  PRIMARY KEY (ts_utc)
);
GO

CREATE OR ALTER PROCEDURE dbo.upsert_asset
  @asset_class     TINYINT,
  @symbol          NVARCHAR(64),
  @provider        TINYINT,
  @provider_symbol NVARCHAR(96),
  @name            NVARCHAR(200) = NULL,
  @currency        NVARCHAR(10)  = NULL,
  @exchange        NVARCHAR(64)  = NULL,
  @market_cap      DECIMAL(38,2) = NULL,
  @market_cap_rank INT           = NULL,
  @reference_price DECIMAL(19,8) = NULL,
  @sector          NVARCHAR(64)  = NULL,
  @country         NVARCHAR(8)   = NULL
AS
BEGIN
  SET NOCOUNT ON;
  MERGE dbo.asset WITH (HOLDLOCK) AS t
  USING (SELECT @asset_class AS asset_class, @symbol AS symbol) AS s
     ON t.asset_class = s.asset_class AND t.symbol = s.symbol
  WHEN MATCHED THEN UPDATE SET
        [name]          = COALESCE(@name, t.[name]),
        currency        = COALESCE(@currency, t.currency),
        exchange        = COALESCE(@exchange, t.exchange),
        provider        = @provider,
        provider_symbol = @provider_symbol,
        market_cap      = COALESCE(@market_cap, t.market_cap),
        market_cap_rank = COALESCE(@market_cap_rank, t.market_cap_rank),
        reference_price = COALESCE(@reference_price, t.reference_price),
        sector          = COALESCE(@sector, t.sector),
        country         = COALESCE(@country, t.country),
        reference_price_utc = CASE WHEN @reference_price IS NULL
                                   THEN t.reference_price_utc ELSE SYSUTCDATETIME() END,
        updated_utc     = SYSUTCDATETIME()
  WHEN NOT MATCHED THEN
    INSERT (asset_class, symbol, [name], currency, exchange, provider,
            provider_symbol, market_cap, market_cap_rank,
            reference_price, reference_price_utc, sector, country)
    VALUES (@asset_class, @symbol, @name, @currency, @exchange, @provider,
            @provider_symbol, @market_cap, @market_cap_rank,
            @reference_price,
            CASE WHEN @reference_price IS NULL THEN NULL ELSE SYSUTCDATETIME() END,
            @sector, @country);

  SELECT asset_id FROM dbo.asset WHERE asset_class = @asset_class AND symbol = @symbol;
END
GO

CREATE OR ALTER PROCEDURE dbo.upsert_price_bars
  @asset_id      INT,
  @interval_code VARCHAR(3),
  @provider      TINYINT,
  @bars          dbo.PriceBarList READONLY
AS
BEGIN
  SET NOCOUNT ON;
  MERGE dbo.price_bar WITH (HOLDLOCK) AS t
  USING (SELECT @asset_id AS asset_id, @interval_code AS interval_code, *
           FROM @bars) AS s
     ON t.asset_id = s.asset_id
    AND t.interval_code = s.interval_code
    AND t.ts_utc = s.ts_utc
  WHEN MATCHED THEN UPDATE SET
        [open] = s.[open], [high] = s.[high], [low] = s.[low],
        [close] = s.[close], adj_close = s.adj_close, volume = s.volume,
        provider = @provider, ingested_at_utc = SYSUTCDATETIME()
  WHEN NOT MATCHED THEN
    INSERT (asset_id, interval_code, ts_utc, [open], [high], [low],
            [close], adj_close, volume, provider)
    VALUES (s.asset_id, s.interval_code, s.ts_utc, s.[open], s.[high], s.[low],
            s.[close], s.adj_close, s.volume, @provider);

  SELECT @@ROWCOUNT AS affected;
END
GO

/* Fällige, noch nicht bewertete Prognosen – Basis der Lernschleife.

   Die Spalten MÜSSEN auf die Eigenschaftsnamen aliasiert werden: Dapper
   verbindet snake_case nicht von sich aus mit PascalCase. Ohne Aliase blieben
   sämtliche Felder auf ihren Standardwerten — target_ts_utc also auf
   0001-01-01, was beim nächsten Zugriff einen SqlDateTime-Überlauf auslöst und
   die gesamte Lernschleife lahmlegt. */
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
   ORDER BY f.target_ts_utc;
END
GO
