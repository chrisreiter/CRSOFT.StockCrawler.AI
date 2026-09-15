-- CRSOFT.StockCrawler -- PostgreSQL-Routinen, Gegenstuecke zu den T-SQL-Prozeduren
-- aus infra/sql. Alles sind FUNKTIONEN, keine Prozeduren: Postgres-Prozeduren
-- liefern keine Zeilen, und der Code ruft jede Routine einheitlich ueber
-- SELECT * FROM dbo.name(...) (SqlDialekt.Aufruf). Die Parameter stehen in der
-- Reihenfolge der T-SQL-Signatur, denn der Aufruf ist positionell.
--
-- Zwei Fallen, die beim Portieren jeder dieser Routinen zu pruefen waren:
--   LOG()    ist in T-SQL der natuerliche Logarithmus, in Postgres Basis 10 -> ln()
--   STDEV()  heisst stddev_samp(), STDEVP() stddev_pop()
-- Der Median (PERCENTILE_CONT) ist hier ein Aggregat mit GROUP BY, kein Fenster.

SET search_path = dbo, public;

-- ------------------------------------------------------------ letzter_kurs --
-- Die EINE Definition des letzten Kurses: juengste Bar aus Tag ODER Stunde.
CREATE OR REPLACE FUNCTION dbo.letzter_kurs(p_asset_id integer)
RETURNS TABLE (schluss numeric, ts_utc timestamp, interval_code varchar)
LANGUAGE sql STABLE AS $$
  SELECT p."close", p.ts_utc, p.interval_code
    FROM dbo.price_bar p
   WHERE p.asset_id = p_asset_id
     AND p.interval_code IN ('1d', '1h')
     AND p."close" > 0
   ORDER BY p.ts_utc DESC
   LIMIT 1
$$;

-- ------------------------------------------------------------ upsert_asset --
CREATE OR REPLACE FUNCTION dbo.upsert_asset(
  p_asset_class integer, p_symbol text, p_provider integer, p_provider_symbol text,
  p_name text DEFAULT NULL, p_currency text DEFAULT NULL, p_exchange text DEFAULT NULL,
  p_market_cap numeric DEFAULT NULL, p_market_cap_rank integer DEFAULT NULL,
  p_reference_price numeric DEFAULT NULL, p_sector text DEFAULT NULL, p_country text DEFAULT NULL)
RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
  v_id integer;
BEGIN
  INSERT INTO dbo.asset (asset_class, symbol, "name", currency, exchange, provider,
                         provider_symbol, market_cap, market_cap_rank,
                         reference_price, reference_price_utc, sector, country)
  VALUES (p_asset_class, p_symbol, p_name, p_currency, p_exchange, p_provider,
          p_provider_symbol, p_market_cap, p_market_cap_rank,
          p_reference_price,
          CASE WHEN p_reference_price IS NULL THEN NULL ELSE (now() AT TIME ZONE 'utc') END,
          p_sector, p_country)
  ON CONFLICT (asset_class, symbol) DO UPDATE SET
          "name"          = COALESCE(EXCLUDED."name", dbo.asset."name"),
          currency        = COALESCE(EXCLUDED.currency, dbo.asset.currency),
          exchange        = COALESCE(EXCLUDED.exchange, dbo.asset.exchange),
          provider        = EXCLUDED.provider,
          provider_symbol = EXCLUDED.provider_symbol,
          market_cap      = COALESCE(EXCLUDED.market_cap, dbo.asset.market_cap),
          market_cap_rank = COALESCE(EXCLUDED.market_cap_rank, dbo.asset.market_cap_rank),
          reference_price = COALESCE(EXCLUDED.reference_price, dbo.asset.reference_price),
          sector          = COALESCE(EXCLUDED.sector, dbo.asset.sector),
          country         = COALESCE(EXCLUDED.country, dbo.asset.country),
          reference_price_utc = CASE WHEN EXCLUDED.reference_price IS NULL
                                     THEN dbo.asset.reference_price_utc
                                     ELSE (now() AT TIME ZONE 'utc') END,
          updated_utc     = (now() AT TIME ZONE 'utc')
  RETURNING asset_id INTO v_id;
  RETURN v_id;
END $$;

