using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Api.Endpoints;

public static class AnalysisEndpoints
{
    public static void MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/analysis").WithTags("Analyse");

        g.MapPost("/recompute", async (IAnalysisService svc, string interval = BarInterval.Daily,
                                       int windowBars = 365, CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            return Results.Ok(await svc.RecomputeAsync(interval, Math.Clamp(windowBars, 60, 5000), ct));
        });

        /* Korrelationen eines Assets zu allen anderen. Positiver Lag bedeutet:
           das andere Papier bewegt sich zuerst, dieses zieht später nach. */
        g.MapGet("/correlations/{assetId:int}", async (IPairStatRepository pairs, IAssetRepository assets,
                                                       int assetId, string interval = BarInterval.Daily,
                                                       int limit = 50, CancellationToken ct = default) =>
        {
            var stats = await pairs.GetForAsync(assetId, interval, ct);
            if (stats.Count == 0) return Results.Ok(Array.Empty<object>());

            var otherIds = stats
                .Select(s => s.AssetIdA == assetId ? s.AssetIdB : s.AssetIdA)
                .Distinct()
                .ToArray();

            var names = await LoadNamesAsync(assets, otherIds, ct);

            var result = stats.Take(Math.Clamp(limit, 1, 500)).Select(s =>
            {
                var isA = s.AssetIdA == assetId;
                var otherId = isA ? s.AssetIdB : s.AssetIdA;

                // pair_stat speichert immer (A,B) mit A < B. Fragt jemand nach B,
                // dreht sich die Vorlauf-Richtung um.
                var lag = isA ? s.BestLagBars : -s.BestLagBars;

                return new
                {
                    assetId = otherId,
                    symbol = names.GetValueOrDefault(otherId)?.Symbol,
                    name = names.GetValueOrDefault(otherId)?.Name,
                    corr = s.Corr0,
                    bestLagBars = lag,
                    bestLagCorr = s.BestLagCorr,
                    leads = lag < 0,          // das andere Papier läuft voraus
                    followsBars = Math.Abs(lag),
                    nObs = s.NObs
                };
            });

            return Results.Ok(result);
        });

        // Die stärksten Frühindikatoren für ein Asset.
        g.MapGet("/leaders/{assetId:int}", async (IPairStatRepository pairs, IAssetRepository assets,
                                                  int assetId, string interval = BarInterval.Daily,
                                                  int limit = 10, CancellationToken ct = default) =>
        {
            var top = await pairs.GetTopLeadersAsync(assetId, interval, Math.Clamp(limit, 1, 50), ct);
            var names = await LoadNamesAsync(assets, top.Select(t => t.AssetIdA).ToArray(), ct);

            return Results.Ok(top.Select(t => new
            {
                leaderAssetId = t.AssetIdA,
                symbol = names.GetValueOrDefault(t.AssetIdA)?.Symbol,
                name = names.GetValueOrDefault(t.AssetIdA)?.Name,
                leadBars = t.BestLagBars,
                corr = t.BestLagCorr,
                simultaneousCorr = t.Corr0,
                nObs = t.NObs
            }));
        });

