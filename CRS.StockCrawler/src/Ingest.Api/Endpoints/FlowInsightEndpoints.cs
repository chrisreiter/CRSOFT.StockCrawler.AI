using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Die Auswertungen, die über das reine Zählen von Umsatz hinausgehen:
/// Zuordnung von Kapitalverschiebungen, Bewertung einzelner Ereignisse und die
/// Frage, welche Kurse in der Nähe von Kapitalauffälligkeiten auffällig wurden.
/// </summary>
public static class FlowInsightEndpoints
{
    public static void MapFlowInsightEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/flow").WithTags("Kapitalfluss");

        /* Wohin ist das Kapital gegangen?

           Zerlegt die Entwicklung in zwei Teile, die sich sauber trennen
           lassen: Veränderung der Gesamtsumme (Geld kam dazu oder ging weg) und
           Verschiebung der Anteile bei gleicher Summe (Geld blieb im System und
           wechselte den Platz). Nur der zweite Teil lässt sich zuordnen — und
           das auch nur unter der Annahme, dass das verfolgte Universum als
           geschlossenes System taugt. */
        g.MapGet("/attribution", async (IAssetRepository assets, IPriceBarRepository bars,
                                        string? ids, string interval = BarInterval.Daily,
                                        int months = 12, int lag = 0, int limit = 30,
                                        string groupBy = "class",
                                        CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var grid = await BuildGridAsync(assets, bars, ids, interval, months, ct);
            if (grid is null)
                return Results.BadRequest(new { error = "Zu wenige Daten im Betrachtungsraum" });

            var res = FlowAttribution.Attribute(
                grid.Scope.Keys.ToArray(), grid.GrossPerAsset, grid.Ts.Length,
                Math.Clamp(lag, 0, 30));

            var groupOf = grid.Scope.ToDictionary(
                kv => kv.Key,
                kv => groupBy == "sector"
                    ? kv.Value.Sector ?? "ohne Branche"
                    : kv.Value.AssetClass.ToString());

            var grouped = FlowAttribution.GroupBy(res.Flows, groupOf)
                .OrderByDescending(kv => kv.Value)
                .Take(Math.Clamp(limit, 1, 200))
                .Select(kv => new { von = kv.Key.From, nach = kv.Key.To, betrag = Math.Round(kv.Value, 2) });

            var top = res.Flows
                .Take(Math.Clamp(limit, 1, 200))
                .Select(f => new
                {
                    vonId = f.FromAssetId,
                    von = grid.Scope[f.FromAssetId].Symbol,
                    nachId = f.ToAssetId,
                    nach = grid.Scope[f.ToAssetId].Symbol,
                    betrag = Math.Round(f.Amount, 2)
                });

            var netExternal = res.ExternalInflow - res.ExternalOutflow;

            return Results.Ok(new
            {
                interval,
                months,
                lagBars = lag,
                assets = grid.Scope.Count,
                punkte = grid.Ts.Length,
                von = grid.Ts[0],
                bis = grid.Ts[^1],

                schritte = res.StepsUsed,
                werteJeSchritt = Math.Round(res.AvgParticipants, 1),

                umverteilt = Math.Round(res.TotalRedistributed, 2),
                zufluss = Math.Round(res.ExternalInflow, 2),
                abfluss = Math.Round(res.ExternalOutflow, 2),
                nettoExtern = Math.Round(netExternal, 2),

                /* Das aussagekräftigste Verhältnis der ganzen Auswertung: Wie
                   viel des Geschehens war Umschichtung innerhalb des Marktes,
                   und wie viel ging über seine Grenze? Ein hoher Wert heißt,
                   der Markt hat vor allem mit sich selbst gehandelt. */
                anteilUmverteiltPct = res.TotalRedistributed + Math.Abs(netExternal) > 0
                    ? Math.Round(res.TotalRedistributed
                        / (res.TotalRedistributed + Math.Abs(netExternal)) * 100, 2)
                    : 0,

                hinweis = "Modellrechnung unter der Annahme eines geschlossenen Systems. "
                        + "Zugeordnet wird die Anteilsverschiebung bei gleichbleibender Summe; "
                        + "wohin Geld tatsächlich floss, ist aus Kursdaten nicht ablesbar.",

                gruppen = grouped,
                paare = top
            });
        });

