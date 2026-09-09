/*  Der Zeitzonen-Vorlauf.

    Die Frage kam vom Nutzer, und sie ist die erste in diesem Projekt, die der
    Datenbestand vorher gar nicht beantworten konnte: Ergaben sich vielleicht
    deshalb keine Prognosetendenzen, weil immer nur innerhalb der USA
    verglichen wurde?

    Der Mechanismus ist konkret. Bisher handelten alle 285 Werte im selben
    Fenster -- da KANN es keinen Vorlauf geben, weil es keine Reihenfolge gibt.
    Mit Tokio, Europa und New York gibt es eine:

        Tokio schliesst          06:00 UTC
        Xetra, Wien, Zuerich     15:30-16:30 UTC
        New York                 20:00 UTC

    Tokios Bar vom Tag d liegt vierzehn Stunden VOR New Yorks Bar vom Tag d.

    ACHTUNG, DIE FALLE: Alle Tagesbars sind auf Stunde 0 gerastert. Tokio und
    New York sitzen auf demselben Rasterpunkt. Wer daraus eine
    "gleichzeitige" Korrelation macht, misst in Wahrheit einen Vorlauf von
    vierzehn Stunden -- und wer daraus ein Merkmal baut, hat Lookahead in
    Reinform. Die Reihenfolge kommt deshalb aus der BOERSE und nicht aus dem
    Zeitstempel.

    ------------------------------------------------------------------------

    Gemessen werden drei Renditen je Region und Tag, gleich gewichtet ueber
    ihre Mitglieder:

        r_cc = log(Schluss_t / Schluss_(t-1))   der ganze Tag
        r_co = log(Eroeffnung_t / Schluss_(t-1)) der SPRUNG ueber Nacht
        r_oc = log(Schluss_t / Eroeffnung_t)     der HANDELBARE Teil

    Und daraus vier Zahlen je geordnetem Regionspaar (E frueher, L spaeter):

        1. corr(rE_cc, rL_cc)  -- die naive Zahl, vom Sprung aufgeblaeht
        2. corr(rE_cc, rL_co)  -- landet die Information im Eroeffnungssprung?
        3. corr(rE_cc, rL_oc)  -- bleibt danach noch etwas HANDELBARES uebrig?
        4. corr(rL_cc(d-1), rE_cc(d)) -- die Gegenprobe: umgekehrte Richtung

    Der erwartete Lehrbuchbefund: (2) gross, (3) nahe null. Dann ist die
    Information vollstaendig im Eroeffnungskurs eingepreist und nicht mehr zu
    handeln -- man haette zum Schluss des Vortages kaufen muessen, und da war
    sie noch nicht da.

    Zahl (4) ist die Gegenprobe, ohne die der Rest nichts wert waere: Findet
    sie AUCH etwas, misst man einen gemeinsamen Faktor und keinen Vorlauf.   */
USE stockcrawler;
GO

IF OBJECT_ID('dbo.timezone_lead_run') IS NULL
CREATE TABLE dbo.timezone_lead_run (
  run_id       INT IDENTITY(1,1) NOT NULL,
  started_utc  DATETIME2(0) NOT NULL CONSTRAINT DF_tzl_start DEFAULT(SYSUTCDATETIME()),
  finished_utc DATETIME2(0) NULL,
  von_utc      DATETIME2(0) NOT NULL,
  note         NVARCHAR(600) NULL,
  CONSTRAINT PK_timezone_lead_run PRIMARY KEY (run_id)
);
GO

