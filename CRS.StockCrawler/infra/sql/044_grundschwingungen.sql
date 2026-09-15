/*  044 -- Der Katalog der Grundschwingungen.

    WAS HIER ABGELEGT WIRD. Migration 017 legte `freq_pattern` und `freq_match`
    an -- und nichts hat sie je beschrieben: Die Auswertung hing an einem
    Endpunkt, der je Aufruf rechnete und das Ergebnis wegwarf. Jetzt gibt es
    einen Lauf ueber alle verfolgten Werte, und er hinterlaesst vier Dinge:

      freq_run      der Lauf selbst -- wann, wie viele Werte, Klassen, Paare
      freq_akkord   je Wert und Epoche der AKKORD: die ein bis drei staerksten
                    Grundperioden mit Amplitude und Lage am Epochenende
      freq_class    der Katalog: Akkorde, deren Perioden alle innerhalb von
                    zwoelf Prozent beieinanderliegen, sind eine Klasse
      freq_match    Paare von Werten, die dieselbe Klasse in mehreren Epochen
                    teilen, mit dem Versatz ihrer Grundtoene

    WARUM AKKORDE UND NICHT PERIODEN. Ein Chart ist in einer Epoche nicht durch
    eine Frequenz beschrieben, sondern durch das Zusammenspiel seiner
    staerksten. Zwei Werte mit Grundton 63 Bars sind sich aehnlich; zwei mit
    63 UND dem Oberton 31,5 sind sich aehnlicher; einer mit 63 und 47 ist ein
    anderes Muster -- er schwebt.

    WAS EIN FUND IST. Dass zwei Werte in einer Epoche in derselben Klasse
    landen, ist bei hunderten Werten Zufall. Gefuehrt wird deshalb, wie viele
    Epochen ein Paar die Klasse teilt und wie stark der Versatz dabei streut.
    `bestaendig` heisst: Versatz ueber die Epochen derselbe UND ungleich null
    -- nur das waere prognostisch ueberhaupt von Wert.

    Nur der juengste Lauf je Intervall bleibt stehen; ein neuer loescht den
    alten. Die Geschichte der Laeufe steht in `freq_run`.                    */
USE stockcrawler;
GO

IF OBJECT_ID('dbo.freq_run') IS NULL
CREATE TABLE dbo.freq_run (
  run_id        INT           IDENTITY(1,1) NOT NULL,
  interval_code VARCHAR(3)    NOT NULL,
  started_utc   DATETIME2(0)  NOT NULL CONSTRAINT DF_fr_started DEFAULT(SYSUTCDATETIME()),
  finished_utc  DATETIME2(0)  NULL,
  werte         INT           NOT NULL CONSTRAINT DF_fr_werte DEFAULT(0),
  epochen       INT           NOT NULL CONSTRAINT DF_fr_epochen DEFAULT(0),
  muster        INT           NOT NULL CONSTRAINT DF_fr_muster DEFAULT(0),
  akkorde       INT           NOT NULL CONSTRAINT DF_fr_akkorde DEFAULT(0),
  klassen       INT           NOT NULL CONSTRAINT DF_fr_klassen DEFAULT(0),
  paare         INT           NOT NULL CONSTRAINT DF_fr_paare DEFAULT(0),
  paare_bestaendig INT        NOT NULL CONSTRAINT DF_fr_pb DEFAULT(0),
  dauer_s       FLOAT         NULL,
  note          NVARCHAR(400) NULL,
  CONSTRAINT PK_freq_run PRIMARY KEY (run_id)
);
GO

IF OBJECT_ID('dbo.freq_class') IS NULL
CREATE TABLE dbo.freq_class (
  class_id      INT           IDENTITY(1,1) NOT NULL,
  run_id        INT           NOT NULL,
  nr            INT           NOT NULL,
  stimmen       TINYINT       NOT NULL,
  periode1      FLOAT         NOT NULL,
  periode2      FLOAT         NULL,
  periode3      FLOAT         NULL,
  /*  Amplituden relativ zum Grundton (der ist 1). Absolut waeren sie Kurs-
      niveau und zwischen Werten nicht vergleichbar.                        */
  amp2          FLOAT         NULL,
  amp3          FLOAT         NULL,
  /*  rein | oberton | schwebung | dreiklang -- siehe Grundschwingungen.Harmonik */
  harmonik      NVARCHAR(12)  NOT NULL,
  akkorde       INT           NOT NULL,
  werte         INT           NOT NULL,
  epochen       INT           NOT NULL,
  prominenz     FLOAT         NOT NULL,
  stabilitaet   FLOAT         NOT NULL,
  paare         INT           NOT NULL CONSTRAINT DF_fc_paare DEFAULT(0),
  paare_bestaendig INT        NOT NULL CONSTRAINT DF_fc_pb DEFAULT(0),
  CONSTRAINT PK_freq_class PRIMARY KEY (class_id),
  CONSTRAINT FK_freq_class_run FOREIGN KEY (run_id) REFERENCES dbo.freq_run(run_id)
);
GO

IF OBJECT_ID('dbo.freq_akkord') IS NULL
CREATE TABLE dbo.freq_akkord (
  akkord_id     BIGINT        IDENTITY(1,1) NOT NULL,
  run_id        INT           NOT NULL,
  class_id      INT           NULL,          -- NULL: keiner Klasse zugeordnet (Einzelfall)
  asset_id      INT           NOT NULL,
  epoch_from_utc DATETIME2(0) NOT NULL,
  epoch_to_utc  DATETIME2(0)  NOT NULL,
  periode1      FLOAT         NOT NULL,
  periode2      FLOAT         NULL,
  periode3      FLOAT         NULL,
  amp2          FLOAT         NULL,
  amp3          FLOAT         NULL,
  phase1        FLOAT         NOT NULL,      -- Grad, Lage am Epochenende
  phase2        FLOAT         NULL,
  phase3        FLOAT         NULL,
  prominenz     FLOAT         NOT NULL,
  stabilitaet   FLOAT         NOT NULL,
  CONSTRAINT PK_freq_akkord PRIMARY KEY (akkord_id),
  CONSTRAINT FK_freq_akkord_run FOREIGN KEY (run_id) REFERENCES dbo.freq_run(run_id),
  CONSTRAINT FK_freq_akkord_asset FOREIGN KEY (asset_id) REFERENCES dbo.asset(asset_id)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_freq_akkord_class')
  CREATE INDEX IX_freq_akkord_class ON dbo.freq_akkord(class_id, asset_id) INCLUDE (epoch_to_utc, phase1);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_freq_akkord_asset')
  CREATE INDEX IX_freq_akkord_asset ON dbo.freq_akkord(asset_id, epoch_to_utc) INCLUDE (class_id);
GO

/*  freq_match bekommt den Bezug zur Klasse und die Kennzahlen ueber Epochen.
    Die alten Spalten bleiben: epoch_to_utc ist die juengste gemeinsame Epoche,
    period_a/period_b der Grundton der Klasse, lag_bars der mittlere Versatz,
    score die Zahl gemeinsamer Epochen.                                     */
IF COL_LENGTH('dbo.freq_match', 'run_id') IS NULL
  ALTER TABLE dbo.freq_match ADD run_id INT NULL, class_id INT NULL,
    epochs INT NULL, lag_sd FLOAT NULL, bestaendig BIT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_freq_match_class')
  CREATE INDEX IX_freq_match_class ON dbo.freq_match(class_id, bestaendig);
GO

IF COL_LENGTH('dbo.freq_pattern', 'run_id') IS NULL
  ALTER TABLE dbo.freq_pattern ADD run_id INT NULL;
GO
