using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Datenbank;
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

        /*  Was der Kursspeicher gerade haelt.

            Nicht Zierde, sondern die Pruefung der Auflage: Dieser Speicher
            existiert nur, solange er weniger Arbeitsspeicher braucht als die
            Datenbank, die er entlastet. Eine Obergrenze, die niemand ablesen
            kann, ist eine Behauptung. `bytesGeschaetzt` ist bewusst als
            Schaetzung benannt -- gemessen wird der echte Verbrauch am Prozess,
            nicht hier.                                                        */
        /*  Die Trefferquote gehoert hierher, nicht nur die Fuellmenge.

            Ein Zwischenspeicher verdeckt das Problem, das ihn noetig macht.
            Wer in einem halben Jahr wissen will, ob das Backend besser
            geworden ist, braucht die Quote: Sie sagt, wie oft trotz allem
            gelesen werden musste. Ohne sie bleibt der Speicher fuer immer
            drin, weil niemand belegen kann, dass er entbehrlich ist.

            `ergaenzungen` zaehlt, wie oft ein Schreibvorgang in den Speicher
            eingearbeitet statt weggeworfen wurde -- die Zahl, an der sich
            ablesen laesst, ob das Fortschreiben greift.                       */
        app.MapGet("/api/health/kursspeicher", (Kursspeicher speicher) =>
        {
            var gesamt = speicher.Treffer + speicher.Fehlschlaege;
            return Results.Ok(new
            {
                reihen = speicher.GehalteneReihen,
                bars = speicher.GehalteneBars,
                bytesGeschaetzt = (long)speicher.GehalteneBars * 120,
                treffer = speicher.Treffer,
                fehlschlaege = speicher.Fehlschlaege,
                trefferquote = gesamt > 0 ? Math.Round((double)speicher.Treffer / gesamt, 3) : (double?)null,
                ergaenzungen = speicher.Ergaenzungen,
            });
        }).WithTags("Health");

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
                und liefert dieselbe Antwort. Zehn Minuten sind kurz genug, dass
                nach einem Lauf bald neue Zahlen erscheinen, und lang genug,
                dass mehrfaches Öffnen der Seite nichts mehr kostet.            */
            if (_bestandCache is { } c && DateTime.UtcNow - c.Stand < TimeSpan.FromMinutes(10))
                return Results.Ok(c.Wert);

            /*  Ein Budget für die ganze Seite, nicht eine Frist je Abfrage.

                Mit acht Abfragen zu je fünfzehn Sekunden Frist dauert der
                schlimmste Fall zwei Minuten — und genau der trat ein, solange
                das Backend zäh war. Eine Übersichtsseite darf aber nie länger
                dauern als die Geduld dessen, der sie öffnet. Also läuft eine
                gemeinsame Uhr: Was in zwölf Sekunden beantwortet ist, steht
                da; der Rest bleibt leer und wird beim nächsten Aufruf nach
                Ablauf des Zwischenspeichers erneut versucht.                   */
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(12));
            ct = budget.Token;

            await using var conn = await factory.OpenAsync(ct);
            var d = conn.Dialekt();

            /*  Eine Zahl, die nicht kommt, darf die Seite nicht mitreissen.

                `crossing` hat 7,8 Millionen Zeilen, `forecast` 1,26 Millionen.
                Zaehlt das Backend eine davon langsam oder gar nicht, stand
                vorher die ganze Uebersicht: Die Seite lieferte 500 nach 75
                Sekunden, obwohl die uebrigen zehn Zahlen laengst da waren.
                Jetzt fehlt im schlimmsten Fall eine Zahl -- die Oberflaeche
                zeigt dort einen Strich -- und alles andere steht.              */
            async Task<int?> ZaehleAsync(string tabelle, string? bedingung = null)
            {
                try
                {
                    var wo = bedingung is null ? "" : $" WHERE {bedingung}";
                    return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                        $"SELECT CAST(COUNT(*) AS INT) FROM {tabelle}{wo}",
                        commandTimeout: 12, cancellationToken: ct));
                }
                catch (Exception ex)
                {
                    protokoll.CreateLogger("Health")
                             .LogWarning(ex, "Zählung über {Tabelle} übersprungen", tabelle);
                    return null;
                }
            }

            /*  Wie ZaehleAsync, nur fuer alles andere: Eine Abfrage, die nicht
                zurueckkommt, liefert null statt die Seite mitzureissen.        */
            async Task<T?> VersucheAsync<T>(string was, Func<Task<T?>> abfrage)
            {
                try { return await abfrage(); }
                catch (Exception ex)
                {
                    protokoll.CreateLogger("Health").LogWarning(ex, "{Was} übersprungen", was);
                    return default;
                }
            }

            var byClass = await VersucheAsync("Werte je Klasse", async () =>
                (await conn.QueryAsync<(byte AssetClass, int Total, int Tracked)>(
                    new CommandDefinition($"""
                        SELECT asset_class,
                               CAST(COUNT(*) AS INT) AS total,
                               CAST(SUM(CASE WHEN is_tracked = {d.Wahr} THEN 1 ELSE 0 END) AS INT) AS tracked
                          FROM dbo.asset
                         GROUP BY asset_class
                        """, commandTimeout: 12, cancellationToken: ct))).ToList())
                ?? [];

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
            var barStats = new List<(string Interval, long? Bars, int Assets,
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
                /*  Erst die billige Frage, dann die teure.

                    Die Frage, auf die es bei dieser Seite ankommt, ist „wie
                    aktuell sind die Kurse" -- also MIN und MAX. Die kosten
                    gemessen rund 100 ms, weil sie an den Raendern des Index
                    stehen. `COUNT(*)` ueber dieselbe Menge kostet Sekunden und
                    auf dem DataCell-Backend Gigabyte, weil dafuer jede Zeile
                    angefasst wird. Beides in einer Anweisung zu fragen hiess,
                    die billige Antwort mit dem Preis der teuren zu bezahlen.

                    Bleibt die Zaehlung aus, steht in der Oberflaeche ein
                    Strich -- die Aktualitaet steht trotzdem da.                */
                var rand = await VersucheAsync($"Zeitraum {iv}", async () =>
                    (await conn.QueryAsync<(DateTime? Oldest, DateTime? Newest)>(
                        new CommandDefinition("""
                            SELECT MIN(ts_utc) AS oldest, MAX(ts_utc) AS newest
                              FROM dbo.price_bar
                             WHERE interval_code = @iv
                            """, new { iv }, commandTimeout: 12, cancellationToken: ct)))
                        .Cast<(DateTime? Oldest, DateTime? Newest)?>().FirstOrDefault());

                if (rand is not { Newest: not null }) continue;

                var anzahl = await ZaehleAsync("dbo.price_bar", $"interval_code = '{iv}'");

                barStats.Add((iv, anzahl, 0, rand.Value.Oldest, rand.Value.Newest));
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
