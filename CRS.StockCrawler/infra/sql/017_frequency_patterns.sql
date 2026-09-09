/*  Ablage der zerlegten Grundfrequenzen.

    Je Wert, Zeitepoche und Auflösung werden die stabilen Spektralspitzen
    festgehalten. Aus diesem Bestand lassen sich später Übereinstimmungen
    zwischen Kursen finden, ohne jedes Mal alles neu zu zerlegen — die
    Zerlegung über 280 Werte und 25 Jahre ist Minuten, ein Bereichsabgleich
    darauf Millisekunden.

    Warum SQL und nicht Qdrant: Ein Frequenzmuster ist Periode, Prominenz,
    Stabilität und Phase — vier bis sechs Zahlen. Näherungsindizes für Vektoren
    zahlen sich erst bei Hunderten von Dimensionen aus; die hier gebrauchte
    Abfrage lautet "alle Muster mit Periode zwischen T·0,9 und T·1,1" und ist
    ein Bereichs-Scan. Dafür ist ein zusammengesetzter Index das richtige
    Werkzeug.

    Die Phase ist zyklisch und wird deshalb NICHT indiziert — 359 und 1 Grad
    liegen zwei Grad auseinander, jeder Bereichsindex hielte sie für maximal
    entfernt. Sie wird beim Vergleich gerechnet, nicht gesucht.               */
USE stockcrawler;
GO

IF OBJECT_ID('dbo.freq_pattern') IS NULL
CREATE TABLE dbo.freq_pattern (
  pattern_id     BIGINT        IDENTITY(1,1) NOT NULL,
  asset_id       INT           NOT NULL,
  interval_code  VARCHAR(3)    NOT NULL,

  -- Die Epoche, aus der das Muster stammt.
  epoch_from_utc DATETIME2(0)  NOT NULL,
  epoch_to_utc   DATETIME2(0)  NOT NULL,

  period_bars    FLOAT         NOT NULL,
  prominence     FLOAT         NOT NULL,

  /* Anteil der Teilfenster innerhalb der Epoche, in denen dieselbe Periode
     dominierte. Ohne diese Zahl ist eine Spitze wertlos: In jeder Reihe gibt
     es eine stärkste Frequenz, auch in reinem Rauschen. */
  stability      FLOAT         NOT NULL,

  -- Lage am Ende der Epoche, in Grad. Grundlage jedes Phasenvergleichs.
  phase_deg      FLOAT         NULL,
  amplitude      FLOAT         NULL,

  created_utc    DATETIME2(0)  NOT NULL CONSTRAINT DF_fp_created DEFAULT(SYSUTCDATETIME()),

  CONSTRAINT PK_freq_pattern PRIMARY KEY (pattern_id),
  CONSTRAINT UQ_freq_pattern UNIQUE (asset_id, interval_code, epoch_to_utc, period_bars),
  CONSTRAINT FK_freq_pattern_asset FOREIGN KEY (asset_id) REFERENCES dbo.asset(asset_id)
);
GO

/*  Der Abgleich sucht nach Periode innerhalb einer Epoche. Genau diese
    Reihenfolge im Index: erst das Intervall, dann die Epoche, dann die
    Periode. Andersherum müsste über alle Epochen gescannt werden.          */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_freq_pattern_lookup' AND object_id = OBJECT_ID('dbo.freq_pattern'))
CREATE INDEX IX_freq_pattern_lookup
  ON dbo.freq_pattern (interval_code, epoch_to_utc, period_bars)
  INCLUDE (asset_id, prominence, stability, phase_deg);
GO

/*  Gefundene Übereinstimmungen. Getrennt gespeichert, weil sie sich mit jedem
    neuen Abgleich ändern können, die Muster selbst aber nicht.             */
IF OBJECT_ID('dbo.freq_match') IS NULL
CREATE TABLE dbo.freq_match (
  match_id       BIGINT        IDENTITY(1,1) NOT NULL,
  interval_code  VARCHAR(3)    NOT NULL,
  epoch_to_utc   DATETIME2(0)  NOT NULL,

  asset_a        INT           NOT NULL,
  asset_b        INT           NOT NULL,

  period_a       FLOAT         NOT NULL,
  period_b       FLOAT         NOT NULL,

  /* Relative Abweichung der Perioden. Relativ, nicht absolut: Fünf Bars
     Unterschied sind bei Periode 10 eine andere Welt als bei Periode 200. */
  period_delta   FLOAT         NOT NULL,

  phase_delta_deg FLOAT        NULL,

  /* Phasenverschiebung in Bars, aus der Phasendifferenz und der Periode.
     Ungleich null heißt: dasselbe Muster, zeitlich versetzt -- und nur das
     ist prognostisch interessant. */
  lag_bars       FLOAT         NULL,

  score          FLOAT         NOT NULL,
  created_utc    DATETIME2(0)  NOT NULL CONSTRAINT DF_fm_created DEFAULT(SYSUTCDATETIME()),

  CONSTRAINT PK_freq_match PRIMARY KEY (match_id),
  CONSTRAINT CK_freq_match_order CHECK (asset_a < asset_b)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_freq_match_lookup' AND object_id = OBJECT_ID('dbo.freq_match'))
CREATE INDEX IX_freq_match_lookup
  ON dbo.freq_match (interval_code, epoch_to_utc, score DESC);
GO