IF OBJECT_ID('dbo.timezone_lead_stat') IS NULL
CREATE TABLE dbo.timezone_lead_stat (
  run_id       INT          NOT NULL,
  region_frueh NVARCHAR(24) NOT NULL,
  region_spaet NVARCHAR(24) NOT NULL,
  stunden_vorsprung INT     NOT NULL,
  tage         INT          NOT NULL,
  werte_frueh  INT          NOT NULL,
  werte_spaet  INT          NOT NULL,
  korr_cc      FLOAT NULL,   -- naiv: ganzer Tag gegen ganzen Tag
  korr_gap     FLOAT NULL,   -- gegen den Eroeffnungssprung
  korr_handelbar FLOAT NULL, -- gegen Eroeffnung-bis-Schluss
  korr_gegenprobe FLOAT NULL,-- umgekehrte Richtung, Folgetag
  stunden_ueberlappung INT NULL, -- gemeinsame Handelszeit: dann KEIN Vorlauf
  CONSTRAINT PK_timezone_lead_stat PRIMARY KEY (run_id, region_frueh, region_spaet),
  CONSTRAINT FK_tzls_run FOREIGN KEY (run_id) REFERENCES dbo.timezone_lead_run(run_id)
);
GO

CREATE OR ALTER PROCEDURE dbo.run_timezone_lead_test
  @jahre INT = 10
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @von DATETIME2(0) = DATEADD(YEAR, -@jahre, SYSUTCDATETIME());
  DECLARE @run INT;

  INSERT INTO dbo.timezone_lead_run (von_utc) VALUES (@von);
  SET @run = SCOPE_IDENTITY();

  /*  Regionen und ihr Schluss in UTC. Die Reihenfolge kommt HIER her und
      nicht aus dem Zeitstempel -- der ist gerastert und weiss nichts davon. */
  /*  Neben dem Schluss braucht es die EROEFFNUNG -- sonst laesst sich nicht
      sagen, ob zwei Regionen ueberlappen. Und das ist entscheidend: Xetra
      schliesst 16:30 UTC, die NYSE oeffnet 13:30. Drei Stunden handeln beide
      zugleich. Europas Tagesrendite enthaelt damit Zeit, die INNERHALB des
      amerikanischen Fensters liegt -- was dort als "Vorlauf" erscheint, ist
      Gleichzeitigkeit. Ohne diese Spalte haette die Zeile Europa -> Amerika
      mit 0,197 handelbarer Korrelation wie ein Fund ausgesehen.            */
  DECLARE @region TABLE (land NVARCHAR(8), region NVARCHAR(24),
                         oeffnung_utc INT, schluss_utc INT);
  INSERT INTO @region VALUES
    (N'JP', N'Asien',  0,  6), (N'HK', N'Asien',  1,  8), (N'AU', N'Asien', 0, 6),
    (N'DE', N'Europa', 7, 16), (N'AT', N'Europa', 7, 16), (N'CH', N'Europa', 7, 16),
    (N'FR', N'Europa', 7, 16), (N'NL', N'Europa', 7, 16), (N'IT', N'Europa', 7, 16),
    (N'ES', N'Europa', 7, 16), (N'GB', N'Europa', 7, 16), (N'SE', N'Europa', 7, 16),
    (N'DK', N'Europa', 7, 16), (N'NO', N'Europa', 7, 16), (N'FI', N'Europa', 7, 16),
    (N'US', N'Amerika', 13, 20), (N'CA', N'Amerika', 13, 20);

  /*  Renditen je Wert und Tag. Nur Werte mit Land -- Krypto und die
      Sammelfonds haben keine Boerse mit Schlusszeit und gehoeren nicht in
      diese Rechnung. Krypto handelt durchgehend; ein "Eroeffnungssprung"
      existiert dort nicht.                                                 */
  SELECT p.asset_id, r.region, CAST(p.ts_utc AS DATE) AS tag,
         LOG(CAST(p.[close] AS FLOAT)
             / LAG(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc)) AS r_cc,
         LOG(CAST(p.[open] AS FLOAT)
             / LAG(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc)) AS r_co,
         LOG(CAST(p.[close] AS FLOAT) / CAST(p.[open] AS FLOAT)) AS r_oc
    INTO #w
    FROM dbo.price_bar p
    JOIN dbo.asset a ON a.asset_id = p.asset_id AND a.is_tracked = 1
                     AND a.asset_class IN (0, 1)
    JOIN @region r ON r.land = a.country
   WHERE p.interval_code = '1d' AND p.ts_utc >= @von
     AND p.[close] > 0 AND p.[open] > 0;

  /*  Je Region und Tag der gleich gewichtete Durchschnitt. Gleich gewichtet
      und nicht nach Marktkapitalisierung: Sonst misst man in Amerika im
      Wesentlichen die sieben groessten Technologiewerte.                    */
  SELECT region, tag, COUNT(*) AS werte,
         AVG(r_cc) AS r_cc, AVG(r_co) AS r_co, AVG(r_oc) AS r_oc
    INTO #r
    FROM #w
   WHERE r_cc IS NOT NULL AND r_co IS NOT NULL AND r_oc IS NOT NULL
   GROUP BY region, tag;

  CREATE INDEX IX_r ON #r(region, tag);

  INSERT INTO dbo.timezone_lead_stat
    (run_id, region_frueh, region_spaet, stunden_vorsprung, tage,
     werte_frueh, werte_spaet, korr_cc, korr_gap, korr_handelbar, korr_gegenprobe,
     stunden_ueberlappung)
  SELECT @run, f.region, s.region,
         MAX(sz.schluss) - MAX(fz.schluss),
         COUNT(*), AVG(f.werte), AVG(s.werte),
         (AVG(f.r_cc * s.r_cc) - AVG(f.r_cc) * AVG(s.r_cc))
           / NULLIF(STDEVP(f.r_cc) * STDEVP(s.r_cc), 0),
         (AVG(f.r_cc * s.r_co) - AVG(f.r_cc) * AVG(s.r_co))
           / NULLIF(STDEVP(f.r_cc) * STDEVP(s.r_co), 0),
         (AVG(f.r_cc * s.r_oc) - AVG(f.r_cc) * AVG(s.r_oc))
           / NULLIF(STDEVP(f.r_cc) * STDEVP(s.r_oc), 0),
         /* Gegenprobe: die spaetere Region am VORTAG gegen die fruehere heute.
            Das ist die bekannte, unbestrittene Richtung -- sie muss etwas
            finden, sonst ist die Rechnung kaputt und nicht der Markt leer. */
         (AVG(g.r_cc * f.r_cc) - AVG(g.r_cc) * AVG(f.r_cc))
           / NULLIF(STDEVP(g.r_cc) * STDEVP(f.r_cc), 0),
         /* Gemeinsame Handelszeit. Ist sie groesser als null, ist die Zeile
            KEIN Vorlauf, sondern zu einem Teil Gleichzeitigkeit. */
         CASE WHEN MAX(fz.schluss) > MAX(sz.oeffnung)
              THEN MAX(fz.schluss) - MAX(sz.oeffnung) ELSE 0 END
    FROM #r f
    JOIN #r s ON s.tag = f.tag AND s.region <> f.region
    JOIN (SELECT region, MAX(schluss_utc) AS schluss, MIN(oeffnung_utc) AS oeffnung
            FROM @region GROUP BY region) fz ON fz.region = f.region
    JOIN (SELECT region, MAX(schluss_utc) AS schluss, MIN(oeffnung_utc) AS oeffnung
            FROM @region GROUP BY region) sz ON sz.region = s.region
    LEFT JOIN #r g ON g.region = s.region AND g.tag = DATEADD(DAY, -1, f.tag)
   WHERE sz.schluss > fz.schluss          -- nur echte Reihenfolge
   GROUP BY f.region, s.region
  HAVING COUNT(*) >= 200;

  UPDATE dbo.timezone_lead_run
     SET finished_utc = SYSUTCDATETIME(),
         note = N'Die Reihenfolge kommt aus der Börse, nicht aus dem Zeitstempel — '
              + N'alle Tagesbars sind auf Stunde 0 gerastert. `korr_handelbar` ist '
              + N'die einzige Spalte, die eine Handlung zulässt: Was im '
              + N'Eröffnungssprung steckt, war zum Schluss des Vortages noch nicht da. '
              + N'Zeilen mit stunden_ueberlappung > 0 sind KEIN Vorlauf: Dort handeln '
              + N'beide Regionen zeitweise gleichzeitig.'
   WHERE run_id = @run;

  DROP TABLE #w; DROP TABLE #r;

  SELECT @run AS run_id;
END
GO
