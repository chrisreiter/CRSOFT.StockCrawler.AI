/*  Die Spur der Bot-Herde.

    Die Überlegung: Hunderte automatischer Handelssysteme folgen denselben,
    öffentlich bekannten Auslösern — dem Kreuzen zweier gleitender Mittel, dem
    Über- oder Unterschreiten einer RSI-Schwelle, dem Ausbruch aus einem
    Bollinger-Band, dem neuen Jahreshoch. Wenn viele zugleich handeln, müsste
    das eine Spur hinterlassen: auffälliges Volumen am Auslösetag und eine
    Bewegung, die sich von einem beliebigen Tag unterscheidet.

    Das ist prüfbar, und genau darum geht es hier. Gemessen wird je Auslöser:

      - die mittlere Rendite über 1, 5 und 20 Handelstage NACH dem Auslöser
      - dieselbe Größe über ALLE Tage derselben Werte im selben Zeitraum
        (die Vergleichslatte -- ohne sie misst man den Marktgang)
      - das Volumen am Auslösetag gegen das übliche Volumen desselben Wertes

    Und dann die Frage, die zuletzt entscheidet: Ist der Unterschied größer als
    ein Rundlauf aus Gebühren und Schlupf? Ein Effekt von 0,05 Prozent ist
    wissenschaftlich vielleicht interessant und praktisch nicht vorhanden.

    ACHTUNG bei der Deutung: Die Fenster überlappen. Ein t-Wert über
    überlappende Folgerenditen ist um rund die Wurzel der Fensterlänge zu hoch
    — dieselbe Falle wie beim Buchgewinn-Merkmal, wo aus t = 10,56 nach der
    Korrektur t = 1,64 wurde. Die Anzahl steht deshalb dabei, ein t-Wert nicht. */
USE stockcrawler;
GO

IF OBJECT_ID('dbo.bot_trigger_run') IS NULL
CREATE TABLE dbo.bot_trigger_run (
  run_id       INT IDENTITY(1,1) NOT NULL,
  started_utc  DATETIME2(0) NOT NULL CONSTRAINT DF_btr_start DEFAULT(SYSUTCDATETIME()),
  finished_utc DATETIME2(0) NULL,
  von_utc      DATETIME2(0) NOT NULL,
  note         NVARCHAR(500) NULL,
  CONSTRAINT PK_bot_trigger_run PRIMARY KEY (run_id)
);
GO

IF OBJECT_ID('dbo.bot_trigger_stat') IS NULL
CREATE TABLE dbo.bot_trigger_stat (
  run_id        INT           NOT NULL,
  ausloeser     NVARCHAR(60)  NOT NULL,
  klasse        TINYINT       NOT NULL,
  ereignisse    INT           NOT NULL,
  werte         INT           NOT NULL,
  -- Rendite nach dem Ausloeser
  r1            FLOAT NULL, r5  FLOAT NULL, r20 FLOAT NULL,
  -- Dieselbe Groesse ueber alle Tage derselben Werte: die Vergleichslatte
  b1            FLOAT NULL, b5  FLOAT NULL, b20 FLOAT NULL,
  -- Trefferquote der Richtung, die der Ausloeser nahelegt
  richtung1     FLOAT NULL, richtung5 FLOAT NULL, richtung20 FLOAT NULL,
  -- Volumen am Ausloesetag, als Vielfaches des ueblichen
  volumen_faktor FLOAT NULL,
  CONSTRAINT PK_bot_trigger_stat PRIMARY KEY (run_id, ausloeser, klasse),
  CONSTRAINT FK_bts_run FOREIGN KEY (run_id) REFERENCES dbo.bot_trigger_run(run_id)
);
GO

