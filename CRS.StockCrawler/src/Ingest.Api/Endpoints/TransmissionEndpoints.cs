using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Säule „Differential": auffällige Änderungen der geglätteten Kurve, und was
/// danach bei den <b>anderen</b> Werten geschieht.
///
/// <b>Warum bei den anderen und nicht beim eigenen.</b> Ob ein Muster die
/// eigene Fortsetzung vorhersagt, ist gemessen und beantwortet: nein.
/// Trefferquote 43 bis 52 Prozent über 125.000 Ausschnitte, in jeder
/// Konfiguration schlechter als die Annahme, der Kurs bleibe stehen.
///
/// Die Übertragung auf andere Werte ist eine andere Frage, und die vorhandenen
/// Messungen sprechen dafür, sie zu stellen: Die Modenanalyse hat eine
/// Vorlaufordnung gefunden, die über zwei getrennte Dreizehnjahreszeiträume
/// hielt. Wo Vorlauf ist, kann Übertragung sein.
/// </summary>
public static class TransmissionEndpoints
{
    public static void MapTransmissionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/transmission").WithTags("Differential");

        /* Die Ereignisse eines einzelnen Wertes — zum Hinsehen, bevor man
           Schlüsse über Paare zieht. */
        g.MapGet("/events/{assetId:int}", async (IAssetRepository assets, IPriceBarRepository bars,
                                                 int assetId, string interval = BarInterval.Daily,
                                                 int months = 300, int smoothSpan = 10,
                                                 double minZ = 3.0, int limit = 40,
                                                 CancellationToken ct = default) =>
        {
            var s = await LoadAsync(assets, bars, assetId, interval, months, ct);
            if (s is null) return Results.BadRequest(new { error = "Zu wenige Kursdaten" });

            var (asset, closes, stamps) = s.Value;

            var ev = DerivativeEvents.Detect(closes, smoothSpan, 120, minZ);

            return Results.Ok(new
            {
                asset.Symbol,
                interval,
                smoothSpan,
                minZ,
                bars = closes.Length,
                gefunden = ev.Count,
                jeJahr = Math.Round(ev.Count / (closes.Length / 252.0), 2),
                hinweis = "Gemessen wird die Ableitung der geglätteten Kurve — also wo eine "
                        + "Bewegung einsetzt oder kippt, nicht wo ein einzelner Tag ausreißt.",
                ereignisse = ev.OrderByDescending(e => e.Strength).Take(Math.Clamp(limit, 1, 500))
                    .Select(e => new
                    {
                        ts = stamps[e.Index],
                        richtung = e.Sign > 0 ? "aufwärts" : "abwärts",
                        staerke = e.Strength
                    })
            });
        });

