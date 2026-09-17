using System.Collections.Concurrent;
using Dapper;
using Ingest.Api.Scheduling;
using Ingest.Core.Abstractions;
using Ingest.Infrastructure.Datenbank;
using Ingest.Infrastructure.Repositories;
using Ingest.Infrastructure.Services;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Die Startseite: was heute wichtig ist, und wohin man von hier kommt.
///
/// <para><b>Warum ein Sammel-Endpunkt.</b> Die Kacheln brauchen acht
/// verschiedene Quellen. Acht Abfragen aus dem Browser hiessen acht
/// Ladeanzeigen und eine Seite, die stückweise erscheint. Hier laufen sie
/// parallel, jede mit eigener Frist und eigenem <c>try</c>: Fällt eine aus —
/// Ollama weg, Qdrant weg —, zeigt ihre Kachel das, und die übrigen stehen
/// trotzdem. Eine Startseite, die wegen einer Kachel leer bleibt, wäre die
/// schlechteste Startseite.</para>
///
/// <para><b>Fünf Minuten Zwischenspeicher.</b> Die Tagesübersicht kostet
/// gemessen 0,2 s, der Markt-Querschnitt eine Abfrage über 600 Werte, die
/// Depotbewertung eine Handvoll. Zusammen unter einer Sekunde — aber die Seite
/// ist die, die man am häufigsten öffnet, und nichts darauf ändert sich
/// minütlich. Der Kursabruf läuft stündlich.</para>
/// </summary>
public static class StartEndpoints
{
    private static readonly ConcurrentDictionary<string, (DateTime Bis, object Wert)> Cache = new();
    private static readonly TimeSpan Frist = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Haltbar = TimeSpan.FromMinutes(5);

    public static void MapStartEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/start").WithTags("Start");

