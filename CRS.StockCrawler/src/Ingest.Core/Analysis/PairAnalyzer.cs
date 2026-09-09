using Ingest.Core.Models;

namespace Ingest.Core.Analysis;

public sealed record PairAnalysisResult(List<PairStat> Stats, List<Crossing> Crossings);

public sealed class PairAnalyzerOptions
{
    /// <summary>Maximaler untersuchter Vorlauf in Bars.</summary>
    public int MaxLagBars { get; init; } = 12;

    /// <summary>Unter dieser Zahl gemeinsamer Punkte ist eine Korrelation Zufall.</summary>
    public int MinObservations { get; init; } = 60;

    /// <summary>
    /// Erst ab diesem Gleichlauf wird nach einem Vorlauf gesucht. Ohne
    /// erkennbaren Zusammenhang ist der „beste Lag" reines Rauschen — und die
    /// Suche ist der mit Abstand teuerste Teil.
    /// </summary>
    public double LagSearchThreshold { get; init; } = 0.15;

    public bool DetectCrossings { get; init; } = true;
    public double MinSpread { get; init; } = 0.5;
    public int MaxCrossingsPerPair { get; init; } = 40;
}

/// <summary>
/// Berechnet Korrelation, Vorlauf und Kreuzungen für alle Wertepaare.
///
/// Bewusst frei von Datenbank und HTTP: derselbe Code bedient den regulären
/// Analyselauf und den Walk-Forward, der die Struktur wiederholt aus einem
/// wachsenden Zeitfenster neu bestimmen muss, ohne etwas zu speichern.
///
/// Die Ausrichtung erfolgt <b>paarweise</b>. Eine gemeinsame Zeitachse über
/// alle Werte hinweg gibt es nicht — ein einziger Feiertag an einer Börse
/// würde den Tag für sämtliche Paare unbrauchbar machen.
/// </summary>
public static class PairAnalyzer
{
    /// <summary>
    /// <paramref name="dense"/> hält je Wert ein Array über <paramref name="grid"/>;
    /// fehlende Punkte sind <see cref="double.NaN"/>. Ausgewertet wird das
    /// Fenster [<paramref name="fromIdx"/>, <paramref name="toIdx"/>] einschließlich —
    /// dadurch kann der Aufrufer den Blick auf die Vergangenheit begrenzen.
    /// </summary>
    public static PairAnalysisResult Analyze(
        int[] assetIds,
        IReadOnlyDictionary<int, double[]> dense,
        DateTime[] grid,
        int fromIdx,
        int toIdx,
        string intervalCode,
        int windowBars,
        PairAnalyzerOptions? options = null)
    {
        var opt = options ?? new PairAnalyzerOptions();

        var stats = new List<PairStat>();
        var crossings = new List<Crossing>();

        fromIdx = Math.Max(0, fromIdx);
        toIdx = Math.Min(grid.Length - 1, toIdx);

        var span = toIdx - fromIdx + 1;
        if (span < opt.MinObservations || assetIds.Length < 2)
            return new PairAnalysisResult(stats, crossings);

        /* Über die äußere Schleife parallelisiert.

           Die Paare sind voneinander unabhängig, es gibt keinen gemeinsamen
           Zustand — bis auf die Ergebnislisten, die deshalb je Thread geführt
           und erst am Ende zusammengelegt werden. Die Arbeitspuffer sind
           ebenfalls threadlokal; geteilt würden sie sich gegenseitig
           überschreiben.

           Auf einem 24-Thread-Rechner ist das der mit Abstand größte Hebel:
           bis dahin lief die gesamte Rechnung auf einem einzigen Kern. */
        var partial = new List<(List<PairStat> Stats, List<Crossing> Crossings)>();
        var gate = new object();

        Parallel.For(
            0, assetIds.Length,
            () =>
            {
                var local = (Stats: new List<PairStat>(), Crossings: new List<Crossing>(),
                             BufA: new double[span], BufB: new double[span],
                             BufTs: new DateTime[span]);
                return local;
            },
            (i, _, local) =>
            {
                if (!dense.TryGetValue(assetIds[i], out var da)) return local;
                var a = assetIds[i];

                for (var j = i + 1; j < assetIds.Length; j++)
                {
                    if (!dense.TryGetValue(assetIds[j], out var db)) continue;
                    var b = assetIds[j];

                    // Paarweise Schnittmenge in einem Durchlauf.
                    var n = 0;
                    for (var k = fromIdx; k <= toIdx; k++)
                    {
                        var va = da[k];
                        var vb = db[k];
                        if (double.IsNaN(va) || double.IsNaN(vb)) continue;

                        local.BufA[n] = va;
                        local.BufB[n] = vb;
                        local.BufTs[n] = grid[k];
                        n++;
                    }

                    if (n < opt.MinObservations) continue;

                    var ra = LogReturns(local.BufA, n);
                    var rb = LogReturns(local.BufB, n);

                    var corr0 = Statistics.Correlation(ra, rb);

                    var lag = 0;
                    var lagCorr = corr0;

                    if (Math.Abs(corr0) >= opt.LagSearchThreshold)
                        (lag, lagCorr) = Statistics.BestLag(ra, rb, opt.MaxLagBars);

                    local.Stats.Add(new PairStat
                    {
                        AssetIdA = a,
                        AssetIdB = b,
                        IntervalCode = intervalCode,
                        WindowBars = windowBars,
                        Corr0 = Sane(corr0),
                        BestLagBars = lag,
                        BestLagCorr = Sane(lagCorr),
                        NObs = n
                    });

                    if (opt.DetectCrossings)
                        AddCrossings(local.Crossings, a, b, intervalCode,
                                     local.BufTs, local.BufA, local.BufB, n, opt);
                }

                return local;
            },
            local =>
            {
                lock (gate) partial.Add((local.Stats, local.Crossings));
            });

        foreach (var (s2, c2) in partial)
        {
            stats.AddRange(s2);
            crossings.AddRange(c2);
        }

        return new PairAnalysisResult(stats, crossings);
    }