        /* Der eigentliche Lauf: alle Werte durcharbeiten, Ereignisse finden,
           und für jedes Paar messen, wie der Empfänger reagiert. Anschließend
           dasselbe in einem zweiten Zeitraum, den die Suche nie gesehen hat. */
        g.MapGet("/scan", async (IAssetRepository assets, IPriceBarRepository bars,
                                 string? ids, string interval = BarInterval.Daily,
                                 int smoothSpan = 10, double minZ = 3.0,
                                 int horizon = 5, int minEvents = 12,
                                 int topN = 40, int shuffle = 0,
                                 DateTime? trainFrom = null, DateTime? trainTo = null,
                                 DateTime? testFrom = null, DateTime? testTo = null,
                                 CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var scope = await ResolveScopeAsync(assets, ids, ct);
            if (scope.Count < 5) return Results.BadRequest(new { error = "Mindestens fünf Werte nötig" });

            trainFrom ??= new DateTime(2001, 1, 1);
            trainTo ??= new DateTime(2013, 12, 31);
            testFrom ??= new DateTime(2014, 1, 1);
            testTo ??= DateTime.UtcNow;

            var train = await RunAsync(assets, bars, scope, interval,
                trainFrom.Value, trainTo.Value, smoothSpan, minZ, horizon, minEvents, shuffle, ct);

            if (train.Links.Count == 0)
                return Results.BadRequest(new { error = "Im Lernzeitraum keine auswertbaren Paare", train.Note });

            var test = await RunAsync(assets, bars, scope, interval,
                testFrom.Value, testTo.Value, smoothSpan, minZ, horizon, minEvents, shuffle, ct);

            var (checkedN, same, sharePct, meanT, meanTest) =
                Transmission.Confirm(train.Links, test.Links, topN);

            var top = train.Links
                .Where(l => Math.Abs(l.TStat) >= 2)
                .OrderByDescending(l => Math.Abs(l.TStat))
                .Take(Math.Clamp(topN, 1, 200))
                .Select(l =>
                {
                    var m = test.Links.FirstOrDefault(x =>
                        x.FromAssetId == l.FromAssetId && x.ToAssetId == l.ToAssetId
                        && x.HorizonBars == l.HorizonBars);

                    return new
                    {
                        von = l.FromSymbol,
                        nach = l.ToSymbol,
                        horizontBars = l.HorizonBars,
                        lernEreignisse = l.Events,
                        lernReaktionPct = l.MeanResponsePct,
                        lernEinigkeitPct = l.ConsistencyPct,
                        lernT = l.TStat,
                        pruefReaktionPct = m?.MeanResponsePct,
                        pruefEreignisse = m?.Events,
                        gleicheRichtung = m is null
                            ? (bool?)null
                            : Math.Sign(m.MeanResponsePct) == Math.Sign(l.MeanResponsePct)
                    };
                });

            return Results.Ok(new
            {
                interval,
                horizon,
                smoothSpan,
                minZ,
                werte = train.Assets,
                lernzeitraum = new { von = trainFrom, bis = trainTo, punkte = train.Points, ereignisse = train.Events },
                pruefzeitraum = new { von = testFrom, bis = testTo, punkte = test.Points, ereignisse = test.Events },
                paareGeprueft = train.Links.Count,

                bestaetigung = new
                {
                    geprueft = checkedN,
                    gleicheRichtung = same,
                    anteilPct = sharePct,

                    /* Fünfzig Prozent ist der Münzwurf. Alles darunter heißt:
                       Die im Lernzeitraum gefundenen Übertragungen kehren sich
                       später eher um, als dass sie sich wiederholen. */
                    mittlereLernStaerke = meanT,
                    mittlerePruefReaktionPct = meanTest,

                    urteil = checkedN < 10 ? "zu wenige bestätigbare Paare"
                        : sharePct >= 60 && meanTest > 0 ? "die Übertragungen wiederholen sich"
                        : sharePct >= 55 ? "schwacher Hinweis, nicht belastbar"
                        : "die Übertragungen wiederholen sich nicht"
                },

                hinweis = "Alle Reaktionen sind marktbereinigt: Von jeder Bewegung ist abgezogen, "
                        + "was der Markt im selben Zeitraum im Mittel tat. Ohne das misst man den "
                        + "Marktfaktor, der über die Hälfte aller Bewegung erklärt — und findet für "
                        + "jedes Paar eine starke Übertragung.",

                paare = top
            });
        });
        /* Der Anteil im Gewinn — das Merkmal aus dem Dispositionseffekt.

           Prüft, ob der geschätzte Buchgewinn der Halter etwas über die
           weitere, marktbereinigte Entwicklung sagt. Aufgeteilt in Fünftel:
           Besteht ein Zusammenhang, ordnen sie sich; besteht keiner, springen
           sie. */
        g.MapGet("/overhang", async (IAssetRepository assets, IPriceBarRepository bars,
                                     string? ids, string interval = BarInterval.Daily,
                                     int halfLife = 60, int horizon = 20,
                                     DateTime? from = null, DateTime? to = null,
                                     int months = 300,
                                     CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var scope = await ResolveScopeAsync(assets, ids, ct);
            if (scope.Count < 5) return Results.BadRequest(new { error = "Mindestens fünf Werte nötig" });

            var (fromUtc, toUtc) = TimeRange.Resolve(from, to, months);
            var series = await bars.GetManyAsync(scope.Keys.ToArray(), interval, fromUtc, toUtc, ct);

            var (grid, closes, _) = AlignByCoverage(series, scope.Keys);
            if (closes.Count < 5)
                return Results.BadRequest(new { error = "Kein tragfähiges gemeinsames Zeitraster" });

            var neutral = Transmission.MarketNeutral(closes);

            /* Die Umsätze müssen auf demselben Raster liegen wie die Kurse,
               sonst passen Gewicht und Kurs nicht zusammen. */
            var volumes = new Dictionary<int, double?[]>(closes.Count);

            var index = new Dictionary<DateTime, int>(grid.Length);
            for (var i = 0; i < grid.Length; i++) index[grid[i]] = i;

            foreach (var id in closes.Keys)
            {
                var arr = new double?[grid.Length];

                if (series.TryGetValue(id, out var list))
                {
                    foreach (var b in list)
                    {
                        if (!index.TryGetValue(b.TsUtc, out var k)) continue;
                        arr[k] = b.Volume is { } v ? (double)v : null;
                    }
                }

                volumes[id] = arr;
            }

            var data = new List<(double?[] Overhang, double[] NeutralReturns)>(closes.Count);
            var samples = new List<object>();

            foreach (var (id, c) in closes)
            {
                var over = GainsOverhang.Compute(c, volumes[id], halfLife);
                data.Add((over, neutral[id]));

                var last = over[^1];
                if (last is { } v && scope.TryGetValue(id, out var a))
                    samples.Add(new { a.Symbol, ueberhangPct = Math.Round(v * 100, 2) });
            }

            var (means, counts, spread, t) = GainsOverhang.Evaluate(data, horizon);

            return Results.Ok(new
            {
                interval,
                halfLife,
                horizon,
                werte = closes.Count,
                punkte = grid.Length,
                von = grid[0],
                bis = grid[^1],

                fuenftel = Enumerable.Range(0, 5).Select(i => new
                {
                    fuenftel = i + 1,
                    beschreibung = i == 0 ? "am tiefsten im Verlust"
                                 : i == 4 ? "am höchsten im Gewinn" : "",
                    beobachtungen = counts[i],
                    mittlereFolgePct = Math.Round((Math.Exp(means[i]) - 1) * 100, 4)
                }),

                abstandPct = Math.Round((Math.Exp(spread) - 1) * 100, 4),
                tWert = Math.Round(t, 2),

                urteil = Math.Abs(t) < 2 ? "kein Zusammenhang"
                    : t < 0 ? "hoher Buchgewinn geht mit schwächerer Folgeentwicklung einher — "
                            + "das erwartete Vorzeichen des Dispositionseffekts"
                    : "hoher Buchgewinn geht mit stärkerer Folgeentwicklung einher — "
                    + "entgegengesetzt zur Erwartung",

                hinweis = "Alle Folgerenditen sind marktbereinigt. Der geschätzte Einstand ist "
                        + "ein umsatzgewichteter, abklingender Durchschnittskurs — eine Näherung "
                        + "für den Kaufpreis der Halter, keine Messung.",

                aktuell = samples
            });
        });