        g.MapGet("/", async (
            ISqlConnectionFactory factory, IBriefingService briefing, IInvestService invest,
            INeuzugangService neuzugang, IGrundschwingungService grund, IReasoningUrteilService urteile,
            IKnowledgeService knowledge, SchedulerState scheduler, bool frisch = false,
            CancellationToken ct = default) =>
        {
            const string key = "start";
            if (!frisch && Cache.TryGetValue(key, out var c) && c.Bis > DateTime.UtcNow)
                return Results.Ok(c.Wert);

            var t = new Dictionary<string, Task<object?>>
            {
                ["lage"] = Teil(async k => await LageAsync(factory, briefing, k), ct),
                ["markt"] = Teil(async k => await MarktAsync(factory, k), ct),
                ["nachrichten"] = Teil(async k => await NachrichtenAsync(factory, k), ct),
                ["depot"] = Teil(async k => await DepotAsync(invest, k), ct),
                ["prognose"] = Teil(async k => await PrognoseAsync(factory, urteile, k), ct),
                ["neuzugaenge"] = Teil(async k => await NeuzugaengeAsync(neuzugang, k), ct),
                ["grundschwingungen"] = Teil(async k => await GrundschwingungenAsync(grund, k), ct),
                ["system"] = Teil(async k => await SystemAsync(factory, knowledge, scheduler, k), ct),
            };

            await Task.WhenAll(t.Values);

            var wert = new
            {
                standUtc = DateTime.UtcNow,
                lage = t["lage"].Result,
                markt = t["markt"].Result,
                nachrichten = t["nachrichten"].Result,
                depot = t["depot"].Result,
                prognose = t["prognose"].Result,
                neuzugaenge = t["neuzugaenge"].Result,
                grundschwingungen = t["grundschwingungen"].Result,
                system = t["system"].Result,
            };
            Cache[key] = (DateTime.UtcNow + Haltbar, wert);
            return Results.Ok(wert);
        });
    }

    /// <summary>Ein Teil mit eigener Frist; ein Fehler wird zum Ergebnis, nicht zum Abbruch.</summary>
    private static async Task<object?> Teil(Func<CancellationToken, Task<object?>> f, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Frist);
        try { return await f(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new { fehler = $"keine Antwort innerhalb von {Frist.TotalSeconds:0} s" }; }
        catch (Exception ex)
        { return new { fehler = ex.Message.Length > 160 ? ex.Message[..160] : ex.Message }; }
    }

    // ------------------------------------------------------------- Kacheln --

    private static async Task<object?> LageAsync(ISqlConnectionFactory factory, IBriefingService briefing, CancellationToken ct)
    {
        var gewichte = await PillarWeightEndpoints.LadenAsync(factory, ct);
        var b = await briefing.BuildAsync(gewichte, null, ct);
        // Eine Kachel ist kein Journal: das Detail auf eine Zeile kuerzen.
        static string Kurz(string t) => t.Length > 200 ? t[..200].TrimEnd() + " …" : t;
        return new
        {
            zusammenfassung = b.Summary,
            punkte = b.Items.OrderByDescending(i => i.Kind == "warnung" ? 1 : 0).ThenByDescending(i => i.Weight).Take(6)
                .Select(i => new { i.Kind, i.Pillar, i.Title, Detail = Kurz(i.Detail), gewicht = Math.Round(i.Weight), i.Symbol, i.Link }),
            vorbehalte = b.Caveats.Take(2),
            anzahl = b.Items.Count
        };
    }

    /// <summary>
    /// Der Markt heute: letzter gegen vorletzten Tagesschluss je verfolgtem Wert.
    /// Nur Werte, deren jüngster Schluss höchstens vier Tage alt ist — ein Wert,
    /// dessen letzte Bar von voriger Woche stammt, beschreibt nicht „heute".
    /// </summary>
    private static async Task<object?> MarktAsync(ISqlConnectionFactory factory, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var d = conn.Dialekt();
        var rows = (await conn.QueryAsync<(string Symbol, string? Name, string Klasse, decimal Heute, decimal Gestern, DateTime Ts)>(
            new CommandDefinition($"""
                WITH b AS (
                  SELECT asset_id, ts_utc, "close",
                         ROW_NUMBER() OVER (PARTITION BY asset_id ORDER BY ts_utc DESC) AS rn
                    FROM dbo.price_bar
                   WHERE interval_code = '1d' AND "close" > 0
                     AND ts_utc >= {d.PlusTage("-14", d.Jetzt)})
                SELECT a.symbol, a."name", CAST(a.asset_class AS VARCHAR(8)), b1."close", b2."close", b1.ts_utc
                  FROM b b1
                  JOIN b b2 ON b2.asset_id = b1.asset_id AND b2.rn = 2
                  JOIN dbo.asset a ON a.asset_id = b1.asset_id
                 WHERE b1.rn = 1 AND a.is_tracked = {d.Wahr}
                   AND b1.ts_utc >= {d.PlusTage("-4", d.Jetzt)}
                """, cancellationToken: ct))).ToList();

        if (rows.Count == 0) return new { werte = 0 };

        var bew = rows.Select(r => new
        {
            r.Symbol, r.Name,
            pct = Math.Round((double)((r.Heute - r.Gestern) / r.Gestern) * 100, 2),
            r.Ts
        }).ToList();

        return new
        {
            werte = bew.Count,
            mittel = Math.Round(bew.Average(x => x.pct), 2),
            anteilPlus = Math.Round(bew.Count(x => x.pct > 0) * 100.0 / bew.Count),
            standUtc = bew.Max(x => x.Ts),
            gewinner = bew.OrderByDescending(x => x.pct).Take(4).Select(x => new { x.Symbol, x.Name, x.pct }),
            verlierer = bew.OrderBy(x => x.pct).Take(4).Select(x => new { x.Symbol, x.Name, x.pct })
        };
    }

    private static async Task<object?> NachrichtenAsync(ISqlConnectionFactory factory, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var d = conn.Dialekt();
        var rows = await conn.QueryAsync<(string Title, string Origin, DateTime? Published, DateTime Added, string? Feed, string? Region)>(
            new CommandDefinition("""
                SELECT s.title, s.origin, s.published_utc, s.added_utc, f.title, s.region
                  FROM dbo.knowledge_source s
                  LEFT JOIN dbo.knowledge_source f ON f.source_id = s.parent_source_id
                 WHERE s.pillar = 'semantic' AND s.kind = 'article' AND s.indexed_utc IS NOT NULL
                 ORDER BY COALESCE(s.published_utc, s.added_utc) DESC
                OFFSET 0 ROWS FETCH NEXT (60) ROWS ONLY
                """, cancellationToken: ct));

        /*  Hoechstens zwei Meldungen je Quelle. Die juengsten zehn kamen im
            ersten Aufbau alle aus EINEM Feed (NASA-Erdbeobachtung, sieben
            Waldbraende) -- ein Feed, der im Minutentakt liefert, verdraengt
            sonst jede andere Quelle, und die Kachel zeigt den Takt eines Feeds
            statt der Lage. Rundlauf ueber die Quellen, Reihenfolge nach Zeit. */
        var jeQuelle = new Dictionary<string, int>();
        var auswahl = new List<(string Title, string Origin, DateTime? Published, DateTime Added, string? Feed, string? Region)>();
        foreach (var r in rows)
        {
            var q = r.Feed ?? Host(r.Origin);
            jeQuelle.TryGetValue(q, out var n);
            if (n >= 2) continue;
            jeQuelle[q] = n + 1;
            auswahl.Add(r);
            if (auswahl.Count >= 10) break;
        }
        rows = auswahl;
        var heute = await conn.ExecuteScalarAsync<int>(new CommandDefinition($"""
            SELECT CAST(COUNT(*) AS INT) FROM dbo.knowledge_source
             WHERE pillar = 'semantic' AND kind = 'article' AND added_utc >= {d.PlusTage("-1", d.Jetzt)}
            """, cancellationToken: ct));
        return new
        {
            neueSeitGestern = heute,
            meldungen = rows.Select(r => new
            {
                titel = r.Title, url = r.Origin, zeitUtc = r.Published ?? r.Added,
                quelle = r.Feed ?? Host(r.Origin), region = r.Region
            })
        };
    }

    private static async Task<object?> DepotAsync(IInvestService invest, CancellationToken ct)
    {
        var g = await invest.GesamtAsync("USD", ct);
        var punkte = g.Punkte;
        double? tag = null;
        if (punkte.Count >= 2)
        {
            var letzter = (double)punkte[^1].Vermoegen;
            var vortag = (double)punkte[^2].Vermoegen;
            if (vortag > 0) tag = Math.Round((letzter / vortag - 1) * 100, 2);
        }
        return new
        {
            waehrung = g.Waehrung, vermoegen = g.Vermoegen, eingezahlt = g.Eingezahlt, gewinn = g.Gewinn,
            renditePct = g.RenditePct, seitVortagPct = tag,
            depots = g.Depots.Select(x => new { x.Depot, x.Vermoegen, x.RenditePct, x.Zaehlt })
        };
    }

    private static async Task<object?> PrognoseAsync(ISqlConnectionFactory factory, IReasoningUrteilService urteile, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var d = conn.Dialekt();
        var (bar, gestellt, zeilen) = await conn.QuerySingleAsync<(DateTime?, DateTime?, int)>(new CommandDefinition($"""
            SELECT (SELECT MAX(b.ts_utc) FROM dbo.price_bar b JOIN dbo.asset a ON a.asset_id = b.asset_id WHERE a.is_tracked = {d.Wahr}),
                   (SELECT MAX(made_at_utc) FROM dbo.forecast),
                   (SELECT CAST(COUNT(*) AS INT) FROM dbo.forecast WHERE made_at_utc = (SELECT MAX(made_at_utc) FROM dbo.forecast))
            """, cancellationToken: ct));
        /*  Die Trefferquote der bewerteten Live-Prognosen der letzten 30 Tage --
            nur ens-1, nicht die Rueckrechnung (CLAUDE.md). Eine Zahl, die sagt,
            ob das Ganze gerade besser ist als ein Muenzwurf.                    */
        var (n, treffer) = await conn.QuerySingleAsync<(int, int)>(new CommandDefinition($"""
            SELECT CAST(COUNT(*) AS INT), CAST(SUM(CASE WHEN s.direction_correct = {d.Wahr} THEN 1 ELSE 0 END) AS INT)
              FROM dbo.forecast_score s JOIN dbo.forecast f ON f.forecast_id = s.forecast_id
             WHERE f.model_version = 'ens-1' AND s.scored_at_utc >= {d.PlusTage("-30", d.Jetzt)}
            """, cancellationToken: ct));
        var u = await urteile.StandAsync(5, ct);
        return new
        {
            neuesterBarUtc = bar, zuletztGestelltUtc = gestellt, zeilen,
            veraltet = bar is not null && (gestellt is null || bar > gestellt),
            bewertet30Tage = n, trefferquote30Tage = n > 0 ? Math.Round((double)treffer / n, 3) : (double?)null,
            urteile = new { u.Gesamt, u.Aktuell, u.Trefferquote, u.Verdienst,
                            letzte = u.Letzte.Take(3).Select(x => new { x.Symbol, x.Richtung, x.Zuversicht }) }
        };
    }

    private static async Task<object?> NeuzugaengeAsync(INeuzugangService svc, CancellationToken ct)
    {
        var u = await svc.UebersichtAsync(ct);
        return new
        {
            angekuendigt = u.Angekuendigt, gehandelt = u.Gehandelt, ausgefallen = u.Ausgefallen,
            naechste = u.Anstehend.OrderBy(a => a.ErwartetAm ?? DateTime.MaxValue).Take(4)
                .Select(a => new { a.Symbol, a.Name, a.Markt, a.ErwartetAm, a.Quelle })
        };
    }

    private static async Task<object?> GrundschwingungenAsync(IGrundschwingungService svc, CancellationToken ct)
    {
        var u = await svc.UebersichtAsync("1d", ct);
        if (u.Lauf is null) return new { lauf = false };
        return new
        {
            lauf = true, u.Lauf.Klassen, u.Lauf.Paare, u.Lauf.PaareBestaendig, u.Lauf.Werte, standUtc = u.Lauf.BeendetUtc,
            groesste = u.Klassen.Take(3).Select(k => new { k.Nr, k.Perioden, k.Harmonik, k.Werte })
        };
    }

    private static async Task<object?> SystemAsync(ISqlConnectionFactory factory, IKnowledgeService knowledge,
                                                   SchedulerState scheduler, CancellationToken ct)
    {
        object? gesundheit = null;
        try { gesundheit = await knowledge.HealthAsync(ct); } catch { /* Kachel zeigt „unbekannt" */ }

        /*  Der Zeitplanzustand lebt im Speicher und ist nach jedem Neustart
            leer -- die Kachel zeigte dann „–", obwohl vor einer Stunde ein Lauf
            war. Die Datenbank weiss es: ingest_run haelt jeden Lauf.          */
        DateTime? stunde = scheduler.LastHourlyUtc, tag = scheduler.LastDailyUtc;
        try
        {
            await using var conn = await factory.OpenAsync(ct);
            var (h, t) = await conn.QuerySingleAsync<(DateTime?, DateTime?)>(new CommandDefinition("""
                SELECT (SELECT MAX(finished_utc) FROM dbo.ingest_run WHERE job_name = 'update:1h'),
                       (SELECT MAX(finished_utc) FROM dbo.ingest_run WHERE job_name = 'update:1d')
                """, cancellationToken: ct));
            stunde ??= h; tag ??= t;
        }
        catch { /* dann eben nur der Speicherstand */ }

        return new
        {
            datenbank = factory.Dialekt is SqlServerDialekt ? "SQL Server" : "PostgreSQL",
            wissen = gesundheit,
            letzterStundenlaufUtc = stunde,
            letzterTageslaufUtc = tag,
            naechsterStundenlaufUtc = scheduler.NextHourlyUtc,
            letztesErgebnis = scheduler.LastResult
        };
    }

    private static string Host(string url)
    {
        try { return new Uri(url).Host.Replace("www.", ""); } catch { return url; }
    }
}
