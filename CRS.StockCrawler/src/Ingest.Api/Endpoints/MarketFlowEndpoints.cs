using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;

namespace Ingest.Api.Endpoints;

public sealed record MarketFlowPoint(
    long T,
    double GrossFlow,
    double NetFlow,
    double CumulativeNet,
    Dictionary<string, double> ByClass,
    Dictionary<string, double> SharesByClass);

public sealed record FlowContributor(
    int AssetId, string Symbol, string? Name, string AssetClass, string? Sector,
    double GrossFlow, double NetFlow, double SharePct, double ShareChangePct);

public sealed record RotationPair(
    int AssetIdA, string SymbolA, int AssetIdB, string SymbolB,
    double Score, double ShareChangeA, double ShareChangeB);

/// <summary>
/// Kapitalfluss im Markt — welcher Anteil des Umsatzes wo stattfindet, wohin
/// er sich verschiebt, und ob unterm Strich Geld hinein- oder abfließt.
/// </summary>
public static class MarketFlowEndpoints
{
    public static void MapMarketFlowEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/flow").WithTags("Kapitalfluss");

        /* Marktentwicklung über die Zeit. Der Betrachtungsraum ist wählbar:
           entweder die übergebenen Werte oder alles Verfolgte. Genau darin
           liegt der Nutzen — „der Markt“ ist eine Setzung, und man will beide
           Sichten vergleichen können. */
        g.MapGet("/market", async (IAssetRepository assets, IPriceBarRepository bars,
                                   string? ids, string interval = BarInterval.Daily,
                                   int months = 12,
                                   DateTime? from = null, DateTime? to = null,
                                   CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var (fromUtc, toUtc) = TimeRange.Resolve(from, to, months);

            var scope = await ResolveScopeAsync(assets, ids, ct);
            if (scope.Count < 1)
                return Results.BadRequest(new { error = "Keine Werte im Betrachtungsraum" });

            var series = await bars.GetManyAsync(
                scope.Keys.ToArray(), interval, fromUtc, toUtc, ct);

            if (series.Count == 0)
                return Results.Ok(new { interval, fromUtc, toUtc, assets = 0, points = Array.Empty<object>() });

            // Nach Zeitpunkt bündeln, damit Anteile je Bar berechnet werden können.
            var perTs = new SortedDictionary<DateTime, List<(Asset A, PriceBar B)>>();

            foreach (var (id, list) in series)
            {
                if (!scope.TryGetValue(id, out var asset)) continue;

                foreach (var bar in list)
                {
                    if (!perTs.TryGetValue(bar.TsUtc, out var bucket))
                    {
                        bucket = [];
                        perTs[bar.TsUtc] = bucket;
                    }
                    bucket.Add((asset, bar));
                }
            }

            var points = new List<MarketFlowPoint>(perTs.Count);
            double cumulative = 0;

            foreach (var (ts, bucket) in perTs)
            {
                double gross = 0, net = 0;
                var byClass = new Dictionary<string, double>();
                var grossByClass = new Dictionary<string, double>();

                foreach (var (asset, bar) in bucket)
                {
                    var gr = NetFlow.Gross(asset.AssetClass, bar);
                    if (gr <= 0) continue;

                    var nt = NetFlow.Signed(asset.AssetClass, bar);
                    var cls = asset.AssetClass.ToString();

                    gross += gr;
                    net += nt;

                    byClass[cls] = byClass.GetValueOrDefault(cls) + nt;
                    grossByClass[cls] = grossByClass.GetValueOrDefault(cls) + gr;
                }

                if (gross <= 0) continue;

                cumulative += net;

                points.Add(new MarketFlowPoint(
                    new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                    Math.Round(gross, 2),
                    Math.Round(net, 2),
                    Math.Round(cumulative, 2),
                    byClass.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
                    grossByClass.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value / gross, 5))));
            }

            var totalGross = points.Sum(p => p.GrossFlow);
            var totalNet = points.Sum(p => p.NetFlow);

