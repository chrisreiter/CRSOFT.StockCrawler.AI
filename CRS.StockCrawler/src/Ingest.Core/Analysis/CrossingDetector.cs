using Ingest.Core.Models;

namespace Ingest.Core.Analysis;

/// <summary>
/// Findet Kreuzungen zweier auf 100 normalisierter Kurven: Momente, in denen
/// die relative Rangfolge zweier Papiere kippt.
/// </summary>
public static class CrossingDetector
{
    /// <summary>
    /// <paramref name="minSpread"/> filtert Zittern um die Nulllinie heraus –
    /// ohne das erzeugt jede Mikrobewegung Dutzende Scheinkreuzungen.
    /// </summary>
    public static List<Crossing> Detect(
        int assetIdA, int assetIdB, string intervalCode,
        DateTime[] timestamps, IReadOnlyList<decimal> closesA, IReadOnlyList<decimal> closesB,
        double minSpread = 0.5)
    {
        var result = new List<Crossing>();
        var n = Math.Min(timestamps.Length, Math.Min(closesA.Count, closesB.Count));
        if (n < 2) return result;

        var a = SeriesAligner.RebaseTo100(closesA);
        var b = SeriesAligner.RebaseTo100(closesB);

        var prevSpread = a[0] - b[0];

        for (var i = 1; i < n; i++)
        {
            var spread = a[i] - b[i];

            // Vorzeichenwechsel = Kreuzung. Beide Seiten müssen deutlich genug
            // vom Nullpunkt weg sein, damit Rauschen nicht als Signal zählt.
            var crossed = prevSpread < 0 && spread > 0 || prevSpread > 0 && spread < 0;
            if (crossed && Math.Abs(prevSpread) >= minSpread && Math.Abs(spread) >= minSpread)
            {
                result.Add(new Crossing
                {
                    AssetIdA = assetIdA,
                    AssetIdB = assetIdB,
                    IntervalCode = intervalCode,
                    TsUtc = timestamps[i],
                    Upward = spread > 0,
                    SpreadBefore = prevSpread,
                    SpreadAfter = spread
                });
            }

            // Nur fortschreiben, wenn der Abstand aussagekräftig ist. Sonst
            // bliebe ein Wert nahe 0 stehen und löste Dauerkreuzungen aus.
            if (Math.Abs(spread) >= minSpread || crossed) prevSpread = spread;
        }
        return result;
    }

    /// <summary>
    /// Klassischer Moving-Average-Crossover innerhalb EINER Reihe
    /// (schnelle MA kreuzt langsame) – gängiges Trendwechsel-Signal.
    /// </summary>
    public static List<(DateTime Ts, bool GoldenCross)> MovingAverageCross(
        DateTime[] timestamps, IReadOnlyList<decimal> closes, int fast = 20, int slow = 50)
    {
        var result = new List<(DateTime, bool)>();
        var n = Math.Min(timestamps.Length, closes.Count);
        if (n <= slow) return result;

        double SmaAt(int end, int len)
        {
            double s = 0;
            for (var i = end - len + 1; i <= end; i++) s += (double)closes[i];
            return s / len;
        }

        var prevDiff = SmaAt(slow, fast) - SmaAt(slow, slow);
        for (var i = slow + 1; i < n; i++)
        {
            var diff = SmaAt(i, fast) - SmaAt(i, slow);
            if (prevDiff <= 0 && diff > 0) result.Add((timestamps[i], true));
            else if (prevDiff >= 0 && diff < 0) result.Add((timestamps[i], false));
            prevDiff = diff;
        }
        return result;
    }
}
