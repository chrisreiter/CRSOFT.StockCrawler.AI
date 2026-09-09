/*  Bestand: mit wieviel Kapital der Nutzer in welchem Wert steckt.

    Warum eine eigene Tabelle und nicht app_state. Der Zustand der Oberfläche
    hängt an einer Sitzung und wird nach 180 Tagen weggeräumt — das ist für
    Filtereinstellungen richtig und für einen Bestand falsch. Wer seine
    Positionen einträgt, hat Daten eingegeben, keine Ansicht eingestellt.

    Eine Zeile je Wert. Eine zweite Position im selben Wert ist kein zweiter
    Eintrag, sondern mehr Kapital — alles andere führte dazu, dass zwei Zeilen
    für BTC-USD unterschiedliche Tauschvorschläge bekommen.                  */
USE stockcrawler;
GO

IF OBJECT_ID('dbo.holding') IS NULL
CREATE TABLE dbo.holding (
  holding_id  INT            IDENTITY(1,1) NOT NULL,
  asset_id    INT            NOT NULL,
  kapital     DECIMAL(18,2)  NOT NULL,
  waehrung    NVARCHAR(8)    NOT NULL CONSTRAINT DF_hold_waehr DEFAULT('USD'),
  einstand    DECIMAL(28,10) NULL,
  gekauft_utc DATETIME2(0)   NULL,
  notiz       NVARCHAR(400)  NULL,
  updated_utc DATETIME2(0)   NOT NULL CONSTRAINT DF_hold_upd DEFAULT(SYSUTCDATETIME()),
  CONSTRAINT PK_holding PRIMARY KEY (holding_id),
  CONSTRAINT FK_holding_asset FOREIGN KEY (asset_id) REFERENCES dbo.asset(asset_id),
  CONSTRAINT UQ_holding_asset UNIQUE (asset_id),
  CONSTRAINT CK_holding_kapital CHECK (kapital > 0)
);
GO

/*  Kreuzungen von der Seite des GEHALTENEN Wertes aus gesucht.

    UX_crossing beginnt mit asset_id_a, IX_crossing_ts mit dem Zeitpunkt.
    Beide helfen nicht bei „alle jüngsten Kreuzungen, an denen dieser eine
    Wert beteiligt ist" — und der Wert kann auf beiden Seiten stehen, also
    braucht es je Spalte einen eigenen Index. Dieselbe Lehre wie bei den
    Verknüpfungen der Kurvendiskussion, wo ein nachgelagerter Filter den
    Reasoning-Agenten wahrheitswidrig „keine gefunden" melden ließ.          */
IF IndexProperty(OBJECT_ID('dbo.crossing'), 'IX_crossing_a_ts', 'IndexID') IS NULL
  CREATE INDEX IX_crossing_a_ts ON dbo.crossing(asset_id_a, interval_code, ts_utc)
    INCLUDE (asset_id_b, direction);
GO

IF IndexProperty(OBJECT_ID('dbo.crossing'), 'IX_crossing_b_ts', 'IndexID') IS NULL
  CREATE INDEX IX_crossing_b_ts ON dbo.crossing(asset_id_b, interval_code, ts_utc)
    INCLUDE (asset_id_a, direction);
GO
