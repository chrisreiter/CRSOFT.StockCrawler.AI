using Ingest.Core.Enums;

namespace Ingest.Core.Analysis;

/// <summary>Ein Wert im Merkmalsaufbau: dichte Reihen über dem gemeinsamen Zeitraster.</summary>
public sealed class FeatureAsset
{
    public required int AssetId { get; init; }
    public required string Symbol { get; init; }
    public required AssetClass AssetClass { get; init; }

    /// <summary>Schlusskurse über dem Raster; fehlende Punkte sind NaN.</summary>
    public required double[] Close { get; init; }

    /// <summary>Geldumsatz über dem Raster; fehlende Punkte sind NaN.</summary>
    public required double[] Flow { get; init; }

    /// <summary>Nachbarn aus der Korrelationsanalyse: (assetId, Korrelation).</summary>
    public (int AssetId, double Corr)[] Neighbours { get; set; } = [];

    /// <summary>Frühindikatoren: (assetId, Vorlauf in Bars, Korrelation).</summary>
    public (int AssetId, int LagBars, double Corr)[] Leaders { get; set; } = [];
}

/// <summary>
/// Baut die Querschnitts-Merkmalsmatrix — Stufe 2.
///
/// Der entscheidende Unterschied zur bisherigen Prognose: zu jedem Zeitpunkt
/// entsteht eine Zeile je Wert, die neben den eigenen Kursmerkmalen auch den
/// Zustand des Gesamtmarkts, der eigenen Anlageklasse und der verwandten Werte
/// enthält. Erst dadurch kann ein Modell überhaupt lernen, dass eine Bewegung
/// bei einem Wert später bei einem anderen ankommt.
///
/// Bewusst frei von Datenbank und HTTP, damit derselbe Code den Trainingsexport
/// und die Merkmale zur Laufzeit erzeugt.
/// </summary>
public static class FeatureMatrix
{
    /// <summary>
    /// Marktzustand zu einem Zeitpunkt. Einmal je Raster-Index berechnet und
    /// von allen Werten geteilt.
    /// </summary>
    public readonly record struct MarketState(
        double MeanR1, double MeanR5, double Dispersion, double Breadth,
        double SessionCoverage, double TotalFlow);

    /// <summary>
    /// Berechnet den Marktzustand je Zeitpunkt sowie die Kennzahlen je
    /// Anlageklasse.
    /// </summary>
    public static (MarketState[] Market, Dictionary<AssetClass, double[]> ClassR1,
                   Dictionary<AssetClass, double[]> ClassShare)
        ComputeMarket(IReadOnlyList<FeatureAsset> assets, int gridLength)
    {
        var market = new MarketState[gridLength];

        var classes = assets.Select(a => a.AssetClass).Distinct().ToArray();
        var classR1 = classes.ToDictionary(c => c, _ => new double[gridLength]);
        var classShare = classes.ToDictionary(c => c, _ => new double[gridLength]);
        var classFlow = classes.ToDictionary(c => c, _ => 0.0);

        for (var t = 0; t < gridLength; t++)
        {
            double sum = 0, sum5 = 0, totalFlow = 0;
            int n = 0, up = 0, present = 0;

            foreach (var key in classes)
            {
                classR1[key][t] = 0;
                classShare[key][t] = 0;
                classFlow[key] = 0;
            }

            var classCount = classes.ToDictionary(c => c, _ => 0);
            var classSum = classes.ToDictionary(c => c, _ => 0.0);

            foreach (var a in assets)
            {
                if (double.IsNaN(a.Close[t])) continue;
                present++;

                var r1 = Return(a.Close, t, 1);
                if (!double.IsNaN(r1))
                {
                    sum += r1;
                    n++;
                    if (r1 > 0) up++;

                    classSum[a.AssetClass] += r1;
                    classCount[a.AssetClass]++;
                }

                var r5 = Return(a.Close, t, 5);
                if (!double.IsNaN(r5)) sum5 += r5;

                if (!double.IsNaN(a.Flow[t]))
                {
                    totalFlow += a.Flow[t];
                    classFlow[a.AssetClass] += a.Flow[t];
                }
            }

            var mean = n > 0 ? sum / n : 0;

            // Streuung der Einzelrenditen: hoher Wert heißt, die Werte laufen
            // auseinander — ein anderes Marktregime als ein Gleichlauf.
            double disp = 0;
            if (n > 1)
            {
                double acc = 0;
                foreach (var a in assets)
                {
                    if (double.IsNaN(a.Close[t])) continue;
                    var r1 = Return(a.Close, t, 1);
                    if (double.IsNaN(r1)) continue;
                    var d = r1 - mean;
                    acc += d * d;
                }
                disp = Math.Sqrt(acc / (n - 1));
            }

            foreach (var key in classes)
            {
                classR1[key][t] = classCount[key] > 0 ? classSum[key] / classCount[key] : 0;
                classShare[key][t] = totalFlow > 0 ? classFlow[key] / totalFlow : 0;
            }

            market[t] = new MarketState(
                mean,
                n > 0 ? sum5 / n : 0,
                disp,
                n > 0 ? (double)up / n : 0,

                /* Anteil der Werte, die zu diesem Zeitpunkt überhaupt handeln.
                   Am Wochenende handelt nur Krypto — ohne diese Kennzahl
                   sähe das Modell dort einen „Markt“, der zu 100 % aus Krypto
                   besteht, und könnte den Sonderfall nicht erkennen. */
                assets.Count > 0 ? (double)present / assets.Count : 0,
                totalFlow);
        }

        return (market, classR1, classShare);
    }

