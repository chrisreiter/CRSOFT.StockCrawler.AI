using System.Text;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Die Day-Trading-Seite.
///
/// <para><b>Was sie beantwortet.</b> Nicht „was soll ich heute kaufen", sondern
/// die Frage davor: <i>Trägt der Markt heute überhaupt einen Handel innerhalb
/// des Tages — nach Gebühren?</i> Diese Frage hat eine messbare Antwort, und
/// sie ist die einzige, die dieses System auf Stundenbasis ehrlich geben
/// kann.</para>
///
/// <para><b>Die entscheidende Kennzahl</b> ist der Anteil der Stundenbars,
/// deren Betragsbewegung den Rundlauf aus Gebühren und Schlupf übersteigt.
/// Liegt er bei einem Drittel, sind zwei von drei Stunden verloren, bevor die
/// Richtungsfrage überhaupt gestellt ist. Keine Prognosegüte der Welt holt das
/// zurück — man kann eine Bewegung nicht handeln, die kleiner ist als ihre
/// Kosten.</para>
///
/// <para><b>Warum die Seite keine Signale ausgibt.</b> Die geladenen
/// Deep-Modelle rechnen auf TAGESBARS — sie haben zum Handel innerhalb des
/// Tages nichts zu sagen, und ihre Zahlen als Stundenmaß auszugeben wäre ein
/// Faktor 24 daneben. Was auf Stundenbasis wirklich gemessen wurde, sind die
/// bewerteten Prognosen des Ensembles mit einer und vier Stunden Horizont.
/// Eine Seite, die ohne diesen Beleg Einstiege nennte, wäre die gefährlichste
/// in dieser Anwendung.</para>
/// </summary>
public sealed class DayTradingService(
    ISqlConnectionFactory factory,
    IKnowledgeService knowledge) : IDayTradingService
{
    public async Task<TagesHandel> BuildAsync(int stunden, CancellationToken ct = default)
    {
        stunden = Math.Clamp(stunden, 24, 24 * 90);

        var kosten = Handelskosten.Standard;

        await using var conn = await factory.OpenAsync(ct);

        var beweglichkeit = await BeweglichkeitAsync(conn, stunden, kosten, ct);
        var fenster = await FensterAsync(conn, ct);
        var bewegungen = await BewegungenAsync(conn, ct);
        var guete = await GueteAsync(conn, kosten, ct);
        var literatur = await LiteraturAsync(ct);

        return new TagesHandel(
            DateTime.UtcNow, kosten.Rundlauf, kosten.Beschreibung,
            Kernaussage(beweglichkeit, guete, kosten),
            fenster, beweglichkeit, bewegungen, guete,
            Anweisungen(beweglichkeit, guete, kosten),
            literatur);
    }

    // -------------------------------------------------------- Kernaussage ---

    /// <summary>
    /// Ein Satz, der oben steht und die Seite zusammenfasst. Er nennt zuerst
    /// die Kostenhürde, nicht die Chance — denn die Hürde gilt immer, die
    /// Chance nur manchmal.
    /// </summary>
    private static string Kernaussage(
        IReadOnlyList<Beweglichkeit> b, IReadOnlyList<IntradayGuete> g, Handelskosten k)
    {
        if (b.Count == 0)
            return "Keine Stundendaten im Zeitraum — ohne sie ist über den Handel innerhalb "
                 + "des Tages nichts zu sagen.";

        var krypto = b.FirstOrDefault(x => x.Klasse == AssetClass.Crypto);
        var aktie = b.FirstOrDefault(x => x.Klasse == AssetClass.Stock);

        var traegt = g.Any(x => x.Traegt);

        var sb = new StringBuilder();

        sb.Append($"Ein Rundlauf kostet {k.Rundlauf * 100:0.##} Prozent. ");

        if (aktie is not null)
            sb.Append($"Bei Aktien überschreiten **{aktie.AnteilUeberKosten * 100:0} Prozent** ")
              .Append("der Stunden diese Schwelle überhaupt");

        if (krypto is not null)
            sb.Append(aktie is null ? "Bei Krypto sind es " : ", bei Krypto ")
              .Append($"**{krypto.AnteilUeberKosten * 100:0} Prozent**");

        sb.Append(". ");

        sb.Append(traegt
            ? "Auf mindestens einem Stundenhorizont ist der Erwartungswert nach Kosten "
              + "positiv — die Zahlen stehen unten."
            : "**Auf keinem Stundenhorizont ist der Erwartungswert nach Kosten positiv.** "
              + "Diese Seite nennt deshalb keine Einstiege, sondern nur, was messbar ist.");

        return sb.ToString();
    }

    // -------------------------------------------------------- Anweisungen ---

    /// <summary>
    /// Die „Anweisungen" sind bewusst Regeln über das Verfahren, nicht über
    /// einzelne Werte. Was dieses System belastbar sagen kann, ist: unter
    /// welchen Bedingungen ein Handel innerhalb des Tages überhaupt eine
    /// Chance hat. Welcher Wert — das kann es nicht sagen, und so zu tun als
    /// ob wäre die eine Sache, die hier nicht passieren darf.
    /// </summary>
    private static List<string> Anweisungen(
        IReadOnlyList<Beweglichkeit> b, IReadOnlyList<IntradayGuete> g, Handelskosten k)
    {
        var liste = new List<string>();

        var krypto = b.FirstOrDefault(x => x.Klasse == AssetClass.Crypto);
        var aktie = b.FirstOrDefault(x => x.Klasse == AssetClass.Stock);

        liste.Add(
            $"**Rechnen Sie die Kosten zuerst, nicht zuletzt.** Ein Rundlauf kostet "
            + $"{k.Rundlauf * 100:0.##} Prozent ({k.Beschreibung}). Fünf Geschäfte an einem Tag "
            + $"sind {k.Rundlauf * 5 * 100:0.#} Prozent, die vor jedem Gewinn hereinzuholen sind.");

        if (aktie is not null && krypto is not null)
            liste.Add(
                $"**Die Beweglichkeit unterscheidet sich um ein Vielfaches.** Die mittlere "
                + $"Stundenbewegung liegt bei Aktien um {aktie.MittlereBewegung * 100:0.00} "
                + $"Prozent, bei Krypto um {krypto.MittlereBewegung * 100:0.00} Prozent. "
                + "Dieselbe Gebühr wiegt deshalb bei Aktien deutlich schwerer.");

        if (aktie is { AnteilUeberKosten: < 0.5 })
            liste.Add(
                $"**Bei Aktien sind {(1 - aktie.AnteilUeberKosten) * 100:0} Prozent der Stunden "
                + "von vornherein verloren** — ihre Bewegung ist kleiner als der Rundlauf. Wer "
                + "in einer solchen Stunde ein- und aussteigt, zahlt, gleich ob er richtig lag.");

        if (!g.Any(x => x.Traegt))
            liste.Add(
                "**Kein Verfahren dieser Anwendung trägt auf Stundenbasis.** Die Deep-Modelle "
                + "rechnen auf Tagesbars, und die bewerteten Stundenprognosen des Ensembles "
                + "erreichen keinen Vorsprung, der die Kosten deckt. Es gibt hier keine "
                + "Rechengrundlage für einen Einstieg — nur eine für die Feststellung, dass "
                + "keine da ist.");

        if (krypto is not null)
            liste.Add(
                "**Die nötige Trefferquote ist der ernüchterndste Wert dieser Seite.** Damit "
                + "sich ein Geschäft lohnt, muss gelten: (2p − 1) · Bewegung > Kosten. Bei "
                + $"{krypto.MittlereBewegung * 100:0.00} Prozent mittlerer Stundenbewegung in "
                + $"Krypto und {k.Rundlauf * 100:0.##} Prozent Rundlauf sind das "
                + $"**{k.NoetigeTrefferquote(krypto.MittlereBewegung) * 100:0.0} Prozent** — "
                + "nicht 51, nicht 55.");

        liste.Add(
            "**Gleichzeitigkeit ist kein Vorlauf.** Der Querschnitt aller Kurse erklärt die "
            + "Bewegung eines einzelnen am selben Tag mit einem Bestimmtheitsmaß von 0,355, die "
            + "morgige mit 0,0039. Was gleichzeitig zusammenhängt, lässt sich nicht handeln.");

        liste.Add(
            "**Eine hohe Trefferquote ist kein Gewinn.** Zwölf der vierundfünfzig Kreuzungspaare "
            + "mit einer Trefferquote über 55 Prozent verlieren im Mittel Geld — viele kleine "
            + "Gewinne, seltene große Verluste. Neben der Trefferquote muss immer der mittlere "
            + "Ertrag nach Kosten stehen.");

        return liste;
    }

    // ------------------------------------------------------ Beweglichkeit ---

    private static async Task<List<Beweglichkeit>> BeweglichkeitAsync(
        System.Data.Common.DbConnection conn, int stunden, Handelskosten k, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<BeweglichkeitZeile>(new CommandDefinition("""
            SET NOCOUNT ON;

            /* Betragsbewegung je Stundenbar, nur auf verfolgten Werten. */
            WITH r AS (
              SELECT a.asset_class,
                     p.asset_id,
                     ABS(LOG(CAST(p.[close] AS FLOAT) /
                         LAG(CAST(p.[close] AS FLOAT))
                             OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc))) AS bew
                FROM dbo.price_bar p
                JOIN dbo.asset a ON a.asset_id = p.asset_id AND a.is_tracked = 1
               WHERE p.interval_code = '1h'
                 AND p.ts_utc >= DATEADD(HOUR, -@stunden, SYSUTCDATETIME())
                 AND p.[close] > 0
            ),
            g AS (
              SELECT asset_class, asset_id, bew,
                     PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY bew)
                       OVER (PARTITION BY asset_class) AS median
                FROM r WHERE bew IS NOT NULL
            )
            SELECT asset_class AS Klasse,
                   COUNT(*) AS Bars,
                   COUNT(DISTINCT asset_id) AS Werte,
                   AVG(bew) AS MittlereBewegung,
                   MAX(median) AS MedianBewegung,
                   AVG(CASE WHEN bew > @kosten THEN 1.0 ELSE 0.0 END) AS AnteilUeberKosten,
                   AVG(CASE WHEN bew > 2 * @kosten THEN 1.0 ELSE 0.0 END) AS AnteilUeberDoppelt
              FROM g
             GROUP BY asset_class
             ORDER BY asset_class;
            """,
            new { stunden, kosten = k.Rundlauf }, commandTimeout: 180, cancellationToken: ct));

        return rows.Select(r => new Beweglichkeit(
            (AssetClass)r.Klasse, KlasseName((AssetClass)r.Klasse),
            r.Bars, r.Werte, r.MittlereBewegung, r.MedianBewegung,
            r.AnteilUeberKosten, r.AnteilUeberDoppelt)).ToList();
    }

    // ----------------------------------------------------- Handelsfenster ---

    /// <summary>
    /// Ob eine Klasse gerade handelt, wird aus den Daten abgelesen und nicht
    /// aus einem hinterlegten Börsenkalender. Ein Kalender wäre eine zweite
    /// Wahrheit, die still veralten kann; die jüngste Bar veraltet nicht.
    /// </summary>
    private static async Task<List<Handelsfenster>> FensterAsync(
        System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<FensterZeile>(new CommandDefinition("""
            SELECT a.asset_class AS Klasse, MAX(p.ts_utc) AS Juengste
              FROM dbo.price_bar p
              JOIN dbo.asset a ON a.asset_id = p.asset_id AND a.is_tracked = 1
             WHERE p.interval_code = '1h'
               AND p.ts_utc >= DATEADD(DAY, -7, SYSUTCDATETIME())
             GROUP BY a.asset_class
             ORDER BY a.asset_class
            """, commandTimeout: 120, cancellationToken: ct));

        var jetzt = DateTime.UtcNow;

        return rows.Select(r =>
        {
            var alt = r.Juengste is null ? 999 : (int)(jetzt - r.Juengste.Value).TotalHours;
            var offen = alt <= 2;
            var kl = (AssetClass)r.Klasse;

            var bemerkung = kl == AssetClass.Crypto
                ? offen ? "Krypto handelt durchgehend." : "Datenabruf hängt — Krypto handelt sonst durchgehend."
                : offen ? "Es kommen frische Bars." :
                    alt < 20 ? "Ausserhalb der Handelszeit oder Datenabruf hängt."
                             : "Wochenende oder Feiertag.";

            return new Handelsfenster(kl, KlasseName(kl), r.Juengste, alt, offen, bemerkung);
        }).ToList();
    }

    // -------------------------------------------------------- Bewegungen ----

    private static async Task<List<Stundenbewegung>> BewegungenAsync(
        System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<BewegungZeile>(new CommandDefinition("""
            SET NOCOUNT ON;

            /* Je Wert die jüngste Stundenbar und der Vergleich eine bzw. sechs
               Stunden davor. Die Fensterfunktion ist der einzige Weg, das ohne
               eine Unterabfrage je Wert zu bekommen. */
            WITH n AS (
              SELECT p.asset_id, p.ts_utc, p.[close], p.high, p.low,
                     ROW_NUMBER() OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc DESC) AS rn
                FROM dbo.price_bar p
                JOIN dbo.asset a ON a.asset_id = p.asset_id AND a.is_tracked = 1
               WHERE p.interval_code = '1h'
                 /* 48 statt 12 Stunden.

                    Mit einem Zwölf-Stunden-Fenster war die Tabelle nach einem
                    Wochenende und nach jeder Ruhepause des Rechners leer: Für
                    den Vergleich über sechs Bars braucht es sechs Bars, und
                    nach acht Stunden ohne Abruf gibt es im Fenster nur noch
                    vier. Leer heißt dann „keine Bewegung", obwohl es „keine
                    Daten" heißt -- zwei sehr verschiedene Aussagen. */
                 AND p.ts_utc >= DATEADD(HOUR, -48, SYSUTCDATETIME())
                 AND p.[close] > 0
            )
            SELECT a.symbol AS Symbol, a.name AS Name, a.asset_class AS Klasse,
                   n1.[close] AS Kurs, n1.ts_utc AS TsUtc,
                   CAST(n1.[close] AS FLOAT) / n2.[close] - 1 AS VeraenderungStunde,
                   CAST(n1.[close] AS FLOAT) / n6.[close] - 1 AS VeraenderungSechs,
                   CASE WHEN n1.low > 0
                        THEN CAST(n1.high AS FLOAT) / n1.low - 1 ELSE 0 END AS Spannweite
              FROM n n1
              JOIN n n2 ON n2.asset_id = n1.asset_id AND n2.rn = 2 AND n2.[close] > 0
              JOIN n n6 ON n6.asset_id = n1.asset_id AND n6.rn = 6 AND n6.[close] > 0
              JOIN dbo.asset a ON a.asset_id = n1.asset_id
             WHERE n1.rn = 1
             ORDER BY ABS(CAST(n1.[close] AS FLOAT) / n2.[close] - 1) DESC
            OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY;
            """, commandTimeout: 180, cancellationToken: ct));

        return rows.Select(r => new Stundenbewegung(
            r.Symbol, r.Name, (AssetClass)r.Klasse, r.Kurs, r.TsUtc,
            r.VeraenderungStunde, r.VeraenderungSechs, r.Spannweite)).ToList();
    }

    // -------------------------------------------------------------- Güte ----

    /// <summary>
    /// Was die Anwendung auf Stundenhorizonten tatsächlich getroffen hat.
    ///
    /// <para><b>Warum nicht die Deep-Modelle.</b> Deren Horizonte sind in
    /// TAGESBARS gezählt — das kürzeste Band sagt einen, zwei, drei und fünf
    /// Tage voraus, nicht Stunden. Sie hier als Stundenmaß auszugeben wäre ein
    /// Faktor 24 daneben. Für den Handel innerhalb des Tages taugt nur, was
    /// auf Stundenbars gemessen wurde: die bewerteten Prognosen des Ensembles
    /// mit <c>horizon_hours</c> 1 und 4.</para>
    ///
    /// <para>Gemessen wird gegen die 0,523, ab der eine Trefferquote in dieser
    /// Anwendung überhaupt als Vorsprung gilt — und zusätzlich gegen die
    /// Kostenschwelle: Eine Richtung zu treffen nützt nichts, wenn die
    /// Bewegung den Rundlauf nicht deckt.</para>
    /// </summary>
    private static async Task<List<IntradayGuete>> GueteAsync(
        System.Data.Common.DbConnection conn, Handelskosten k, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<GueteZeile>(new CommandDefinition("""
            SELECT f.horizon_hours AS Horizont,
                   COUNT(*) AS N,
                   AVG(CASE WHEN s.direction_correct = 1 THEN 1.0 ELSE 0.0 END) AS Trefferquote,
                   AVG(s.abs_pct_error) AS MittlererFehler,
                   AVG(ABS(s.actual_return)) AS MittlereBewegung
              FROM dbo.forecast f
              JOIN dbo.forecast_score s ON s.forecast_id = f.forecast_id
             WHERE f.horizon_hours IN (1, 4)
             GROUP BY f.horizon_hours
             ORDER BY f.horizon_hours
            """, commandTimeout: 180, cancellationToken: ct));

        var liste = new List<IntradayGuete>();

        foreach (var r in rows)
        {
            /* Der Erwartungswert entscheidet, nicht die Trefferquote.

               Der erste Entwurf verlangte „Trefferquote über 0,523 UND
               Bewegung über den Kosten" -- und wies damit h=1 als tragfähig
               aus: 52,4 Prozent bei 0,47 Prozent Bewegung. Nachgerechnet sind
               das (2·0,524−1)·0,47 % = 0,023 % Bruttovorsprung gegen 0,3 %
               Kosten, also −0,28 % je Geschäft. Beide Bedingungen erfüllt,
               und trotzdem ein Verlustgeschäft. */
            var ev = k.Erwartungswert(r.Trefferquote, r.MittlereBewegung);
            var noetig = k.NoetigeTrefferquote(r.MittlereBewegung);
            var traegt = ev > 0;

            var urteil = traegt
                ? $"Trefferquote {r.Trefferquote * 100:0.0} Prozent bei {r.MittlereBewegung * 100:0.00} "
                  + $"Prozent mittlerer Bewegung — Erwartungswert nach Kosten "
                  + $"{ev * 100:+0.000;-0.000} Prozent je Geschäft."
                : $"Trefferquote {r.Trefferquote * 100:0.0} Prozent bei {r.MittlereBewegung * 100:0.00} "
                  + $"Prozent mittlerer Bewegung. Erwartungswert nach Kosten "
                  + $"**{ev * 100:+0.000;-0.000} Prozent** je Geschäft — nötig wären "
                  + $"{noetig * 100:0.0} Prozent Trefferquote.";

            liste.Add(new IntradayGuete(
                $"Ensemble, {r.N:N0} bewertete Prognosen", r.Horizont,
                null, r.Trefferquote, null, null, traegt, urteil));
        }

        if (liste.Count == 0)
            liste.Add(new IntradayGuete("Ensemble", 0, null, null, null, null, false,
                "Noch keine bewertete Stundenprognose. Ohne sie ist über die Güte innerhalb "
                + "des Tages nichts zu sagen."));

        liste.Add(new IntradayGuete(
            "Deep Learning", 0, null, null, null, null, false,
            "Die geladenen Bänder rechnen auf TAGESBARS — das kürzeste sagt einen bis fünf "
            + "Tage voraus. Für den Handel innerhalb des Tages gibt es hier kein Modell, und "
            + "die Tageszahlen als Stundenmaß auszugeben wäre ein Faktor 24 daneben."));

        return liste;
    }

    private sealed class GueteZeile
    {
        public int Horizont { get; set; }
        public int N { get; set; }
        public double Trefferquote { get; set; }
        public double MittlererFehler { get; set; }
        public double MittlereBewegung { get; set; }
    }

    // -------------------------------------------------------- Literatur -----

    /// <summary>
    /// Belege aus der eingebetteten Fachliteratur. Die Fragen sind bewusst auf
    /// das gerichtet, was die Zahlen oben behaupten — eine Literaturstelle,
    /// die etwas anderes sagt als die Messung, ist wertvoller als eine, die
    /// sie bestätigt.
    /// </summary>
    private async Task<List<Literaturstelle>> LiteraturAsync(CancellationToken ct)
    {
        string[] fragen =
        [
            "transaction costs destroy intraday trading profits",
            "day trading profitability retail traders evidence",
            "intraday return predictability short horizon"
        ];

        var liste = new List<Literaturstelle>();
        var gesehen = new HashSet<long>();

        foreach (var f in fragen)
        {
            try
            {
                foreach (var h in await knowledge.SearchAsync("knowledge", f, 3, ct))
                {
                    if (!gesehen.Add(h.ChunkId)) continue;

                    liste.Add(new Literaturstelle(
                        h.Title, h.Origin,
                        Kuerzen(h.Content, 420),
                        h.PageFrom is { } s ? $"Seite {s}" : null,
                        h.Score));
                }
            }
            catch
            {
                /* Ohne Qdrant oder ohne Einbettungsmodell fehlt der Abschnitt.
                   Das ist kein Grund, die ganze Seite scheitern zu lassen —
                   die Zahlen oben stehen unabhängig davon. */
            }
        }

        return liste.OrderByDescending(x => x.Score).Take(6).ToList();
    }

    private static string Kuerzen(string s, int max)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length <= max ? s : s[..max].TrimEnd() + " …";
    }

    private static string KlasseName(AssetClass k) => k switch
    {
        AssetClass.Stock => "Aktien",
        AssetClass.Etf => "Fonds und ETFs",
        AssetClass.Crypto => "Krypto",
        AssetClass.Index => "Indizes",
        _ => k.ToString()
    };

    // ------------------------------------------------------------ Rohzeilen -

    private sealed class BeweglichkeitZeile
    {
        public byte Klasse { get; set; }
        public int Bars { get; set; }
        public int Werte { get; set; }
        public double MittlereBewegung { get; set; }
        public double MedianBewegung { get; set; }
        public double AnteilUeberKosten { get; set; }
        public double AnteilUeberDoppelt { get; set; }
    }

    private sealed class FensterZeile
    {
        public byte Klasse { get; set; }
        public DateTime? Juengste { get; set; }
    }

    private sealed class BewegungZeile
    {
        public string Symbol { get; set; } = "";
        public string? Name { get; set; }
        public byte Klasse { get; set; }
        public decimal Kurs { get; set; }
        public DateTime TsUtc { get; set; }
        public double VeraenderungStunde { get; set; }
        public double VeraenderungSechs { get; set; }
        public double Spannweite { get; set; }
    }
}
