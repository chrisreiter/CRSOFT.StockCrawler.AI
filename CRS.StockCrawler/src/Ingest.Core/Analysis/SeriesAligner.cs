using Ingest.Core.Models;

namespace Ingest.Core.Analysis;

/// <summary>
/// Bringt mehrere Kursreihen auf eine gemeinsame Zeitachse. Ohne das
/// vergleicht man Äpfel mit Birnen: Krypto handelt 24/7, Aktien nicht.
/// </summary>
public static class SeriesAligner
{
    /// <summary>
    /// Schnittmenge der Zeitstempel über alle Reihen. Nur Zeitpunkte, für die
    /// JEDE Reihe eine Bar hat – damit Korrelationen paarweise ehrlich sind.
    /// </summary>
    public static (DateTime[] Timestamps, Dictionary<int, decimal[]> Closes) Intersect(
        IReadOnlyDictionary<int, IReadOnlyList<PriceBar>> series)
    {
        if (series.Count == 0) return ([], []);

        HashSet<DateTime>? common = null;
        foreach (var (_, bars) in series)
        {
            var set = bars.Select(b => b.TsUtc).ToHashSet();
            if (common is null) common = set;
            else common.IntersectWith(set);
        }

        var ts = (common ?? []).OrderBy(t => t).ToArray();
        var result = new Dictionary<int, decimal[]>(series.Count);

        foreach (var (id, bars) in series)
        {
            var byTs = bars.ToDictionary(b => b.TsUtc, b => b.Close);
            var arr = new decimal[ts.Length];
            for (var i = 0; i < ts.Length; i++) arr[i] = byTs[ts[i]];
            result[id] = arr;
        }
        return (ts, result);
    }

    /// <summary>
    /// Vorwärtsfüllen auf ein vorgegebenes Zeitraster. Für die Darstellung im
    /// Chart, wo Lücken (Wochenende bei Aktien) sonst als Sprung erscheinen.
    /// </summary>
    public static decimal?[] ForwardFill(IReadOnlyList<PriceBar> bars, DateTime[] grid)
    {
        var result = new decimal?[grid.Length];
        if (bars.Count == 0) return result;

        var ordered = bars.OrderBy(b => b.TsUtc).ToArray();
        var idx = 0;
        decimal? last = null;

        for (var i = 0; i < grid.Length; i++)
        {
            while (idx < ordered.Length && ordered[idx].TsUtc <= grid[i])
            {
                last = ordered[idx].Close;
                idx++;
            }
            result[i] = last;
        }
        return result;
    }

    /// <summary>
    /// Auf 100 zum Startzeitpunkt normalisieren. Erst dadurch lassen sich eine
    /// 300-Dollar-Aktie und ein 70.000-Dollar-Bitcoin sinnvoll übereinanderlegen.
    /// </summary>
    public static double[] RebaseTo100(IReadOnlyList<decimal> closes)
    {
        var result = new double[closes.Count];
        if (closes.Count == 0) return result;

        var base0 = (double)closes[0];
        if (base0 <= 0)
        {
            // Ersten positiven Wert als Basis nehmen, sonst wäre alles NaN.
            var firstPos = closes.FirstOrDefault(c => c > 0);
            base0 = firstPos > 0 ? (double)firstPos : 1;
        }
        for (var i = 0; i < closes.Count; i++) result[i] = (double)closes[i] / base0 * 100.0;
        return result;
    }
}
