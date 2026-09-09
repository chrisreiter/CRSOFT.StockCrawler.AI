using System.Data;
using System.Text.Json;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Models;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Der Autopilot: sucht selbst Werte aus, kauft und verkauft.
///
/// <para><b>Was die Messung sagt, bevor eine Zeile davon läuft.</b> Über 1.650
/// entdoppelte Live-Prognosen liegt die Richtungstrefferquote auf einen Tag bei
/// <b>45,0 %</b>. Die typische Tagesbewegung beträgt 1,546 %, der Rundlauf
/// 0,30 %. Nötig wären damit <b>59,7 %</b>. Der Erwartungswert je Umschichtung
/// ist <b>−0,45 %</b>.</para>
///
/// <para>Ein Autopilot auf dieser Grundlage verliert Geld. Er wird trotzdem
/// gebaut — aber <b>gegen Grundlinien</b>, nicht gegen sich selbst. Ohne die
/// Grundlinie <c>halten</c> sähe ein Verlust von vier Prozent nach Pech aus,
/// obwohl er der rechnerisch erwartete Ausgang war.</para>
///
/// <para><b>Drei Quellen, und alle drei sind gemessen.</b> Die Prognose bringt
/// sämtliche Säulen mit — sie stecken bereits in <c>combined_return</c>, samt
/// ihrer Gewichtung nach <c>pillar_skill</c>. Die Muster kommen aus
/// <c>bot_trigger_stat</c>, also der Chartlage mit gemessenem Überschuss. Und
/// die Trefferquote je Wert entscheidet, ob überhaupt gehandelt werden darf.
/// Nemotron bekommt kein Stimmrecht über Beträge, sondern ein Veto.</para>
/// </summary>
public sealed class AutopilotService(
    ISqlConnectionFactory factory,
    IInvestService invest,
    IReasoningService reasoning,
    ILogger<AutopilotService> log) : IAutopilotService
{
    private static readonly Handelskosten Kosten = Handelskosten.Standard;

    /// <summary>
    /// Wieviele bewertete Prognosen ein Wert braucht, damit seine Trefferquote
    /// zählt. Unter zwanzig ist eine Quote von 0,6 nichts als Rauschen — bei
    /// zehn Fällen reichen sechs Zufallstreffer.
    /// </summary>
    private const int MindestFaelle = 20;

    /// <summary>
    /// Wie viele Fragen an Nemotron je Lauf. Eine Frage mit Denkmodus dauert
    /// Minuten; ohne Deckel steht der Tageslauf still.
    /// </summary>
    private const int MaxUrteile = 8;

    // ----------------------------------------------------------- Rangfolge --

    /// <summary>
    /// Bewertet jeden verfolgten Wert. Ohne jeden Handel — dieselbe Rechnung,
    /// die der Lauf benutzt, damit man sie ansehen kann, bevor Geld fliesst.
    /// </summary>
    public async Task<IReadOnlyList<AutopilotAnwaerter>> RangfolgeAsync(
        string depot, int grenze = 40, CancellationToken ct = default) =>
        (await KennzahlenAsync(depot, grenze, ct)).Anwaerter;

    /// <summary>
    /// Dieselbe Rechnung, zusätzlich mit den Kennzahlen, aus denen sie
    /// entsteht. Sie gehören in die Anzeige: Ohne sie ist die Schrumpfung
    /// eine Zahl, die aus dem Nichts kommt.
    /// </summary>
    public async Task<AutopilotRangfolge> KennzahlenAsync(
        string depot, int grenze = 40, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var roh = (await conn.QueryAsync<Rohwert>(new CommandDefinition("""
            WITH prognose AS (
              SELECT f.asset_id, f.horizon_hours,
                     COALESCE(f.combined_return, f.predicted_return) AS rendite,
                     ROW_NUMBER() OVER (PARTITION BY f.asset_id, f.horizon_hours
                                        ORDER BY f.made_at_utc DESC) rn
                FROM dbo.forecast f
               WHERE f.horizon_hours IN (24, 168)
                 AND f.made_at_utc >= DATEADD(day, -4, SYSUTCDATETIME())
            ),
            /*  Je Wert und Zieltag nur die JÜNGSTE Prognose. Prognosen entstehen
                stündlich, der Zielbar nicht -- ungefiltert zählte dieselbe
                Kursbewegung mehrfach, und die Trefferquote wäre eine Zahl über
                eine Fallzahl, die es nicht gibt.                              */
            guete AS (
              SELECT d.asset_id,
                     AVG(CAST(d.direction_correct AS float)) AS p,
                     SUM(CAST(d.direction_correct AS int))   AS treffer,
                     COUNT(*)                                AS n
                FROM (SELECT f.asset_id, s.direction_correct,
                             ROW_NUMBER() OVER (PARTITION BY f.asset_id,
                                                             CONVERT(date, f.target_ts_utc)
                                                ORDER BY f.made_at_utc DESC) rn
                        FROM dbo.forecast f
                        JOIN dbo.forecast_score s ON s.forecast_id = f.forecast_id
                       WHERE f.horizon_hours = 24
                         /*  NUR LIVE. Das Aufrollen der Vergangenheit hat
                             rueckdatierte Prognosen ueber bereits bekannte
                             Kurse geschrieben; sie tragen `model_version`
                             ens-1-bt. Mit ihnen lag die mittlere Trefferquote
                             bei 0,571, ohne sie bei 0,474 -- und die hohen
                             Werte einzelner Papiere stammten fast vollstaendig
                             aus dem rueckdatierten Abschnitt. Ein Depot danach
                             auszurichten hiesse, auf eine Rueckrechnung mit
                             bekanntem Ausgang zu setzen.                      */
                         AND f.model_version NOT LIKE '%-bt') d
               WHERE d.rn = 1
               GROUP BY d.asset_id
            ),
            beweg AS (
              SELECT asset_id, AVG(ABS(r)) AS bewegung, COUNT(*) AS tage
                FROM (SELECT p.asset_id,
                             LOG(p.[close] / NULLIF(LAG(p.[close])
                                 OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc), 0)) AS r
                        FROM dbo.price_bar p
                       WHERE p.interval_code = '1d'
                         AND p.ts_utc >= DATEADD(day, -90, SYSUTCDATETIME())
                         AND p.[close] > 0) x
               WHERE r IS NOT NULL AND ABS(r) < 0.5
               GROUP BY asset_id
            )
            SELECT a.asset_id                AS AssetId,
                   a.symbol                  AS Symbol,
                   a.name                    AS Name,
                   a.asset_class             AS Klasse,
                   k.schluss                 AS Kurs,
                   k.ts_utc                  AS KursUtc,
                   p24.rendite               AS Tag,
                   p168.rendite              AS Woche,
                   g.p                       AS Trefferquote,
                   ISNULL(g.treffer, 0)      AS Treffer,
                   ISNULL(g.n, 0)            AS Bewertet,
                   ISNULL(b.bewegung, 0)     AS Bewegung,
                   ISNULL(b.tage, 0)         AS Tage
              FROM dbo.asset a
              /*  Derselbe letzte Kurs wie in der Anzeige und beim Buchen --
                  eine Rangfolge auf Kursen von vorgestern waehlte nach
                  Zahlen aus, zu denen niemand mehr handeln kann.             */
              OUTER APPLY dbo.letzter_kurs(a.asset_id) k
              LEFT JOIN prognose p24  ON p24.asset_id  = a.asset_id
                                     AND p24.horizon_hours = 24  AND p24.rn = 1
              LEFT JOIN prognose p168 ON p168.asset_id = a.asset_id
                                     AND p168.horizon_hours = 168 AND p168.rn = 1
              LEFT JOIN guete g ON g.asset_id = a.asset_id
              LEFT JOIN beweg b ON b.asset_id = a.asset_id
             WHERE a.is_tracked = 1
               /*  Ohne frische Bar ist ein Wert nicht handelbar. Fünf Tage
                   decken ein langes Wochenende samt Feiertag ab.             */
               AND k.ts_utc >= DATEADD(day, -5, SYSUTCDATETIME())
            """, cancellationToken: ct))).ToList();

        var muster = await MusterAsync(conn, ct);
        var gehalten = await GehaltenAsync(conn, depot, ct);

        var schrumpf = Schrumpfung.Aus(roh);

        var liste = new List<AutopilotAnwaerter>(roh.Count);

        foreach (var r in roh)
        {
            var roheQuote = r.Bewertet > 0 ? r.Trefferquote : null;
            var p = schrumpf.Geschrumpft(r.Treffer, r.Bewertet);

            /*  Der Verdienst haengt an der geschrumpften Quote, nicht an der
                rohen. Nullpunkt 0,523 wie bei der Saeulenmischung.            */
            var verdienst = p is { } q ? Math.Clamp((q - 0.523) / 0.10, 0, 1) : 0;

            var beitraege = new List<AutopilotBeitrag>();

            /*  1. Prognose — und damit ALLE Saeulen. Sie stecken bereits in
                `combined_return`, jede mit ihrem gemessenen Verdienst
                gewichtet. Sie hier ein zweites Mal einzeln zu holen hiesse,
                dieselbe Zahl doppelt zu zaehlen.                              */
            if (r.Tag is { } tag)
            {
                beitraege.Add(new AutopilotBeitrag("Prognose 1 Tag", tag, verdienst, 0,
                    r.Bewertet > 0
                        ? $"{r.Treffer} von {r.Bewertet} Live-Prognosen getroffen "
                        + $"({roheQuote:0.000}), geschrumpft {p:0.000}"
                        : "noch keine bewertete Live-Prognose"));
            }

            if (r.Woche is { } woche)
            {
                /*  Halbe Anerkennung: Fuer den Wochenhorizont ist die
                    Trefferquote dieses Wertes nicht gemessen, und eine
                    ungemessene Zahl darf nicht so viel wiegen wie eine
                    gemessene.                                                 */
                beitraege.Add(new AutopilotBeitrag("Prognose 1 Woche", woche, verdienst * 0.5,
                    0, "halbe Anerkennung — für diesen Horizont liegt keine eigene Messung vor"));
            }

            // 2. Chartlage ueber die gemessenen Bot-Muster.
            if (muster.TryGetValue(r.AssetId, out var m)) beitraege.Add(m);

            var summe = beitraege.Sum(b => b.Verdienst);

            /*  Solange KEIN Bestandteil nachgewiesenen Verdienst hat, gibt es
                nichts zu gewichten. Dann wird schlicht gemittelt — und die
                Punktzahl heisst genau das, was sie ist: die Erwartung des
                Modells, nicht ein belegter Vorsprung. Der Unterschied steht in
                `Grundlage` und gehoert in die Anzeige, nicht in eine Fussnote.
                Ohne diesen Zweig waere die Rangfolge derzeit ueberall null,
                weil die Live-Trefferquote unter dem Nullpunkt liegt.          */
            var gewichtet = summe > 1e-9;

            var punktzahl = beitraege.Count == 0
                ? 0
                : gewichtet
                    ? beitraege.Sum(b => b.Rendite * b.Verdienst) / summe
                    : beitraege.Average(b => b.Rendite);

            var mitAnteil = beitraege
                .Select(b => b with
                {
                    Anteil = gewichtet ? b.Verdienst / summe : 1.0 / beitraege.Count
                })
                .ToList();

            var ew = p is { } q2 && r.Bewegung > 0
                ? Kosten.Erwartungswert(q2, r.Bewegung)
                : (double?)null;

            liste.Add(new AutopilotAnwaerter(
                r.AssetId, r.Symbol, r.Name, Klassenname(r.Klasse), r.Kurs, r.KursUtc,
                punktzahl, mitAnteil, roheQuote, p, r.Bewertet, r.Bewegung, ew,
                Kosten.NoetigeTrefferquote(r.Bewegung), verdienst,
                gewichtet ? "gemessener Verdienst" : "blosse Erwartung des Modells",
                gehalten.GetValueOrDefault(r.AssetId),
                Lage(p, ew, r.Bewertet, verdienst)));
        }

        var gezeigt = liste
            .OrderByDescending(x => x.Punktzahl)
            .Take(Math.Clamp(grenze, 1, 600))
            .ToList();

        var bewegung = liste.Where(x => x.Bewegung > 0).Select(x => x.Bewegung).ToList();
        var mittlereBewegung = bewegung.Count > 0 ? bewegung.Average() : 0;
        var noetig = Kosten.NoetigeTrefferquote(mittlereBewegung);

        var faelle = roh.Sum(r => r.Bewertet);
        var messbar = gezeigt.Count(x => x.Bewertet >= MindestFaelle);
        var lohnend = gezeigt.Count(x => x.Erwartungswert > 0);

        var hinweis =
            $"Über {liste.Count} verfolgte Werte mit {faelle} bewerteten Live-Prognosen liegt "
          + $"die Richtungstrefferquote bei {schrumpf.Mittel:0.000}. Bei einer mittleren "
          + $"Tagesbewegung von {mittlereBewegung * 100:0.00} % und {Kosten.Rundlauf * 100:0.##} % "
          + $"Rundlauf wären {noetig:0.000} nötig, damit ein Geschäft die Kosten deckt. "
          + $"Von den {gezeigt.Count} gezeigten Werten haben {messbar} genug Fälle für einen "
          + $"Nachweis und {lohnend} einen positiven Erwartungswert.\n\n"
          + "Die Quoten sind zum Bestandsmittel hin geschrumpft, weil ihre Streuung über die "
          + $"Werte ({schrumpf.Streuung(liste.Count):0.000}) kaum über dem liegt, was allein "
          + $"die endliche Fallzahl erzeugt ({schrumpf.Zufallsstreuung:0.000}). Wer aus so "
          + "vielen Werten den mit der höchsten Quote auswählt, wählt den glücklichsten, nicht "
          + "den besten.";

        return new AutopilotRangfolge(gezeigt, liste.Count, messbar, lohnend,
            schrumpf.Mittel, faelle, schrumpf.Beobachtet, schrumpf.Zufallsstreuung,
            noetig, mittlereBewegung, hinweis);
    }

    /// <summary>
    /// Zieht die Trefferquoten der einzelnen Werte zum Bestandsmittel hin.
    ///
    /// <para><b>Warum das sein muss.</b> Bei rund vier bewerteten
    /// Live-Prognosen je Wert lag die Streuung der Quoten ueber 571 Werte bei
    /// 0,285 — reines Wuerfeln erzeugte bei dieser Fallzahl schon 0,250. Die
    /// Unterschiede zwischen den Werten sind also weit ueberwiegend Rauschen.
    /// Wer daraus den Wert mit der hoechsten Quote auswaehlt, waehlt den
    /// gluecklichsten, nicht den besten — und ein Depot, das so entscheidet,
    /// handelt nach Zufall und nennt es Auswahl.</para>
    ///
    /// <para><b>Der Faktor wird gemessen, nicht gesetzt.</b> Die beobachtete
    /// Streuung zerfaellt in Rauschen und echten Unterschied; nur der zweite
    /// Teil darf durchschlagen. Waechst die Fallzahl, schrumpft die Schrumpfung
    /// von allein — es braucht keine Regel, die man spaeter zurueckdrehen muss.
    /// Dasselbe Muster wie beim Nullpunkt 0,523 der Saeulenmischung: eine
    /// Schwelle aus den Daten, keine aus dem Bauch.</para>
    /// </summary>
    private sealed record Schrumpfung(double Mittel, double Staerke, double EchteStreuung,
                                      double Beobachtet, double Zufallsstreuung)
    {
        public double Streuung(int _) => Beobachtet;

        public static Schrumpfung Aus(IReadOnlyList<Rohwert> roh)
        {
            var mit = roh.Where(r => r.Bewertet > 0).ToList();

            if (mit.Count < 10) return new Schrumpfung(0.5, double.PositiveInfinity, 0, 0, 0);

            var treffer = mit.Sum(r => (double)r.Treffer);
            var faelle = mit.Sum(r => (double)r.Bewertet);
            var mittel = faelle > 0 ? treffer / faelle : 0.5;

            // Beobachtete Streuung der Quoten ueber die Werte.
            var quoten = mit.Select(r => r.Trefferquote ?? mittel).ToList();
            var beobachtet = quoten.Sum(q => (q - mittel) * (q - mittel)) / quoten.Count;

            // Was davon allein aus der endlichen Fallzahl folgt.
            var rauschen = mit.Average(r => mittel * (1 - mittel) / Math.Max(1, r.Bewertet));

            var echt = beobachtet - rauschen;

            /*  Bleibt nichts uebrig, unterscheiden sich die Werte nicht
                nachweisbar — dann zieht jede Quote vollstaendig auf das Mittel.
                Unendliche Staerke ist hier die richtige Zahl und kein
                Sonderfall.                                                     */
            if (echt <= 0)
                return new Schrumpfung(mittel, double.PositiveInfinity, 0,
                                       Math.Sqrt(beobachtet), Math.Sqrt(rauschen));

            return new Schrumpfung(mittel, mittel * (1 - mittel) / echt, Math.Sqrt(echt),
                                   Math.Sqrt(beobachtet), Math.Sqrt(rauschen));
        }

        /// <summary>
        /// Die geschrumpfte Quote. <c>null</c>, solange gar nichts vorliegt —
        /// ein Rueckfall auf 0,5 waere bequem und falsch: Er behauptete einen
        /// Muenzwurf, wo nichts gemessen ist.
        /// </summary>
        public double? Geschrumpft(int treffer, int faelle)
        {
            if (faelle <= 0) return null;
            if (double.IsPositiveInfinity(Staerke)) return Mittel;

            return (Mittel * Staerke + treffer) / (Staerke + faelle);
        }
    }

    private static string Lage(double? p, double? ew, int bewertet, double verdienst)
    {
        if (p is null)
            return "noch keine bewertete Live-Prognose — ohne Messung kein Nachweis";

        if (verdienst <= 0)
            return $"{bewertet} bewertete Live-Prognosen, geschrumpft {p:0.000} — unter dem "
                 + "Nullpunkt 0,523, also kein nachgewiesener Vorsprung";

        if (ew is null) return "keine Bewegung messbar";

        return ew > 0
            ? $"Erwartungswert +{ew * 100:0.000} % je Geschäft"
            : $"Erwartungswert {ew * 100:0.000} % je Geschäft — Kosten nicht gedeckt";
    }

    private static string Klassenname(byte k) => k switch
    {
        1 => "Aktie", 2 => "Krypto", 3 => "Devisen", _ => "Fonds/ETF"
    };

    /// <summary>
    /// Die aktiven Bot-Muster je Wert, gewichtet mit ihrer gemessenen
    /// Richtungstrefferquote.
    ///
    /// <para>Wörtlich dieselbe Auswertung wie in <c>SaeulenbeitragService</c>,
    /// samt ihrer beiden Regeln: Dubletten je (Wert, Auslöser) fliegen raus —
    /// ein Bollinger-Ausbruch feuert gern an zwei Tagen hintereinander —, und
    /// ein Muster, dessen gemessener Überschuss der eigenen Richtung
    /// widerspricht, trägt nichts bei. Ein „Goldenes Kreuz“ mit negativem
    /// Überschuss ist kein Verkaufssignal, sondern gar keins.</para>
    /// </summary>
    private async Task<Dictionary<int, AutopilotBeitrag>> MusterAsync(
        Microsoft.Data.SqlClient.SqlConnection conn, CancellationToken ct)
    {
        try
        {
            var aktiv = (await conn.QueryAsync<Ausloeser>(new CommandDefinition("""
                CREATE TABLE #a (ausloeser NVARCHAR(100), richtung INT, asset_id INT,
                                 asset_class TINYINT, ts_utc DATETIME2(0));
                INSERT INTO #a EXEC dbo.get_active_triggers @tage = 3;

                SELECT a.asset_id AS AssetId, a.ausloeser AS Name, a.richtung AS Richtung,
                       s.r20 - s.b20 AS Ueber20, s.richtung20 AS Treffer20,
                       s.ereignisse AS Ereignisse
                  FROM #a a
                  JOIN dbo.bot_trigger_stat s
                    ON s.ausloeser = a.ausloeser AND s.klasse = a.asset_class
                   AND s.run_id = (SELECT MAX(run_id) FROM dbo.bot_trigger_stat);

                DROP TABLE #a;
                """, cancellationToken: ct))).ToList();

            var ergebnis = new Dictionary<int, AutopilotBeitrag>();

            foreach (var g in aktiv.GroupBy(x => x.AssetId))
            {
                // Je Wert und Auslöser nur einmal.
                var einzeln = g.GroupBy(x => x.Name).Select(x => x.First()).ToList();

                double summe = 0, gewichte = 0;
                var namen = new List<string>();

                foreach (var t in einzeln)
                {
                    if (t.Ueber20 is not { } ueber || t.Treffer20 is not { } quote) continue;

                    // Widerspricht die Messung dem Muster, gibt es keinen Beitrag.
                    if (ueber <= 0) continue;

                    var verdienst = Math.Clamp((Math.Abs(quote - 0.5) - 0.02) / 0.08, 0, 1);
                    if (verdienst <= 0) continue;

                    /*  Der Zwanzigtagesüberschuss auf einen Tag heruntergerechnet.
                        NICHT hochgerechnet: Aus einem Zwanzigtageseffekt einen
                        Jahreseffekt zu machen hiesse, dreissig Mal zu behaupten,
                        was einmal gemessen wurde.                              */
                    var jeTag = ueber * t.Richtung / 20.0;

                    summe += jeTag * verdienst;
                    gewichte += verdienst;
                    namen.Add(t.Name);
                }

                if (gewichte <= 1e-9) continue;

                ergebnis[g.Key] = new AutopilotBeitrag("Chartmuster", summe / gewichte,
                    Math.Min(1, gewichte), 0,
                    string.Join(", ", namen) + " — gemessener Überschuss über die Grundlinie");
            }

            return ergebnis;
        }
        catch (Exception ex)
        {
            /*  Die Muster sind ein Beitrag, kein Fundament. Fehlen sie, wird
                nach der Prognose allein bewertet — der Lauf darf daran nicht
                scheitern. Gemeldet wird es trotzdem: Ein Bestandteil, der
                stillschweigend wegfällt, verschiebt jede Rangfolge, ohne dass
                jemand es merkt.                                               */
            log.LogWarning(ex, "Autopilot: Bot-Muster nicht verfügbar, "
                             + "bewertet allein nach Prognose");
            return [];
        }
    }

    private static async Task<Dictionary<int, decimal>> GehaltenAsync(
        Microsoft.Data.SqlClient.SqlConnection conn, string depot, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<Bestandszeile>(new CommandDefinition("""
            SELECT b.asset_id AS AssetId, SUM(b.anteile) AS Anteile
              FROM dbo.invest_buchung b
             WHERE b.depot = @depot
             GROUP BY b.asset_id
            HAVING SUM(b.anteile) > 0
            """, new { depot }, cancellationToken: ct));

        return rows.ToDictionary(r => r.AssetId, r => r.Anteile);
    }

    private readonly record struct Bestandszeile(int AssetId, decimal Anteile);

    private sealed record Rohwert(int AssetId, string Symbol, string? Name, byte Klasse,
                                  decimal? Kurs, DateTime? KursUtc,
                                  double? Tag, double? Woche,
                                  double? Trefferquote, int Treffer, int Bewertet,
                                  double Bewegung, int Tage);

    private sealed record Ausloeser(int AssetId, string Name, int Richtung,
                                    double? Ueber20, double? Treffer20, int Ereignisse);

    // ------------------------------------------------------- Einstellungen --

    public async Task<IReadOnlyList<AutopilotEinstellung>> EinstellungenAsync(
        CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await EinstellungenAsync(conn, ct);
    }

    private static async Task<List<AutopilotEinstellung>> EinstellungenAsync(
        IDbConnection conn, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<AutopilotEinstellung>(new CommandDefinition("""
            SELECT depot AS Depot, aktiv AS Aktiv, werte AS Werte, max_anteil AS MaxAnteil,
                   hysterese AS Hysterese, takt AS Takt, waehrung AS Waehrung,
                   nemotron AS Nemotron, startkapital AS Startkapital,
                   zaehlt AS Zaehlt, updated_utc AS UpdatedUtc
              FROM dbo.autopilot_einstellung
             ORDER BY depot
            """, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<AutopilotEinstellung?> EinstellungSetzeAsync(
        string depot, bool? aktiv, int? werte, decimal? maxAnteil, decimal? hysterese,
        string? takt, string? waehrung, bool? nemotron, decimal? startkapital,
        bool? zaehlt, CancellationToken ct = default)
    {
        var d = (depot ?? "").Trim().ToLowerInvariant();
        if (d is not ("streng" or "aktiv" or "halten" or "invers")) return null;

        var t = (takt ?? "").Trim().ToUpperInvariant();
        if (t.Length > 0 && t is not ("1T" or "1W" or "1M" or "3M" or "6M" or "1J")) return null;

        await using var conn = await factory.OpenAsync(ct);

        /*  COALESCE statt einzelner UPDATE-Zweige: Wer nur den Takt umstellt,
            soll die uebrigen Felder nicht mitschicken muessen -- und ein
            zurueckgeschicktes Feld ist eines, das veraltet sein kann.         */
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.autopilot_einstellung
               SET aktiv       = COALESCE(@aktiv, aktiv),
                   werte       = COALESCE(@werte, werte),
                   max_anteil  = COALESCE(@maxAnteil, max_anteil),
                   hysterese   = COALESCE(@hysterese, hysterese),
                   takt        = COALESCE(NULLIF(@takt, ''), takt),
                   waehrung    = COALESCE(NULLIF(@waehrung, ''), waehrung),
                   nemotron    = COALESCE(@nemotron, nemotron),
                   startkapital = COALESCE(@startkapital, startkapital),
                   updated_utc = SYSUTCDATETIME()
             WHERE depot = @d
            """,
            new
            {
                d, aktiv, nemotron, takt = t,
                werte = werte is { } w ? Math.Clamp(w, 1, 30) : (int?)null,
                maxAnteil = maxAnteil is { } m ? Math.Clamp(m, 0.02m, 1m) : (decimal?)null,
                hysterese = hysterese is { } h ? Math.Clamp(h, 0m, 0.2m) : (decimal?)null,
                waehrung = (waehrung ?? "").Trim().ToUpperInvariant(),

                /*  Nach oben gedeckelt, weil eine versehentlich zusaetzliche
                    Null hier nicht auffaellt: Ein Depot mit zehn Millionen
                    sieht genauso aus wie eines mit tausend, nur die Zahlen
                    sind laenger.                                             */
                startkapital = startkapital is { } k
                    ? Math.Clamp(k, 0m, 10_000_000m) : (decimal?)null
            }, cancellationToken: ct));

        /*  Die Leitstrategie ist ein Auswahlknopf, kein Haekchen -- es ist
            immer genau eine. Deshalb eine Prozedur und nicht zwei Aufrufe:
            Zwischen „alte loeschen" und „neue setzen" darf es keinen Zustand
            geben, in dem gar keine oder zwei zaehlen; das Band zeigte in diesem
            Moment eine falsche Summe.

            Nur Einschalten wirkt. Ein `zaehlt = false` wuerde die Auswahl
            aufheben, ohne eine neue zu treffen.                              */
        if (zaehlt is true)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "EXEC dbo.set_leitstrategie @depot = @d",
                new { d }, cancellationToken: ct));
        }

        return (await EinstellungenAsync(conn, ct)).FirstOrDefault(x => x.Depot == d);
    }

    /// <summary>
    /// Stellt eine Strategie auf Anfang: Buchungen, Kassenbewegungen, Läufe und
    /// Beschlüsse fallen weg, danach steht das Startbudget auf dem Konto.
    ///
    /// <para>Die Arbeit macht <c>reset_autopilot_depot</c> in einer einzigen
    /// Transaktion. In SQL und nicht hier, weil die Reihenfolge an den
    /// Fremdschlüsseln hängt — Beschlüsse vor Läufen, Kassenbewegungen vor
    /// Buchungen. Ein halb aufgeräumtes Depot wäre schlimmer als ein
    /// volles.</para>
    ///
    /// <para>Geleert werden <b>alle</b> Währungen des Depots, nicht nur die
    /// eingestellte: Bliebe ein alter EUR-Stand neben einem frischen
    /// USD-Budget stehen, zeigte das Gesamtvermögen Geld, das zu keiner
    /// Strategie mehr gehört.</para>
    /// </summary>
    public async Task<AutopilotEinstellung?> ZuruecksetzenAsync(
        string depot, CancellationToken ct = default)
    {
        var d = (depot ?? "").Trim().ToLowerInvariant();
        if (d is not ("streng" or "aktiv" or "halten" or "invers")) return null;

        await using var conn = await factory.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "EXEC dbo.reset_autopilot_depot @depot = @d",
            new { d }, cancellationToken: ct));

        log.LogInformation("Autopilot {Depot} zurückgesetzt", d);

        return (await EinstellungenAsync(conn, ct)).FirstOrDefault(x => x.Depot == d);
    }

    // --------------------------------------------------------------- Laeufe --

    /// <summary>
    /// Laesst jede eingeschaltete Strategie einen Lauf machen.
    ///
    /// <para>Die Grundlinie zuerst: Sie kauft nur beim allerersten Mal, und sie
    /// soll dieselbe Rangfolge sehen wie die anderen — nicht eine, die deren
    /// Kaeufe schon veraendert haben.</para>
    /// </summary>
    public async Task<IReadOnlyList<AutopilotLauf>> LaufeAlleAsync(CancellationToken ct = default)
    {
        var einst = await EinstellungenAsync(ct);
        var ergebnis = new List<AutopilotLauf>();

        foreach (var e in einst.Where(x => x.Aktiv).OrderBy(x => x.Depot == "halten" ? 0 : 1))
        {
            try
            {
                ergebnis.Add(await LaufeAsync(e.Depot, false, ct));
            }
            catch (Exception ex)
            {
                /*  Eine Strategie darf die anderen nicht mitreissen. Sonst waere
                    ein Fehler in `aktiv` das Ende auch fuer die Grundlinie --
                    und ohne Grundlinie ist der Vergleich fuer diesen Tag
                    verloren.                                                  */
                log.LogError(ex, "Autopilot: Lauf für {Depot} gescheitert", e.Depot);
            }
        }

        return ergebnis;
    }

    /// <param name="erzwingen">
    /// Uebergeht den Takt. Fuer den Knopf „jetzt laufen lassen“ — sonst liesse
    /// sich bei Jahrestakt elf Monate lang nicht ausprobieren, ob ueberhaupt
    /// etwas passiert.
    /// </param>
    public async Task<AutopilotLauf> LaufeAsync(string depot, bool erzwingen = false,
                                                CancellationToken ct = default)
    {
        var d = (depot ?? "").Trim().ToLowerInvariant();

        var einst = (await EinstellungenAsync(ct)).FirstOrDefault(x => x.Depot == d)
            ?? throw new InvalidOperationException($"Keine Einstellungen für Depot {d}.");

        await using var conn = await factory.OpenAsync(ct);

        var handelstag = erzwingen || await HandelstagAsync(conn, d, einst.TaktTage, ct);

        var laufId = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO dbo.autopilot_lauf (depot, handelstag)
            OUTPUT INSERTED.lauf_id VALUES (@d, @ht)
            """, new { d, ht = handelstag }, cancellationToken: ct));

        var anwaerter = await RangfolgeAsync(d, 600, ct);
        var uebersicht = await invest.UebersichtAsync(d, null, ct);

        var block = uebersicht.Waehrungen.FirstOrDefault(b => b.Waehrung == einst.Waehrung);
        var vermoegen = block?.Vermoegen ?? 0;

        var gehalten = uebersicht.Positionen
            .Where(x => x.Buchungen > 0 && x.Anteile > 0)
            .ToDictionary(x => x.AssetId, x => x);

        var beschluesse = new List<Beschluss>();
        var geschaefte = 0;
        var gebuehren = 0m;
        var notiz = "";

        if (vermoegen <= 0)
        {
            notiz = $"Kein Kapital auf dem {einst.Waehrung}-Konto dieses Depots. "
                  + $"„Zurücksetzen\" stellt das Startbudget von "
                  + $"{einst.Startkapital:N2} {einst.Waehrung} bereit.";
        }
        else if (!handelstag)
        {
            notiz = $"Takt {einst.TaktName}: heute kein Handelstag. Bewertet und "
                  + "protokolliert wird trotzdem.";
        }

        /*  Handelbar ist nur, was einen Kurs hat. Was darüber hinaus verlangt
            wird, unterscheidet die drei Strategien — und das ist ihr ganzer
            Zweck:

              streng  verlangt einen NACHWEIS: genug bewertete Live-Prognosen,
                      daraus einen Verdienst über dem Nullpunkt und einen
                      Erwartungswert über den Kosten. Heute erfüllt das kein
                      einziger Wert, und dass dieser Korb leer bleibt, ist die
                      Aussage des Ganzen.

              aktiv   verlangt das ausdrücklich NICHT. Er folgt der Erwartung
                      des Modells, auch wenn sie unbewiesen ist, und zeigt in
                      Euro, was das kostet. Ihn an denselben Nachweis zu binden
                      hiesse, zwei gleiche Körbe zu bauen und einen Vergleich zu
                      verlieren.

              halten  kauft einmal dieselbe Startauswahl und rührt sich nie
                      wieder — die Grundlinie.                                 */
        var handelbar = anwaerter.Where(a => a.Kurs > 0).ToList();

        var ziel = d switch
        {
            "streng" => handelbar.Where(a => a.Bewertet >= MindestFaelle
                                          && a.Verdienst > 0
                                          && a.Erwartungswert > 0
                                          && a.Punktzahl > 0)
                                 .Take(einst.Werte).ToList(),

            "aktiv"  => handelbar.Where(a => a.Punktzahl > 0).Take(einst.Werte).ToList(),

            /*  Das genaue Gegenteil von `aktiv`.

                `aktiv` nimmt die hoechsten Erwartungen von oben, `invers` die
                niedrigsten von unten -- also genau die Werte, von denen das
                Modell einen Rueckgang erwartet. Dieselbe Anzahl, dieselben
                Gebuehren, derselbe Takt; nur das Vorzeichen des Signals ist
                gedreht.

                KEIN LEERVERKAUF. Dieses System kann nicht leerverkaufen, also
                werden die schlechtbewerteten Werte GEKAUFT. Erwartet das Modell
                zu Recht einen Rueckgang, verliert dieses Depot; irrt es,
                gewinnt es. Genau daran liegt der Wert: `aktiv` minus `invers`
                ist der Informationsgehalt des Signals, bereinigt um Marktgang
                und Kosten -- was `halten` allein nicht leisten kann, weil sie
                gar nicht handelt.

                `Punktzahl < 0` und nicht einfach die letzten der Liste: Ein
                Wert mit Erwartung null ist kein erwarteter Rueckgang, sondern
                gar keine Aussage. Ihn zu kaufen waere Zufall und nicht das
                Gegenteil von irgendetwas.                                    */
            "invers" => handelbar.Where(a => a.Punktzahl < 0)
                                 .OrderBy(a => a.Punktzahl)
                                 .Take(einst.Werte).ToList(),

            "halten" => gehalten.Count > 0
                            ? new List<AutopilotAnwaerter>()   // einmal gekauft, nie wieder
                            : handelbar.Where(a => a.Punktzahl > 0).Take(einst.Werte).ToList(),

            _ => new List<AutopilotAnwaerter>()
        };

        ziel = await EntflechteAsync(conn, ziel, ct);

        if (handelstag && vermoegen > 0)
        {
            /*  Die Anwaerter, die einen gehaltenen Wert ersetzen koennten -- als
                Schlange, aus der JE VERKAUF einer entnommen wird.

                Vorher verglich jeder Verkauf gegen denselben besten Anwaerter.
                Bei zwei Verkaeufen und zwei Kaeufen ging das gut; bei vier
                Verkaeufen und einem guten Anwaerter waeren alle vier gegen
                diesen einen gemessen worden, alle vier haetten die Hysterese
                gerissen -- und das Depot saesse auf Kasse, fuer die es keinen
                Ersatz gibt. Ein Tausch hat zwei Seiten, und die Sperre muss
                beide kennen.                                                  */
            var ersatz = new Queue<AutopilotAnwaerter>(
                ziel.Where(z => !gehalten.ContainsKey(z.AssetId)));

            // ---- Verkaufen: was nicht mehr im Zielkorb ist ------------------
            if (d != "halten")
            {
                foreach (var (assetId, pos) in gehalten)
                {
                    /*  Bleibt der Wert im Zielkorb, wird er GEHALTEN -- und
                        das gehoert so ins Protokoll. Ohne diese Zeile fiel er
                        durch bis in die Sammelschleife am Ende und stand dort
                        als „abgelehnt -- nicht unter den besten N". Gemessen an
                        Lauf 29: MNST mit der HOECHSTEN Erwartung von 7,226 %
                        war als abgelehnt ausgewiesen, obwohl es im Korb lag und
                        gehalten wurde. Ein Protokoll, das den bestbewerteten
                        Wert als verworfen meldet, ist schlimmer als keines.   */
                    if (ziel.Any(z => z.AssetId == assetId))
                    {
                        var g = anwaerter.FirstOrDefault(x => x.AssetId == assetId);

                        beschluesse.Add(new Beschluss(assetId, pos.Symbol, 0,
                            g?.Punktzahl ?? 0, g, "halten",
                            "im Zielkorb — gehalten, kein Handel nötig",
                            null, null, null, false));
                        continue;
                    }

                    var a = anwaerter.FirstOrDefault(x => x.AssetId == assetId);

                    /*  Hysterese: Getauscht wird erst, wenn der beste noch nicht
                        gehaltene Anwaerter den gehaltenen um mehr als eine
                        Bandbreite schlaegt. Ohne diese Sperre tauscht ein
                        Rangwechsel um einen Platz taeglich hin und her und zahlt
                        jedes Mal den Rundlauf.                                */
                    var bester = ersatz.Count > 0 ? ersatz.Peek() : null;
                    var vorsprung = (bester?.Punktzahl ?? 0) - (a?.Punktzahl ?? 0);

                    if (bester is not null && vorsprung <= (double)einst.Hysterese)
                    {
                        beschluesse.Add(new Beschluss(assetId, pos.Symbol, 0,
                            a?.Punktzahl ?? 0, a, "halten",
                            $"Vorsprung des besten Anwärters {vorsprung * 100:0.000} % liegt "
                          + $"unter der Hysterese von {einst.Hysterese * 100:0.00} % — "
                          + "ein Tausch kostete mehr, als der Rangunterschied hergibt.",
                            null, null, null, false));
                        continue;
                    }

                    var r = await invest.SetzeAsync(d, pos.Symbol, 0, einst.Waehrung,
                        "Autopilot: nicht mehr im Zielkorb", ct);

                    if (r is { Gebucht: true })
                    {
                        geschaefte++;
                        gebuehren += r.Gebuehr;

                        // Der Ersatz ist vergeben; der naechste Verkauf misst
                        // sich am naechsten Anwaerter.
                        if (ersatz.Count > 0) ersatz.Dequeue();
                    }

                    beschluesse.Add(new Beschluss(assetId, pos.Symbol, 0, a?.Punktzahl ?? 0, a,
                        "aufloesen", r?.Meldung ?? "verkauft", r?.Betrag, null, null,
                        r is { Gebucht: true }));
                }
            }

            // ---- Kaufen ----------------------------------------------------
            var frisch = ziel.Where(z => !gehalten.ContainsKey(z.AssetId)).ToList();

            /*  Die Zielgroesse bemisst sich am GESAMTVERMOEGEN, nicht an der
                freien Kasse: Sonst bekaeme der erste Kauf ein Achtel von allem
                und der letzte ein Achtel vom Rest -- die Positionen waeren
                systematisch ungleich, ohne dass es jemand angeordnet haette.

                DIE GEBUEHR GEHOERT IN DEN NENNER. Acht Positionen zu einem
                Achtel des Vermoegens ergeben zusammen genau das Vermoegen --
                und die acht Gebuehren kommen obendrauf. Gemessen im ersten
                Lauf: Die achte Buchung scheiterte mit „es fehlen 150,00", und
                zwar zuverlaessig jedes Mal, weil 8 x 18,75 genau der Fehlbetrag
                ist. Wer durch (1 + Gebuehrensatz) teilt, laesst Platz fuer
                genau das, was der Kauf zusaetzlich kostet.                    */
            var satz = (double)(block?.GebuehrPct ?? 0) / 100.0;

            var jePosition = Math.Round(
                Math.Min(vermoegen / (decimal)(Math.Max(1, einst.Werte) * (1 + satz)),
                         vermoegen * einst.MaxAnteil),
                2, MidpointRounding.ToZero);

            var urteile = 0;

            foreach (var z in frisch)
            {
                bool? urteil = null;
                string? urteilText = null;

                // Die Grundlinie bekommt kein Veto: Sie soll die Auswahl nicht veraendern.
                /*  Kein Veto fuer `invers`. Nemotron pruefte, ob ein Kauf
                    plausibel ist -- und wuerde damit genau das ablehnen, was
                    dieses Depot absichtlich tut. Eine Gegenkontrolle, die man
                    vor sich selbst schuetzt, ist keine mehr.                 */
                if (einst.Nemotron && d != "halten" && d != "invers"
                    && urteile < MaxUrteile)
                {
                    urteile++;
                    (urteil, urteilText) = await UrteilAsync(z, ct);

                    if (urteil is false)
                    {
                        beschluesse.Add(new Beschluss(z.AssetId, z.Symbol, 0, z.Punktzahl, z,
                            "abgelehnt", "Nemotron hat abgelehnt.", null, false, urteilText,
                            false));
                        continue;
                    }
                }

                var r = await invest.SetzeAsync(d, z.Symbol, jePosition, einst.Waehrung,
                    "Autopilot", ct);

                var gebucht = r is { Gebucht: true };

                if (gebucht)
                {
                    geschaefte++;
                    gebuehren += r!.Gebuehr;
                }

                beschluesse.Add(new Beschluss(z.AssetId, z.Symbol, 0, z.Punktzahl, z,
                    gebucht ? "kaufen" : "abgelehnt",
                    r?.Meldung ?? "nicht gebucht", r?.Betrag, urteil, urteilText, gebucht));
            }
        }

        /*  Und jetzt alles, was NICHT gehandelt wurde -- mit dem Grund. Ein
            Autopilot, der nur seine Geschaefte protokolliert, laesst sich nicht
            pruefen: Man sieht, was er getan hat, und nie, was er erwogen und
            verworfen hat. Beim strengen Depot ist genau das das Ergebnis.     */
        var schonDa = beschluesse.Select(b => b.AssetId).ToHashSet();

        foreach (var a in anwaerter.Take(40))
        {
            if (schonDa.Contains(a.AssetId)) continue;

            /*  Der Grund muss die Zahl nennen, an der es scheiterte. „Nicht
                gekauft" ohne sie ist keine Begründung, sondern eine Behauptung
                — und beim strengen Korb ist genau das das Ergebnis des Laufs. */
            var grund =
                d == "streng" && a.Bewertet < MindestFaelle
                    ? $"nur {a.Bewertet} bewertete Live-Prognosen, nötig {MindestFaelle} "
                    + "— ohne Nachweis wird hier nicht gehandelt"
                : d == "streng" && a.Verdienst <= 0
                    ? $"geschrumpfte Trefferquote {a.Trefferquote:0.000} unter dem Nullpunkt "
                    + "0,523 — kein nachgewiesener Vorsprung"
                : d == "streng" && a.Erwartungswert <= 0
                    ? $"Trefferquote {a.Trefferquote:0.000}, nötig {a.NoetigeTrefferquote:0.000}"
                    + $" — Erwartungswert {a.Erwartungswert * 100:0.000} % je Geschäft"
                /*  Bei `invers` ist die Bedingung umgedreht, also muss es
                    auch die Begruendung sein. „kein Anstieg" als Ablehnung in
                    einem Depot, das Rueckgaenge sucht, waere schlicht falsch
                    aufgeschrieben -- und ein Protokoll, das sich selbst
                    widerspricht, entwertet auch die richtigen Zeilen.        */
                : d == "invers" && a.Punktzahl >= 0
                    ? $"Erwartung {a.Punktzahl * 100:0.000} % — kein erwarteter Rückgang"
                : d == "invers"
                    ? "nicht unter den " + einst.Werte + " schwächsten"
                : a.Punktzahl <= 0
                    ? $"Erwartung {a.Punktzahl * 100:0.000} % — kein Anstieg"
                    : "nicht unter den besten " + einst.Werte;

            beschluesse.Add(new Beschluss(a.AssetId, a.Symbol, 0, a.Punktzahl, a,
                "abgelehnt", grund, null, null, null, false));
        }

        var sortiert = beschluesse
            .OrderByDescending(b => b.Ausgefuehrt)
            .ThenByDescending(b => b.Punktzahl)
            .Select((b, i) => b with { Rang = i + 1 })
            .ToList();

        await SchreibeAsync(conn, laufId, sortiert, ct);

        var nachher = await invest.UebersichtAsync(d, null, ct);
        var endstand = nachher.Waehrungen
            .FirstOrDefault(b => b.Waehrung == einst.Waehrung)?.Vermoegen ?? 0;

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.autopilot_lauf
               SET beendet_utc = SYSUTCDATETIME(), geprueft = @gepr, geschaefte = @gesch,
                   gebuehren = @geb, vermoegen = @verm, notiz = @notiz
             WHERE lauf_id = @id
            """,
            new { id = laufId, gepr = anwaerter.Count, gesch = geschaefte,
                  geb = gebuehren, verm = endstand,
                  notiz = notiz.Length > 0 ? notiz : null },
            cancellationToken: ct));

        log.LogInformation(
            "Autopilot {Depot}: {Gepr} geprüft, {Gesch} Geschäfte, {Geb} Gebühren, "
          + "Vermögen {Verm} {W}", d, anwaerter.Count, geschaefte, gebuehren, endstand,
            einst.Waehrung);

        return new AutopilotLauf(laufId, d, DateTime.UtcNow, DateTime.UtcNow,
            anwaerter.Count, geschaefte, gebuehren, endstand, handelstag,
            notiz.Length > 0 ? notiz : null);
    }

    /// <summary>
    /// Darf heute gehandelt werden? Gemessen am Abstand zur letzten Buchung
    /// dieses Depots.
    /// </summary>
    private static async Task<bool> HandelstagAsync(IDbConnection conn, string depot,
                                                    int taktTage, CancellationToken ct)
    {
        if (taktTage <= 1) return true;

        var letzte = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(am_utc) FROM dbo.invest_buchung WHERE depot = @depot",
            new { depot }, cancellationToken: ct));

        // Noch nie gehandelt: Der erste Einstieg darf nicht auf den Takt warten.
        if (letzte is null) return true;

        return (DateTime.UtcNow - letzte.Value).TotalDays >= taktTage;
    }

    /// <summary>
    /// Wirft aus dem Zielkorb, was einem besser bewerteten Wert zu aehnlich ist.
    ///
    /// <para>Die Korbsuche hatte dasselbe Problem: Sie waehlte HWM, XAUT-USD und
    /// PAXG-USD — die letzten beiden sind goldgedeckte Marken und laufen
    /// praktisch identisch. Ein Korb aus drei Teilen, von denen zwei dasselbe
    /// sind, ist ein Korb aus zwei Teilen, und die Streuung, die er verspricht,
    /// gibt es nicht.</para>
    /// </summary>
    private static async Task<List<AutopilotAnwaerter>> EntflechteAsync(
        IDbConnection conn, List<AutopilotAnwaerter> ziel, CancellationToken ct)
    {
        if (ziel.Count < 2) return ziel;

        var ids = ziel.Select(z => z.AssetId).ToArray();

        var paare = (await conn.QueryAsync<Paar>(new CommandDefinition("""
            SELECT asset_id_a AS A, asset_id_b AS B, corr0 AS Korrelation
              FROM dbo.pair_stat
             WHERE interval_code = '1d'
               AND asset_id_a IN @ids AND asset_id_b IN @ids
               AND ABS(corr0) > 0.9
            """, new { ids }, cancellationToken: ct))).ToList();

        if (paare.Count == 0) return ziel;

        var behalten = new List<AutopilotAnwaerter>();

        // Die Liste ist nach Punktzahl sortiert: Wer zuerst kommt, bleibt.
        foreach (var z in ziel)
        {
            var kollidiert = behalten.Any(b => paare.Any(pp =>
                (pp.A == b.AssetId && pp.B == z.AssetId) ||
                (pp.B == b.AssetId && pp.A == z.AssetId)));

            if (!kollidiert) behalten.Add(z);
        }

        return behalten;
    }

    private readonly record struct Paar(int A, int B, double Korrelation);

    // ------------------------------------------------------------- Nemotron --

    /// <summary>
    /// Nemotrons Veto.
    ///
    /// <para><b>Kein Stimmrecht ueber Betraege.</b> Bei den ersten drei echten
    /// Fragen an diesen Agenten hatte er zweimal <b>null</b> Werkzeugaufrufe —
    /// er hat frei geantwortet. Ein Modell, das frei antwortet, darf keine
    /// Zahlen setzen; es darf ablehnen und begruenden.</para>
    ///
    /// <para><b>Faellt es aus, wird gehandelt.</b> Ein fehlendes Urteil ist
    /// ausdruecklich keine Ablehnung — sonst entschiede die Verfuegbarkeit einer
    /// gemieteten Grafikkarte ueber das Depot, und das Protokoll unterschiede
    /// nicht mehr zwischen „abgelehnt“ und „nicht gefragt“.</para>
    /// </summary>
    private async Task<(bool? Urteil, string? Text)> UrteilAsync(
        AutopilotAnwaerter z, CancellationToken ct)
    {
        var frage =
            $"Ein automatisches Depot will {z.Symbol} ({z.Name}) kaufen. Gemessen: "
          + $"Richtungstrefferquote {z.Trefferquote:0.000} über {z.Bewertet} bewertete "
          + $"Prognosen, mittlere Tagesbewegung {z.Bewegung * 100:0.00} %, "
          + $"Erwartungswert nach Kosten {z.Erwartungswert * 100:0.000} % je Geschäft, "
          + $"Bewertung des Modells {z.Punktzahl * 100:0.000} %.\n\n"
          + "Prüfe mit deinen Werkzeugen, ob etwas gegen diesen Kauf spricht — etwa eine "
          + "kaputte Kursreihe, ein eingestellter Wert oder eine Nachricht, die die Zahlen "
          + "überholt. Antworte AUSSCHLIESSLICH mit JSON in genau dieser Form:\n"
          + "{\"ablehnen\": true oder false, \"grund\": \"ein Satz\"}";

        try
        {
            using var frist = CancellationTokenSource.CreateLinkedTokenSource(ct);
            frist.CancelAfter(TimeSpan.FromSeconds(120));

            var antwort = await reasoning.AskAsync([], frage, ct: frist.Token);
            var text = antwort.Text ?? "";

            /*  Das Modell verpackt JSON gern in Fliesstext oder einen Codeblock.
                Gesucht wird deshalb von der ersten geschweiften Klammer bis zur
                letzten -- ein strenger Parser auf die ganze Antwort scheiterte
                an einem einzigen einleitenden Satz.                            */
            var von = text.IndexOf('{');
            var bis = text.LastIndexOf('}');

            if (von >= 0 && bis > von)
            {
                using var doc = JsonDocument.Parse(text[von..(bis + 1)]);
                var wurzel = doc.RootElement;

                var ablehnen = wurzel.TryGetProperty("ablehnen", out var a)
                            && a.ValueKind == JsonValueKind.True;

                var grund = wurzel.TryGetProperty("grund", out var gr) ? gr.GetString() : null;

                return (!ablehnen, grund ?? text[..Math.Min(400, text.Length)]);
            }

            /*  Kein verwertbares JSON ist KEINE Ablehnung. Sonst hinge das Depot
                daran, ob das Modell gerade Lust auf Formvorschriften hatte.    */
            log.LogWarning("Autopilot: Nemotron antwortete ohne JSON für {Symbol}", z.Symbol);

            return (null, "Antwort ohne verwertbares JSON: "
                        + text[..Math.Min(300, text.Length)]);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Autopilot: kein Urteil für {Symbol}", z.Symbol);
            return (null, "Nemotron nicht erreichbar — gehandelt ohne Urteil.");
        }
    }

    // ------------------------------------------------------------ Protokoll --

    private sealed record Beschluss(int AssetId, string Symbol, int Rang, double Punktzahl,
                                    AutopilotAnwaerter? Anwaerter, string Was, string Grund,
                                    decimal? Betrag, bool? Urteil, string? UrteilText,
                                    bool Ausgefuehrt);

    private static async Task SchreibeAsync(IDbConnection conn, int laufId,
                                            List<Beschluss> beschluesse, CancellationToken ct)
    {
        foreach (var b in beschluesse)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO dbo.autopilot_entscheidung
                       (lauf_id, asset_id, rang, punktzahl, bestandteile, trefferquote,
                        bewegung, erwartungswert, beschluss, grund, betrag, urteil,
                        urteil_text, ausgefuehrt)
                VALUES (@lauf, @asset, @rang, @punkt, @teile, @p, @bew, @ew, @was, @grund,
                        @betrag, @urteil, @utext, @aus)
                """,
                new
                {
                    lauf = laufId, asset = b.AssetId, rang = b.Rang, punkt = b.Punktzahl,
                    teile = b.Anwaerter is null
                        ? null : JsonSerializer.Serialize(b.Anwaerter.Beitraege),
                    p = b.Anwaerter?.Trefferquote,
                    bew = b.Anwaerter?.Bewegung,
                    ew = b.Anwaerter?.Erwartungswert,
                    was = b.Was,
                    grund = b.Grund.Length > 600 ? b.Grund[..600] : b.Grund,
                    betrag = b.Betrag,
                    urteil = b.Urteil,
                    utext = b.UrteilText is { } t && t.Length > 2000 ? t[..2000] : b.UrteilText,
                    aus = b.Ausgefuehrt
                }, cancellationToken: ct));
        }
    }

    public async Task<IReadOnlyList<AutopilotLauf>> LaeufeAsync(string? depot, int grenze = 20,
                                                                CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var laeufe = (await conn.QueryAsync<AutopilotLauf>(new CommandDefinition("""
            SELECT TOP (@grenze)
                   lauf_id AS LaufId, depot AS Depot, gestartet_utc AS GestartetUtc,
                   beendet_utc AS BeendetUtc, geprueft AS Geprueft, geschaefte AS Geschaefte,
                   gebuehren AS Gebuehren, vermoegen AS Vermoegen, handelstag AS Handelstag,
                   notiz AS Notiz
              FROM dbo.autopilot_lauf
             WHERE (@depot IS NULL OR depot = @depot)
             ORDER BY lauf_id DESC
            """, new { depot, grenze = Math.Clamp(grenze, 1, 200) },
            cancellationToken: ct))).ToList();

        return laeufe;
    }

    public async Task<IReadOnlyList<AutopilotEntscheidung>> BeschluesseAsync(
        int laufId, int grenze = 200, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<AutopilotEntscheidung>(new CommandDefinition("""
            SELECT TOP (@grenze)
                   e.entscheidung_id AS EntscheidungId, e.lauf_id AS LaufId,
                   l.depot AS Depot, e.asset_id AS AssetId, a.symbol AS Symbol,
                   e.rang AS Rang, e.punktzahl AS Punktzahl,
                   e.trefferquote AS Trefferquote, e.bewegung AS Bewegung,
                   e.erwartungswert AS Erwartungswert, e.beschluss AS Beschluss,
                   e.grund AS Grund, e.betrag AS Betrag, e.urteil AS Urteil,
                   e.urteil_text AS UrteilText, e.ausgefuehrt AS Ausgefuehrt,
                   e.am_utc AS AmUtc
              FROM dbo.autopilot_entscheidung e
              JOIN dbo.autopilot_lauf l ON l.lauf_id = e.lauf_id
              JOIN dbo.asset a ON a.asset_id = e.asset_id
             WHERE e.lauf_id = @lauf
             ORDER BY e.rang
            """, new { lauf = laufId, grenze = Math.Clamp(grenze, 1, 1000) },
            cancellationToken: ct));

        return rows.ToList();
    }

    // ------------------------------------------------------------ Vergleich --

    /// <summary>
    /// Die Strategien gegeneinander und gegen die Grundlinie.
    ///
    /// <para>Der Vorsprung wird gegen <c>halten</c> gerechnet — nicht gegen
    /// null. Eine Strategie, die in einem steigenden Markt fuenf Prozent macht,
    /// hat nichts geleistet, wenn Kaufen-und-Halten sieben gemacht haette.</para>
    /// </summary>
    public async Task<AutopilotVergleich> VergleichAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var was = new Dictionary<string, string>
        {
            ["manuell"] = "von Hand eingetragen",
            ["streng"]  = "handelt nur bei positivem Erwartungswert",
            ["aktiv"]   = "hält immer die bestbewerteten Werte",
            ["halten"]  = "Grundlinie: einmal gekauft, nie wieder angefasst",
            ["invers"]  = "Gegenkontrolle: hält die schwächstbewerteten Werte"
        };

        var geschaefte = (await conn.QueryAsync<Zaehlung>(new CommandDefinition("""
            SELECT depot AS Depot, COUNT(*) AS Anzahl
              FROM dbo.invest_buchung GROUP BY depot
            """, cancellationToken: ct))).ToDictionary(x => x.Depot, x => x.Anzahl);

        var zeilen = new List<AutopilotVergleichszeile>();

        foreach (var depot in new[] { "manuell", "streng", "aktiv", "halten", "invers" })
        {
            var u = await invest.UebersichtAsync(depot, null, ct);

            foreach (var b in u.Waehrungen)
            {
                zeilen.Add(new AutopilotVergleichszeile(
                    depot, was[depot], b.Eingezahlt, b.Vermoegen, b.Gewinn, b.RenditePct,
                    b.Gebuehren, geschaefte.GetValueOrDefault(depot), b.Positionen, null));
            }
        }

        var grundlinie = zeilen.FirstOrDefault(z => z.Depot == "halten")?.RenditePct;

        var mitVorsprung = zeilen
            .Select(z => z.Depot == "halten" || grundlinie is null || z.RenditePct is null
                ? z
                : z with { VorsprungPct = z.RenditePct - grundlinie })
            .ToList();

        return new AutopilotVergleich(mitVorsprung, DateTime.UtcNow,
            grundlinie is null
                ? "Die Grundlinie „halten“ hat noch nichts gekauft — ohne sie lässt sich kein "
                + "Vorsprung bestimmen. Sie ist der einzige Massstab, der beantwortet, ob das "
                + "Handeln überhaupt etwas beiträgt."
                : "Der Vorsprung ist gegen „halten“ gerechnet, nicht gegen null. Eine "
                + "Strategie, die in einem steigenden Markt fünf Prozent macht, hat nichts "
                + "geleistet, wenn Kaufen-und-Halten sieben gemacht hätte. Die Grundlinie "
                + "zahlt dieselben Gebühren wie die anderen.");
    }

    private readonly record struct Zaehlung(string Depot, int Anzahl);
}

