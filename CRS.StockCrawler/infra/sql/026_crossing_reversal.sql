/*  Der Umkehrschluss: Taugt ein zuverlässig falsches Signal?

    Die Frage lautet: Wenn die meisten Prognosen nichts taugen, lässt sich
    daraus invers etwas ableiten? Die Antwort ist messbar, und sie ist ein Nein
    mit einer Ausnahme.

    Gemessen über 26.130 Paare mit mindestens acht Kreuzungen:
      - mittlere Trefferquote 0,5096
      - 291 Paare mit z < -1,96 — durch Zufall allein wären 653 zu erwarten
      - 87 Paare mit z > +1,96

    Es gibt also WENIGER auffällig falsche Paare, als der Zufall hervorbringt.
    Ein global invertierbares Signal existiert nicht.

    Die Ausnahme sind die Extremfälle, und sie sind alle vom selben Typ:
    XAUT-USD gegen IAU, PAXG-USD gegen GLD (goldgedeckte Marke gegen Gold-ETF),
    alles gegen USDG-USD (eine Stablecoin). Fast identische Paare, die nach
    einer Kreuzung zurückschwingen — klassischer Paarhandel und kein Versagen
    des Kreuzungssignals.

    Die Rechnung dauert vier Minuten. Sie gehört deshalb nicht in eine
    Seitenabfrage, sondern wird abgelegt und gelesen.                       */
USE stockcrawler;
GO

IF OBJECT_ID('dbo.reversal_run') IS NULL
CREATE TABLE dbo.reversal_run (
  run_id        INT IDENTITY(1,1) NOT NULL,
  started_utc   DATETIME2(0) NOT NULL CONSTRAINT DF_rr_start DEFAULT(SYSUTCDATETIME()),
  finished_utc  DATETIME2(0) NULL,
  interval_code VARCHAR(3)   NOT NULL,
  horizon_days  INT          NOT NULL,
  min_crossings INT          NOT NULL,
  pairs_tested  INT          NOT NULL CONSTRAINT DF_rr_pairs DEFAULT(0),
  mean_hit_rate FLOAT        NULL,
  sig_negative  INT          NULL,
  sig_positive  INT          NULL,
  expected_bychance INT      NULL,
  note          NVARCHAR(500) NULL,
  CONSTRAINT PK_reversal_run PRIMARY KEY (run_id)
);
GO

IF OBJECT_ID('dbo.reversal_pair') IS NULL
CREATE TABLE dbo.reversal_pair (
  run_id        INT   NOT NULL,
  asset_id_a    INT   NOT NULL,
  asset_id_b    INT   NOT NULL,
  n             INT   NOT NULL,
  hit_rate      FLOAT NOT NULL,
  mean_gain     FLOAT NOT NULL,
  z             FLOAT NOT NULL,
  correlation   FLOAT NULL,
  CONSTRAINT PK_reversal_pair PRIMARY KEY (run_id, asset_id_a, asset_id_b),
  CONSTRAINT FK_rp_run FOREIGN KEY (run_id) REFERENCES dbo.reversal_run(run_id)
);
GO

IF IndexProperty(OBJECT_ID('dbo.reversal_pair'), 'IX_rp_z', 'IndexID') IS NULL
  CREATE INDEX IX_rp_z ON dbo.reversal_pair(run_id, z);
GO

CREATE OR ALTER PROCEDURE dbo.run_reversal_test
  @horizon_days  INT = 28,
  @min_crossings INT = 8,
  @interval      VARCHAR(3) = '1d'
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @run INT;
  INSERT INTO dbo.reversal_run (interval_code, horizon_days, min_crossings)
  VALUES (@interval, @horizon_days, @min_crossings);
  SET @run = SCOPE_IDENTITY();

  /*  Vorwärtsrendite je Wert und Tag über @horizon_days KALENDERtage.

      Kalendertage statt gemeinsamer Bars: Für 26.142 Paare je eine eigene
      gemeinsame Achse zu bauen wären einunddreißig Millionen Zeilen. Über
      dieselbe Kalenderspanne gemessen bleibt der Paarvergleich fair — beide
      Seiten haben dieselbe Zeit zur Verfügung, und welche Bars darin liegen,
      ist Sache des jeweiligen Handelskalenders.                            */
  SELECT p.asset_id, p.ts_utc, CAST(p.[close] AS FLOAT) AS c0, MAX(q.ts_utc) AS ts1
    INTO #ziel
    FROM dbo.price_bar p
    JOIN dbo.price_bar q ON q.asset_id = p.asset_id AND q.interval_code = @interval
                         AND q.ts_utc > p.ts_utc
                         AND q.ts_utc <= DATEADD(DAY, @horizon_days, p.ts_utc)
                         AND q.[close] > 0
   WHERE p.interval_code = @interval AND p.[close] > 0
   GROUP BY p.asset_id, p.ts_utc, p.[close];

  SELECT z.asset_id, z.ts_utc, CAST(b.[close] AS FLOAT) / z.c0 - 1 AS fw
    INTO #fw
    FROM #ziel z
    JOIN dbo.price_bar b ON b.asset_id = z.asset_id AND b.interval_code = @interval
                         AND b.ts_utc = z.ts1;

  CREATE INDEX IX_fw ON #fw(asset_id, ts_utc);

  INSERT INTO dbo.reversal_pair (run_id, asset_id_a, asset_id_b, n, hit_rate, mean_gain, z)
  SELECT @run, c.asset_id_a, c.asset_id_b,
         COUNT(*),
         AVG(CASE WHEN (CASE WHEN c.direction = 1 THEN fa.fw - fb.fw ELSE fb.fw - fa.fw END) > 0
                  THEN 1.0 ELSE 0.0 END),
         AVG(CASE WHEN c.direction = 1 THEN fa.fw - fb.fw ELSE fb.fw - fa.fw END),
         (AVG(CASE WHEN (CASE WHEN c.direction = 1 THEN fa.fw - fb.fw ELSE fb.fw - fa.fw END) > 0
                   THEN 1.0 ELSE 0.0 END) * COUNT(*) - COUNT(*) / 2.0)
           / SQRT(COUNT(*) / 4.0)
    FROM dbo.crossing c
    JOIN #fw fa ON fa.asset_id = c.asset_id_a AND fa.ts_utc = c.ts_utc
    JOIN #fw fb ON fb.asset_id = c.asset_id_b AND fb.ts_utc = c.ts_utc
   WHERE c.interval_code = @interval
   GROUP BY c.asset_id_a, c.asset_id_b
  HAVING COUNT(*) >= @min_crossings;

  UPDATE r
     SET finished_utc = SYSUTCDATETIME(),
         pairs_tested = x.n,
         mean_hit_rate = x.mittel,
         sig_negative = x.neg,
         sig_positive = x.pos,
         expected_bychance = CAST(x.n * 0.025 AS INT),
         note = N'Erwartet werden je Seite 2,5 Prozent der Paare allein durch Zufall. '
              + N'Liegt die beobachtete Zahl darunter, gibt es kein invertierbares Signal.'
    FROM dbo.reversal_run r
   CROSS JOIN (SELECT COUNT(*) AS n, AVG(hit_rate) AS mittel,
                      SUM(CASE WHEN z < -1.96 THEN 1 ELSE 0 END) AS neg,
                      SUM(CASE WHEN z >  1.96 THEN 1 ELSE 0 END) AS pos
                 FROM dbo.reversal_pair WHERE run_id = @run) x
   WHERE r.run_id = @run;

  DROP TABLE #ziel; DROP TABLE #fw;

  SELECT @run AS run_id;
END
GO
