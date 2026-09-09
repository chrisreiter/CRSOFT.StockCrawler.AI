using System.Data;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Rangliste der jüngsten Kreuzungen: welches Paar seit dem Kippen am meisten
/// eingebracht hätte, welche Seite dafür zu halten und welche abzugeben wäre.
///
/// <para><b>Warum als Paar und nicht als einzelner Wert.</b> Eine Kreuzung sagt
/// nichts darüber, ob ein Wert steigt — sie sagt, dass er den anderen überholt
/// hat. Beides zugleich zu messen ist der einzige Weg, den allgemeinen
/// Marktgang herauszurechnen: In einer Woche, in der alles um fünf Prozent
/// steigt, hat ein Paar nichts geleistet, wenn beide Seiten fünf Prozent
/// gestiegen sind.</para>
///
/// <para><b>Warum die Bewährungsspalte nicht wegzulassen ist.</b> Eine
/// Rangliste nach erzieltem Gewinn zeigt, was bereits gelaufen ist. Wer oben
/// einsteigt, kauft nach der Bewegung. Erst der Blick auf die FRÜHEREN
/// Kreuzungen desselben Paares sagt, ob dem Signal überhaupt zu folgen war.
/// Gemessen wird über getrennte Zeiträume: die Bewährung ausschließlich auf
/// Kreuzungen VOR dem Anzeigefenster, damit sich die aktuelle Zeile nicht
/// selbst benotet.</para>
/// </summary>
public sealed class CrossingOpportunityService(ISqlConnectionFactory factory)
    : ICrossingOpportunityService
{
    /// <summary>
    /// Ein Ein-Bar-Sprung dieser Größe ist bei einer Aktie kein Kurs, sondern
    /// ein Datenfehler oder ein Split. Beides erzeugt einen Scheingewinn von
    /// rund fünfzig Prozent — und der stünde in einer Rangliste nach Gewinn
    /// zwangsläufig ganz oben.
    ///
    /// Belegt an MNST: Yahoo liefert um den 2:1-Split vom 10.08.2026 eine
    /// gemischte Reihe (97,65 → 48,19 → 93,55 → 90,36 → 45,53). Ohne diese
    /// Prüfung waren zwanzig der fünfundzwanzig besten „Gewinne" nichts
    /// anderes als „irgendetwas gegen MNST".
    /// </summary>
    private const double SprungAktie = 0.40;   // ±49 %

    /// <summary>
    /// Krypto darf mehr. Ein Tagesverlust von fünfzig Prozent ist dort kein
    /// Datenfehler, sondern kommt vor — wer ihn wegfiltert, filtert echte
    /// Bewegung weg. Erst jenseits von rund 150 Prozent wird es zur
    /// Neubenennung oder zum Datenfehler.
    /// </summary>
    private const double SprungKrypto = 0.90;  // ±146 %

    public async Task<CrossingOverview> BuildAsync(
        string intervalCode, int tage, int haltedauer, int limit,
        bool nurKlassenwechsel, int maxJeSymbol = 3, CancellationToken ct = default)
    {
        tage = Math.Clamp(tage, 1, 365);
        haltedauer = Math.Clamp(haltedauer, 1, 250);
        limit = Math.Clamp(limit, 1, 500);
        maxJeSymbol = Math.Clamp(maxJeSymbol, 1, 100);

        /* Mehr holen als angezeigt wird: Die Grenze je Symbol streicht Zeilen
           erst nach der Sortierung, und ohne Vorrat bliebe die Liste kurz. */
        var rohlimit = Math.Min(limit * 12, 4000);

        /* Was ein Tausch kostet, bevor er etwas einbringt. Ohne diese Schwelle
           gilt ein Paar mit 60 Prozent Trefferquote und 0,1 Prozent mittlerem
           Ertrag als bewährt — und kostet im Mittel Geld. */
        var kosten = Handelskosten.Standard;

        await using var conn = await factory.OpenAsync(ct);

        var p = new DynamicParameters();
        p.Add("@interval", intervalCode);
        p.Add("@tage", tage);
        p.Add("@limit", rohlimit);
        p.Add("@nur_wechsel", nurKlassenwechsel);
        p.Add("@sprung_aktie", SprungAktie);
        p.Add("@sprung_krypto", SprungKrypto);

        using var mehrfach = await conn.QueryMultipleAsync(new CommandDefinition(
            RanglisteSql, p, commandTimeout: 180, cancellationToken: ct));

        var roh = (await mehrfach.ReadAsync<RohZeile>()).ToList();
        var verdaechtig = (await mehrfach.ReadAsync<RohZeile>()).ToList();
        var geprueft = await mehrfach.ReadSingleAsync<int>();

        /* ERST kappen, DANN die Bewährung holen.

           Die Reihenfolge war umgekehrt, und das kostete mit dem gewachsenen
           Universum das Zwölffache: `rohlimit` holt limit*12 Zeilen, damit die
           Grenze je Symbol noch etwas zu streichen hat -- bei limit 60 also
           720 Paare. Für jedes davon baute die Bewährungsrechnung die
           gemeinsame Kursreihe über die ganze Historie auf, rund 1.300 Bars,
           und 660 der 720 flogen unmittelbar danach heraus.

           Gemessen: 65 Sekunden für eine Seite, die vorher 5 brauchte. Die
           Kappung hängt nur an Symbolen und Reihenfolge, nicht an der
           Bewährung -- sie lässt sich also vorziehen. */
        var (gekapptRoh, verdraengt) = JeSymbolBegrenzen(roh, maxJeSymbol, limit);

        var bewaehrung = await KreuzungsBewaehrung.LadenAsync(
            conn, gekapptRoh.Select(r => (r.AssetIdA, r.AssetIdB)),
            intervalCode, tage, haltedauer, ct);

        var zeilen = gekapptRoh.Select(r =>
        {
            // Richtung 1 heißt: A hat B überholt. Dann ist A die Seite, die man
            // hält, und B die, von der man sich trennt.
            var kaufA = r.Direction == 1;

            bewaehrung.TryGetValue((r.AssetIdA, r.AssetIdB), out var b);

            return new CrossingOpportunity(
                SymbolKaufen: kaufA ? r.SymbolA : r.SymbolB,
                NameKaufen: (kaufA ? r.NameA : r.NameB) ?? "",
                KlasseKaufen: (AssetClass)(kaufA ? r.KlasseA : r.KlasseB),
                SymbolVerkaufen: kaufA ? r.SymbolB : r.SymbolA,
                NameVerkaufen: (kaufA ? r.NameB : r.NameA) ?? "",
                KlasseVerkaufen: (AssetClass)(kaufA ? r.KlasseB : r.KlasseA),
                Kreuzung: r.TsUtc,
                TageSeither: (int)Math.Round((DateTime.UtcNow - r.TsUtc).TotalDays),
                RenditeKaufen: kaufA ? r.RenditeA : r.RenditeB,
                RenditeVerkaufen: kaufA ? r.RenditeB : r.RenditeA,
                Paargewinn: r.Paargewinn,
                HistorischeKreuzungen: b?.N ?? 0,
                HistorischeTrefferquote: b is { N: >= KreuzungsBewaehrung.MindestKreuzungen }
                    ? b.Trefferquote : null,
                HistorischerMittelgewinn: b is { N: >= KreuzungsBewaehrung.MindestKreuzungen }
                    ? b.Mittelgewinn : null,
                Rundlaufkosten: kosten.Rundlauf);
        }).ToList();

        var ausgelassen = verdaechtig.Select(r => new CrossingSkipped(
            r.SymbolA, r.SymbolB,
            $"Ein-Bar-Sprung von {Math.Round((Math.Exp(r.MaxSprung) - 1) * 100)} % im Fenster — "
            + "das ist kein Kurs, sondern ein Split oder ein Datenfehler.")).ToList();

        var mitBewaehrung = zeilen.Count(z => z.Bewaehrt);

        return new CrossingOverview(
            zeilen, ausgelassen, geprueft, verdraengt,
            kosten.Rundlauf, kosten.Beschreibung,
            intervalCode, tage, haltedauer, DateTime.UtcNow,
            Hinweis(zeilen.Count, mitBewaehrung, ausgelassen.Count, verdraengt,
                    maxJeSymbol, haltedauer, kosten));
    }

    /// <summary>
    /// Der Hinweis über der Tabelle. Er nennt zuerst, was der Rangliste fehlt —
    /// nicht, was sie kann. Eine Ansicht, die nach Gewinn sortiert und das
    /// nicht dazusagt, liest sich wie eine Empfehlung.
    /// </summary>
    private static string Hinweis(int zeilen, int bewaehrt, int ausgelassen,
                                 int verdraengt, int maxJeSymbol, int haltedauer,
                                 Handelskosten kosten)
    {
        if (zeilen == 0)
            return "Keine Kreuzung im Fenster. Zeitraum vergrößern oder die Analyse neu rechnen.";

        var kern = bewaehrt == 0
            ? "**Kein einziges Paar hat sich bewährt.** Die Reihenfolge zeigt, was bereits "
              + "gelaufen ist — nicht, was noch kommt. Wer oben einsteigt, kauft nach der Bewegung."
            : $"{bewaehrt} von {zeilen} Paaren haben sich bewährt: Bei ihren FRÜHEREN Kreuzungen "
              + $"blieb die obere Seite über {haltedauer} gemeinsame Bars in mehr als 55 Prozent "
              + "der Fälle vorne UND der mittlere Ertrag lag über den Handelskosten. Die übrigen "
              + "Zeilen zeigen nur, was schon gelaufen ist.";

        kern += $" Gerechnet wird gegen {kosten.Beschreibung}. Ein Signal, dessen mittlerer "
              + "Ertrag darunter liegt, ist nicht schwach — es kostet Geld, ihm zu folgen, "
              + "gleich wie hoch seine Trefferquote ist.";

        if (ausgelassen > 0)
            kern += $" {ausgelassen} Paare sind wegen eines Kurssprungs ausgelassen — siehe unten.";

        if (verdraengt > 0)
            kern += $" {verdraengt} weitere Zeilen wurden ausgeblendet, weil derselbe Wert "
                  + $"höchstens {maxJeSymbol}-mal vorkommen darf — sonst beherrscht ein einziger "
                  + "Absturz die ganze Liste.";

        return kern;
    }

    /// <summary>
    /// Begrenzt, wie oft derselbe Wert in der Rangliste auftaucht.
    ///
    /// <para>Ohne diese Grenze ist die Übersicht wertlos, und zwar messbar:
    /// MNT-USD verlor binnen eines Monats die Hälfte, und damit standen 24 der
    /// 25 besten Zeilen auf „irgendetwas gegen MNT-USD". Jede einzelne war
    /// rechnerisch richtig — als Übersicht war es eine einzige Zeile,
    /// vierundzwanzigmal geschrieben.</para>
    ///
    /// <para>Gezählt werden BEIDE Seiten. Ein Wert, der dreimal als Kaufseite
    /// erscheint, ist genauso einseitig wie einer, der dreimal verkauft
    /// wird.</para>
    /// </summary>
    private static (List<RohZeile> Zeilen, int Verdraengt) JeSymbolBegrenzen(
        List<RohZeile> zeilen, int maxJeSymbol, int limit)
    {
        var zaehler = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var behalten = new List<RohZeile>(limit);
        var verdraengt = 0;

        foreach (var z in zeilen)
        {
            if (behalten.Count >= limit) break;

            var k = zaehler.GetValueOrDefault(z.SymbolA);
            var v = zaehler.GetValueOrDefault(z.SymbolB);

            if (k >= maxJeSymbol || v >= maxJeSymbol) { verdraengt++; continue; }

            zaehler[z.SymbolA] = k + 1;
            zaehler[z.SymbolB] = v + 1;
            behalten.Add(z);
        }

        return (behalten, verdraengt);
    }

    // ------------------------------------------------------------ Abfragen ---

    /// <summary>
    /// Drei Ergebnismengen: die Rangliste, die wegen eines Kurssprungs
    /// ausgelassenen Paare, die Zahl der geprüften Paare.
    /// </summary>
    private const string RanglisteSql = """
        SET NOCOUNT ON;
        DECLARE @seit DATETIME2(0) = DATEADD(DAY, -@tage, SYSUTCDATETIME());

        /* Je Paar zählt nur die JÜNGSTE Kreuzung. Ein Paar, das im Fenster
           dreimal hin und her gekippt ist, hat kein dreifaches Signal — es hat
           gar keines, und die letzte Lage ist alles, was zählt. */
        SELECT l.asset_id_a, l.asset_id_b, l.ts_utc, l.direction
          INTO #paar
          FROM (SELECT c.asset_id_a, c.asset_id_b, c.ts_utc, c.direction,
                       ROW_NUMBER() OVER (PARTITION BY c.asset_id_a, c.asset_id_b
                                          ORDER BY c.ts_utc DESC) AS rn
                  FROM dbo.crossing c
                 WHERE c.interval_code = @interval AND c.ts_utc >= @seit) l
          JOIN dbo.asset aa ON aa.asset_id = l.asset_id_a AND aa.is_tracked = 1
          JOIN dbo.asset ab ON ab.asset_id = l.asset_id_b AND ab.is_tracked = 1
         WHERE l.rn = 1
           AND (@nur_wechsel = 0
                OR (CASE WHEN aa.asset_class = 2 THEN 1 ELSE 0 END)
                 <> (CASE WHEN ab.asset_class = 2 THEN 1 ELSE 0 END));

        /* Der jüngste Zeitpunkt, zu dem BEIDE gehandelt haben.

           Nicht die jeweils letzte Bar: Samstags handelt nur Krypto. Wer den
           Krypto-Samstag gegen den Aktien-Freitag rechnet, misst den Kalender
           statt den Markt. */
        SELECT p.asset_id_a, p.asset_id_b, MAX(pa.ts_utc) AS ts_jetzt
          INTO #jetzt
          FROM #paar p
          JOIN dbo.price_bar pa ON pa.asset_id = p.asset_id_a AND pa.interval_code = @interval
                                AND pa.ts_utc >= DATEADD(DAY, -15, SYSUTCDATETIME())
          JOIN dbo.price_bar pb ON pb.asset_id = p.asset_id_b AND pb.interval_code = @interval
                                AND pb.ts_utc = pa.ts_utc
         GROUP BY p.asset_id_a, p.asset_id_b;

        /* Der größte Ein-Bar-Sprung je Wert im Fenster — die Sprungprüfung. */
        SELECT s.asset_id, MAX(ABS(LOG(s.c / s.vor))) AS max_sprung
          INTO #sprung
          FROM (SELECT p.asset_id, p.[close] AS c,
                       LAG(p.[close]) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc) AS vor
                  FROM dbo.price_bar p
                  JOIN (SELECT asset_id_a AS id FROM #paar
                        UNION SELECT asset_id_b FROM #paar) w ON w.id = p.asset_id
                 WHERE p.interval_code = @interval
                   AND p.ts_utc >= DATEADD(DAY, -(@tage + 5), SYSUTCDATETIME())
                   AND p.[close] > 0) s
         WHERE s.vor > 0
         GROUP BY s.asset_id;

        SELECT p.asset_id_a AS AssetIdA, p.asset_id_b AS AssetIdB,
               aa.symbol AS SymbolA, aa.name AS NameA, aa.asset_class AS KlasseA,
               ab.symbol AS SymbolB, ab.name AS NameB, ab.asset_class AS KlasseB,
               p.ts_utc AS TsUtc, p.direction AS Direction,
               CAST(na.[close] AS FLOAT) / ka.[close] - 1 AS RenditeA,
               CAST(nb.[close] AS FLOAT) / kb.[close] - 1 AS RenditeB,
               CASE WHEN p.direction = 1
                    THEN CAST(na.[close] AS FLOAT) / ka.[close] - CAST(nb.[close] AS FLOAT) / kb.[close]
                    ELSE CAST(nb.[close] AS FLOAT) / kb.[close] - CAST(na.[close] AS FLOAT) / ka.[close]
               END AS Paargewinn,
               CASE WHEN ISNULL(sa.max_sprung, 0)
                       >= CASE WHEN aa.asset_class = 2 THEN @sprung_krypto ELSE @sprung_aktie END
                      OR ISNULL(sb.max_sprung, 0)
                       >= CASE WHEN ab.asset_class = 2 THEN @sprung_krypto ELSE @sprung_aktie END
                    THEN 1 ELSE 0 END AS Verdacht,
               CASE WHEN ISNULL(sa.max_sprung, 0) > ISNULL(sb.max_sprung, 0)
                    THEN ISNULL(sa.max_sprung, 0) ELSE ISNULL(sb.max_sprung, 0) END AS MaxSprung
          INTO #alle
          FROM #paar p
          JOIN #jetzt j ON j.asset_id_a = p.asset_id_a AND j.asset_id_b = p.asset_id_b
          JOIN dbo.asset aa ON aa.asset_id = p.asset_id_a
          JOIN dbo.asset ab ON ab.asset_id = p.asset_id_b
          JOIN dbo.price_bar ka ON ka.asset_id = p.asset_id_a AND ka.interval_code = @interval
                                AND ka.ts_utc = p.ts_utc AND ka.[close] > 0
          JOIN dbo.price_bar kb ON kb.asset_id = p.asset_id_b AND kb.interval_code = @interval
                                AND kb.ts_utc = p.ts_utc AND kb.[close] > 0
          JOIN dbo.price_bar na ON na.asset_id = p.asset_id_a AND na.interval_code = @interval
                                AND na.ts_utc = j.ts_jetzt AND na.[close] > 0
          JOIN dbo.price_bar nb ON nb.asset_id = p.asset_id_b AND nb.interval_code = @interval
                                AND nb.ts_utc = j.ts_jetzt AND nb.[close] > 0
          LEFT JOIN #sprung sa ON sa.asset_id = p.asset_id_a
          LEFT JOIN #sprung sb ON sb.asset_id = p.asset_id_b;

        SELECT TOP (@limit) * FROM #alle WHERE Verdacht = 0 ORDER BY Paargewinn DESC;

        SELECT TOP (50) * FROM #alle WHERE Verdacht = 1 ORDER BY MaxSprung DESC;

        SELECT COUNT(*) FROM #alle;
        """;

    // ------------------------------------------------------------ Rohzeilen --

    private sealed class RohZeile
    {
        public int AssetIdA { get; set; }
        public int AssetIdB { get; set; }
        public string SymbolA { get; set; } = "";
        public string? NameA { get; set; }
        public byte KlasseA { get; set; }
        public string SymbolB { get; set; } = "";
        public string? NameB { get; set; }
        public byte KlasseB { get; set; }
        public DateTime TsUtc { get; set; }
        public byte Direction { get; set; }
        public double RenditeA { get; set; }
        public double RenditeB { get; set; }
        public double Paargewinn { get; set; }
        public int Verdacht { get; set; }
        public double MaxSprung { get; set; }
    }
}