-- ------------------------------------------------------- get_due_forecasts --
-- Spaltennamen in Anfuehrungszeichen, damit sie wie in T-SQL heissen; Dapper
-- ordnet ohnehin ohne Ruecksicht auf Gross- und Kleinschreibung zu.
CREATE OR REPLACE FUNCTION dbo.get_due_forecasts(p_now_utc timestamp, p_max_rows integer DEFAULT 5000)
RETURNS TABLE ("ForecastId" bigint, "AssetId" integer, "HorizonHours" integer,
               "MadeAtUtc" timestamp, "TargetTsUtc" timestamp,
               "BaseClose" numeric, "PredictedClose" numeric, "PredictedReturn" double precision,
               "Confidence" double precision, "ModelVersion" varchar,
               "CombinedClose" numeric, "CombinedReturn" double precision, "PillarMix" varchar)
LANGUAGE sql STABLE AS $$
  SELECT f.forecast_id, f.asset_id, f.horizon_hours, f.made_at_utc, f.target_ts_utc,
         f.base_close, f.predicted_close, f.predicted_return, f.confidence, f.model_version,
         f.combined_close, f.combined_return, f.pillar_mix
    FROM dbo.forecast f
    LEFT JOIN dbo.forecast_score s ON s.forecast_id = f.forecast_id
   WHERE s.forecast_id IS NULL
     AND f.target_ts_utc <= p_now_utc
     AND f.unscoreable_utc IS NULL
     AND EXISTS (SELECT 1
                   FROM dbo.price_bar b
                  WHERE b.asset_id = f.asset_id
                    AND b.interval_code = CASE WHEN f.horizon_hours >= 96 THEN '1d' ELSE '1h' END
                    AND b.ts_utc > f.made_at_utc
                    AND b.ts_utc <= f.target_ts_utc)
   ORDER BY f.target_ts_utc
   LIMIT p_max_rows
$$;

-- ---------------------------------------------- mark_unscoreable_forecasts --
CREATE OR REPLACE FUNCTION dbo.mark_unscoreable_forecasts()
RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
  n integer;
BEGIN
  UPDATE dbo.forecast f
     SET unscoreable_utc = (now() AT TIME ZONE 'utc'),
         unscoreable_reason = 'Zielzeitpunkt in geschlossenem Marktfenster — '
                           || 'zwischen Prognose und Ziel lag keine Handelszeit.'
   WHERE NOT EXISTS (SELECT 1 FROM dbo.forecast_score s WHERE s.forecast_id = f.forecast_id)
     AND f.unscoreable_utc IS NULL
     AND f.target_ts_utc <= (now() AT TIME ZONE 'utc')
     -- Die Reihe ist ueber den Zielzeitpunkt hinaus fortgeschritten ...
     AND EXISTS (SELECT 1 FROM dbo.price_bar p
                  WHERE p.asset_id = f.asset_id
                    AND p.interval_code = CASE WHEN f.horizon_hours >= 96 THEN '1d' ELSE '1h' END
                    AND p.ts_utc > f.target_ts_utc)
     -- ... und die letzte Bar davor ist trotzdem nicht neuer als die Prognose.
     AND COALESCE((SELECT MAX(p.ts_utc) FROM dbo.price_bar p
                    WHERE p.asset_id = f.asset_id
                      AND p.interval_code = CASE WHEN f.horizon_hours >= 96 THEN '1d' ELSE '1h' END
                      AND p.ts_utc <= f.target_ts_utc), TIMESTAMP '1900-01-01') <= f.made_at_utc;
  GET DIAGNOSTICS n = ROW_COUNT;
  RETURN n;
END $$;

-- ------------------------------------------------------ close_orphaned_runs --
CREATE OR REPLACE FUNCTION dbo.close_orphaned_runs(p_stunden integer DEFAULT 2)
RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
  n integer;
BEGIN
  UPDATE dbo.ingest_run
     SET finished_utc = started_utc,
         note = COALESCE(NULLIF(note, ''), '')
              || CASE WHEN COALESCE(note, '') = '' THEN '' ELSE ' — ' END
              || 'abgebrochen (Neustart), nachträglich geschlossen'
   WHERE finished_utc IS NULL
     AND started_utc < (now() AT TIME ZONE 'utc') - make_interval(hours => p_stunden);
  GET DIAGNOSTICS n = ROW_COUNT;
  RETURN n;