        /* Korrelationsmatrix über eine Auswahl von Werten — die Grundlage der
           Heatmap.

           Bewusst LIVE gerechnet statt aus pair_stat gelesen. Die gespeicherten
           Paarwerte stammen aus einem festen Analysefenster; würde die Heatmap
           daraus bedient, zeigte sie bei gewähltem Datumsbereich stillschweigend
           einen anderen Zeitraum als die Charts daneben. Bei höchstens 40 Werten
           sind das keine 800 Paare — der Aufwand ist vernachlässigbar. */
        g.MapGet("/matrix", async (IAssetRepository assets, IPriceBarRepository bars,
                                   string ids, string interval = BarInterval.Daily,
                                   int months = 12,
                                   DateTime? from = null, DateTime? to = null,
                                   CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var assetIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .Select(s => int.TryParse(s, out var v) ? v : -1)
                              .Where(v => v > 0)
                              .Distinct()
                              .Take(40)
                              .ToArray();

            if (assetIds.Length < 2)
                return Results.BadRequest(new { error = "Mindestens zwei gültige assetIds nötig" });

            var (fromUtc, toUtc) = TimeRange.Resolve(from, to, months);

            var meta = await LoadNamesAsync(assets, assetIds, ct);
            var order = assetIds.Where(meta.ContainsKey).ToArray();
            if (order.Length < 2)
                return Results.BadRequest(new { error = "Zu wenige bekannte Werte" });

            var series = await bars.GetManyAsync(order, interval, fromUtc, toUtc, ct);
            var usable = series.Where(kv => kv.Value.Count >= 10)
                               .ToDictionary(kv => kv.Key, kv => kv.Value);

            var (grid, dense) = PairAnalyzer.Densify(usable);

            /* Weniger streng als beim großen Analyselauf: hier hat der Nutzer
               den Zeitraum bewusst gewählt und erwartet auch für ein kurzes
               Fenster eine Antwort. */
            var opt = new PairAnalyzerOptions
            {
                MinObservations = 10,
                DetectCrossings = false,
                LagSearchThreshold = 0.1
            };

            var result = grid.Length == 0
                ? new PairAnalysisResult([], [])
                : PairAnalyzer.Analyze(order.Where(dense.ContainsKey).ToArray(), dense, grid,
                                       0, grid.Length - 1, interval, grid.Length, opt);

            var lookup = result.Stats.ToDictionary(
                st => (Math.Min(st.AssetIdA, st.AssetIdB), Math.Max(st.AssetIdA, st.AssetIdB)),
                st => (st.Corr0, st.BestLagBars, st.NObs));

            var n = order.Length;
            var corr = new double?[n][];
            var lags = new int?[n][];

            for (var i = 0; i < n; i++)
            {
                corr[i] = new double?[n];
                lags[i] = new int?[n];

                for (var j = 0; j < n; j++)
                {
                    if (i == j) { corr[i][j] = 1.0; lags[i][j] = 0; continue; }

                    var key = (Math.Min(order[i], order[j]), Math.Max(order[i], order[j]));
                    if (!lookup.TryGetValue(key, out var v)) continue;

                    corr[i][j] = Math.Round(v.Corr0, 4);

                    // PairAnalyzer legt (A,B) mit A < B ab; aus Sicht des
                    // größeren Werts dreht sich der Vorlauf um.
                    lags[i][j] = order[i] < order[j] ? v.BestLagBars : -v.BestLagBars;
                }
            }

            return Results.Ok(new
            {
                interval,
                fromUtc,
                toUtc,
                gridPoints = grid.Length,
                assets = order.Select(id => new
                {
                    assetId = id,
                    symbol = meta[id].Symbol,
                    name = meta[id].Name,
                    assetClass = meta[id].AssetClass.ToString()
                }),
                corr,
                lags
            });
        });

        /* Auto-Suche: findet die auffälligsten Paare über den GESAMTEN Bestand,
           unabhängig davon, was gerade ausgewählt ist. Beantwortet die Frage
           „wo im Markt passiert überhaupt etwas Interessantes?“, statt nur zu
           zeigen, was man ohnehin schon im Blick hatte. */
        g.MapGet("/extremes", async (IPairStatRepository pairs, IAssetRepository assets,
                                     ISqlConnectionFactory factory,
                                     string interval = BarInterval.Daily,
                                     string mode = "negative", int limit = 12,
                                     int minObs = 100, CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            limit = Math.Clamp(limit, 2, 40);
            await using var conn = await factory.OpenAsync(ct);

            // Reihenfolge und Filter hängen davon ab, wonach gesucht wird.
            var (order, extra) = mode.ToLowerInvariant() switch
            {
                "positive" => ("p.corr0 DESC", "AND p.corr0 > 0"),
                "lead" => ("ABS(p.best_lag_corr) DESC", "AND p.best_lag_bars <> 0"),
                "crossings" => ("cx.n DESC", ""),
                _ => ("p.corr0 ASC", "AND p.corr0 < 0")   // gegenläufig
            };

            var sql = $"""
                SELECT TOP (@limit)
                       p.asset_id_a AS AssetIdA, p.asset_id_b AS AssetIdB,
                       p.corr0 AS Corr0, p.best_lag_bars AS BestLagBars,
                       p.best_lag_corr AS BestLagCorr, p.n_obs AS NObs,
                       ISNULL(cx.n, 0) AS Crossings
                  FROM dbo.pair_stat p
                  JOIN dbo.asset a ON a.asset_id = p.asset_id_a AND a.is_tracked = 1
                  JOIN dbo.asset b ON b.asset_id = p.asset_id_b AND b.is_tracked = 1
                  LEFT JOIN (
                        SELECT asset_id_a, asset_id_b, COUNT(*) AS n
                          FROM dbo.crossing
                         WHERE interval_code = @interval
                         GROUP BY asset_id_a, asset_id_b
                  ) cx ON cx.asset_id_a = p.asset_id_a AND cx.asset_id_b = p.asset_id_b
                 WHERE p.interval_code = @interval AND p.n_obs >= @minObs {extra}
                 ORDER BY {order}
                """;

            var rows = (await conn.QueryAsync<ExtremePair>(new CommandDefinition(
                sql, new { interval, limit, minObs }, commandTimeout: 180, cancellationToken: ct))).ToList();

            var ids = rows.SelectMany(r => new[] { r.AssetIdA, r.AssetIdB }).Distinct().ToArray();
            var meta = await LoadNamesAsync(assets, ids, ct);

            string Sym(int id) => meta.TryGetValue(id, out var a) ? a.Symbol : id.ToString();

            return Results.Ok(new
            {
                interval,
                mode,
                pairs = rows.Select(r => new
                {
                    a = new { id = r.AssetIdA, symbol = Sym(r.AssetIdA), name = meta.GetValueOrDefault(r.AssetIdA)?.Name },
                    b = new { id = r.AssetIdB, symbol = Sym(r.AssetIdB), name = meta.GetValueOrDefault(r.AssetIdB)?.Name },
                    corr = Math.Round(r.Corr0, 4),
                    bestLagBars = r.BestLagBars,
                    bestLagCorr = Math.Round(r.BestLagCorr, 4),
                    crossings = r.Crossings,
                    nObs = r.NObs
                }),

                // Alle beteiligten Werte, damit die Oberfläche sie direkt laden kann.
                assetIds = ids
            });
        });

