namespace Ingest.Core.Analysis.Spectral;

/// <summary>Eine stabile Spektralspitze eines Wertes in einer Zeitepoche.</summary>
public sealed record FreqPattern(
    int AssetId, string Symbol,
    DateTime EpochFrom, DateTime EpochTo,
    double PeriodBars, double Prominence, double Stability,
    double PhaseDeg, double Amplitude);

/// <summary>Zwei Werte, die in derselben Epoche dieselbe Grundfrequenz tragen.</summary>
/// <param name="LagBars">
/// Zeitlicher Versatz zwischen beiden, aus der Phasendifferenz und der Periode.
/// Ungleich null heißt: dasselbe Muster, zeitversetzt — und <b>nur das</b> ist
/// prognostisch brauchbar. Zwei Werte, die dieselbe Schwingung gleichzeitig
/// tragen, sagen einander nichts voraus.
/// </param>
public sealed record FreqMatch(
    DateTime EpochTo,
    int AssetA, string SymbolA, double PeriodA,
    int AssetB, string SymbolB, double PeriodB,
    double PeriodDelta, double PhaseDeltaDeg, double LagBars,
    double Score);

/// <summary>
/// Zerlegt jeden Kurs in Epochen, hält die stabilen Grundfrequenzen fest und
/// sucht Übereinstimmungen zwischen den Werten.
///
/// <b>Warum in Epochen und nicht über die ganze Historie.</b> Eine Frequenz,
/// die über fünfundzwanzig Jahre gemittelt auftaucht, kann drei Jahre lang
/// stark gewesen und zweiundzwanzig Jahre abwesend sein. Der Mittelwert
/// verwischt genau das, worauf es ankommt. Epochenweise zerlegt bleibt
/// sichtbar, <i>wann</i> eine Frequenz da war — und nur Werte, die sie zur
/// <i>selben Zeit</i> tragen, sind ein Fund.
///
/// <b>Was ein Fund ist und was nicht.</b> Zwei Kurse mit ähnlicher Periode sind
/// noch nichts: Bei zweihundert Werten und einem Dutzend Perioden je Epoche
/// entstehen zwangsläufig Zufallstreffer. Gewertet wird deshalb aus drei
/// Teilen — wie nah die Perioden beieinanderliegen, wie stabil beide Spitzen
/// über ihre Teilfenster waren, und wie deutlich sie aus ihrem Untergrund
/// herausragen. Ein Fund aus zwei wackligen Spitzen bekommt eine schlechte
/// Bewertung, auch wenn die Perioden auf die Stelle genau passen.
/// </summary>
public static class FrequencyPatterns
{
    /// <summary>
    /// Zerlegt eine Kursreihe in Epochen und gibt je Epoche die stabilen
    /// Spitzen zurück.
    /// </summary>
    /// <param name="epochBars">Länge einer Epoche in Bars.</param>
    /// <param name="stepBars">Vorrücken zwischen Epochen. Überlappung ist erwünscht.</param>
    /// <param name="topK">Wie viele Spitzen je Epoche behalten werden.</param>
    public static List<FreqPattern> Extract(
        int assetId, string symbol,
        double[] closes, DateTime[] stamps,
        int epochBars = 1024, int stepBars = 256,
        int segment = 256, double minPeriod = 5, double maxPeriod = 120,
        int topK = 5, double minProminence = 4, double minStability = 0.5)
    {
        var result = new List<FreqPattern>();
        var n = closes.Length;

        if (n < epochBars + 8) return result;

        for (var start = 0; start + epochBars <= n; start += Math.Max(1, stepBars))
        {
            var slice = closes[start..(start + epochBars)];

            /* Bandpass statt roher Kurse: Ein Trend schiebt die gesamte Energie
               in die niedrigsten Frequenzen und erzeugt eine Scheinperiode in
               Länge des Fensters. Der Durchlassbereich ist bewusst weiter
               gefasst als das ausgewertete Band, damit die eigene Flanke des
               Filters nicht als Struktur erscheint. */
            var band = Spectrum.BandPass(slice, 2, maxPeriod * 3);
            if (band.Length < segment * 2) continue;

            var stab = CycleStability.Analyze(
                band, Math.Min(512, band.Length / 2), 64, segment, minPeriod, maxPeriod);

            var sp = Spectrum.Welch(band, segment, 0.5, minPeriod, maxPeriod);
            if (sp.DominantPeriod <= 0) continue;

            var hilbert = HilbertCycle.Analyze(band, 20, minPeriod, maxPeriod);

            /* Die stärksten Spitzen des Bandes, gemessen am örtlichen
               Untergrund. Benachbarte Stützstellen derselben Spitze werden
               übersprungen — sonst bestünde die Liste aus fünf Punkten
               derselben Schwingung. */
            var peaks = TopPeaks(sp, minPeriod, maxPeriod, topK);

            foreach (var (period, prom) in peaks)
            {
                if (prom < minProminence) continue;
                if (stab.Stability < minStability) continue;

                result.Add(new FreqPattern(
                    assetId, symbol,
                    stamps[start], stamps[start + epochBars - 1],
                    Math.Round(period, 2), Math.Round(prom, 2),
                    Math.Round(stab.Stability, 4),

                    /* Die Phase stammt aus dem Momentanzyklus und gilt nur,
                       wenn dessen Länge zur Spitze passt. Andernfalls
                       beschriebe sie eine andere Schwingung als die, die hier
                       festgehalten wird. */
                    hilbert.Valid && Math.Abs(hilbert.CyclePeriod - period) / period < 0.3
                        ? hilbert.Phase : double.NaN,
                    hilbert.Valid ? hilbert.Amplitude : double.NaN));
            }
        }

        return result;
    }

