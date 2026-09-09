namespace Ingest.Core.Analysis;

/// <summary>Reine Statistikhelfer – keine Abhängigkeiten, damit gut testbar.</summary>
public static class Statistics
{
    public static double Mean(ReadOnlySpan<double> x)
    {
        if (x.Length == 0) return 0;
        double s = 0;
        foreach (var v in x) s += v;
        return s / x.Length;
    }

    public static double StdDev(ReadOnlySpan<double> x, double? mean = null)
    {
        if (x.Length < 2) return 0;
        var m = mean ?? Mean(x);
        double s = 0;
        foreach (var v in x) { var d = v - m; s += d * d; }
        return Math.Sqrt(s / (x.Length - 1));
    }

    /// <summary>Pearson-Korrelation. 0, wenn eine Reihe konstant ist.</summary>
    public static double Correlation(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n < 3) return 0;
        a = a[..n]; b = b[..n];

        double ma = Mean(a), mb = Mean(b);
        double num = 0, da = 0, db = 0;
        for (var i = 0; i < n; i++)
        {
            double x = a[i] - ma, y = b[i] - mb;
            num += x * y; da += x * x; db += y * y;
        }
        var den = Math.Sqrt(da * db);
        return den <= double.Epsilon ? 0 : num / den;
    }

    /// <summary>
    /// Kreuzkorrelation über Lags. Ergebnis[k] gehört zu Lag <c>k - maxLag</c>.
    /// Ein positiver Lag bedeutet: <paramref name="a"/> läuft <paramref name="b"/>
    /// voraus, also korreliert a[t] mit b[t + lag].
    /// </summary>
    public static double[] CrossCorrelation(double[] a, double[] b, int maxLag)
    {
        var n = Math.Min(a.Length, b.Length);
        var result = new double[2 * maxLag + 1];
        if (n < maxLag + 3) return result;

        for (var lag = -maxLag; lag <= maxLag; lag++)
        {
            // a[t] gegen b[t+lag] – nur der überlappende Bereich zählt.
            int aStart = Math.Max(0, -lag);
            int bStart = Math.Max(0, lag);
            int len = n - Math.Abs(lag);
            if (len < 3) { result[lag + maxLag] = 0; continue; }

            result[lag + maxLag] = Correlation(
                a.AsSpan(aStart, len),
                b.AsSpan(bStart, len));
        }
        return result;
    }

    /// <summary>
    /// Lag mit der betragsmäßig stärksten Korrelation. Lag 0 wird bewusst
    /// mitbetrachtet, damit "läuft gleichzeitig" nicht als Vorlauf erscheint.
    /// </summary>
    public static (int Lag, double Corr) BestLag(double[] a, double[] b, int maxLag)
    {
        var xc = CrossCorrelation(a, b, maxLag);
        int best = 0; double bestAbs = -1, bestVal = 0;
        for (var k = 0; k < xc.Length; k++)
        {
            var abs = Math.Abs(xc[k]);
            if (abs > bestAbs) { bestAbs = abs; best = k - maxLag; bestVal = xc[k]; }
        }
        return (best, bestVal);
    }

    /// <summary>OLS-Steigung von y auf x (ohne Achsenabschnitt-Rückgabe).</summary>
    public static double Beta(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        var n = Math.Min(x.Length, y.Length);
        if (n < 3) return 0;
        double mx = Mean(x[..n]), my = Mean(y[..n]);
        double cov = 0, varx = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = x[i] - mx;
            cov += dx * (y[i] - my);
            varx += dx * dx;
        }
        return varx <= double.Epsilon ? 0 : cov / varx;
    }

    /// <summary>Log-Returns aus einer Kursreihe. Länge = n-1.</summary>
    public static double[] LogReturns(IReadOnlyList<decimal> closes)
    {
        if (closes.Count < 2) return [];
        var r = new double[closes.Count - 1];
        for (var i = 1; i < closes.Count; i++)
        {
            var prev = (double)closes[i - 1];
            var cur = (double)closes[i];
            r[i - 1] = prev > 0 && cur > 0 ? Math.Log(cur / prev) : 0;
        }
        return r;
    }

    /// <summary>Exponentiell gewichteter Mittelwert (jüngste Werte zählen mehr).</summary>
    public static double Ewma(ReadOnlySpan<double> x, double alpha)
    {
        if (x.Length == 0) return 0;
        double acc = x[0];
        for (var i = 1; i < x.Length; i++) acc = alpha * x[i] + (1 - alpha) * acc;
        return acc;
    }
}