END $$;

-- ------------------------------------------------------- set_leitstrategie --
CREATE OR REPLACE FUNCTION dbo.set_leitstrategie(p_depot text)
RETURNS TABLE (depot varchar, zaehlt boolean)
LANGUAGE plpgsql AS $$
BEGIN
  IF p_depot NOT IN ('streng', 'aktiv', 'halten', 'invers') THEN
    RAISE EXCEPTION 'Nur streng, aktiv, halten oder invers können zum Vermögen zählen.';
  END IF;

  UPDATE dbo.autopilot_einstellung e
     SET zaehlt = (e.depot = p_depot),
         updated_utc = (now() AT TIME ZONE 'utc');

  RETURN QUERY SELECT e.depot, e.zaehlt FROM dbo.autopilot_einstellung e ORDER BY e.depot;
END $$;

-- --------------------------------------------------- reset_autopilot_depot --
CREATE OR REPLACE FUNCTION dbo.reset_autopilot_depot(p_depot text)
RETURNS TABLE (depot varchar, waehrung varchar, startkapital numeric)
LANGUAGE plpgsql AS $$
DECLARE
  v_waehrung varchar; v_betrag numeric;
BEGIN
  IF p_depot NOT IN ('streng', 'aktiv', 'halten', 'invers') THEN
    RAISE EXCEPTION 'Nur streng, aktiv, halten oder invers lassen sich zurücksetzen.';
  END IF;

  SELECT e.waehrung, e.startkapital INTO v_waehrung, v_betrag
    FROM dbo.autopilot_einstellung e WHERE e.depot = p_depot;

  DELETE FROM dbo.autopilot_entscheidung e
   USING dbo.autopilot_lauf l
   WHERE l.lauf_id = e.lauf_id AND l.depot = p_depot;

  DELETE FROM dbo.autopilot_lauf       l WHERE l.depot = p_depot;
  -- ALLE Waehrungen, nicht nur die eingestellte.
  DELETE FROM dbo.invest_kontobewegung m WHERE m.depot = p_depot;
  DELETE FROM dbo.invest_buchung       b WHERE b.depot = p_depot;

  /*  Das Budget als gewoehnliche Einzahlung -- damit ist der Startbetrag im
      Kassenjournal sichtbar und der Kontostand bleibt das, was er ueberall
      sonst ist: die Summe seiner Bewegungen.                                */
  IF v_betrag > 0 THEN
    INSERT INTO dbo.invest_kontobewegung (depot, waehrung, betrag, grund, notiz)
    VALUES (p_depot, v_waehrung, v_betrag, 'einzahlung', 'Startbudget');
  END IF;

  RETURN QUERY SELECT p_depot::varchar, v_waehrung, v_betrag;
END $$;

-- ------------------------------------------------------ Kennzahlen je Bar --
-- Gemeinsamer Unterbau von get_active_triggers und run_bot_trigger_test: die
-- T-SQL-Fassungen enthalten denselben Block zweimal (mit vol60 nur im Test).
-- Hier einmal, damit Erkennung und Messung nicht auseinanderlaufen koennen --
-- genau der Grund, aus dem get_active_triggers die Bedingung von 027 spiegelt.
CREATE OR REPLACE FUNCTION dbo.bot_kennzahlen(p_von timestamp)
RETURNS TABLE (asset_id integer, asset_class smallint, ts_utc timestamp,
               c double precision, v double precision, rn bigint,
               sma50 double precision, sma200 double precision, sma20 double precision,
               sd20 double precision, hoch252 double precision, vol60 double precision,
               c_vor double precision, rsi14 double precision, ma_vor double precision,
               rsi_vor double precision)
