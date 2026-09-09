/*  Der letzte bekannte Kurs — an EINER Stelle definiert.

    ------------------------------------------------------------------------

    DER FEHLER, DEN DAS BEHEBT. Das Depot bewertete Positionen ausschliesslich
    auf Tagesschlüssen. Gemessen am 26.08.2026 um 14:45 UTC:

        Tagesbars   bis 25.08. 00:00   (571 Werte)
        Stundenbars bis 26.08. 14:00   (571 Werte)

    Der angezeigte Depotwert war damit **38 Stunden alt** und bewegte sich
    zwischen zwei Tagesläufen überhaupt nicht -- obwohl stündlich frische Kurse
    hereinkamen. Wer zusah, hielt das Depot für eingefroren, und das war es in
    der Anzeige auch.

    WARUM EINE FUNKTION UND NICHT VIERMAL DIESELBE ABFRAGE. Der letzte Kurs
    wird an vier Stellen gebraucht: für den angezeigten Positionswert, für den
    Kurs, zu dem gebucht wird, für die Handelbarkeit in der Autopilot-Rangfolge
    und für den letzten Punkt der Vermögenskurve. Liefen diese vier
    auseinander, zeigte die Tabelle einen Preis und die Buchung benutzte einen
    anderen -- und die Zahlen liessen sich nicht mehr gegeneinander prüfen.
    Dieselbe Überlegung wie bei `get_active_triggers`, das die SQL-Bedingung
    der Messung wörtlich spiegelt, statt sie in C# nachzubauen.

    WARUM NICHT NUR STUNDENBARS. 34 der verfolgten Werte haben gar keine
    Stundendaten, und junge Kryptowerte oft ebenfalls nicht. Genommen wird die
    jüngste Bar aus BEIDEN Auflösungen -- welche das ist, steht in
    `interval_code` und lässt sich anzeigen.

    Der Primärschlüssel (asset_id, interval_code, ts_utc) trägt das: zwei
    Suchen, je ein TOP 1, zusammengeführt.                                    */
USE stockcrawler;
GO

CREATE OR ALTER FUNCTION dbo.letzter_kurs(@asset_id INT)
RETURNS TABLE
AS RETURN
  SELECT TOP 1
         p.[close]        AS schluss,
         p.ts_utc         AS ts_utc,
         p.interval_code  AS interval_code
    FROM dbo.price_bar p
   WHERE p.asset_id = @asset_id
     AND p.interval_code IN ('1d', '1h')
     AND p.[close] > 0
   ORDER BY p.ts_utc DESC;
GO
