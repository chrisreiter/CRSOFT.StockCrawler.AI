/*  Branche und Land je Wert.

    Yahoos Screener liefert im Kursdatensatz KEIN Branchenfeld — wohl aber
    eigene Screener je Branche (ms_technology, ms_healthcare …). Die Zuordnung
    entsteht deshalb, indem diese elf Listen durchlaufen und die enthaltenen
    Symbole markiert werden.

    Auflösung ist die Branchenebene (elf Kategorien). Feinere Einteilungen wie
    „Rüstung" gibt es dort nicht — Rüstungswerte fallen unter Industrials.     */
USE stockcrawler;
GO

IF COL_LENGTH('dbo.asset', 'sector') IS NULL
  ALTER TABLE dbo.asset ADD sector NVARCHAR(64) NULL;
GO

IF COL_LENGTH('dbo.asset', 'country') IS NULL
  ALTER TABLE dbo.asset ADD country NVARCHAR(8) NULL;
GO

IF IndexProperty(OBJECT_ID('dbo.asset'),'IX_asset_sector','IndexID') IS NULL
  CREATE INDEX IX_asset_sector ON dbo.asset(sector) INCLUDE(asset_class, symbol, is_tracked);
GO
