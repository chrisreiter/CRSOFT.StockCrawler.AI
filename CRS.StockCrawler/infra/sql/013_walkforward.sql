/*  Walk-Forward-Training: Kennzahlen und Lernkurve.

    Die einzelnen Prognosen werden bewusst NICHT gespeichert — bei einem vollen
    Durchlauf entstehen über sechs Millionen davon, und die eigentliche Frage
    ist nicht, was an Tag 143 vorhergesagt wurde, sondern ob die Prognose über
    den Verlauf besser wird.                                                 */
USE stockcrawler;
GO

/* Ein Durchlauf: eine Auflösung, eine Epoche. */
IF OBJECT_ID('dbo.learning_epoch') IS NULL
CREATE TABLE dbo.learning_epoch (
  epoch_id       INT IDENTITY(1,1) PRIMARY KEY,
  run_label      NVARCHAR(64)  NOT NULL,   -- klammert zusammengehörige Epochen
  pass_no        INT           NOT NULL,   -- 1 = erster Tageslauf, 2 = Stunden, 3 = zweiter Tageslauf
  interval_code  VARCHAR(3)    NOT NULL,
  horizons       NVARCHAR(100) NOT NULL,   -- welche Horizonte trainiert wurden
  started_utc    DATETIME2(0)  NOT NULL CONSTRAINT DF_le_start DEFAULT SYSUTCDATETIME(),
  finished_utc   DATETIME2(0)  NULL,
  assets         INT           NULL,
  steps          INT           NULL,       -- Zeitschritte über das Gitter
  forecasts      BIGINT        NULL,
  scored         BIGINT        NULL,
  mape           FLOAT         NULL,
  hit_rate       FLOAT         NULL,
  pair_refreshes INT           NULL,       -- wie oft die Vorlaufstruktur neu gerechnet wurde
  note           NVARCHAR(1000) NULL
);
GO

/* Die Lernkurve: gleich große Zeitfenster über den Durchlauf. */
IF OBJECT_ID('dbo.learning_curve') IS NULL
CREATE TABLE dbo.learning_curve (
  epoch_id      INT          NOT NULL,
  bucket_no     INT          NOT NULL,
  horizon_hours INT          NOT NULL,
  from_utc      DATETIME2(0) NOT NULL,
  to_utc        DATETIME2(0) NOT NULL,
  n             INT          NOT NULL,
  mape          FLOAT        NOT NULL,
  hit_rate      FLOAT        NOT NULL,
  CONSTRAINT PK_learning_curve PRIMARY KEY (epoch_id, bucket_no, horizon_hours),
  CONSTRAINT FK_lc_epoch FOREIGN KEY (epoch_id) REFERENCES dbo.learning_epoch(epoch_id)
);
GO

/* Wie sich die Gewichte der Teilmodelle über den Durchlauf verschieben —
   das macht das Lernen überhaupt erst sichtbar. */
IF OBJECT_ID('dbo.learning_model_curve') IS NULL
CREATE TABLE dbo.learning_model_curve (
  epoch_id    INT          NOT NULL,
  bucket_no   INT          NOT NULL,
  model_name  NVARCHAR(32) NOT NULL,
  avg_weight  FLOAT        NOT NULL,
  hit_rate    FLOAT        NOT NULL,
  mean_error  FLOAT        NOT NULL,
  n           INT          NOT NULL,
  CONSTRAINT PK_learning_model_curve PRIMARY KEY (epoch_id, bucket_no, model_name),
  CONSTRAINT FK_lmc_epoch FOREIGN KEY (epoch_id) REFERENCES dbo.learning_epoch(epoch_id)
);
GO