LANGUAGE sql STABLE AS $$
  WITH k AS (
    SELECT p.asset_id, a.asset_class, p.ts_utc,
           CAST(p."close" AS double precision) AS c,
           CAST(p.volume AS double precision)  AS v,
           ROW_NUMBER() OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc) AS rn,
           AVG(CAST(p."close" AS double precision)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
                ROWS BETWEEN 49 PRECEDING AND CURRENT ROW) AS sma50,
           AVG(CAST(p."close" AS double precision)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
                ROWS BETWEEN 199 PRECEDING AND CURRENT ROW) AS sma200,
           AVG(CAST(p."close" AS double precision)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
                ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS sma20,
           STDDEV_SAMP(CAST(p."close" AS double precision)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
                ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS sd20,
           MAX(CAST(p."close" AS double precision)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
                ROWS BETWEEN 251 PRECEDING AND 1 PRECEDING) AS hoch252,
           AVG(CAST(p.volume AS double precision)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
                ROWS BETWEEN 60 PRECEDING AND 1 PRECEDING) AS vol60,
           LAG(CAST(p."close" AS double precision)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc) AS c_vor
      FROM dbo.price_bar p
      JOIN dbo.asset a ON a.asset_id = p.asset_id AND a.is_tracked
     WHERE p.interval_code = '1d'
       AND p.ts_utc >= p_von
       AND p."close" > 0
  ),
  kr AS (
    /* RSI ueber vierzehn Bars, gleitendes einfaches Mittel der Auf- und
       Abwaertsbewegungen -- nicht Wilders Glaettung, die laeuft rekursiv. */
    SELECT k.*,
           AVG(CASE WHEN k.c > k.c_vor THEN k.c - k.c_vor ELSE 0 END)
               OVER (PARTITION BY k.asset_id ORDER BY k.ts_utc
                     ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) AS auf14,
           AVG(CASE WHEN k.c < k.c_vor THEN k.c_vor - k.c ELSE 0 END)
               OVER (PARTITION BY k.asset_id ORDER BY k.ts_utc
                     ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) AS ab14
      FROM k
  )
  SELECT r.asset_id, r.asset_class, r.ts_utc, r.c, r.v, r.rn,
         r.sma50, r.sma200, r.sma20, r.sd20, r.hoch252, r.vol60, r.c_vor,
         CASE WHEN r.auf14 + r.ab14 > 0 THEN 100.0 * r.auf14 / (r.auf14 + r.ab14) END AS rsi14,
         LAG(r.sma50 - r.sma200) OVER (PARTITION BY r.asset_id ORDER BY r.ts_utc) AS ma_vor,
         LAG(CASE WHEN r.auf14 + r.ab14 > 0 THEN 100.0 * r.auf14 / (r.auf14 + r.ab14) END)
             OVER (PARTITION BY r.asset_id ORDER BY r.ts_utc) AS rsi_vor
    FROM kr r
$$;

-- Die sieben Ausloeser -- EINE Definition fuer Erkennung und Messung.
CREATE OR REPLACE FUNCTION dbo.bot_ausloeser(p_von timestamp, p_ab timestamp)
RETURNS TABLE (ausloeser varchar, richtung integer, asset_id integer, asset_class smallint,
               ts_utc timestamp, volumen_faktor double precision)
LANGUAGE sql STABLE AS $$
  WITH s AS (SELECT * FROM dbo.bot_kennzahlen(p_von))
  SELECT x.ausloeser::varchar, x.richtung, x.asset_id, x.asset_class, x.ts_utc, x.volumen_faktor
    FROM (
      SELECT 'Goldenes Kreuz (SMA 50 über 200)' AS ausloeser, 1 AS richtung,
             s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END AS volumen_faktor
        FROM s WHERE s.ts_utc >= p_ab AND s.ma_vor < 0 AND s.sma50 - s.sma200 > 0
      UNION ALL
      SELECT 'Todeskreuz (SMA 50 unter 200)', -1, s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END
        FROM s WHERE s.ts_utc >= p_ab AND s.ma_vor > 0 AND s.sma50 - s.sma200 < 0
      UNION ALL
      SELECT 'RSI unter 30 (überverkauft)', 1, s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END
        FROM s WHERE s.ts_utc >= p_ab AND s.rsi_vor >= 30 AND s.rsi14 < 30
      UNION ALL
      SELECT 'RSI über 70 (überkauft)', -1, s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END
        FROM s WHERE s.ts_utc >= p_ab AND s.rsi_vor <= 70 AND s.rsi14 > 70
      UNION ALL
      SELECT 'Bollinger-Ausbruch nach oben', 1, s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END
        FROM s WHERE s.ts_utc >= p_ab AND s.sd20 > 0 AND s.c > s.sma20 + 2 * s.sd20
      UNION ALL
      SELECT 'Bollinger-Ausbruch nach unten', -1, s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END
        FROM s WHERE s.ts_utc >= p_ab AND s.sd20 > 0 AND s.c < s.sma20 - 2 * s.sd20
      UNION ALL
      SELECT 'Neues 52-Wochen-Hoch', 1, s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END
        FROM s WHERE s.ts_utc >= p_ab AND s.hoch252 > 0 AND s.c > s.hoch252
    ) x
$$;

-- ----------------------------------------------------- get_active_triggers --
-- Ein Jahr plus Vorlauf (420 Tage): SMA200 und das 52-Wochen-Hoch brauchen
-- 252 Bars. `p_tage` statt "genau der letzte Bar": Ein Ausloeser vom Vortag ist
-- noch aktuell, die gemessene Wirkung reicht ueber 1, 5 und 20 Tage.
CREATE OR REPLACE FUNCTION dbo.get_active_triggers(p_tage integer DEFAULT 3)
RETURNS TABLE (ausloeser varchar, richtung integer, asset_id integer, asset_class smallint, ts_utc timestamp)
LANGUAGE sql STABLE AS $$
  SELECT b.ausloeser, b.richtung, b.asset_id, b.asset_class, b.ts_utc
    FROM dbo.bot_ausloeser((now() AT TIME ZONE 'utc') - INTERVAL '420 days',
                           (now() AT TIME ZONE 'utc') - make_interval(days => p_tage)) b
   ORDER BY b.asset_id, b.ausloeser
$$;

-- ---------------------------------------------------- run_bot_trigger_test --
CREATE OR REPLACE FUNCTION dbo.run_bot_trigger_test(p_jahre integer DEFAULT 5)
RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
  v_von timestamp := (now() AT TIME ZONE 'utc') - make_interval(years => p_jahre);
  v_run integer;
BEGIN
  INSERT INTO dbo.bot_trigger_run (von_utc) VALUES (v_von) RETURNING run_id INTO v_run;

  DROP TABLE IF EXISTS bt_s; DROP TABLE IF EXISTS bt_fw; DROP TABLE IF EXISTS bt_e;

  -- Kennzahlen ab einem Jahr VOR dem Fenster, damit SMA200 und Hoch252 voll sind.
  CREATE TEMP TABLE bt_s ON COMMIT DROP AS
    SELECT * FROM dbo.bot_kennzahlen(v_von - INTERVAL '1 year');
  CREATE INDEX ON bt_s (asset_id, rn);

  -- Folgerenditen ueber 1, 5 und 20 Bars.
  CREATE TEMP TABLE bt_fw ON COMMIT DROP AS
    SELECT s.asset_id, s.ts_utc, s.rn,
           f1.c / s.c - 1 AS r1, f5.c / s.c - 1 AS r5, f20.c / s.c - 1 AS r20
      FROM bt_s s
      LEFT JOIN bt_s f1  ON f1.asset_id  = s.asset_id AND f1.rn  = s.rn + 1
      LEFT JOIN bt_s f5  ON f5.asset_id  = s.asset_id AND f5.rn  = s.rn + 5
      LEFT JOIN bt_s f20 ON f20.asset_id = s.asset_id AND f20.rn = s.rn + 20;
  CREATE INDEX ON bt_fw (asset_id, ts_utc);

  -- Ausloeser im Fenster. `richtung` +1 = Aufwaerts nahegelegt, sonst -1.
  CREATE TEMP TABLE bt_e ON COMMIT DROP AS
    SELECT * FROM dbo.bot_ausloeser(v_von - INTERVAL '1 year', v_von);

  /*  MEDIAN, nicht Mittelwert: Ein Mittelwert ueber sterbende Kleinstwerte
      beschreibt den Rand der Verteilung, nicht die Mitte (siehe 027).
      Die Vergleichslatte `medl` ist der Median ueber ALLE Tage derselben
      Werte im selben Zeitraum -- ohne sie misst man den Marktgang.        */
  INSERT INTO dbo.bot_trigger_stat
    (run_id, ausloeser, klasse, ereignisse, werte, r1, r5, r20, b1, b5, b20,
     richtung1, richtung5, richtung20, volumen_faktor)
  WITH med AS (
    SELECT e.ausloeser, e.asset_class,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.r1)  AS m1,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.r5)  AS m5,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.r20) AS m20,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY e.volumen_faktor) AS mvol,
           COUNT(*) AS ereignisse, COUNT(DISTINCT e.asset_id) AS werte,
           AVG(CASE WHEN f.r1  * e.richtung > 0 THEN 1.0 ELSE 0.0 END) AS ri1,
           AVG(CASE WHEN f.r5  * e.richtung > 0 THEN 1.0 ELSE 0.0 END) AS ri5,
           AVG(CASE WHEN f.r20 * e.richtung > 0 THEN 1.0 ELSE 0.0 END) AS ri20
      FROM bt_e e
      JOIN bt_fw f ON f.asset_id = e.asset_id AND f.ts_utc = e.ts_utc
     GROUP BY e.ausloeser, e.asset_class
  ),
  medl AS (
    SELECT s.asset_class,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.r1)  AS l1,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.r5)  AS l5,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.r20) AS l20
      FROM bt_s s
      JOIN bt_fw f ON f.asset_id = s.asset_id AND f.ts_utc = s.ts_utc
     WHERE s.ts_utc >= v_von
     GROUP BY s.asset_class
  )
  SELECT v_run, m.ausloeser, m.asset_class, m.ereignisse, m.werte,
         m.m1, m.m5, m.m20, l.l1, l.l5, l.l20,
         m.ri1, m.ri5, m.ri20, m.mvol
    FROM med m
    JOIN medl l ON l.asset_class = m.asset_class;

  UPDATE dbo.bot_trigger_run
     SET finished_utc = (now() AT TIME ZONE 'utc'),
         note = 'Fenster überlappen — die Anzahl ist keine Fallzahl. '
             || 'Verglichen wird gegen alle Tage derselben Werte im selben Zeitraum. '
             || 'Gerechnet wird mit dem MEDIAN — ein Mittelwert beschreibt bei diesen '
             || 'Verteilungen den Rand und nicht die Mitte.'
   WHERE run_id = v_run;

  RETURN v_run;