        // Zuletzt erkannte Kreuzungen zweier normalisierter Kurven.
        g.MapGet("/crossings", async (IPairStatRepository pairs, IAssetRepository assets,
                                      string interval = BarInterval.Daily, int days = 30,
                                      int limit = 200, CancellationToken ct = default) =>
        {
            var from = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 3650));
            var list = await pairs.GetCrossingsAsync(from, interval, Math.Clamp(limit, 1, 2000), ct);

            var ids = list.SelectMany(c => new[] { c.AssetIdA, c.AssetIdB }).Distinct().ToArray();
            var names = await LoadNamesAsync(assets, ids, ct);

            return Results.Ok(list.Select(c => new
            {
                c.CrossingId,
                tsUtc = c.TsUtc,
                a = new { id = c.AssetIdA, symbol = names.GetValueOrDefault(c.AssetIdA)?.Symbol },
                b = new { id = c.AssetIdB, symbol = names.GetValueOrDefault(c.AssetIdB)?.Symbol },
                direction = c.Upward ? "A über B" : "A unter B",
                c.SpreadBefore,
                c.SpreadAfter
            }));
        });

        /* Rangliste der Kreuzungen: welches Paar seit dem Kippen am meisten
           eingebracht hätte, welche Seite dafür zu halten wäre.

           `haltedauer` legt fest, über wie viele GEMEINSAME Bars die Bewährung
           früherer Kreuzungen gemessen wird -- nicht über Kalendertage, denn
           Aktien und Krypto handeln an verschiedenen Tagen. */
        g.MapGet("/crossings/chancen", async (ICrossingOpportunityService svc,
                                              string interval = BarInterval.Daily,
                                              int tage = 30, int haltedauer = 20,
                                              int limit = 50, bool nurKlassenwechsel = false,
                                              int maxJeSymbol = 3,
                                              CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var u = await svc.BuildAsync(interval, tage, haltedauer, limit, nurKlassenwechsel, maxJeSymbol, ct);

            return Results.Ok(new
            {
                stand = u.StandUtc,
                u.Intervall,
                u.Tage,
                u.Haltedauer,
                u.PaareGeprueft,
                u.VerdraengtDurchGrenze,
                u.Rundlaufkosten,
                u.Kostenhinweis,
                u.Hinweis,
                zeilen = u.Zeilen.Select(z => new
                {
                    kaufen = new { symbol = z.SymbolKaufen, name = z.NameKaufen, klasse = z.KlasseKaufen.ToString() },
                    verkaufen = new { symbol = z.SymbolVerkaufen, name = z.NameVerkaufen, klasse = z.KlasseVerkaufen.ToString() },
                    kreuzung = z.Kreuzung,
                    z.TageSeither,
                    renditeKaufen = z.RenditeKaufen,
                    renditeVerkaufen = z.RenditeVerkaufen,
                    z.Paargewinn,
                    z.PaargewinnNachKosten,
                    z.Klassenwechsel,
                    z.Bewaehrt,
                    historie = new
                    {
                        kreuzungen = z.HistorischeKreuzungen,
                        trefferquote = z.HistorischeTrefferquote,
                        mittelgewinn = z.HistorischerMittelgewinn,
                        mittelgewinnNachKosten = z.MittelgewinnNachKosten
                    }
                }),
                ausgelassen = u.Ausgelassen.Select(a => new { a.SymbolA, a.SymbolB, a.Grund })
            });
        });
    }

    private static async Task<Dictionary<int, Core.Models.Asset>> LoadNamesAsync(
        IAssetRepository assets, int[] ids, CancellationToken ct)
    {
        var map = new Dictionary<int, Core.Models.Asset>(ids.Length);
        foreach (var id in ids)
        {
            var a = await assets.GetAsync(id, ct);
            if (a is not null) map[id] = a;
        }
        return map;
    }

    private sealed record ExtremePair(
        int AssetIdA, int AssetIdB, double Corr0, int BestLagBars,
        double BestLagCorr, int NObs, int Crossings);
}