    /// <summary>
    /// Die stärksten Spitzen eines Spektrums, gemessen am örtlichen Untergrund
    /// und ohne Nachbarn derselben Spitze.
    /// </summary>
    private static List<(double Period, double Prominence)> TopPeaks(
        SpectrumResult sp, double minPeriod, double maxPeriod, int topK)
    {
        var found = new List<(double Period, double Prominence)>();

        if (sp.Background is not { } bg || sp.Points.Count == 0) return found;

        var ranked = new List<(int Index, double Period, double Ratio)>();

        for (var i = 0; i < sp.Points.Count && i < bg.Count; i++)
        {
            var p = sp.Points[i];
            if (p.PeriodBars < minPeriod || p.PeriodBars > maxPeriod) continue;
            if (bg[i] <= 0) continue;

            ranked.Add((i, p.PeriodBars, p.Power / bg[i]));
        }

        foreach (var cand in ranked.OrderByDescending(x => x.Ratio))
        {
            if (found.Count >= topK) break;

            /* Mindestabstand relativ zur Periode. Absolut gemessen wären zehn
               Bars bei Periode 12 ein anderer Zyklus und bei Periode 200
               dieselbe Spitze. */
            if (found.Any(f => Math.Abs(f.Period - cand.Period) / cand.Period < 0.15)) continue;

            found.Add((cand.Period, cand.Ratio));
        }

        return found;
    }

