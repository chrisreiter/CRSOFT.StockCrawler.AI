namespace Ingest.Core.Analysis.Spectral;

/// <summary>Was die rollierende Prüfung über einen vermuteten Zyklus ergibt.</summary>
/// <param name="Stability">
/// Anteil der Fenster, in denen dieselbe Periode dominierte — von 0 bis 1.
/// </param>
/// <param name="MedianPeriod">Die typische dominante Periode über alle Fenster.</param>
/// <param name="Spread">
/// Streuung der gefundenen Perioden, in Bars. Klein heißt: dieselbe Schwingung
/// wurde immer wieder gefunden. Groß heißt: jedes Fenster fand etwas anderes.
/// </param>
public sealed record CycleStabilityResult(
    double Stability,
    double MedianPeriod,
    double Spread,
    double MedianProminence,
    int Windows,
    IReadOnlyList<double> PeriodsPerWindow);

/// <summary>
/// Prüft, ob ein im Spektrum gefundener Zyklus überhaupt Bestand hat.
///
/// <b>Warum das der wichtigste Schritt der ganzen Säule ist:</b> Jede
/// Kursreihe hat ein Spektrum, und jedes Spektrum hat eine stärkste Frequenz.
/// Reines Rauschen liefert eine, und sie sieht genauso überzeugend aus wie eine
/// echte. Der Unterschied zeigt sich erst, wenn man dieselbe Rechnung auf
/// verschiedenen Abschnitten wiederholt: Ein tatsächlich vorhandener Zyklus
/// taucht immer wieder an derselben Stelle auf, ein zufälliger springt.
///
/// Ein Peak, der nur über den Gesamtzeitraum erscheint und in rollierenden
/// Fenstern verschwindet, ist keine Marktstruktur — er ist die Fensterlänge.
/// </summary>
public static class CycleStability
{
    /// <summary>
    /// Zerlegt die Reihe in überlappende Abschnitte und schaut, ob jeder für
    /// sich dieselbe dominante Periode findet.
    /// </summary>
    /// <param name="tolerance">
    /// Wie weit eine Periode vom Median abweichen darf, um noch als dieselbe
    /// zu gelten — als Anteil, nicht absolut. Ein 60-Tage-Zyklus, der einmal
    /// als 55 und einmal als 65 erscheint, ist derselbe Zyklus; bei einem
    /// 8-Tage-Zyklus wären fünf Tage Abweichung eine andere Größenordnung.
    /// </param>
    public static CycleStabilityResult Analyze(
        ReadOnlySpan<double> signal,
        int windowBars = 512,
        int stepBars = 64,
        int segment = 128,
        double minPeriod = 5,
        double maxPeriod = 120,
        double tolerance = 0.2,
        double minProminence = 4)
    {
        var n = signal.Length;

        // Unter zwei Fenstern gibt es nichts zu vergleichen, und ohne Vergleich
        // ist die Aussage über Stabilität keine.
        if (n < windowBars + stepBars)
            return new CycleStabilityResult(0, 0, 0, 0, 0, []);

        /* Auch hier gilt die Kopplung an die Fensterlaenge: Eine Periode, von
           der nur drei Durchlaeufe ins Analysefenster passen, laesst sich
           ueber Fenster hinweg nicht sinnvoll vergleichen -- ihre Lage haengt
           dann staerker vom Zuschnitt des Fensters ab als von den Daten. */
        maxPeriod = Math.Min(maxPeriod, windowBars / 4.0);
        if (maxPeriod <= minPeriod)
            return new CycleStabilityResult(0, 0, 0, 0, 0, []);

        var periods = new List<double>();
        var prominences = new List<double>();

        for (var start = 0; start + windowBars <= n; start += stepBars)
        {
            var r = Spectrum.Welch(
                signal.Slice(start, windowBars),
                segment, 0.5, minPeriod, maxPeriod);

            /* Fenster ohne erkennbare Spitze zaehlen mit -- als Fehlschlag. Sie
               herauszunehmen waere das genaue Gegenteil dessen, was hier
               gemessen werden soll.

               Entscheidend ist dabei die Prominenz: In JEDEM Fenster gibt es
               eine staerkste Frequenz, auch in reinem Rauschen. Wuerde nur die
               Lage verglichen, kaeme Rauschen auf hohe Stabilitaetswerte,
               solange seine zufaellige Spitze zufaellig oft in derselben
               Gegend landet. Erst die Forderung, dass die Spitze ueberhaupt
               aus ihrer Umgebung herausragt, trennt das eine vom anderen. */
            periods.Add(r.Prominence >= minProminence ? r.DominantPeriod : 0);
            prominences.Add(r.Prominence);
        }

        if (periods.Count < 2)
            return new CycleStabilityResult(0, 0, 0, 0, periods.Count, periods);

        var valid = periods.Where(p => p > 0).ToArray();
        if (valid.Length == 0)
            return new CycleStabilityResult(0, 0, 0, 0, periods.Count, periods);

        var median = Median(valid);
        var hits = periods.Count(p => p > 0 && Math.Abs(p - median) / median <= tolerance);

        // Streuung als mittlere absolute Abweichung vom Median — robuster als
        // die Standardabweichung, wenn einzelne Fenster weit danebenliegen.
        double dev = 0;
        foreach (var p in valid) dev += Math.Abs(p - median);
        dev /= valid.Length;

        return new CycleStabilityResult(
            Math.Round((double)hits / periods.Count, 4),
            Math.Round(median, 2),
            Math.Round(dev, 2),
            Math.Round(Median(prominences.Where(v => v > 0).DefaultIfEmpty(0).ToArray()), 2),
            periods.Count,
            periods.Select(p => Math.Round(p, 2)).ToList());
    }

    private static double Median(double[] v)
    {
        if (v.Length == 0) return 0;

        var c = (double[])v.Clone();
        Array.Sort(c);

        var m = c.Length / 2;
        return c.Length % 2 == 1 ? c[m] : (c[m - 1] + c[m]) / 2;
    }
}
