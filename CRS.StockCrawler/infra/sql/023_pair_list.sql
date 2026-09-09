/*  Tabellentyp für Paarlisten.

    Die Kreuzungs-Übersicht ermittelt zuerst die Rangliste und rechnet danach
    für genau diese Paare nach, was ihre früheren Kreuzungen wert waren. Ohne
    die Einschränkung müsste die Bewährungsrechnung über alle Paare mit einer
    Kreuzung im Fenster laufen — 6.625 Paare mal rund 2.000 gemeinsame Bars
    sind dreizehn Millionen Zeilen für eine Ansicht mit fünfzig Zeilen.        */

IF TYPE_ID('dbo.IntPairList') IS NULL
  CREATE TYPE dbo.IntPairList AS TABLE (
    a INT NOT NULL,
    b INT NOT NULL,
    PRIMARY KEY (a, b)
  );
GO

/*  Kreuzungen werden je Paar und Zeitpunkt gesucht. Der vorhandene eindeutige
    Index UX_crossing beginnt mit asset_id_a — für die Suche „alle Kreuzungen
    dieses Paares" reicht das, für „alle Kreuzungen ab Datum X über alle Paare"
    nicht: dort muss der Zeitpunkt vorn stehen, sonst wird die ganze Tabelle
    gelesen. Bei 548.064 Zeilen ist das der Unterschied zwischen Sekunden und
    Sekundenbruchteilen.                                                       */
IF IndexProperty(OBJECT_ID('dbo.crossing'), 'IX_crossing_ts', 'IndexID') IS NULL
  CREATE INDEX IX_crossing_ts ON dbo.crossing(interval_code, ts_utc)
    INCLUDE (asset_id_a, asset_id_b, direction);
GO
