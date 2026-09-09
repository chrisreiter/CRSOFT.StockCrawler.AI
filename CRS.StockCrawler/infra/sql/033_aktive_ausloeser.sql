/*  Welche Bot-Auslöser stehen JETZT an — je verfolgtem Wert.

    ------------------------------------------------------------------------

    WARUM DIE BEDINGUNGEN NICHT NOCH EINMAL IN C# STEHEN

    Die Wissens-Säule soll in die Prognose einfliessen, und ihr einziger
    messbarer Zahlenbeitrag sind genau diese Muster: `bot_trigger_stat` hält je
    Auslöser den Median-Ertrag nach 1, 5 und 20 Tagen — gegen eine Grundlinie
    aus denselben Werten im selben Zeitraum, über tausende Ereignisse.

    Damit der gemessene Verdienst zum erkannten Signal passt, MUSS die Erkennung
    dieselbe Bedingung benutzen wie die Messung. Eine zweite Umsetzung in C#
    liefe unweigerlich auseinander -- ein anderer RSI, ein anderes Fenster, und
    der Verdienst gehörte zu einem anderen Signal als dem gemeldeten. Die
    Bedingungen bleiben deshalb an einer Stelle, hier, wörtlich wie in 027.

    Der einzige Unterschied: 027 sucht über Jahre, dies hier nur den JÜNGSTEN
    Bar je Wert.

    ------------------------------------------------------------------------

    RSI wie in 027: einfaches gleitendes Mittel, nicht Wilders Glättung. Nicht
    weil das besser wäre, sondern weil die Messung es so gerechnet hat. Wer das
    hier ändert, ändert das Signal, ohne den Verdienst mitzuändern.
*/

CREATE OR ALTER PROCEDURE dbo.get_active_triggers
    @tage INT = 3          -- wie viele Bars zurück noch als „jetzt" gelten
AS
BEGIN
  SET NOCOUNT ON;

  /*  Ein Jahr plus Vorlauf: SMA200 und das 52-Wochen-Hoch brauchen 252 Bars,
      und der Vorgängerwert für die Kreuzungen einen weiteren. Weniger zu holen
      spart Zeit und liefert stillschweigend NULL, wo das Fenster nicht voll
      ist -- die Auslöser feuerten dann nie, ohne dass es auffiele. */
  SELECT p.asset_id, a.asset_class, p.ts_utc,
         CAST(p.[close] AS FLOAT) AS c,
         CAST(p.volume AS FLOAT)  AS v,
         ROW_NUMBER() OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc) AS rn,
         AVG(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
              ROWS BETWEEN 49 PRECEDING AND CURRENT ROW) AS sma50,
         AVG(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
              ROWS BETWEEN 199 PRECEDING AND CURRENT ROW) AS sma200,
         AVG(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
              ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS sma20,
         STDEV(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
              ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS sd20,
         MAX(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc
              ROWS BETWEEN 251 PRECEDING AND 1 PRECEDING) AS hoch252,
         LAG(CAST(p.[close] AS FLOAT)) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc) AS c_vor
    INTO #k
    FROM dbo.price_bar p
    JOIN dbo.asset a ON a.asset_id = p.asset_id AND a.is_tracked = 1
   WHERE p.interval_code = '1d'
     AND p.ts_utc >= DATEADD(DAY, -420, SYSUTCDATETIME())
     AND p.[close] > 0;

  SELECT k.*,
         AVG(CASE WHEN k.c > k.c_vor THEN k.c - k.c_vor ELSE 0 END)
             OVER (PARTITION BY k.asset_id ORDER BY k.ts_utc
                   ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) AS auf14,
         AVG(CASE WHEN k.c < k.c_vor THEN k.c_vor - k.c ELSE 0 END)
             OVER (PARTITION BY k.asset_id ORDER BY k.ts_utc
                   ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) AS ab14
    INTO #kr
    FROM #k k;

  SELECT r.*,
         CASE WHEN r.auf14 + r.ab14 > 0 THEN 100.0 * r.auf14 / (r.auf14 + r.ab14) END AS rsi14,
         LAG(r.sma50 - r.sma200) OVER (PARTITION BY r.asset_id ORDER BY r.ts_utc) AS ma_vor,
         LAG(CASE WHEN r.auf14 + r.ab14 > 0 THEN 100.0 * r.auf14 / (r.auf14 + r.ab14) END)
             OVER (PARTITION BY r.asset_id ORDER BY r.ts_utc) AS rsi_vor
    INTO #s
    FROM #kr r;

  /*  Nur die letzten Bars. `@tage` statt „genau der letzte": Ein Auslöser vom
      Vortag ist noch aktuell -- die gemessene Wirkung reicht über 1, 5 und 20
      Tage. Wer nur den letzten Bar nimmt, verliert die meisten Signale an
      Werten, deren Börse gerade zu hat. */
  DECLARE @grenze DATETIME2(0) = DATEADD(DAY, -@tage, SYSUTCDATETIME());

  SELECT ausloeser, richtung, asset_id, asset_class, ts_utc
    FROM (
      SELECT N'Goldenes Kreuz (SMA 50 über 200)' AS ausloeser, 1 AS richtung,
             s.asset_id, s.asset_class, s.ts_utc
        FROM #s s
       WHERE s.ts_utc >= @grenze AND s.ma_vor < 0 AND s.sma50 - s.sma200 > 0

      UNION ALL
      SELECT N'Todeskreuz (SMA 50 unter 200)', -1, s.asset_id, s.asset_class, s.ts_utc
        FROM #s s
       WHERE s.ts_utc >= @grenze AND s.ma_vor > 0 AND s.sma50 - s.sma200 < 0

      UNION ALL
      SELECT N'RSI unter 30 (überverkauft)', 1, s.asset_id, s.asset_class, s.ts_utc
        FROM #s s
       WHERE s.ts_utc >= @grenze AND s.rsi_vor >= 30 AND s.rsi14 < 30

      UNION ALL
      SELECT N'RSI über 70 (überkauft)', -1, s.asset_id, s.asset_class, s.ts_utc
        FROM #s s
       WHERE s.ts_utc >= @grenze AND s.rsi_vor <= 70 AND s.rsi14 > 70

      UNION ALL
      SELECT N'Bollinger-Ausbruch nach oben', 1, s.asset_id, s.asset_class, s.ts_utc
        FROM #s s
       WHERE s.ts_utc >= @grenze AND s.sd20 > 0 AND s.c > s.sma20 + 2 * s.sd20

      UNION ALL
      SELECT N'Bollinger-Ausbruch nach unten', -1, s.asset_id, s.asset_class, s.ts_utc
        FROM #s s
       WHERE s.ts_utc >= @grenze AND s.sd20 > 0 AND s.c < s.sma20 - 2 * s.sd20

      UNION ALL
      SELECT N'Neues 52-Wochen-Hoch', 1, s.asset_id, s.asset_class, s.ts_utc
        FROM #s s
       WHERE s.ts_utc >= @grenze AND s.hoch252 > 0 AND s.c > s.hoch252
    ) x
   ORDER BY asset_id, ausloeser;

  DROP TABLE #k, #kr, #s;
END
GO