CREATE OR ALTER PROCEDURE dbo.run_bot_trigger_test
  @jahre INT = 5
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @von DATETIME2(0) = DATEADD(YEAR, -@jahre, SYSUTCDATETIME());
  DECLARE @run INT;

  INSERT INTO dbo.bot_trigger_run (von_utc) VALUES (@von);
  SET @run = SCOPE_IDENTITY();

  /* ------------------------------------------------- Kennzahlen je Bar --- */
  SELECT p.asset_id, a.asset_class, p.ts_utc,
         CAST(p.[close] AS FLOAT) AS c,
         CAST(p.volume AS FLOAT)  AS v,
         ROW_NUMBER() OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc) AS rn,
         AVG(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
              ROWS BETWEEN 49 PRECEDING AND CURRENT ROW) AS sma50,
         AVG(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
              ROWS BETWEEN 199 PRECEDING AND CURRENT ROW) AS sma200,
         AVG(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
              ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS sma20,
         STDEV(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
              ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS sd20,
         MAX(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
              ROWS BETWEEN 251 PRECEDING AND 1 PRECEDING) AS hoch252,
         AVG(CAST(p.volume AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
              ROWS BETWEEN 60 PRECEDING AND 1 PRECEDING) AS vol60,
         LAG(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc) AS c_vor
    INTO #k
    FROM dbo.price_bar p
    JOIN dbo.asset a ON a.asset_id = p.asset_id AND a.is_tracked = 1
   WHERE p.interval_code = '1d' AND p.ts_utc >= DATEADD(YEAR, -1, @von)
     AND p.[close] > 0;

  /* RSI über vierzehn Bars, gleitendes einfaches Mittel der Auf- und
     Abwärtsbewegungen. Nicht Wilders Glättung — die läuft rekursiv und ist in
     einer Mengenabfrage nicht auszudrücken. Für die Frage „wo liegt die
     Schwelle, an der Systeme auslösen" reicht die einfache Fassung; sie
     unterscheidet sich nur in der Trägheit, nicht im Vorzeichen. */
  SELECT k.*,
         AVG(CASE WHEN k.c > k.c_vor THEN k.c - k.c_vor ELSE 0 END)
             OVER (PARTITION BY k.asset_id ORDER BY k.ts_utc
                   ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) AS auf14,
         AVG(CASE WHEN k.c < k.c_vor THEN k.c_vor - k.c ELSE 0 END)
             OVER (PARTITION BY k.asset_id ORDER BY k.ts_utc
                   ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) AS ab14
    INTO #kr
    FROM #k k;

  SELECT r.*,
         CASE WHEN r.auf14 + r.ab14 > 0 THEN 100.0 * r.auf14 / (r.auf14 + r.ab14) END AS rsi14,
         LAG(r.sma50 - r.sma200) OVER (PARTITION BY r.asset_id ORDER BY r.ts_utc) AS ma_vor,
         LAG(CASE WHEN r.auf14 + r.ab14 > 0 THEN 100.0 * r.auf14 / (r.auf14 + r.ab14) END)
             OVER (PARTITION BY r.asset_id ORDER BY r.ts_utc) AS rsi_vor
    INTO #s
    FROM #kr r;

  /* ------------------------------------------------- Folgerenditen ------- */
  SELECT s.asset_id, s.ts_utc, s.rn,
         f1.c / s.c - 1 AS r1, f5.c / s.c - 1 AS r5, f20.c / s.c - 1 AS r20
    INTO #fw
    FROM #s s
    LEFT JOIN #s f1  ON f1.asset_id  = s.asset_id AND f1.rn  = s.rn + 1
    LEFT JOIN #s f5  ON f5.asset_id  = s.asset_id AND f5.rn  = s.rn + 5
    LEFT JOIN #s f20 ON f20.asset_id = s.asset_id AND f20.rn = s.rn + 20;

  CREATE INDEX IX_fw2 ON #fw(asset_id, ts_utc);

  /* ------------------------------------------------- Auslöser ------------ */
  /* `richtung` ist +1, wenn der Auslöser Aufwärts nahelegt, sonst -1. Ohne
     dieses Vorzeichen wäre die Trefferquote eines Verkaufssignals systematisch
     falsch herum gelesen. */
  SELECT ausloeser, richtung, asset_id, asset_class, ts_utc, volumen_faktor
    INTO #e
    FROM (
      SELECT N'Goldenes Kreuz (SMA 50 über 200)' AS ausloeser, 1 AS richtung,
             s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END AS volumen_faktor
        FROM #s s
       WHERE s.ts_utc >= @von AND s.ma_vor < 0 AND s.sma50 - s.sma200 > 0

      UNION ALL
      SELECT N'Todeskreuz (SMA 50 unter 200)', -1, s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END
        FROM #s s
       WHERE s.ts_utc >= @von AND s.ma_vor > 0 AND s.sma50 - s.sma200 < 0

      UNION ALL
      SELECT N'RSI unter 30 (überverkauft)', 1, s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END
        FROM #s s
       WHERE s.ts_utc >= @von AND s.rsi_vor >= 30 AND s.rsi14 < 30

      UNION ALL
      SELECT N'RSI über 70 (überkauft)', -1, s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END
        FROM #s s
       WHERE s.ts_utc >= @von AND s.rsi_vor <= 70 AND s.rsi14 > 70

      UNION ALL
      SELECT N'Bollinger-Ausbruch nach oben', 1, s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END
        FROM #s s
       WHERE s.ts_utc >= @von AND s.sd20 > 0 AND s.c > s.sma20 + 2 * s.sd20

      UNION ALL
      SELECT N'Bollinger-Ausbruch nach unten', -1, s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END
        FROM #s s
       WHERE s.ts_utc >= @von AND s.sd20 > 0 AND s.c < s.sma20 - 2 * s.sd20

      UNION ALL
      SELECT N'Neues 52-Wochen-Hoch', 1, s.asset_id, s.asset_class, s.ts_utc,
             CASE WHEN s.vol60 > 0 THEN s.v / s.vol60 END
        FROM #s s
       WHERE s.ts_utc >= @von AND s.hoch252 > 0 AND s.c > s.hoch252
    ) x;

  /* Die Vergleichslatte steht unten in `medl`: der Median über ALLE Tage
     derselben Werte im selben Zeitraum. Ohne sie misst man den Marktgang --
     in einem Jahr, in dem alles steigt, sieht jeder Aufwärts-Auslöser gut aus.

  */

  /*  MEDIAN, nicht Mittelwert.

      Der erste Entwurf mittelte -- und lieferte für Krypto: „RSI unter 30"
      +45,0 Prozent am Folgetag und einen Volumenfaktor von 15.591. Beides ist
      kein Marktverhalten, sondern das Ende sterbender Kleinstwerte: Ein Kurs
      von 0,0000 erzeugt beim kleinsten Sprung dreistellige Prozentzahlen, und
      ein Volumen gegen ein Durchschnittsvolumen nahe null ergibt beliebig
      große Vielfache.

      Ein Mittelwert über eine Verteilung mit solchen Rändern beschreibt den
      Rand, nicht die Mitte. Der Median tut es -- und zwar ohne dass man erst
      eine Ausschlussregel für „tote" Werte erfinden und begründen muss.      */
  WITH med AS (
    SELECT e.ausloeser, e.asset_class,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.r1)
             OVER (PARTITION BY e.ausloeser, e.asset_class) AS m1,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.r5)
             OVER (PARTITION BY e.ausloeser, e.asset_class) AS m5,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.r20)
             OVER (PARTITION BY e.ausloeser, e.asset_class) AS m20,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY e.volumen_faktor)
             OVER (PARTITION BY e.ausloeser, e.asset_class) AS mvol
      FROM #e e
      JOIN #fw f ON f.asset_id = e.asset_id AND f.ts_utc = e.ts_utc
  ),
  medl AS (
    SELECT s.asset_class,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.r1)
             OVER (PARTITION BY s.asset_class) AS l1,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.r5)
             OVER (PARTITION BY s.asset_class) AS l5,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.r20)
             OVER (PARTITION BY s.asset_class) AS l20
      FROM #s s
      JOIN #fw f ON f.asset_id = s.asset_id AND f.ts_utc = s.ts_utc
     WHERE s.ts_utc >= @von
  ),
  zahl AS (
    SELECT e.ausloeser, e.asset_class,
           COUNT(*) AS ereignisse, COUNT(DISTINCT e.asset_id) AS werte,
           AVG(CASE WHEN f.r1  * e.richtung > 0 THEN 1.0 ELSE 0.0 END) AS ri1,
           AVG(CASE WHEN f.r5  * e.richtung > 0 THEN 1.0 ELSE 0.0 END) AS ri5,
           AVG(CASE WHEN f.r20 * e.richtung > 0 THEN 1.0 ELSE 0.0 END) AS ri20
      FROM #e e
      JOIN #fw f ON f.asset_id = e.asset_id AND f.ts_utc = e.ts_utc
     GROUP BY e.ausloeser, e.asset_class
  )
  INSERT INTO dbo.bot_trigger_stat
    (run_id, ausloeser, klasse, ereignisse, werte, r1, r5, r20, b1, b5, b20,
     richtung1, richtung5, richtung20, volumen_faktor)
  SELECT DISTINCT @run, z.ausloeser, z.asset_class, z.ereignisse, z.werte,
         m.m1, m.m5, m.m20, l.l1, l.l5, l.l20,
         z.ri1, z.ri5, z.ri20, m.mvol
    FROM zahl z
    JOIN med m ON m.ausloeser = z.ausloeser AND m.asset_class = z.asset_class
    JOIN medl l ON l.asset_class = z.asset_class;

  UPDATE dbo.bot_trigger_run
     SET finished_utc = SYSUTCDATETIME(),
         note = N'Fenster überlappen — die Anzahl ist keine Fallzahl. '
              + N'Verglichen wird gegen alle Tage derselben Werte im selben Zeitraum. '
              + N'Gerechnet wird mit dem MEDIAN — ein Mittelwert beschreibt bei diesen '
              + N'Verteilungen den Rand und nicht die Mitte.'
   WHERE run_id = @run;

  DROP TABLE #k; DROP TABLE #kr; DROP TABLE #s; DROP TABLE #fw;
  DROP TABLE #e;

  SELECT @run AS run_id;
END
GO