    /// <summary>Log-Returns über die ersten <paramref name="n"/> Werte eines Puffers.</summary>
    public static double[] LogReturns(double[] closes, int n)
    {
        if (n < 2) return [];

        var r = new double[n - 1];
        for (var i = 1; i < n; i++)
        {
            var prev = closes[i - 1];
            var cur = closes[i];
            r[i - 1] = prev > 0 && cur > 0 ? Math.Log(cur / prev) : 0;
        }
        return r;
    }

    private static void AddCrossings(
        List<Crossing> into, int a, int b, string interval,
        DateTime[] ts, double[] ca, double[] cb, int n, PairAnalyzerOptions opt)
    {
        if (n < 2 || ca[0] <= 0 || cb[0] <= 0) return;

        var baseA = ca[0];
        var baseB = cb[0];
        var prev = ca[0] / baseA * 100.0 - cb[0] / baseB * 100.0;
        var found = 0;

        for (var i = 1; i < n && found < opt.MaxCrossingsPerPair; i++)
        {
            var spread = ca[i] / baseA * 100.0 - cb[i] / baseB * 100.0;

            var crossed = (prev < 0 && spread > 0) || (prev > 0 && spread < 0);
            if (crossed && Math.Abs(prev) >= opt.MinSpread && Math.Abs(spread) >= opt.MinSpread)
            {
                into.Add(new Crossing
                {
                    AssetIdA = a,
                    AssetIdB = b,
                    IntervalCode = interval,
                    TsUtc = ts[i],
                    Upward = spread > 0,
                    SpreadBefore = prev,
                    SpreadAfter = spread
                });
                found++;
            }

            // Nur fortschreiben, wenn der Abstand aussagekräftig ist — sonst
            // bliebe ein Wert nahe null hängen und löste Dauerkreuzungen aus.
            if (Math.Abs(spread) >= opt.MinSpread || crossed) prev = spread;
        }
    }

    /// <summary>NaN und Unendlich würden beim Schreiben in FLOAT-Spalten stören.</summary>
    private static double Sane(double v)
        => double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 6);

    /// <summary>
    /// Baut aus Bar-Reihen ein dichtes Gitter. Fehlende Punkte werden zu NaN,
    /// damit die paarweise Schnittmenge sie überspringen kann.
    /// </summary>
    public static (DateTime[] Grid, Dictionary<int, double[]> Dense) Densify(
        IReadOnlyDictionary<int, IReadOnlyList<PriceBar>> series)
    {
        var grid = series.Values
            .SelectMany(v => v.Select(b => b.TsUtc))
            .Distinct()
            .OrderBy(t => t)
            .ToArray();

        var index = new Dictionary<DateTime, int>(grid.Length);
        for (var i = 0; i < grid.Length; i++) index[grid[i]] = i;

        var dense = new Dictionary<int, double[]>(series.Count);
        foreach (var (id, bars) in series)
        {
            var arr = new double[grid.Length];
            Array.Fill(arr, double.NaN);

            foreach (var b in bars)
                if (index.TryGetValue(b.TsUtc, out var pos)) arr[pos] = (double)b.Close;

            dense[id] = arr;
        }

        return (grid, dense);
    }
}