    /// <summary>
    /// Füllt die Merkmale eines Werts zum Zeitpunkt <paramref name="t"/>.
    /// Liefert false, wenn die Historie nicht reicht.
    /// </summary>
    public static bool TryBuildRow(
        FeatureAsset asset,
        IReadOnlyDictionary<int, FeatureAsset> byId,
        MarketState[] market,
        IReadOnlyDictionary<AssetClass, double[]> classR1,
        IReadOnlyDictionary<AssetClass, double[]> classShare,
        int t,
        double[] into)
    {
        if (t < FeatureSet.MinHistoryBars) return false;
        if (double.IsNaN(asset.Close[t]) || asset.Close[t] <= 0) return false;

        Array.Clear(into, 0, into.Length);

        // --- eigene Kursdynamik ---
        into[FeatureSet.R1] = Safe(Return(asset.Close, t, 1));
        into[FeatureSet.R2] = Safe(Return(asset.Close, t, 2));
        into[FeatureSet.R3] = Safe(Return(asset.Close, t, 3));
        into[FeatureSet.R5] = Safe(Return(asset.Close, t, 5));
        into[FeatureSet.R10] = Safe(Return(asset.Close, t, 10));
        into[FeatureSet.R20] = Safe(Return(asset.Close, t, 20));

        into[FeatureSet.Vol20] = Volatility(asset.Close, t, 20);
        into[FeatureSet.Vol60] = Volatility(asset.Close, t, 60);

        into[FeatureSet.Ma20Dist] = MaDistance(asset.Close, t, 20);
        into[FeatureSet.Ma50Dist] = MaDistance(asset.Close, t, 50);
        into[FeatureSet.Z50] = ZScore(asset.Close, t, 50);

        // --- eigener Kapitalfluss ---
        into[FeatureSet.FlowZ20] = FlowZ(asset.Flow, t, 20);

        var flow = double.IsNaN(asset.Flow[t]) ? 0 : asset.Flow[t];
        var total = market[t].TotalFlow;

        // In Basispunkten, sonst wären die Werte durchweg winzig.
        into[FeatureSet.FlowShareBp] = total > 0 ? flow / total * 10_000 : 0;
        into[FeatureSet.FlowRot20] = FlowRotation(asset.Flow, market, t, 20);

        // --- Marktzustand ---
        var m = market[t];
        into[FeatureSet.MktR1] = Safe(m.MeanR1);
        into[FeatureSet.MktR5] = Safe(m.MeanR5);
        into[FeatureSet.MktDisp] = Safe(m.Dispersion);
        into[FeatureSet.MktBreadth] = Safe(m.Breadth);
        into[FeatureSet.SessionCov] = Safe(m.SessionCoverage);

        // --- eigene Anlageklasse ---
        into[FeatureSet.ClsR1] = classR1.TryGetValue(asset.AssetClass, out var cr) ? Safe(cr[t]) : 0;

        var share = classShare.TryGetValue(asset.AssetClass, out var cs) ? cs[t] : 0;
        into[FeatureSet.ClsShare] = Safe(share);

        var baseShare = Mean(cs, t, 20);
        into[FeatureSet.ClsRot] = baseShare > 0 ? Safe(share / baseShare - 1.0) : 0;

        // --- Umfeld ---
        (into[FeatureSet.NbR1], into[FeatureSet.NbR5]) = NeighbourReturns(asset, byId, t);
        into[FeatureSet.LeadSignal] = LeadSignal(asset, byId, t);
        into[FeatureSet.RelStr20] = Safe(into[FeatureSet.R20] - Return20Market(market, t));

        // --- statische Zugehörigkeit ---
        into[FeatureSet.IsStock] = asset.AssetClass == AssetClass.Stock ? 1 : 0;
        into[FeatureSet.IsEtf] = asset.AssetClass == AssetClass.Etf ? 1 : 0;
        into[FeatureSet.IsCrypto] = asset.AssetClass == AssetClass.Crypto ? 1 : 0;

        return true;
    }

