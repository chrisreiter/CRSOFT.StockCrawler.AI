namespace Ingest.Core.Analysis;

/// <summary>Ein Ausschnitt aus einer Kursreihe, auf seine Form reduziert.</summary>
/// <param name="Shape">
/// Der Verlauf, auf Mittelwert null und Streuung eins gebracht. Damit ist er
/// von Kursniveau und Schwankungsbreite unabhängig — verglichen wird die
/// Gestalt, nicht die Höhe.
/// </param>
public sealed record ShapeWindow(
    int AssetId,
    string Symbol,
    int StartIndex,
    DateTime StartUtc,
    DateTime EndUtc,
    double[] Shape,
    double ForwardReturn);

/// <summary>Zwei Ausschnitte, die einander ähneln.</summary>
/// <param name="Correlation">
/// Übereinstimmung der Form, −1 bis 1. Bei 1 decken sich die Verläufe.
/// </param>
/// <param name="LagBars">
/// Um wie viele Bars der zweite Ausschnitt verschoben am besten passt. Ist der
/// Wert von null verschieden, liegt eine Phasenverschiebung vor.
/// </param>
public sealed record ShapeMatch(
    ShapeWindow A,
    ShapeWindow B,
    double Correlation,
    int LagBars,
    double DistanceRms);

/// <summary>
/// Findet Kursausschnitte, die einander in der Form gleichen — über Werte und
/// Zeiträume hinweg.
///
/// <b>Warum das VOR dem Bildmodell steht.</b> Ein Bildmodell kann sagen, ob
/// zwei gezeichnete Kurven ähnlich aussehen. Es kann das aber nicht für
/// Millionen Paare tun: Bei 40 Werten und 4000 Tagen entstehen bei jeder
/// Fensterlänge rund 160.000 Ausschnitte und damit 13 Milliarden Paare. Selbst
/// bei zehn Millisekunden je Vergleich wären das vier Jahre Rechenzeit.
///
/// Die Vorauswahl trifft deshalb Rechnung, nicht Anschauung. Sie ist dafür das
/// bessere Werkzeug: exakt, in Sekunden, und unbestechlich gegenüber optischen
/// Täuschungen wie unterschiedlicher Achsenstreckung. Das Bildmodell bekommt
/// danach eine Handvoll Kandidaten und beantwortet die Frage, die Rechnung
/// nicht beantwortet: <i>was</i> für ein Muster das ist.
///
/// <b>Die Falle, die hier lauert.</b> Zwei zufällige Ausschnitte einer
/// Kursreihe korrelieren fast immer erheblich, weil beide einen Trend
/// enthalten. Wer die stärksten Übereinstimmungen sucht, findet deshalb
/// zuverlässig Paare, die beide steigen — und hält das für ein Muster.
/// Deswegen wird jeder Ausschnitt normiert UND es wird gegen eine
/// Zufallsverteilung derselben Ausschnitte gemessen.
/// </summary>
public static class ShapeMatcher
{
    /// <summary>
    /// Zerlegt eine Reihe in überlappende Ausschnitte fester Länge.
    /// </summary>
    /// <param name="forwardBars">
    /// Wie weit nach dem Ausschnitt die spätere Entwicklung gemessen wird. Das
    /// ist der einzige Grund, warum die ganze Suche interessant ist: Ein
    /// ähnlicher Verlauf nützt nur, wenn das, was danach kam, etwas aussagt.
    /// </param>
    public static List<ShapeWindow> Extract(
        int assetId, string symbol,
        double[] closes, DateTime[] stamps,
        int windowBars, int stepBars, int forwardBars)
    {
        var result = new List<ShapeWindow>();

        if (closes.Length < windowBars + forwardBars + 1) return result;

        for (var s = 0; s + windowBars + forwardBars <= closes.Length; s += Math.Max(1, stepBars))
        {
            var shape = Normalize(closes, s, windowBars);
            if (shape is null) continue;

            var last = closes[s + windowBars - 1];
            var later = closes[s + windowBars - 1 + forwardBars];

            var fwd = last > 0 && later > 0 ? Math.Log(later / last) : 0;

            result.Add(new ShapeWindow(
                assetId, symbol, s, stamps[s], stamps[s + windowBars - 1], shape, fwd));
        }

        return result;
    }