            return Results.Ok(new
            {
                interval,
                fromUtc,
                toUtc,
                assets = scope.Count,
                scope = string.IsNullOrWhiteSpace(ids) ? "alle verfolgten" : "Auswahl",
                totalGross,
                totalNet,

                /* Netto im Verhältnis zum Umsatz: sagt, wie einseitig gehandelt
                   wurde. Ein Prozentwert nahe null heißt, Käufe und Verkäufe
                   hielten sich die Waage. */
                netSharePct = totalGross > 0 ? Math.Round(totalNet / totalGross * 100, 3) : 0,
                points
            });
        });

        /* Wer trug wie viel bei — und wohin verschob sich der Anteil?
           Vergleicht die jüngste Periode mit der davorliegenden. */
        g.MapGet("/contributors", async (IAssetRepository assets, IPriceBarRepository bars,
                                         string? ids, string interval = BarInterval.Daily,
                                         int recentDays = 30, int limit = 25,
                                         CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            recentDays = Math.Clamp(recentDays, 1, 365);

            var now = DateTime.UtcNow;
            var recentFrom = now.AddDays(-recentDays);
            var baseFrom = now.AddDays(-recentDays * 2);

            var scope = await ResolveScopeAsync(assets, ids, ct);
            var series = await bars.GetManyAsync(scope.Keys.ToArray(), interval, baseFrom, now, ct);

            double totalRecent = 0, totalBase = 0;
            var perAsset = new Dictionary<int, (double GrossR, double NetR, double GrossB)>();

            foreach (var (id, list) in series)
            {
                if (!scope.TryGetValue(id, out var asset)) continue;

                double gr = 0, nt = 0, gb = 0;

                foreach (var bar in list)
                {
                    var g2 = NetFlow.Gross(asset.AssetClass, bar);
                    if (g2 <= 0) continue;

                    if (bar.TsUtc >= recentFrom)
                    {
                        gr += g2;
                        nt += NetFlow.Signed(asset.AssetClass, bar);
                    }
                    else gb += g2;
                }

                perAsset[id] = (gr, nt, gb);
                totalRecent += gr;
                totalBase += gb;
            }

            var rows = perAsset
                .Where(kv => kv.Value.GrossR > 0)
                .Select(kv =>
                {
                    var a = scope[kv.Key];
                    var shareR = totalRecent > 0 ? kv.Value.GrossR / totalRecent : 0;
                    var shareB = totalBase > 0 ? kv.Value.GrossB / totalBase : 0;

                    return new FlowContributor(
                        a.AssetId, a.Symbol, a.Name, a.AssetClass.ToString(), a.Sector,
                        Math.Round(kv.Value.GrossR, 2),
                        Math.Round(kv.Value.NetR, 2),
                        Math.Round(shareR * 100, 4),
                        shareB > 0 ? Math.Round((shareR / shareB - 1) * 100, 2) : 0);
                })
                .ToList();

            return Results.Ok(new
            {
                interval,
                recentDays,
                totalRecent,
                zufluss = rows.OrderByDescending(r => r.NetFlow).Take(Math.Clamp(limit, 1, 200)),
                abfluss = rows.OrderBy(r => r.NetFlow).Take(Math.Clamp(limit, 1, 200)),
                anteilGewinner = rows.OrderByDescending(r => r.ShareChangePct).Take(Math.Clamp(limit, 1, 200))
            });
        });

        /* Rotationsverdacht: Paare, bei denen der eine regelmäßig Anteil
           gewinnt, während der andere verliert.

           Ausdrücklich ein Verdacht. Aus Kursdaten lässt sich nicht ablesen,
           dass Geld von A nach B floss — es kann ebenso ein gemeinsamer
           Auslöser sein, der den einen begünstigt und den anderen belastet. */
        g.MapGet("/rotation-pairs", async (IAssetRepository assets, IPriceBarRepository bars,
                                           string? ids, string interval = BarInterval.Daily,
                                           int months = 6, int limit = 20,
                                           CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var to = DateTime.UtcNow;
            var from = to.AddMonths(-Math.Clamp(months, 1, 120));

            var scope = await ResolveScopeAsync(assets, ids, ct);

            // Bei sehr vielen Werten würde die Paarrechnung ausufern.
            if (scope.Count > 60)
                scope = scope.OrderBy(kv => kv.Value.MarketCapRank ?? int.MaxValue)
                             .Take(60)
                             .ToDictionary(kv => kv.Key, kv => kv.Value);

            var series = await bars.GetManyAsync(scope.Keys.ToArray(), interval, from, to, ct);

            // Anteilsreihen je Wert über ein gemeinsames Zeitraster aufbauen.
            var grid = series.Values.SelectMany(v => v.Select(b => b.TsUtc))
                             .Distinct().OrderBy(t => t).ToArray();

            if (grid.Length < 20)
                return Results.Ok(new { pairs = Array.Empty<object>(), note = "Zu wenige gemeinsame Zeitpunkte" });

            var index = new Dictionary<DateTime, int>(grid.Length);
            for (var i = 0; i < grid.Length; i++) index[grid[i]] = i;

            var grossPerAsset = new Dictionary<int, double[]>();
            var totalPerTs = new double[grid.Length];

            foreach (var (id, list) in series)
            {
                if (!scope.TryGetValue(id, out var asset)) continue;

                var arr = new double[grid.Length];
                foreach (var bar in list)
                {
                    if (!index.TryGetValue(bar.TsUtc, out var k)) continue;
                    var g2 = NetFlow.Gross(asset.AssetClass, bar);
                    arr[k] = g2;
                    totalPerTs[k] += g2;
                }
                grossPerAsset[id] = arr;
            }

            var shares = grossPerAsset.ToDictionary(
                kv => kv.Key,
                kv =>
                {
                    var s = new double[grid.Length];
                    for (var k = 0; k < grid.Length; k++)
                        s[k] = totalPerTs[k] > 0 ? kv.Value[k] / totalPerTs[k] : 0;
                    return s;
                });

            var idList = shares.Keys.OrderBy(i => i).ToArray();
            var pairs = new List<RotationPair>();

            // Wiederverwendete Puffer statt Millionen kurzlebiger Arrays.
            var bufA = new double[grid.Length];
            var bufB = new double[grid.Length];

            for (var i = 0; i < idList.Length; i++)
            {
                var rawA = grossPerAsset[idList[i]];

                for (var j = i + 1; j < idList.Length; j++)
                {
                    var rawB = grossPerAsset[idList[j]];

                    /* Nur Zeitpunkte, an denen BEIDE Werte tatsächlich
                       gehandelt haben.

                       Ohne diese Einschränkung misst die Rechnung den
                       Handelskalender statt Kapitalrotation: am Wochenende
                       handelt nur Krypto, der Aktienanteil ist dann null. Jedes
                       Krypto-Aktien-Paar käme so auf eine Korrelation um −0,93
                       — perfekt gegenläufig und völlig ohne Aussage.

                       Die Anteile werden zusätzlich AUF DIESE Zeitpunkte neu
                       bezogen, sonst schleppte man die Gesamtsumme aus Tagen
                       mit, an denen einer der beiden gar nicht handelte. */
                    var n = 0;
                    for (var k = 0; k < grid.Length; k++)
                    {
                        if (rawA[k] <= 0 || rawB[k] <= 0 || totalPerTs[k] <= 0) continue;

                        bufA[n] = rawA[k] / totalPerTs[k];
                        bufB[n] = rawB[k] / totalPerTs[k];
                        n++;
                    }

                    // Unter dieser Zahl gemeinsamer Punkte ist das Ergebnis Zufall.
                    if (n < 30) continue;

                    var score = NetFlow.RotationScore(bufA.AsSpan(0, n), bufB.AsSpan(0, n));
                    if (score >= -0.1) continue;   // nur Gegenläufiges interessiert

                    pairs.Add(new RotationPair(
                        idList[i], scope[idList[i]].Symbol,
                        idList[j], scope[idList[j]].Symbol,
                        Math.Round(score, 4),
                        Math.Round((bufA[n - 1] - bufA[0]) * 100, 4),
                        Math.Round((bufB[n - 1] - bufB[0]) * 100, 4)));
                }
            }

            return Results.Ok(new
            {
                interval,
                months,
                assets = scope.Count,
                hinweis = "Rotationsverdacht, kein nachgewiesener Kapitalfluss — "
                        + "aus Kursdaten ist nicht ablesbar, wohin Geld tatsächlich floss.",
                pairs = pairs.OrderBy(p => p.Score).Take(Math.Clamp(limit, 1, 200))
            });
        });
    }

    /// <summary>
    /// Betrachtungsraum: die übergebenen Werte oder — ohne Angabe — alles
    /// Verfolgte.
    /// </summary>
    private static async Task<Dictionary<int, Asset>> ResolveScopeAsync(
        IAssetRepository assets, string? ids, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ids))
            return (await assets.GetTrackedAsync(ct)).ToDictionary(a => a.AssetId);

        var wanted = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => int.TryParse(s, out var v) ? v : -1)
                        .Where(v => v > 0)
                        .Distinct()
                        .ToHashSet();

        var result = new Dictionary<int, Asset>(wanted.Count);
        foreach (var id in wanted)
        {
            var a = await assets.GetAsync(id, ct);
            if (a is not null) result[id] = a;
        }

        return result;
    }
}