    /// <summary>
    /// Zielgröße: Log-Rendite über <paramref name="horizonBars"/> <b>eigene</b>
    /// Bars nach vorn. NaN, wenn so weit keine Daten mehr vorliegen.
    ///
    /// Gezählt werden die Bars des Werts, nicht Positionen im gemeinsamen
    /// Zeitraster. Andernfalls hinge die Zielgröße davon ab, ob zufällig ein
    /// Wochenende dazwischenliegt: „3 Rasterschritte" landen bei einer Aktie
    /// oft auf einem Samstag und ergäben kein Ziel, „7 Schritte" dagegen wieder
    /// auf einem Handelstag. Krypto (durchgehender Handel) und Aktien hätten
    /// dann unterschiedlich definierte Ziele — und das Modell lernte den
    /// Unterschied statt des Markts.
    /// </summary>
    public static double ForwardReturn(double[] close, int t, int horizonBars)
    {
        var a = close[t];
        if (double.IsNaN(a) || a <= 0) return double.NaN;

        var seen = 0;
        for (var k = t + 1; k < close.Length; k++)
        {
            var b = close[k];
            if (double.IsNaN(b) || b <= 0) continue;

            if (++seen >= horizonBars) return Math.Log(b / a);
        }

        return double.NaN;
    }

    // ------------------------------------------------------------------ Bausteine

    /// <summary>
    /// Log-Rendite über <paramref name="bars"/> Bars zurück. Lücken werden
    /// übersprungen: der letzte bekannte Kurs davor zählt.
    /// </summary>
    public static double Return(double[] close, int t, int bars)
    {
        var cur = close[t];
        if (double.IsNaN(cur) || cur <= 0) return double.NaN;

        var seen = 0;
        for (var k = t - 1; k >= 0; k--)
        {
            if (double.IsNaN(close[k]) || close[k] <= 0) continue;
            seen++;
            if (seen >= bars) return Math.Log(cur / close[k]);
        }
        return double.NaN;
    }

    private static double Volatility(double[] close, int t, int window)
    {
        Span<double> buf = stackalloc double[Math.Min(window, 128)];
        var n = 0;
        var prev = double.NaN;

        for (var k = t; k >= 0 && n < buf.Length; k--)
        {
            var c = close[k];
            if (double.IsNaN(c) || c <= 0) continue;

            if (!double.IsNaN(prev)) buf[n++] = Math.Log(prev / c);
            prev = c;
        }

        if (n < 5) return 0;
        return Safe(Statistics.StdDev(buf[..n]));
    }

    private static double MaDistance(double[] close, int t, int window)
    {
        var mean = MeanClose(close, t, window);
        if (mean <= 0 || double.IsNaN(close[t])) return 0;
        return Safe(close[t] / mean - 1.0);
    }

    private static double ZScore(double[] close, int t, int window)
    {
        Span<double> buf = stackalloc double[Math.Min(window, 128)];
        var n = 0;

        for (var k = t; k >= 0 && n < buf.Length; k--)
        {
            var c = close[k];
            if (double.IsNaN(c) || c <= 0) continue;
            buf[n++] = Math.Log(c);
        }

        if (n < 10) return 0;

        var slice = buf[..n];
        var mean = Statistics.Mean(slice);
        var sd = Statistics.StdDev(slice, mean);

        return sd <= double.Epsilon ? 0 : Safe((Math.Log(close[t]) - mean) / sd);
    }