        /* Geschlossene Gruppen: Werte, deren gemeinsame Umsatzsumme konstant
           bleibt, und die Zirkularbewegung darin.

           Das behebt zugleich eine Schwäche der Zuordnung weiter oben: Dort
           wird unterstellt, das verfolgte Universum sei geschlossen. Hier wird
           stattdessen GESUCHT, wo diese Annahme tatsächlich zutrifft. */
        g.MapGet("/groups", async (IAssetRepository assets, IPriceBarRepository bars,
                                   string? ids, string interval = BarInterval.Daily,
                                   int months = 300, int groups = 3,
                                   int maxSize = 12, double splitFraction = 0.6,
                                   CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var grid = await BuildGridAsync(assets, bars, ids, interval, months, ct);
            if (grid is null)
                return Results.BadRequest(new { error = "Zu wenige Daten im Betrachtungsraum" });

            /* Anteile statt Beträge. Der Gesamtumsatz wächst über
               fünfundzwanzig Jahre um Größenordnungen; eine konstante SUMME
               gibt es in absoluten Zahlen nicht und kann es nicht geben. Die
               Frage lautet richtig: Bleibt der gemeinsame ANTEIL am Markt
               konstant, während sich innerhalb der Gruppe etwas verschiebt. */
            var series = new List<(int AssetId, string Symbol, double[] Share)>(grid.Scope.Count);

            foreach (var (id, gross) in grid.GrossPerAsset)
            {
                if (!grid.Scope.TryGetValue(id, out var a)) continue;

                var share = new double[grid.Ts.Length];
                var covered = 0;

                for (var t = 0; t < grid.Ts.Length; t++)
                {
                    if (grid.Total[t] <= 0) continue;

                    share[t] = gross[t] / grid.Total[t];
                    if (gross[t] > 0) covered++;
                }

                // Werte mit vielen Handelspausen erzeugen Scheinausgleich.
                if ((double)covered / grid.Ts.Length < 0.9) continue;

                series.Add((id, a.Symbol, share));
            }

            if (series.Count < 6)
                return Results.BadRequest(new
                {
                    error = $"Nur {series.Count} Werte mit durchgehendem Handel",
                    hinweis = "Anlageklassen mit verschiedenen Handelskalendern trennen."
                });

            var splitAt = (int)(grid.Ts.Length * Math.Clamp(splitFraction, 0.3, 0.8));

            var found = ConservedGroups.Find(
                series, splitAt, Math.Clamp(groups, 1, 8), Math.Clamp(maxSize, 3, 40));

            return Results.Ok(new
            {
                interval,
                werte = series.Count,
                punkte = grid.Ts.Length,
                suchzeitraum = new { von = grid.Ts[0], bis = grid.Ts[splitAt - 1] },
                pruefzeitraum = new { von = grid.Ts[splitAt], bis = grid.Ts[^1] },

                hinweis = "Geschlossenheit 1,0 heißt: Die Summe schwankt so stark, wie es "
                        + "unabhängige Werte hergäben — kein Ausgleich. Entscheidend ist "
                        + "allein der Wert im Prüfzeitraum gegen den Zufallswert.",

                gruppen = found.Select(x => new
                {
                    x.Rank,
                    x.Size,
                    geschlossenheitInnen = x.Closure,
                    geschlossenheitAussen = x.OutOfSampleClosure,
                    zufall = x.NullClosure,
                    haelt = x.Holds,
                    wippenAnteil = x.SwingShare,
                    seiteA = x.Members.Where(m => m.Side > 0).Select(m => m.Symbol),
                    seiteB = x.Members.Where(m => m.Side < 0).Select(m => m.Symbol),
                    mitglieder = x.Members.Select(m => new
                    {
                        m.Symbol,
                        seite = m.Side > 0 ? "A" : "B",
                        gewicht = m.SwingWeight,
                        klasse = grid.Scope.TryGetValue(m.AssetId, out var a) ? a.AssetClass.ToString() : null,
                        sektor = grid.Scope.TryGetValue(m.AssetId, out var b) ? b.Sector : null
                    })
                })
            });
        });

        /* Auffälligkeiten in den Kursen, bewertet von 1 bis 100. */
        g.MapGet("/events", async (IAssetRepository assets, IPriceBarRepository bars,
                                   string? ids, string interval = BarInterval.Daily,
                                   int months = 12, int minSeverity = 60, int limit = 100,
                                   CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var (fromUtc, toUtc) = TimeRange.Resolve(null, null, months);

            var scope = await ResolveScopeAsync(assets, ids, ct);
            var series = await bars.GetManyAsync(scope.Keys.ToArray(), interval, fromUtc, toUtc, ct);

            var events = new List<MarketEvent>();
            foreach (var (id, list) in series)
            {
                if (!scope.TryGetValue(id, out var a)) continue;
                events.AddRange(EventDetector.Detect(
                    id, a.Symbol, a.AssetClass, list,
                    minSeverity: Math.Clamp(minSeverity, 1, 100)));
            }

            return Results.Ok(new
            {
                interval,
                months,
                minSeverity,
                gefunden = events.Count,
                skala = "1 = alltäglich, 100 = außergewöhnlich; gemessen an der "
                      + "eigenen üblichen Schwankung des jeweiligen Wertes",
                ereignisse = events
                    .OrderByDescending(e => e.Severity)
                    .ThenByDescending(e => e.TsUtc)
                    .Take(Math.Clamp(limit, 1, 1000))
            });
        });

