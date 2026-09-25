/*  046 -- Der Hausmeister: was nach welcher Frist entbehrlich ist.

    WARUM ES IHN BRAUCHT. Die Datenbank waechst an Stellen, die niemand
    liest. Gemessen am 25.09.2026, nach einem Monat Betrieb:

      forecast_track    9,3 Mio Zeilen   956 MB   EIN Rueckrechnungslauf
      forecast          1,3 Mio            802 MB   davon 43k dauerhaft unbewertbar
      crossing          7,2 Mio            431 MB
      forecast_component 6,4 Mio           317 MB   nach der Bewertung ohne Zweck
      curve_link/-event 3,2 Mio            332 MB   davon 8 UEBERHOLTE Laeufe

    Die Kurvendiskussion liest ausschliesslich MAX(run_id) je Intervall und
    Glaettungsart -- acht aeltere Laeufe mit zusammen 1,5 Mio Zeilen liest
    nichts mehr. Das ist kein Randfall, sondern das Muster: Was einmal
    gerechnet und ersetzt wurde, bleibt liegen.

    WAS NICHT ANGETASTET WIRD, unabhaengig von jeder Einstellung:
    Kurse (price_bar), Werte (asset), das Depot (invest_*), Benutzer und
    Sitzungen, Saeulengewichte, Autopilot-Einstellungen, die
    Modellgewichte (model_weight -- dort steckt das Gelernte) sowie jede
    Prognose, deren Zielzeitpunkt noch aussteht. Wer den Bestand kuerzt,
    kuerzt die Beweislage; deshalb steht in jeder Regel, was verloren geht.

    PROBELAUF IST DIE VOREINSTELLUNG. Ein Loeschlauf, den man nicht vorher
    sehen kann, ist ein Vertrauensakt. `probe = 1` zaehlt nur.             */
USE stockcrawler;
GO

IF OBJECT_ID('dbo.housekeeping_lauf') IS NULL
CREATE TABLE dbo.housekeeping_lauf (
  lauf_id       INT           IDENTITY(1,1) NOT NULL,
  gestartet_utc DATETIME2(0)  NOT NULL CONSTRAINT DF_hkl_start DEFAULT(SYSUTCDATETIME()),
  beendet_utc   DATETIME2(0)  NULL,
  probe         BIT           NOT NULL CONSTRAINT DF_hkl_probe DEFAULT(1),
  ausgeloest    NVARCHAR(16)  NOT NULL CONSTRAINT DF_hkl_aus DEFAULT('tageslauf'),
  regeln        INT           NOT NULL CONSTRAINT DF_hkl_regeln DEFAULT(0),
  zeilen        BIGINT        NOT NULL CONSTRAINT DF_hkl_zeilen DEFAULT(0),
  dauer_s       FLOAT         NULL,
  note          NVARCHAR(400) NULL,
  CONSTRAINT PK_housekeeping_lauf PRIMARY KEY (lauf_id)
);
GO

/*  Eine Zeile je Regel und Lauf. Auch die Regeln mit null Treffern werden
    festgehalten: Eine Regel, die nichts findet, ist ein Ergebnis -- sie
    unterscheidet sich von einer Regel, die gar nicht lief.                */
IF OBJECT_ID('dbo.housekeeping_regel') IS NULL
CREATE TABLE dbo.housekeeping_regel (
  regel_id      INT           IDENTITY(1,1) NOT NULL,
  lauf_id       INT           NOT NULL,
  schluessel    NVARCHAR(40)  NOT NULL,
  tabelle       NVARCHAR(64)  NOT NULL,
  frist_tage    INT           NOT NULL,
  zeilen        BIGINT        NOT NULL CONSTRAINT DF_hkr_zeilen DEFAULT(0),
  dauer_s       FLOAT         NULL,
  fehler        NVARCHAR(400) NULL,
  CONSTRAINT PK_housekeeping_regel PRIMARY KEY (regel_id),
  CONSTRAINT FK_hkr_lauf FOREIGN KEY (lauf_id) REFERENCES dbo.housekeeping_lauf(lauf_id)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_housekeeping_regel_lauf')
  CREATE INDEX IX_housekeeping_regel_lauf ON dbo.housekeeping_regel(lauf_id, schluessel);
GO

/*  Der Kopfstau der Bewertung sichtbar machen: Ein Index auf die dauerhaft
    unbewertbaren Prognosen. Ohne ihn kostet die Regel einen Tabellenscan
    ueber 1,3 Mio Zeilen -- gefiltert wird nicht, weil ein gefilterter Index
    QUOTED_IDENTIFIER ON fuer JEDES Update der Tabelle verlangt (CLAUDE.md). */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_forecast_unscoreable')
  CREATE INDEX IX_forecast_unscoreable ON dbo.forecast(unscoreable_utc, target_ts_utc);
GO
