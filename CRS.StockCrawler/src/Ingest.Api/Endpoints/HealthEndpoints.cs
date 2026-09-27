using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Options;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Options;
using Dapper;
using Ingest.Infrastructure.Datenbank;

namespace Ingest.Api.Endpoints;

public static class HealthEndpoints
{
    /// <summary>
    /// Der zuletzt errechnete Bestandsüberblick, für eine Minute gemerkt.
    /// Siehe die Begründung am Endpunkt <c>/api/health/stats</c>.
    /// </summary>
    private static (DateTime Stand, object Wert)? _bestandCache;

    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }))
           .WithTags("Health");

        app.MapGet("/api/health/runs", async (IIngestRunRepository repo, int last = 50,
                                              CancellationToken ct = default) =>
            Results.Ok(await repo.GetRecentAsync(Math.Clamp(last, 1, 500), ct)))
           .WithTags("Health");

        // Welche Provider sind einsatzbereit? Beantwortet die Frage
        // "warum kommen keine Daten" ohne Log-Wühlen.
        app.MapGet("/api/health/providers", (IEnumerable<IMarketDataProvider> providers,
                                             IEnumerable<IUniverseProvider> universe) =>
            Results.Ok(new
            {
                marketData = providers.Select(p => new
                {
                    provider = p.Id.ToString(),
                    configured = p.IsConfigured,
                    classes = p.SupportedClasses.Select(c => c.ToString()),
                    intervals = BarInterval.All.Where(p.SupportsInterval)
                }),
                universe = universe.Select(p => new
                {
                    provider = p.Id.ToString(),
                    configured = p.IsConfigured
                })
            }))
           .WithTags("Health");

        /* Überblick über den Datenbestand: wie viele Assets, wie viele Bars,
           wie aktuell. Das ist die Seite, die man morgens zuerst aufmacht. */
        app.MapGet("/api/health/stats", async (ISqlConnectionFactory factory,
                                               IOptions<IngestOptions> opt,
                                               ILoggerFactory protokoll,
                                               CancellationToken ct) =>
        {
            /*  Gemerkt für eine Minute.

                Diese Seite zählt sechs Tabellen mit zusammen über fünfzehn
                Millionen Zeilen. Solche Zahlen ändern sich im Minutentakt um
                Promille — sie bei jedem Aufruf neu zu zählen, kostet Sekunden
                und liefert dieselbe Antwort. Eine Minute ist kurz genug, dass
                nach einem Lauf sofort neue Zahlen erscheinen, und lang genug,
                dass mehrfaches Öffnen der Seite nichts mehr kostet.            */
            if (_bestandCache is { } c && DateTime.UtcNow - c.Stand < TimeSpan.FromMinutes(1))
                return Results.Ok(c.Wert);

            await using var conn = await factory.OpenAsync(ct);
            var d = conn.Dialekt();

            /*  Eine Zahl, die nicht kommt, darf die Seite nicht mitreissen.

                `crossing` hat 7,8 Millionen Zeilen, `forecast` 1,26 Millionen.
                Zaehlt das Backend eine davon langsam oder gar nicht, stand
                vorher die ganze Uebersicht: Die Seite lieferte 500 nach 75
                Sekunden, obwohl die uebrigen zehn Zahlen laengst da waren.
                Jetzt fehlt im schlimmsten Fall eine Zahl -- die Oberflaeche
                zeigt dort einen Strich -- und alles andere steht.              */
            async Task<int?> ZaehleAsync(string tabelle)
            {
                try
                {
                    return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                        $"SELECT CAST(COUNT(*) AS INT) FROM {tabelle}",
                        commandTimeout: 15, cancellationToken: ct));
                }
                catch (Exception ex)
                {
                    protokoll.CreateLogger("Health")
                             .LogWarning(ex, "Zählung über {Tabelle} übersprungen", tabelle);
                    return null;
                }
            }

            var byClass = await conn.QueryAsync<(byte AssetClass, int Total, int Tracked)>(
                new CommandDefinition($"""
                    SELECT asset_class,
                           CAST(COUNT(*) AS INT) AS total,
                           CAST(SUM(CASE WHEN is_tracked = {d.Wahr} THEN 1 ELSE 0 END) AS INT) AS tracked
                      FROM dbo.asset
                     GROUP BY asset_class
                    """, cancellationToken: ct));

            /*  Je Intervall eine eigene Abfrage statt eines GROUP BY ueber die
                ganze Tabelle.

                `GROUP BY interval_code` ohne WHERE laeuft ueber alle vier
                Millionen Zeilen. Das EventMesh-DataCell-Backend weist so etwas
                seit dem 27.09.2026 ausdruecklich ab ("Komplexe Query ueber die
                grosse Tabelle 'price_bar' ... ohne einschraenkendes WHERE"),
                und diese Seite lieferte deshalb 500 -- sichtbar als roter
                Hinweis im Kopf der Anwendung.

                Es gibt genau zwei Intervalle, also sind es zwei Abfragen mit
                Gleichheitsbedingung. Das ist auch auf SQL Server nicht
                langsamer: Der Index liegt ohnehin auf (interval_code, ...),
                und zwei Bereichssuchen kosten weniger als ein voller
                Durchlauf.                                                      */
            var barStats = new List<(string Interval, long Bars, int Assets,
                                     DateTime? Oldest, DateTime? Newest)>();

            foreach (var iv in BarInterval.All)
            {
                /*  Ohne COUNT(DISTINCT asset_id).

                    Das EventMesh-DataCell-Backend ignoriert DISTINCT in der
                    Zaehlung: Es gab am 27.09.2026 fuer `1d` 2.915.379 zurueck
                    statt rund 600 -- also die Zeilenzahl. Eine falsche Zahl in
                    der Oberflaeche ist schlimmer als keine, denn sie sieht aus
                    wie eine Aussage. Die Zahl der verfolgten Werte steht
                    ohnehin schon weiter oben in `assets`.                      */
                var r = await conn.QuerySingleOrDefaultAsync<(long Bars,
                                                              DateTime? Oldest, DateTime? Newest)>(
                    new CommandDefinition("""
                        SELECT CAST(COUNT(*) AS BIGINT) AS bars,
                               MIN(ts_utc)              AS oldest,
                               MAX(ts_utc)              AS newest
                          FROM dbo.price_bar
                         WHERE interval_code = @iv
                        """, new { iv }, commandTimeout: 60, cancellationToken: ct));

                if (r.Bars > 0)
                    barStats.Add((iv, r.Bars, 0, r.Oldest, r.Newest));
            }

            /*  Offene Prognosen werden gerechnet, nicht verbunden.

                Vorher stand hier ein Anti-Join: `forecast LEFT JOIN
                forecast_score ... WHERE s.forecast_id IS NULL`. Ueber 1,26
                Millionen Prognosen gegen 484.000 Bewertungen lief der auf dem
                EventMesh-DataCell-Backend am 27.09.2026 nicht nur in den
                Timeout -- er hat den Datenbankprozess mitgenommen. Danach war
                der Node weg und JEDER Endpunkt lieferte 500.

                Die Subtraktion ist hier keine Naeherung, sondern exakt:
                `forecast_score` hat `PRIMARY KEY (forecast_id)`, also genau
                eine Bewertung je Prognose. Zwei Zaehlungen mit je rund einer
                Sekunde ersetzen einen Verbund, der nie haette sein muessen --
                auch auf SQL Server war er die teuerste Abfrage dieser Seite.  */
            var total = await ZaehleAsync("dbo.forecast");
            var scored = await ZaehleAsync("dbo.forecast_score");

            var forecasts = (Total: total, Scored: scored,
                             Pending: total is { } t && scored is { } s ? t - s : (int?)null);

            var pairs = await ZaehleAsync("dbo.pair_stat");
            var crossings = await ZaehleAsync("dbo.crossing");

            var antwort = new
            {
                assets = byClass.Select(r => new
                {
                    assetClass = ((AssetClass)r.AssetClass).ToString(),
                    r.Total,
                    r.Tracked
                }),
                bars = barStats.Select(r => new { r.Interval, r.Bars, r.Assets, r.Oldest, r.Newest }),
                forecasts = new { forecasts.Total, forecasts.Scored, forecasts.Pending },
                analysis = new { pairs, crossings },
                config = new
                {
                    Horizons = opt.Value.EffectiveHorizons,
                    opt.Value.HistoryMonths,
                    opt.Value.HourlyHistoryMonths,
                    opt.Value.HourlyCronUtc,
                    opt.Value.DailyCronUtc
                }
            };

            _bestandCache = (DateTime.UtcNow, antwort);
            return Results.Ok(antwort);
        }).WithTags("Health");
    }
}