        /* Die Rückwärtsfrage: An Zeitpunkten, an denen sich am Kapital etwas
           Auffälliges getan hat — welche Kurse waren in unmittelbarer Nähe
           selbst auffällig, und waren sie vorher oder nachher dran? */
        g.MapGet("/coincidence", async (IAssetRepository assets, IPriceBarRepository bars,
                                        string? ids, string interval = BarInterval.Daily,
                                        int months = 12, int windowBars = 3,
                                        int minFlowSeverity = 60, int minEventSeverity = 50,
                                        int limit = 40,
                                        CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var grid = await BuildGridAsync(assets, bars, ids, interval, months, ct);
            if (grid is null)
                return Results.BadRequest(new { error = "Zu wenige Daten im Betrachtungsraum" });

            var anomalies = FlowCoincidence.FindAnomalies(
                grid.Ts, grid.GrossPerAsset,
                minSeverity: Math.Clamp(minFlowSeverity, 1, 100));

            if (anomalies.Count == 0)
                return Results.Ok(new
                {
                    interval, months,
                    kapitalereignisse = 0,
                    hinweis = "Keine Kapitalauffälligkeit oberhalb der Schwelle gefunden."
                });

            var events = new List<MarketEvent>();
            foreach (var (id, list) in grid.Series)
            {
                if (!grid.Scope.TryGetValue(id, out var a)) continue;
                events.AddRange(EventDetector.Detect(
                    id, a.Symbol, a.AssetClass, list,
                    minSeverity: Math.Clamp(minEventSeverity, 1, 100)));
            }

            var matches = FlowCoincidence.Match(
                anomalies, events, grid.Ts, Math.Clamp(windowBars, 1, 30));

            return Results.Ok(new
            {
                interval,
                months,
                windowBars,
                kapitalereignisse = anomalies.Count,
                kursereignisse = events.Count,

                hinweis = "Der Faktor rechnet gegen die eigene Unruhe des Wertes: "
                        + "1,0 heißt zufällig, erst darüber ist es ein Hinweis. "
                        + "Ein negativer Versatz heißt, der Kurs war vor der "
                        + "Kapitalbewegung auffällig — nur solche taugen als Frühindikator.",

                /* Zuerst die möglichen Frühindikatoren: häufiger als zufällig
                   UND überwiegend vor der Kapitalbewegung dran. */
                fruehindikatoren = matches
                    .Where(m => m.Lift >= 1.3 && m.AvgLagBars < 0 && m.Hits >= 3)
                    .OrderByDescending(m => m.Lift)
                    .Take(Math.Clamp(limit, 1, 200)),

                begleiter = matches
                    .Where(m => m.Lift >= 1.3)
                    .OrderByDescending(m => m.Hits)
                    .Take(Math.Clamp(limit, 1, 200)),

                kapital = anomalies
                    .OrderByDescending(a => a.Severity)
                    .Take(Math.Clamp(limit, 1, 200))
            });
        });
    }

    /// <summary>Gemeinsames Zeitraster mit Umsätzen je Wert.</summary>
    private sealed record FlowGrid(
        Dictionary<int, Asset> Scope,
        IReadOnlyDictionary<int, IReadOnlyList<PriceBar>> Series,
        DateTime[] Ts,
        Dictionary<int, double[]> GrossPerAsset,
        double[] Total);

    private static async Task<FlowGrid?> BuildGridAsync(
        IAssetRepository assets, IPriceBarRepository bars,
        string? ids, string interval, int months, CancellationToken ct)
    {
        var (fromUtc, toUtc) = TimeRange.Resolve(null, null, months);

        var scope = await ResolveScopeAsync(assets, ids, ct);
        if (scope.Count < 2) return null;

        var series = await bars.GetManyAsync(scope.Keys.ToArray(), interval, fromUtc, toUtc, ct);
        if (series.Count < 2) return null;

        var grid = series.Values.SelectMany(v => v.Select(b => b.TsUtc))
                         .Distinct().OrderBy(t => t).ToArray();

        if (grid.Length < 20) return null;

        var index = new Dictionary<DateTime, int>(grid.Length);
        for (var i = 0; i < grid.Length; i++) index[grid[i]] = i;

        var grossPerAsset = new Dictionary<int, double[]>(series.Count);
        var total = new double[grid.Length];

        foreach (var (id, list) in series)
        {
            if (!scope.TryGetValue(id, out var asset)) continue;

            var arr = new double[grid.Length];
            foreach (var bar in list)
            {
                if (!index.TryGetValue(bar.TsUtc, out var k)) continue;

                var gr = NetFlow.Gross(asset.AssetClass, bar);
                arr[k] = gr;
                total[k] += gr;
            }
            grossPerAsset[id] = arr;
        }

        // Werte ohne jeden Umsatz tragen nichts bei und verwässern die Anteile.
        foreach (var id in grossPerAsset.Where(kv => kv.Value.All(v => v <= 0))
                                        .Select(kv => kv.Key).ToList())
        {
            grossPerAsset.Remove(id);
            scope.Remove(id);
        }

        return grossPerAsset.Count < 2
            ? null
            : new FlowGrid(scope, series, grid, grossPerAsset, total);
    }

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
