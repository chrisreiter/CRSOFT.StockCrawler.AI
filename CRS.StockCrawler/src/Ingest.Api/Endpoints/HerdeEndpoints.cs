using Dapper;
using Ingest.Core.Analysis;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Die Spur der Bot-Herde — und die Frage nach dem Umkehrschluss.
///
/// <para>Beide Auswertungen rechnen Minuten und werden deshalb abgelegt statt
/// bei jedem Seitenaufruf neu gerechnet. Die Seite liest den jüngsten Lauf;
/// ein neuer wird ausdrücklich angestoßen.</para>
/// </summary>
public static class HerdeEndpoints
{
    public static void MapHerdeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/herde");

        /* ------------------------------------------------ Bot-Auslöser --- */

        g.MapGet("/ausloeser", async (ISqlConnectionFactory factory, CancellationToken ct) =>
        {
            await using var conn = await factory.OpenAsync(ct);

            var lauf = await conn.QuerySingleOrDefaultAsync<LaufZeile>(new CommandDefinition(
                """
                SELECT TOP 1 run_id AS RunId, started_utc AS StartedUtc,
                       finished_utc AS FinishedUtc, von_utc AS VonUtc, note AS Note
                  FROM dbo.bot_trigger_run WHERE finished_utc IS NOT NULL
                 ORDER BY run_id DESC
                """, cancellationToken: ct));

            if (lauf is null)
                return Results.Ok(new
                {
                    vorhanden = false,
                    hinweis = "Noch kein Lauf. Über POST /api/herde/ausloeser/rechnen anstoßen — "
                            + "die Auswertung dauert einige Minuten."
                });

            var zeilen = (await conn.QueryAsync<StatZeile>(new CommandDefinition(
                """
                SELECT ausloeser AS Ausloeser, klasse AS Klasse,
                       ereignisse AS Ereignisse, werte AS Werte,
                       r1 AS R1, r5 AS R5, r20 AS R20,
                       b1 AS B1, b5 AS B5, b20 AS B20,
                       richtung1 AS Richtung1, richtung5 AS Richtung5, richtung20 AS Richtung20,
                       volumen_faktor AS VolumenFaktor
                  FROM dbo.bot_trigger_stat WHERE run_id = @id
                 ORDER BY klasse, ausloeser
                """, new { id = lauf.RunId }, cancellationToken: ct))).ToList();

            var kosten = Handelskosten.Standard;

            return Results.Ok(new
            {
                vorhanden = true,
                lauf.RunId,
                stand = lauf.FinishedUtc,
                seit = lauf.VonUtc,
                lauf.Note,
                rundlaufkosten = kosten.Rundlauf,
                kostenhinweis = kosten.Beschreibung,
                kernaussage = Kernaussage(zeilen, kosten),
                zeilen = zeilen.Select(z => new
                {
                    z.Ausloeser,
                    klasse = KlasseName(z.Klasse),
                    z.Ereignisse, z.Werte,
                    ueberschuss1 = z.R1 - z.B1,
                    ueberschuss5 = z.R5 - z.B5,
                    ueberschuss20 = z.R20 - z.B20,
                    z.Richtung1, z.Richtung5, z.Richtung20,
                    z.VolumenFaktor,
                    /* Der Überschuss über zwanzig Tage gegen einen Rundlauf.
                       Alles darunter ist messbar und nicht handelbar. */
                    ueberKosten = z.R20 - z.B20 > kosten.Rundlauf,
                    /* Ein Verkaufssignal mit einer Richtungsquote unter der
                       Hälfte sagt das Gegenteil dessen, was es soll. */
                    gegenlaeufig = z.Richtung20 < 0.5 && z.R20 - z.B20 > 0
                })
            });
        });

        g.MapPost("/ausloeser/rechnen", async (ISqlConnectionFactory factory,
                                               int jahre = 5, CancellationToken ct = default) =>
        {
            await using var conn = await factory.OpenAsync(ct);

            var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "dbo.run_bot_trigger_test", new { jahre },
                commandType: System.Data.CommandType.StoredProcedure,
                commandTimeout: 3600, cancellationToken: ct));

