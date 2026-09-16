/*  045 -- Die Reasoning-Saeule bekommt ein Gewicht an der Prognose.

    WAS SICH AENDERT. Bisher las der Agent die Prognosen nur ab und erklaerte
    sie; ein Gewicht am Ergebnis hatte er nicht, die Checkbox war gesperrt.
    Jetzt gibt er je Wert ein URTEIL ab -- Richtung und Zuversicht -- und das
    fliesst als Auf- oder Abschlag in die Mischung ein.

    WAS SICH NICHT AENDERT: die Regel „keine Zahl ohne Messung".

      1. Das Modell liefert KEINEN Betrag. Es liefert eine Richtung von -2
         bis +2 und eine Zuversicht von 0 bis 1, nachdem es Journal,
         Tagesuebersicht, Prognosen, Nachrichten und Grundschwingungen per
         Werkzeug gelesen hat. Ein Urteil ohne einen einzigen Werkzeugaufruf
         wird verworfen -- gemessen antwortete das Modell bei den ersten
         Fragen zweimal von dreimal frei erfunden.
      2. Der Betrag kommt aus der Messung: Richtung/2 x Zuversicht x die
         uebliche Schwankung DIESES Werts ueber den Horizont, gedeckelt wie
         jeder Aufschlag (hoechstens so viel, wie die Grundlage selbst sagt).
      3. Der Verdienst kommt aus der Nachpruefung: Jedes Urteil wird nach
         fuenf Handelstagen gegen den Kurs gehalten. Ab zwanzig bewerteten
         Urteilen gilt die Trefferquote; davor der vorsichtige Zwischenwert
         0,25 -- derselbe wie bei der ersten Saeule, solange sie jung ist.

    Der Urteilslauf ist vom Prognoselauf entkoppelt: 644 Modellaufrufe auf
    der CPU kosten Stunden. Der Prognoselauf liest die juengsten Urteile
    (hoechstens drei Tage alt), der Urteilslauf fuellt sie mit Zeitbudget.  */
USE stockcrawler;
GO

IF OBJECT_ID('dbo.reasoning_urteil') IS NULL
CREATE TABLE dbo.reasoning_urteil (
  urteil_id       BIGINT        IDENTITY(1,1) NOT NULL,
  asset_id        INT           NOT NULL,
  made_at_utc     DATETIME2(0)  NOT NULL CONSTRAINT DF_ru_made DEFAULT(SYSUTCDATETIME()),
  modell          NVARCHAR(64)  NOT NULL,

  /*  -2 stark abwaerts .. 0 neutral .. +2 stark aufwaerts                  */
  richtung        SMALLINT      NOT NULL,
  zuversicht      FLOAT         NOT NULL,
  begruendung     NVARCHAR(600) NULL,

  /*  Womit das Urteil gebildet wurde -- Namen der Werkzeugaufrufe, durch
      Komma. Leer waere ein Urteil aus dem Nichts; das wird nicht gespeichert. */
  werkzeuge       NVARCHAR(200) NOT NULL,
  runden          INT           NOT NULL,
  sekunden        FLOAT         NOT NULL,

  base_close      DECIMAL(18,8) NOT NULL,

  -- Nachpruefung nach fuenf Handelstagen.
  bewertet_utc    DATETIME2(0)  NULL,
  realisiert      FLOAT         NULL,           -- Log-Rendite Basis -> Kurs 5 Handelstage spaeter
  treffer         BIT           NULL,           -- Vorzeichen stimmt (nur bei Richtung <> 0)

  CONSTRAINT PK_reasoning_urteil PRIMARY KEY (urteil_id),
  CONSTRAINT FK_reasoning_urteil_asset FOREIGN KEY (asset_id) REFERENCES dbo.asset(asset_id),
  CONSTRAINT CK_reasoning_urteil_richtung CHECK (richtung BETWEEN -2 AND 2),
  CONSTRAINT CK_reasoning_urteil_zuversicht CHECK (zuversicht BETWEEN 0 AND 1)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_reasoning_urteil_asset')
  CREATE INDEX IX_reasoning_urteil_asset ON dbo.reasoning_urteil(asset_id, made_at_utc DESC)
    INCLUDE (richtung, zuversicht, begruendung);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_reasoning_urteil_offen')
  CREATE INDEX IX_reasoning_urteil_offen ON dbo.reasoning_urteil(bewertet_utc, made_at_utc)
    INCLUDE (asset_id, richtung, base_close);
GO

/*  Die Saeule bekommt ihre Gewichtszeile -- mit null, wie jede neue Saeule.
    Ein Gewicht, das von selbst wirkt, weil jemand eine Migration eingespielt
    hat, waere dieselbe unangenehme Ueberraschung wie ein Autopilot, der von
    selbst zu handeln beginnt.                                                */
MERGE dbo.pillar_weight AS t
USING (VALUES (N'reasoning', 0,
  N'Urteil des Agenten als Auf-/Abschlag; Betrag aus der Schwankung des Werts, Verdienst aus der Trefferquote nachgeprüfter Urteile.'))
  AS s(pillar, weight, begruendung) ON t.pillar = s.pillar
WHEN NOT MATCHED THEN INSERT (pillar, weight, begruendung) VALUES (s.pillar, s.weight, s.begruendung);
GO
