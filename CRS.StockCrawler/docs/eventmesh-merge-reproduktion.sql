-- Stellt den Schreibpfad von PriceBarRepository.UpsertAsync nach.
CREATE TEMP TABLE bars (
  ts_utc timestamp NOT NULL PRIMARY KEY,
  "open" DECIMAL(19,8) NULL, "high" DECIMAL(19,8) NULL, "low" DECIMAL(19,8) NULL,
  "close" DECIMAL(19,8) NOT NULL, adj_close DECIMAL(19,8) NULL, volume DECIMAL(38,8) NULL);

\copy bars (ts_utc, "close") FROM 'C:/Users/Chris/AppData/Local/Temp/copytest.tsv'

SELECT count(*) AS in_stage FROM bars;

-- Variante A: genau wie die Anwendung, mit b.*
MERGE INTO dbo.price_bar AS t
USING (SELECT 201 AS asset_id, '1h' AS interval_code, b.* FROM bars b) AS s
   ON t.asset_id = s.asset_id
  AND t.interval_code = s.interval_code
  AND t.ts_utc = s.ts_utc
WHEN MATCHED THEN UPDATE SET
      "open" = s."open", "high" = s."high", "low" = s."low",
      "close" = s."close", adj_close = s.adj_close, volume = s.volume,
      provider = 0, ingested_at_utc = (now() AT TIME ZONE 'utc')
WHEN NOT MATCHED THEN
  INSERT (asset_id, interval_code, ts_utc, "open", "high", "low",
          "close", adj_close, volume, provider)
  VALUES (s.asset_id, s.interval_code, s.ts_utc, s."open", s."high", s."low",
          s."close", s.adj_close, s.volume, 0);

SELECT count(*) AS nach_merge FROM dbo.price_bar
 WHERE asset_id = 201 AND interval_code = '1h' AND ts_utc = '2026-09-26 10:00:00';

-- Aufräumen
DELETE FROM dbo.price_bar WHERE asset_id = 201 AND interval_code = '1h' AND ts_utc = '2026-09-26 10:00:00';
DELETE FROM dbo.price_bar WHERE asset_id = 201 AND interval_code = '1h' AND ts_utc = '2026-09-26 11:00:00';