END $$;

-- ------------------------------------------------------- run_reversal_test --
CREATE OR REPLACE FUNCTION dbo.run_reversal_test(
  p_horizon_days integer DEFAULT 28, p_min_crossings integer DEFAULT 8, p_interval text DEFAULT '1d')
RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
  v_run integer;
BEGIN
  INSERT INTO dbo.reversal_run (interval_code, horizon_days, min_crossings)
  VALUES (p_interval, p_horizon_days, p_min_crossings) RETURNING run_id INTO v_run;

  DROP TABLE IF EXISTS rv_ziel; DROP TABLE IF EXISTS rv_fw;

  /*  Vorwaertsrendite je Wert und Tag ueber p_horizon_days KALENDERtage --
      Kalendertage statt gemeinsamer Bars, siehe 026.                        */
  CREATE TEMP TABLE rv_ziel ON COMMIT DROP AS
    SELECT p.asset_id, p.ts_utc, CAST(p."close" AS double precision) AS c0, MAX(q.ts_utc) AS ts1
      FROM dbo.price_bar p
      JOIN dbo.price_bar q ON q.asset_id = p.asset_id AND q.interval_code = p_interval
                           AND q.ts_utc > p.ts_utc
                           AND q.ts_utc <= p.ts_utc + make_interval(days => p_horizon_days)
                           AND q."close" > 0
     WHERE p.interval_code = p_interval AND p."close" > 0
     GROUP BY p.asset_id, p.ts_utc, p."close";

  CREATE TEMP TABLE rv_fw ON COMMIT DROP AS
    SELECT z.asset_id, z.ts_utc, CAST(b."close" AS double precision) / z.c0 - 1 AS fw
      FROM rv_ziel z
      JOIN dbo.price_bar b ON b.asset_id = z.asset_id AND b.interval_code = p_interval
                           AND b.ts_utc = z.ts1;
  CREATE INDEX ON rv_fw (asset_id, ts_utc);

  INSERT INTO dbo.reversal_pair (run_id, asset_id_a, asset_id_b, n, hit_rate, mean_gain, z)
  SELECT v_run, c.asset_id_a, c.asset_id_b,
         COUNT(*),
         AVG(CASE WHEN (CASE WHEN c.direction = 1 THEN fa.fw - fb.fw ELSE fb.fw - fa.fw END) > 0
                  THEN 1.0 ELSE 0.0 END),
         AVG(CASE WHEN c.direction = 1 THEN fa.fw - fb.fw ELSE fb.fw - fa.fw END),
         (AVG(CASE WHEN (CASE WHEN c.direction = 1 THEN fa.fw - fb.fw ELSE fb.fw - fa.fw END) > 0
                   THEN 1.0 ELSE 0.0 END) * COUNT(*) - COUNT(*) / 2.0)
           / SQRT(COUNT(*) / 4.0)
    FROM dbo.crossing c
    JOIN rv_fw fa ON fa.asset_id = c.asset_id_a AND fa.ts_utc = c.ts_utc
    JOIN rv_fw fb ON fb.asset_id = c.asset_id_b AND fb.ts_utc = c.ts_utc
   WHERE c.interval_code = p_interval
   GROUP BY c.asset_id_a, c.asset_id_b
  HAVING COUNT(*) >= p_min_crossings;

  UPDATE dbo.reversal_run r
     SET finished_utc = (now() AT TIME ZONE 'utc'),
         pairs_tested = x.n,
         mean_hit_rate = x.mittel,
         sig_negative = x.neg,
         sig_positive = x.pos,
         expected_bychance = CAST(x.n * 0.025 AS integer),
         note = 'Erwartet werden je Seite 2,5 Prozent der Paare allein durch Zufall. '
             || 'Liegt die beobachtete Zahl darunter, gibt es kein invertierbares Signal.'
    FROM (SELECT COUNT(*) AS n, AVG(hit_rate) AS mittel,
                 SUM(CASE WHEN z < -1.96 THEN 1 ELSE 0 END) AS neg,
                 SUM(CASE WHEN z >  1.96 THEN 1 ELSE 0 END) AS pos
            FROM dbo.reversal_pair WHERE run_id = v_run) x
   WHERE r.run_id = v_run;

  RETURN v_run;