    /// <summary>
    /// Sucht Übereinstimmungen: Werte, die in derselben Epoche dieselbe
    /// Grundfrequenz tragen.
    /// </summary>
    /// <param name="tolerance">
    /// Wie weit die Perioden auseinanderliegen dürfen, als Anteil. Relativ,
    /// nicht absolut — fünf Bars sind bei Periode 10 eine andere Welt als bei
    /// Periode 200.
    /// </param>
    public static List<FreqMatch> Match(
        IReadOnlyList<FreqPattern> patterns,
        double tolerance = 0.12,
        double minScore = 0.3)
    {
        var matches = new List<FreqMatch>();

        // Nach Epoche gruppieren: Nur gleichzeitig ist eine Übereinstimmung eine.
        foreach (var epoch in patterns.GroupBy(p => p.EpochTo))
        {
            var list = epoch.OrderBy(p => p.PeriodBars).ToList();

            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];

                for (var j = i + 1; j < list.Count; j++)
                {
                    var b = list[j];

                    if (a.AssetId == b.AssetId) continue;

                    // Sortiert nach Periode: ab hier kann nichts mehr passen.
                    var delta = (b.PeriodBars - a.PeriodBars) / a.PeriodBars;
                    if (delta > tolerance) break;

                    /* Die Bewertung aus drei Teilen. Nur die Nähe der Perioden
                       zu nehmen wäre der häufigste Fehler: Zwei wacklige
                       Spitzen treffen sich zufällig genau, und der Fund sieht
                       aus wie der beste im Bestand. */
                    var closeness = 1 - delta / tolerance;
                    var stability = Math.Min(a.Stability, b.Stability);
                    var prominence = Math.Min(1, Math.Min(a.Prominence, b.Prominence) / 12.0);

                    var score = closeness * 0.3 + stability * 0.4 + prominence * 0.3;
                    if (score < minScore) continue;

                    double phaseDelta = double.NaN, lag = double.NaN;

                    if (!double.IsNaN(a.PhaseDeg) && !double.IsNaN(b.PhaseDeg))
                    {
                        var d = b.PhaseDeg - a.PhaseDeg;

                        // Auf −180 bis +180 bringen: Winkel sind zyklisch.
                        while (d > 180) d -= 360;
                        while (d < -180) d += 360;

                        phaseDelta = d;

                        var meanPeriod = (a.PeriodBars + b.PeriodBars) / 2;
                        lag = d / 360.0 * meanPeriod;
                    }

                    matches.Add(new FreqMatch(
                        a.EpochTo,
                        Math.Min(a.AssetId, b.AssetId),
                        a.AssetId < b.AssetId ? a.Symbol : b.Symbol,
                        a.AssetId < b.AssetId ? a.PeriodBars : b.PeriodBars,
                        Math.Max(a.AssetId, b.AssetId),
                        a.AssetId < b.AssetId ? b.Symbol : a.Symbol,
                        a.AssetId < b.AssetId ? b.PeriodBars : a.PeriodBars,
                        Math.Round(delta, 4),
                        double.IsNaN(phaseDelta) ? 0 : Math.Round(phaseDelta, 1),
                        double.IsNaN(lag) ? 0 : Math.Round(lag, 2),
                        Math.Round(score, 4)));
                }
            }
        }

        return matches.OrderByDescending(m => m.Score).ToList();
    }

    /// <summary>
    /// Fasst Übereinstimmungen zusammen, die über mehrere Epochen hinweg immer
    /// wieder auftreten.
    ///
    /// <b>Das ist die eigentlich interessante Auswertung.</b> Ein einzelner
    /// Fund in einer einzelnen Epoche ist bei hunderten Werten Zufall. Ein
    /// Paar, das in acht von zehn Epochen dieselbe Frequenz mit demselben
    /// Versatz teilt, ist etwas anderes — und nur solche Paare taugen als
    /// Grundlage für eine Prognose.
    /// </summary>
    public static List<(int A, string SymA, int B, string SymB, int Epochs,
                        double MeanPeriod, double MeanLag, double LagSd, double MeanScore)>
        Recurring(IReadOnlyList<FreqMatch> matches, int minEpochs = 3)
    {
        var groups = matches
            .GroupBy(m => (m.AssetA, m.AssetB))
            .Where(g => g.Select(m => m.EpochTo).Distinct().Count() >= minEpochs);

        var outp = new List<(int, string, int, string, int, double, double, double, double)>();

        foreach (var g in groups)
        {
            var list = g.ToList();
            var lags = list.Where(m => m.LagBars != 0).Select(m => m.LagBars).ToList();

            var meanLag = lags.Count > 0 ? lags.Average() : 0;

            double sd = 0;
            if (lags.Count > 1)
            {
                foreach (var l in lags) sd += (l - meanLag) * (l - meanLag);
                sd = Math.Sqrt(sd / (lags.Count - 1));
            }

            outp.Add((
                g.Key.AssetA, list[0].SymbolA,
                g.Key.AssetB, list[0].SymbolB,
                list.Select(m => m.EpochTo).Distinct().Count(),
                Math.Round(list.Average(m => (m.PeriodA + m.PeriodB) / 2), 2),
                Math.Round(meanLag, 2),
                Math.Round(sd, 2),
                Math.Round(list.Average(m => m.Score), 4)));
        }

        /* Sortiert nach Beständigkeit des Versatzes, nicht nach Bewertung: Ein
           Paar mit gleichbleibendem Versatz über viele Epochen ist mehr wert
           als eines mit hoher Einzelbewertung und springendem Versatz. */
        return outp
            .OrderByDescending(x => x.Item5)
            .ThenBy(x => x.Item8)
            .ToList();
    }
}