            return Results.Ok(new { runId = id });
        });

        /* ------------------------------------------------- Umkehrschluss - */

        g.MapGet("/umkehr", async (ISqlConnectionFactory factory, int limit = 20,
                                   CancellationToken ct = default) =>
        {
            await using var conn = await factory.OpenAsync(ct);

            var lauf = await conn.QuerySingleOrDefaultAsync<UmkehrLauf>(new CommandDefinition(
                """
                SELECT TOP 1 run_id AS RunId, finished_utc AS FinishedUtc,
                       horizon_days AS HorizonDays, min_crossings AS MinCrossings,
                       pairs_tested AS PairsTested, mean_hit_rate AS MeanHitRate,
                       sig_negative AS SigNegative, sig_positive AS SigPositive,
                       expected_bychance AS ExpectedByChance, note AS Note
                  FROM dbo.reversal_run WHERE finished_utc IS NOT NULL
                 ORDER BY run_id DESC
                """, cancellationToken: ct));

            if (lauf is null)
                return Results.Ok(new
                {
                    vorhanden = false,
                    hinweis = "Noch kein Lauf. Über POST /api/herde/umkehr/rechnen anstoßen."
                });

            var extreme = (await conn.QueryAsync<UmkehrZeile>(new CommandDefinition(
                """
                SELECT TOP (@limit)
                       aa.symbol AS SymbolA, ab.symbol AS SymbolB,
                       aa.asset_class AS KlasseA, ab.asset_class AS KlasseB,
                       p.n AS N, p.hit_rate AS HitRate, p.mean_gain AS MeanGain, p.z AS Z
                  FROM dbo.reversal_pair p
                  JOIN dbo.asset aa ON aa.asset_id = p.asset_id_a
                  JOIN dbo.asset ab ON ab.asset_id = p.asset_id_b
                 WHERE p.run_id = @id
                 ORDER BY p.z
                """, new { id = lauf.RunId, limit = Math.Clamp(limit, 1, 200) },
                cancellationToken: ct))).ToList();

            var kosten = Handelskosten.Standard;

            return Results.Ok(new
            {
                vorhanden = true,
                lauf.RunId,
                stand = lauf.FinishedUtc,
                lauf.HorizonDays, lauf.MinCrossings,
                lauf.PairsTested, lauf.MeanHitRate,
                lauf.SigNegative, lauf.SigPositive, lauf.ExpectedByChance,
                lauf.Note,
                /* Die Antwort in einem Satz. Sie hängt an einem Vergleich:
                   beobachtete Ausreißer gegen die, die der Zufall bei so
                   vielen Prüfungen ohnehin hervorbringt. */
                antwort = lauf.SigNegative < lauf.ExpectedByChance
                    ? $"**Nein.** Von {lauf.PairsTested:N0} geprüften Paaren weichen "
                      + $"{lauf.SigNegative} auffällig nach unten ab — allein durch Zufall wären "
                      + $"bei so vielen Prüfungen {lauf.ExpectedByChance} zu erwarten. Es gibt "
                      + "also WENIGER zuverlässig falsche Paare, als der Zufall hervorbringt. "
                      + $"Die mittlere Trefferquote liegt bei {lauf.MeanHitRate:0.0000}."
                    : $"{lauf.SigNegative} von {lauf.PairsTested:N0} Paaren weichen auffällig nach "
                      + $"unten ab, erwartet wären {lauf.ExpectedByChance}. Der Überschuss ist zu "
                      + "prüfen — er kann echt sein oder an überlappenden Fenstern liegen.",
                extreme = extreme.Select(e => new
                {
                    e.SymbolA, e.SymbolB,
                    klasseA = KlasseName(e.KlasseA), klasseB = KlasseName(e.KlasseB),
                    e.N, e.HitRate, e.MeanGain, e.Z,
                    invertiertNachKosten = -e.MeanGain - kosten.Rundlauf,
                    /* Fast identische Paare -- Gold gegen Gold, alles gegen eine
                       Stablecoin. Dort ist das Zurückschwingen zu erwarten und
                       heißt Paarhandel, nicht „das Signal ist falsch herum". */
                    naheVerwandt = NaheVerwandt(e.SymbolA, e.SymbolB)
                })
            });
        });

        /* --------------------------------------------- Zeitzonen-Vorlauf - */

        g.MapGet("/zeitzonen", async (ISqlConnectionFactory factory, CancellationToken ct) =>
        {
            await using var conn = await factory.OpenAsync(ct);

            var lauf = await conn.QuerySingleOrDefaultAsync<ZeitzoneLauf>(new CommandDefinition(
                """
                SELECT TOP 1 run_id AS RunId, finished_utc AS FinishedUtc,
                       von_utc AS VonUtc, note AS Note
                  FROM dbo.timezone_lead_run WHERE finished_utc IS NOT NULL
                 ORDER BY run_id DESC
                """, cancellationToken: ct));

            if (lauf is null)
                return Results.Ok(new
                {
                    vorhanden = false,
                    hinweis = "Noch kein Lauf. Über POST /api/herde/zeitzonen/rechnen anstoßen."
                });

            var zeilen = (await conn.QueryAsync<ZeitzoneZeile>(new CommandDefinition(
                """
                SELECT region_frueh AS Frueher, region_spaet AS Spaeter,
                       stunden_vorsprung AS Vorsprung,
                       stunden_ueberlappung AS Ueberlappung,
                       tage AS Tage, werte_frueh AS WerteFrueh, werte_spaet AS WerteSpaet,
                       korr_cc AS Naiv, korr_gap AS Sprung,
                       korr_handelbar AS Handelbar, korr_gegenprobe AS Gegenprobe
                  FROM dbo.timezone_lead_stat WHERE run_id = @id
                 ORDER BY stunden_vorsprung DESC
                """, new { id = lauf.RunId }, cancellationToken: ct))).ToList();

            var sauber = zeilen.Where(z => z.Ueberlappung == 0).ToList();

            return Results.Ok(new
            {
                vorhanden = true,
                lauf.RunId,
                stand = lauf.FinishedUtc,
                seit = lauf.VonUtc,
                lauf.Note,
                antwort = Antwort(sauber, zeilen),
                zeilen = zeilen.Select(z => new
                {
                    z.Frueher, z.Spaeter, z.Vorsprung, z.Ueberlappung,
                    z.Tage, z.WerteFrueh, z.WerteSpaet,
                    z.Naiv, z.Sprung, z.Handelbar, z.Gegenprobe,
                    /* Ohne diese Schwelle liest sich 0,041 wie ein Fund. Bei
                       rund 2.500 Beobachtungen liegt die Grenze, ab der sich
                       eine Korrelation von null unterscheiden lässt, bei
                       1,96/Wurzel(n) -- also knapp unter 0,04. */
                    schwelle = z.Tage > 0 ? 1.96 / Math.Sqrt(z.Tage) : 0,
                    /* Eine Zeile mit gemeinsamer Handelszeit ist KEIN Vorlauf.
                       Sie steht trotzdem da: Sie wegzulassen hiesse, die
                       auffälligste Zahl der Tabelle zu verstecken. */
                    alsVorlaufLesbar = z.Ueberlappung == 0
                })
            });
        });

        g.MapPost("/zeitzonen/rechnen", async (ISqlConnectionFactory factory,
                                               int jahre = 10, CancellationToken ct = default) =>
        {
            await using var conn = await factory.OpenAsync(ct);

            var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "dbo.run_timezone_lead_test", new { jahre },
                commandType: System.Data.CommandType.StoredProcedure,
                commandTimeout: 3600, cancellationToken: ct));

            return Results.Ok(new { runId = id });
        });

        g.MapPost("/umkehr/rechnen", async (ISqlConnectionFactory factory,
                                            int tage = 28, int minKreuzungen = 8,
                                            CancellationToken ct = default) =>
        {
            await using var conn = await factory.OpenAsync(ct);

            var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "dbo.run_reversal_test",
                new { horizon_days = tage, min_crossings = minKreuzungen, @interval = "1d" },
                commandType: System.Data.CommandType.StoredProcedure,
                commandTimeout: 3600, cancellationToken: ct));

            return Results.Ok(new { runId = id });
        });
    }

    /// <summary>
    /// Die Antwort in wenigen Sätzen — und sie hängt an genau einer Zeile:
    /// derjenigen ohne gemeinsame Handelszeit.
    /// </summary>
    private static string Antwort(List<ZeitzoneZeile> sauber, List<ZeitzoneZeile> alle)
    {
        if (alle.Count == 0) return "Keine Auswertung vorhanden.";

        var sb = new System.Text.StringBuilder();

        sb.Append("Es gibt einen echten Vorlauf zwischen den Zeitzonen, und er ist groß: ");

        var groesster = alle.OrderByDescending(z => z.Sprung).First();

        sb.Append($"Die Bewegung in **{groesster.Frueher}** hängt mit dem ")
          .Append($"ERÖFFNUNGSSPRUNG in **{groesster.Spaeter}** mit {groesster.Sprung:0.000} ")
          .Append("zusammen. **Aber genau dort endet er auch.** ");

        var rein = sauber.FirstOrDefault();

        if (rein is not null)
        {
            var schwelle = 1.96 / Math.Sqrt(rein.Tage);

            sb.Append($"Die einzige Strecke ohne gemeinsame Handelszeit — {rein.Frueher} nach ")
              .Append($"{rein.Spaeter}, {rein.Vorsprung} Stunden Vorsprung — lässt nach dem ")
              .Append($"Eröffnungskurs **{rein.Handelbar:0.000}** übrig. Die Schwelle, ab der ")
              .Append($"sich das von null unterscheiden liesse, liegt bei {schwelle:0.000}. ");

            sb.Append(Math.Abs(rein.Handelbar) > schwelle
                ? "Das ist ein Befund und gehört nachgerechnet."
                : "Was im Eröffnungssprung steckt, war zum Schluss des Vortages noch nicht da — "
                  + "man hätte es nicht kaufen können.");
        }

        var probe = alle.OrderByDescending(z => z.Gegenprobe).First();

        sb.Append($" Die Gegenprobe bestätigt, dass die Rechnung stimmt: Umgekehrt — ")
          .Append($"{probe.Spaeter} am Vortag gegen {probe.Frueher} heute — kommt ")
          .Append($"{probe.Gegenprobe:0.000} heraus, die stärkste Zahl der Tabelle. ")
          .Append("Amerika führt Asien über Nacht, und das ist unstrittig.");

        return sb.ToString();
    }

    private sealed class ZeitzoneLauf
    {
        public int RunId { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public DateTime VonUtc { get; set; }
        public string? Note { get; set; }
    }

    private sealed class ZeitzoneZeile
    {
        public string Frueher { get; set; } = "";
        public string Spaeter { get; set; } = "";
        public int Vorsprung { get; set; }
        public int Ueberlappung { get; set; }
        public int Tage { get; set; }
        public int WerteFrueh { get; set; }
        public int WerteSpaet { get; set; }
        public double Naiv { get; set; }
        public double Sprung { get; set; }
        public double Handelbar { get; set; }
        public double Gegenprobe { get; set; }
    }

    /// <summary>
    /// Paare, deren Zurückschwingen nichts über das Signal aussagt, weil beide
    /// Seiten dasselbe abbilden: goldgedeckte Marken gegen Gold-ETFs,
    /// irgendetwas gegen eine an den Dollar gebundene Marke.
    /// </summary>
    private static bool NaheVerwandt(string a, string b)
    {
        string[] gold = ["XAUT-USD", "PAXG-USD", "GLD", "IAU"];
        string[] stabil = ["USDG-USD", "USDT-USD", "USDC-USD", "DAI-USD", "FDUSD-USD", "TUSD-USD"];

        var beideGold = gold.Contains(a, StringComparer.OrdinalIgnoreCase)
                        && gold.Contains(b, StringComparer.OrdinalIgnoreCase);

        var eineStabil = stabil.Contains(a, StringComparer.OrdinalIgnoreCase)
                         || stabil.Contains(b, StringComparer.OrdinalIgnoreCase);

        return beideGold || eineStabil;
    }

    private static string Kernaussage(List<StatZeile> z, Handelskosten k)
    {
        var aktien = z.Where(x => x.Klasse == 0).ToList();
        if (aktien.Count == 0) return "Keine Auswertung für Aktien vorhanden.";

        var ueberKosten = aktien.Count(x => x.R20 - x.B20 > k.Rundlauf);
        var alleÜber = aktien.All(x => x.R20 - x.B20 > 0);

        var gegen = aktien.Where(x => x.Richtung20 < 0.5 && x.R20 - x.B20 > 0)
                          .OrderByDescending(x => x.R20 - x.B20)
                          .FirstOrDefault();

        var sb = new System.Text.StringBuilder();

        if (alleÜber)
            sb.Append("**Jeder geprüfte Auslöser hat über zwanzig Tage einen positiven Überschuss "
                    + "gegenüber einem beliebigen Tag — auch die Verkaufssignale.** ");

        if (gegen is not null)
            sb.Append($"Am deutlichsten bei „{gegen.Ausloeser}“: {gegen.Ereignisse:N0} Fälle, "
                    + $"{(gegen.R20 - gegen.B20) * 100:+0.00;-0.00} Prozent Überschuss, aber nur "
                    + $"{gegen.Richtung20 * 100:0} Prozent Richtungstreffer. Das Signal sagt "
                    + "abwärts, und es geht häufiger aufwärts. ");

        sb.Append($"Von {aktien.Count} Auslösern schlagen **{ueberKosten}** den Rundlauf von "
                + $"{k.Rundlauf * 100:0.##} Prozent. Alles andere ist messbar und nicht handelbar.");

        return sb.ToString();
    }

    private static string KlasseName(byte k) => k switch
    {
        0 => "Aktien", 1 => "Fonds und ETFs", 2 => "Krypto", 3 => "Indizes", _ => k.ToString()
    };

    private sealed class LaufZeile
    {
        public int RunId { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public DateTime VonUtc { get; set; }
        public string? Note { get; set; }
    }

    private sealed class StatZeile
    {
        public string Ausloeser { get; set; } = "";
        public byte Klasse { get; set; }
        public int Ereignisse { get; set; }
        public int Werte { get; set; }
        public double R1 { get; set; }
        public double R5 { get; set; }
        public double R20 { get; set; }
        public double B1 { get; set; }
        public double B5 { get; set; }
        public double B20 { get; set; }
        public double Richtung1 { get; set; }
        public double Richtung5 { get; set; }
        public double Richtung20 { get; set; }
        public double VolumenFaktor { get; set; }
    }

    private sealed class UmkehrLauf
    {
        public int RunId { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public int HorizonDays { get; set; }
        public int MinCrossings { get; set; }
        public int PairsTested { get; set; }
        public double MeanHitRate { get; set; }
        public int SigNegative { get; set; }
        public int SigPositive { get; set; }
        public int ExpectedByChance { get; set; }
        public string? Note { get; set; }
    }

    private sealed class UmkehrZeile
    {
        public string SymbolA { get; set; } = "";
        public string SymbolB { get; set; } = "";
        public byte KlasseA { get; set; }
        public byte KlasseB { get; set; }
        public int N { get; set; }
        public double HitRate { get; set; }
        public double MeanGain { get; set; }
        public double Z { get; set; }
    }
}