        /* Kontrolle: Trägt der Buchgewinn etwas bei, was einfaches Momentum
           nicht schon erklärt?

           Der geschätzte Einstand ist der Abstand des Kurses von einem
           gleitenden umsatzgewichteten Mittel — dem Bauplan nach ein
           Momentum-Indikator. Ohne diese Kontrolle wäre der gefundene Effekt
           womöglich nur ein bekannter unter neuem Namen. */
        g.MapGet("/overhang/control", async (IAssetRepository assets, IPriceBarRepository bars,
                                             string? ids, string interval = BarInterval.Daily,
                                             int halfLife = 60, int horizon = 20,
                                             int momentumLookback = 120,
                                             DateTime? from = null, DateTime? to = null,
                                             int months = 300,
                                             CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var scope = await ResolveScopeAsync(assets, ids, ct);
            if (scope.Count < 5) return Results.BadRequest(new { error = "Mindestens fünf Werte nötig" });

            var (fromUtc, toUtc) = TimeRange.Resolve(from, to, months);
            var series = await bars.GetManyAsync(scope.Keys.ToArray(), interval, fromUtc, toUtc, ct);

            var (grid, closes, _) = AlignByCoverage(series, scope.Keys);
            if (closes.Count < 5)
                return Results.BadRequest(new { error = "Kein tragfähiges gemeinsames Zeitraster" });

            var neutral = Transmission.MarketNeutral(closes);

            var index = new Dictionary<DateTime, int>(grid.Length);
            for (var i = 0; i < grid.Length; i++) index[grid[i]] = i;

            var data = new List<(double?[] Candidate, double?[] Control, double[] Returns)>(closes.Count);

            foreach (var (id, c) in closes)
            {
                var vol = new double?[grid.Length];

                if (series.TryGetValue(id, out var list))
                {
                    foreach (var b in list)
                    {
                        if (!index.TryGetValue(b.TsUtc, out var k)) continue;
                        vol[k] = b.Volume is { } v ? (double)v : null;
                    }
                }

                data.Add((
                    GainsOverhang.Compute(c, vol, halfLife),
                    FeatureControl.Momentum(c, momentumLookback),
                    neutral[id]));
            }

            var res = FeatureControl.Compare(data, "Buchgewinn", "Momentum", horizon);

            var sort = new List<object>(5);

            for (var r = 0; r < 5; r++)
            {
                /* Zeilenindex in eine eigene Variable, und die Zählungen sofort
                   ausrechnen.

                   Die Schleifenvariable einer for-Schleife ist EINE Variable,
                   die von jedem Lambda geteilt wird — und LINQ wertet erst beim
                   Serialisieren aus. Bis dahin steht sie auf 5, und der Zugriff
                   läuft aus dem Feld heraus. */
                var rowIndex = r;

                var row = new double[5];
                for (var col = 0; col < 5; col++)
                    row[col] = Math.Round((Math.Exp(res.DoubleSort[rowIndex, col]) - 1) * 100, 4);

                var n = new int[5];
                for (var col = 0; col < 5; col++) n[col] = res.DoubleSortCounts[rowIndex, col];

                sort.Add(new
                {
                    momentumFuenftel = rowIndex + 1,
                    buchgewinnFuenftel = row,
                    gefaelle = Math.Round(row[4] - row[0], 4),
                    n
                });
            }

            var cand = res.Joint.FirstOrDefault(e => e.Name == "Buchgewinn");
            var ctrl = res.Joint.FirstOrDefault(e => e.Name == "Momentum");

            return Results.Ok(new
            {
                interval,
                halfLife,
                horizon,
                momentumLookback,
                von = grid[0],
                bis = grid[^1],
                werte = closes.Count,

                beobachtungen = res.Observations,
                korrelationDerMerkmale = res.Correlation,

                einzeln = res.Univariate,
                gemeinsam = res.Joint,

                urteil = cand is null || ctrl is null ? "nicht auswertbar"
                    : Math.Abs(cand.TStat) < 2
                        ? "der Buchgewinn trägt neben Momentum nichts bei — dasselbe Merkmal unter anderem Namen"
                        : Math.Abs(cand.TStat) > Math.Abs(ctrl.TStat)
                            ? "der Buchgewinn trägt eigenständig bei und ist dabei stärker als Momentum"
                            : "der Buchgewinn trägt eigenständig bei, Momentum bleibt aber stärker",

                res.Note,

                doppelsortierung = sort,
                hinweisSortierung = "Zeilen sind Momentum-Fünftel, Spalten Buchgewinn-Fünftel. "
                                  + "Bleibt innerhalb einer Zeile ein Gefälle, erklärt Momentum "
                                  + "den Buchgewinn nicht."
            });
        });

    }

    // ------------------------------------------------------------- Helfer ---

    private sealed record RunResult(
        int Assets, int Points, int Events, List<TransmissionLink> Links, string Note);

    private static async Task<RunResult> RunAsync(
        IAssetRepository assets, IPriceBarRepository bars,
        Dictionary<int, Asset> scope, string interval,
        DateTime from, DateTime to,
        int smoothSpan, double minZ, int horizon, int minEvents, int shuffle,
        CancellationToken ct)
    {
        var series = await bars.GetManyAsync(scope.Keys.ToArray(), interval, from, to, ct);

        var (grid, closes, _) = AlignByCoverage(series, scope.Keys);
        if (closes.Count < 5) return new RunResult(0, 0, 0, [], "Kein tragfähiges Zeitraster");

        var neutral = Transmission.MarketNeutral(closes);

        // Ereignisse je Wert, einmal berechnet und für alle Paare wiederverwendet.
        var events = new Dictionary<int, List<DerivativeEvent>>(closes.Count);
        var total = 0;

        /* Der Nulltest: dieselbe Anzahl Ereignisse, dieselben Vorzeichen,
           aber an zufälligen Stellen.

           Damit bleibt alles erhalten, was NICHT Übertragung ist — die Zahl der
           Ereignisse, ihre Richtungsverteilung, die Eigenschaften der
           Empfängerreihe. Zerstört wird ausschließlich der zeitliche Bezug
           zwischen Sender und Empfänger. Bleibt die Bestätigungsquote dabei bei
           fünfzig Prozent, ist der gemessene Effekt echt; bleibt sie hoch, misst
           die Auswertung etwas anderes als sie behauptet. */
        var rnd = shuffle > 0 ? new Random(20260821 + shuffle) : null;

        foreach (var (id, c) in closes)
        {
            var e = DerivativeEvents.Detect(c, smoothSpan, 120, minZ);

            if (rnd is not null && e.Count > 0)
            {
                var lo = 121;
                var hi = Math.Max(lo + 1, c.Length - horizon - 1);

                e = e.Select(x => x with { Index = rnd.Next(lo, hi) })
                     .OrderBy(x => x.Index)
                     .ToList();
            }

            events[id] = e;
            total += e.Count;
        }

        var links = new List<TransmissionLink>();

        foreach (var (fromId, ev) in events)
        {
            if (ev.Count < minEvents) continue;

            foreach (var (toId, _) in closes)
            {
                // Der Sender auf sich selbst ist die Frage, die schon beantwortet ist.
                if (toId == fromId) continue;

                var link = Transmission.Measure(
                    fromId, scope[fromId].Symbol,
                    toId, scope[toId].Symbol,
                    ev, neutral[toId], horizon, minEvents);

                if (link is not null) links.Add(link);
            }
        }

        return new RunResult(closes.Count, grid.Length, total, links, "ok");
    }

    /// <summary>
    /// Gemeinsames Zeitraster über den Deckungsgrad — dieselbe Begründung wie
    /// bei der Modenanalyse: Die strenge Schnittmenge über viele Werte und
    /// fünfundzwanzig Jahre ist leer.
    /// </summary>
    private static (DateTime[] Grid, Dictionary<int, double[]> Series, double Filled) AlignByCoverage(
        IReadOnlyDictionary<int, IReadOnlyList<PriceBar>> series, IEnumerable<int> ids,
        double gridCoverage = 0.8, double assetCoverage = 0.9)
    {
        var wanted = ids.Where(series.ContainsKey).ToArray();
        if (wanted.Length == 0) return ([], [], 0);

        var count = new Dictionary<DateTime, int>();

        foreach (var id in wanted)
            foreach (var b in series[id])
                count[b.TsUtc] = count.GetValueOrDefault(b.TsUtc) + 1;

        var need = Math.Max(2, (int)Math.Ceiling(wanted.Length * gridCoverage));

        var grid = count.Where(kv => kv.Value >= need).Select(kv => kv.Key)
                        .OrderBy(t => t).ToArray();

        if (grid.Length < 200) return ([], [], 0);

        var index = new Dictionary<DateTime, int>(grid.Length);
        for (var i = 0; i < grid.Length; i++) index[grid[i]] = i;

        var result = new Dictionary<int, double[]>(wanted.Length);
        long filled = 0, totalPts = 0;

        foreach (var id in wanted)
        {
            var arr = new double[grid.Length];
            var have = new bool[grid.Length];
            var hits = 0;

            foreach (var b in series[id])
            {
                if (!index.TryGetValue(b.TsUtc, out var k)) continue;
                arr[k] = (double)b.Close;
                have[k] = true;
                hits++;
            }

            if ((double)hits / grid.Length < assetCoverage) continue;

            double last = 0;
            var started = false;

            for (var i = 0; i < grid.Length; i++)
            {
                if (have[i]) { last = arr[i]; started = true; continue; }
                if (!started) continue;

                arr[i] = last;
                filled++;
            }

            if (!started || arr[0] <= 0) continue;

            totalPts += grid.Length;
            result[id] = arr;
        }

        return (grid, result, totalPts > 0 ? (double)filled / totalPts : 0);
    }

    private static async Task<Dictionary<int, Asset>> ResolveScopeAsync(
        IAssetRepository assets, string? ids, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ids))
            return (await assets.GetTrackedAsync(ct)).ToDictionary(a => a.AssetId);

        var wanted = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(v => int.TryParse(v, out var x) ? x : -1)
                        .Where(v => v > 0).Distinct().ToHashSet();

        var result = new Dictionary<int, Asset>(wanted.Count);

        foreach (var id in wanted)
        {
            var a = await assets.GetAsync(id, ct);
            if (a is not null) result[id] = a;
        }

        return result;
    }

    private static async Task<(Asset Asset, double[] Closes, DateTime[] Stamps)?> LoadAsync(
        IAssetRepository assets, IPriceBarRepository bars,
        int assetId, string interval, int months, CancellationToken ct)
    {
        if (!BarInterval.IsValid(interval)) return null;

        var asset = await assets.GetAsync(assetId, ct);
        if (asset is null) return null;

        var (fromUtc, toUtc) = TimeRange.Resolve(null, null, months);
        var series = await bars.GetManyAsync([assetId], interval, fromUtc, toUtc, ct);

        if (!series.TryGetValue(assetId, out var list) || list.Count < 200) return null;

        var closes = new double[list.Count];
        var stamps = new DateTime[list.Count];

        for (var i = 0; i < list.Count; i++)
        {
            closes[i] = (double)list[i].Close;
            stamps[i] = list[i].TsUtc;
        }

        return (asset, closes, stamps);
    }
}