END $$;

-- -------------------------------------------------- run_timezone_lead_test --
CREATE OR REPLACE FUNCTION dbo.run_timezone_lead_test(p_jahre integer DEFAULT 10)
RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
  v_von timestamp := (now() AT TIME ZONE 'utc') - make_interval(years => p_jahre);
  v_run integer;
BEGIN
  INSERT INTO dbo.timezone_lead_run (von_utc) VALUES (v_von) RETURNING run_id INTO v_run;

  DROP TABLE IF EXISTS tz_region; DROP TABLE IF EXISTS tz_w; DROP TABLE IF EXISTS tz_r;

  /*  Regionen mit Eroeffnung UND Schluss in UTC. Die Reihenfolge kommt HIER
      her, nicht aus dem gerasterten Zeitstempel; und ohne die Eroeffnung
      liesse sich Ueberlappung nicht von Vorlauf trennen (siehe 029).        */
  CREATE TEMP TABLE tz_region (land varchar(8), region varchar(24), oeffnung_utc integer, schluss_utc integer) ON COMMIT DROP;
  INSERT INTO tz_region VALUES
    ('JP', 'Asien',  0,  6), ('HK', 'Asien',  1,  8), ('AU', 'Asien', 0, 6),
    ('DE', 'Europa', 7, 16), ('AT', 'Europa', 7, 16), ('CH', 'Europa', 7, 16),
    ('FR', 'Europa', 7, 16), ('NL', 'Europa', 7, 16), ('IT', 'Europa', 7, 16),
    ('ES', 'Europa', 7, 16), ('GB', 'Europa', 7, 16), ('SE', 'Europa', 7, 16),
    ('DK', 'Europa', 7, 16), ('NO', 'Europa', 7, 16), ('FI', 'Europa', 7, 16),
    ('US', 'Amerika', 13, 20), ('CA', 'Amerika', 13, 20);

  -- Renditen je Wert und Tag; LN, nicht LOG -- in Postgres ist LOG Basis 10.
  CREATE TEMP TABLE tz_w ON COMMIT DROP AS
    SELECT p.asset_id, r.region, CAST(p.ts_utc AS date) AS tag,
           LN(CAST(p."close" AS double precision)
              / LAG(CAST(p."close" AS double precision)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc)) AS r_cc,
           LN(CAST(p."open" AS double precision)
              / LAG(CAST(p."close" AS double precision)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc)) AS r_co,
           LN(CAST(p."close" AS double precision) / CAST(p."open" AS double precision)) AS r_oc
      FROM dbo.price_bar p
      JOIN dbo.asset a ON a.asset_id = p.asset_id AND a.is_tracked
                       AND a.asset_class IN (0, 1)
      JOIN tz_region r ON r.land = a.country
     WHERE p.interval_code = '1d' AND p.ts_utc >= v_von
       AND p."close" > 0 AND p."open" > 0;

  -- Je Region und Tag der GLEICH gewichtete Durchschnitt.
  CREATE TEMP TABLE tz_r ON COMMIT DROP AS
    SELECT region, tag, COUNT(*) AS werte,
           AVG(r_cc) AS r_cc, AVG(r_co) AS r_co, AVG(r_oc) AS r_oc
      FROM tz_w
     WHERE r_cc IS NOT NULL AND r_co IS NOT NULL AND r_oc IS NOT NULL
     GROUP BY region, tag;
  CREATE INDEX ON tz_r (region, tag);

  INSERT INTO dbo.timezone_lead_stat
    (run_id, region_frueh, region_spaet, stunden_vorsprung, tage,
     werte_frueh, werte_spaet, korr_cc, korr_gap, korr_handelbar, korr_gegenprobe,
     stunden_ueberlappung)
  SELECT v_run, f.region, s.region,
         MAX(sz.schluss) - MAX(fz.schluss),
         COUNT(*), AVG(f.werte), AVG(s.werte),
         (AVG(f.r_cc * s.r_cc) - AVG(f.r_cc) * AVG(s.r_cc))
           / NULLIF(STDDEV_POP(f.r_cc) * STDDEV_POP(s.r_cc), 0),
         (AVG(f.r_cc * s.r_co) - AVG(f.r_cc) * AVG(s.r_co))
           / NULLIF(STDDEV_POP(f.r_cc) * STDDEV_POP(s.r_co), 0),
         (AVG(f.r_cc * s.r_oc) - AVG(f.r_cc) * AVG(s.r_oc))
           / NULLIF(STDDEV_POP(f.r_cc) * STDDEV_POP(s.r_oc), 0),
         -- Gegenprobe: die spaetere Region am VORTAG gegen die fruehere heute.
         (AVG(g.r_cc * f.r_cc) - AVG(g.r_cc) * AVG(f.r_cc))
           / NULLIF(STDDEV_POP(g.r_cc) * STDDEV_POP(f.r_cc), 0),
         -- Gemeinsame Handelszeit: groesser null heisst Gleichzeitigkeit, kein Vorlauf.
         CASE WHEN MAX(fz.schluss) > MAX(sz.oeffnung)
              THEN MAX(fz.schluss) - MAX(sz.oeffnung) ELSE 0 END
    FROM tz_r f
    JOIN tz_r s ON s.tag = f.tag AND s.region <> f.region
    JOIN (SELECT region, MAX(schluss_utc) AS schluss, MIN(oeffnung_utc) AS oeffnung
            FROM tz_region GROUP BY region) fz ON fz.region = f.region
    JOIN (SELECT region, MAX(schluss_utc) AS schluss, MIN(oeffnung_utc) AS oeffnung
            FROM tz_region GROUP BY region) sz ON sz.region = s.region
    LEFT JOIN tz_r g ON g.region = s.region AND g.tag = f.tag - 1
   WHERE sz.schluss > fz.schluss          -- nur echte Reihenfolge
   GROUP BY f.region, s.region
  HAVING COUNT(*) >= 200;

  UPDATE dbo.timezone_lead_run
     SET finished_utc = (now() AT TIME ZONE 'utc'),
         note = 'Die Reihenfolge kommt aus der Börse, nicht aus dem Zeitstempel — '
             || 'alle Tagesbars sind auf Stunde 0 gerastert. `korr_handelbar` ist '
             || 'die einzige Spalte, die eine Handlung zulässt: Was im '
             || 'Eröffnungssprung steckt, war zum Schluss des Vortages noch nicht da. '
             || 'Zeilen mit stunden_ueberlappung > 0 sind KEIN Vorlauf: Dort handeln '
             || 'beide Regionen zeitweise gleichzeitig.'
   WHERE run_id = v_run;

  RETURN v_run;
END $$;