    private static double FlowZ(double[] flow, int t, int window)
    {
        if (double.IsNaN(flow[t])) return 0;

        Span<double> buf = stackalloc double[Math.Min(window, 128)];
        var n = 0;

        for (var k = t - 1; k >= 0 && n < buf.Length; k--)
        {
            if (double.IsNaN(flow[k])) continue;
            buf[n++] = flow[k];
        }

        if (n < 5) return 0;
        return Safe(Math.Clamp(FlowMetrics.VolumeZScore(buf[..n], flow[t]), -10, 10));
    }

    private static double FlowRotation(double[] flow, MarketState[] market, int t, int window)
    {
        if (double.IsNaN(flow[t]) || market[t].TotalFlow <= 0) return 0;

        var current = flow[t] / market[t].TotalFlow;

        double sum = 0;
        var n = 0;
        for (var k = t - 1; k >= 0 && n < window; k--)
        {
            if (double.IsNaN(flow[k]) || market[k].TotalFlow <= 0) continue;
            sum += flow[k] / market[k].TotalFlow;
            n++;
        }

        if (n < 5) return 0;
        var baseline = sum / n;

        return baseline <= 0 ? 0 : Safe(Math.Clamp(current / baseline - 1.0, -10, 10));
    }

    /// <summary>
    /// Korrelationsgewichtete Rendite der verwandten Werte. Das ist der Kanal,
    /// über den eine Bewegung anderswo im Markt überhaupt sichtbar wird.
    /// </summary>
    private static (double R1, double R5) NeighbourReturns(
        FeatureAsset asset, IReadOnlyDictionary<int, FeatureAsset> byId, int t)
    {
        double n1 = 0, n5 = 0, w = 0;

        foreach (var (id, corr) in asset.Neighbours)
        {
            if (!byId.TryGetValue(id, out var nb)) continue;

            var r1 = Return(nb.Close, t, 1);
            var r5 = Return(nb.Close, t, 5);
            if (double.IsNaN(r1)) continue;

            var weight = Math.Abs(corr);

            // Vorzeichen mitnehmen: ein gegenläufiger Nachbar trägt invers bei.
            var sign = corr >= 0 ? 1.0 : -1.0;

            n1 += weight * sign * r1;
            if (!double.IsNaN(r5)) n5 += weight * sign * r5;
            w += weight;
        }

        return w <= double.Epsilon ? (0, 0) : (Safe(n1 / w), Safe(n5 / w));
    }

    /// <summary>
    /// Signal der Frühindikatoren: deren Bewegung über die Dauer ihres
    /// Vorlaufs. Anders als beim bisherigen leadlag-Teilmodell gibt es hier
    /// keine Bedingung, die das Signal meist verstummen lässt — das Modell
    /// entscheidet selbst, ob es damit etwas anfangen kann.
    /// </summary>
    private static double LeadSignal(
        FeatureAsset asset, IReadOnlyDictionary<int, FeatureAsset> byId, int t)
    {
        double acc = 0, w = 0;

        foreach (var (id, lag, corr) in asset.Leaders)
        {
            if (!byId.TryGetValue(id, out var lead)) continue;

            var r = Return(lead.Close, t, Math.Max(1, lag));
            if (double.IsNaN(r)) continue;

            var weight = Math.Abs(corr);
            acc += weight * (corr >= 0 ? 1 : -1) * r;
            w += weight;
        }

        return w <= double.Epsilon ? 0 : Safe(acc / w);
    }

    private static double Return20Market(MarketState[] market, int t)
    {
        // Marktrendite über 20 Bars als Summe der Einzelschritte.
        double acc = 0;
        var n = 0;
        for (var k = t; k > 0 && n < 20; k--, n++) acc += market[k].MeanR1;
        return Safe(acc);
    }

    private static double MeanClose(double[] close, int t, int window)
    {
        double sum = 0;
        var n = 0;
        for (var k = t; k >= 0 && n < window; k--)
        {
            if (double.IsNaN(close[k]) || close[k] <= 0) continue;
            sum += close[k];
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    private static double Mean(double[] series, int t, int window)
    {
        double sum = 0;
        var n = 0;
        for (var k = t; k >= 0 && n < window; k--, n++) sum += series[k];
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>NaN und Unendlich hätten im Modell nichts zu suchen.</summary>
    private static double Safe(double v)
        => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;
}