    /// <summary>
    /// Log-Kurse des Ausschnitts, auf Mittelwert null und Streuung eins.
    ///
    /// Logarithmiert, damit ein Anstieg von 10 auf 12 dieselbe Form ergibt wie
    /// einer von 100 auf 120. Ohne das wären teure Werte systematisch anders
    /// geformt als billige.
    /// </summary>
    private static double[]? Normalize(double[] closes, int start, int len)
    {
        var x = new double[len];

        for (var i = 0; i < len; i++)
        {
            var v = closes[start + i];
            if (v <= 0) return null;
            x[i] = Math.Log(v);
        }

        double mean = 0;
        foreach (var v in x) mean += v;
        mean /= len;

        double var2 = 0;
        for (var i = 0; i < len; i++)
        {
            x[i] -= mean;
            var2 += x[i] * x[i];
        }

        var sd = Math.Sqrt(var2 / len);

        // Ein Ausschnitt ohne jede Bewegung hat keine Form.
        if (sd < 1e-9) return null;

        for (var i = 0; i < len; i++) x[i] /= sd;
        return x;
    }

    /// <summary>
    /// Sucht zu einem Ausschnitt die ähnlichsten aus einem Bestand.
    /// </summary>
    /// <param name="maxLag">
    /// Wie weit verschoben verglichen wird. Ein Treffer bei Verschiebung
    /// ungleich null ist der interessantere Fall: Er heißt, dass dasselbe
    /// Muster zeitversetzt auftrat.
    /// </param>
    /// <param name="excludeOverlap">
    /// Ausschnitte desselben Wertes, die sich zeitlich überschneiden, werden
    /// übergangen. Sonst ist der beste Treffer immer der Ausschnitt selbst
    /// oder sein um eine Bar verschobener Nachbar — mit einer Übereinstimmung
    /// nahe eins und ohne jeden Erkenntniswert.
    /// </param>
    public static List<ShapeMatch> FindSimilar(
        ShapeWindow query,
        IReadOnlyList<ShapeWindow> pool,
        int topK = 10,
        int maxLag = 0,
        bool excludeOverlap = true)
    {
        var found = new List<ShapeMatch>();
        var len = query.Shape.Length;

        foreach (var cand in pool)
        {
            if (cand.Shape.Length != len) continue;

            if (excludeOverlap && cand.AssetId == query.AssetId
                && Math.Abs(cand.StartIndex - query.StartIndex) < len)
                continue;

            var (corr, lag) = BestLag(query.Shape, cand.Shape, maxLag);

            found.Add(new ShapeMatch(query, cand, Math.Round(corr, 5), lag,
                Math.Round(Math.Sqrt(Math.Max(0, 2 - 2 * corr)), 5)));
        }

        return found.OrderByDescending(m => m.Correlation).Take(topK).ToList();
    }

    /// <summary>
    /// Beste Übereinstimmung über alle zulässigen Verschiebungen.
    /// </summary>
    private static (double Corr, int Lag) BestLag(double[] a, double[] b, int maxLag)
    {
        var n = a.Length;

        double best = -2;
        var bestLag = 0;

        for (var lag = -maxLag; lag <= maxLag; lag++)
        {
            var from = Math.Max(0, -lag);
            var to = Math.Min(n, n - lag);
            var m = to - from;

            // Bei starker Verschiebung bleibt zu wenig Überlappung für ein Urteil.
            if (m < n / 2) continue;

            double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;

            for (var i = from; i < to; i++)
            {
                var x = a[i];
                var y = b[i + lag];

                sa += x; sb += y;
                saa += x * x; sbb += y * y;
                sab += x * y;
            }

            var cov = sab - sa * sb / m;
            var da = Math.Sqrt(Math.Max(0, saa - sa * sa / m));
            var db = Math.Sqrt(Math.Max(0, sbb - sb * sb / m));

            if (da <= 0 || db <= 0) continue;

            var c = cov / (da * db);

            if (c > best) { best = c; bestLag = lag; }
        }

        return (best <= -2 ? 0 : best, bestLag);
    }

    /// <summary>
    /// Was sagt die spätere Entwicklung der ähnlichen Ausschnitte über die des
    /// gesuchten?
    ///
    /// <b>Das ist die einzige Frage, die zählt.</b> Ähnlichkeit allein ist eine
    /// Beobachtung; erst wenn die Fortsetzungen der ähnlichen Fälle in
    /// dieselbe Richtung zeigen, entsteht daraus ein Hinweis. Zurückgegeben
    /// wird deshalb nicht nur der Mittelwert, sondern auch die Einigkeit — wie
    /// viele der Treffer sich überhaupt einig sind.
    /// </summary>
    public static (double MeanForward, double AgreementPct, int N) Consensus(
        IReadOnlyList<ShapeMatch> matches, double minCorrelation = 0.7)
    {
        var sel = matches.Where(m => m.Correlation >= minCorrelation).ToList();
        if (sel.Count == 0) return (0, 0, 0);

        var mean = sel.Average(m => m.B.ForwardReturn);

        var same = sel.Count(m => Math.Sign(m.B.ForwardReturn) == Math.Sign(mean));

        return (Math.Round(mean, 6), Math.Round(100.0 * same / sel.Count, 1), sel.Count);
    }
}
