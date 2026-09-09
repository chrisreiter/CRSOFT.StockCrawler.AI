namespace Ingest.Core.Analysis.Spectral;

/// <summary>Zusammenhang zweier Reihen bei einer bestimmten Periode.</summary>
/// <param name="Coherence">
/// Von 0 bis 1. Wie stark die beiden Reihen auf dieser Zeitskala miteinander
/// schwingen — unabhängig davon, wie stark sie es überhaupt tun.
/// </param>
/// <param name="LeadBars">
/// Positiv: die erste Reihe läuft der zweiten voraus. Abgeleitet aus der
/// Phasendifferenz, deshalb auf diese Periode bezogen und nicht allgemein.
/// </param>
public sealed record CoherenceBand(
    double PeriodBars,
    double Coherence,
    double PhaseDegrees,
    double LeadBars);

public sealed record CrossSpectrumResult(
    double MeanCoherence,
    CoherenceBand? Strongest,
    IReadOnlyList<CoherenceBand> Bands);

/// <summary>
/// Kreuzspektrum und Kohärenz zweier Reihen.
///
/// <b>Was das der vorhandenen Vorlaufanalyse voraushat:</b> Die erste Säule
/// bestimmt über die Kreuzkorrelation <i>einen</i> Vorlauf je Paar — eine
/// einzige Zahl für den gesamten Zusammenhang. Das unterstellt, dass zwei Werte
/// auf allen Zeitskalen gleich zusammenhängen. Tatsächlich ist es häufig
/// anders: Zwei Papiere derselben Branche laufen auf Monatssicht praktisch
/// gleich, während ihr Tagesgeschehen nichts miteinander zu tun hat. Eine
/// einzelne Korrelationszahl mittelt beides zu etwas Mittlerem, das keinen der
/// beiden Fälle beschreibt.
///
/// Die Kohärenz trennt das auf: Sie sagt je Zeitskala, wie eng der Zusammenhang
/// ist, und die Phasendifferenz sagt, wer dort vorangeht.
///
/// <b>Die übliche Falle:</b> Bei einem einzelnen Fenster ist die Kohärenz
/// rechnerisch immer eins — bei einer einzigen Beobachtung je Frequenz lässt
/// sich zwischen Zusammenhang und Zufall nicht unterscheiden. Erst die Mittelung
/// über mehrere überlappende Abschnitte macht die Größe aussagekräftig. Wer das
/// übersieht, findet überall perfekte Zusammenhänge.
/// </summary>
public static class CrossSpectrum
{
    public static CrossSpectrumResult Analyze(
        ReadOnlySpan<double> a,
        ReadOnlySpan<double> b,
        int segment = 128,
        double overlap = 0.5,
        double minPeriod = 4,
        double maxPeriod = 120,
        double minCoherence = 0.5)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n < 64) return new CrossSpectrumResult(0, null, []);

        segment = Math.Min(segment, n / 3);
        if (segment < 16) return new CrossSpectrumResult(0, null, []);

        var step = Math.Max(1, (int)(segment * (1 - Math.Clamp(overlap, 0, 0.95))));
        var win = Spectrum.Hann(segment);

        var padded = Fft.NextPow2(segment);
        var half = padded / 2;

        // Auto- und Kreuzleistungen aufsummieren.
        var paa = new double[half];
        var pbb = new double[half];
        var pabRe = new double[half];
        var pabIm = new double[half];

        var reA = new double[padded];
        var imA = new double[padded];
        var reB = new double[padded];
        var imB = new double[padded];

        var segments = 0;

        for (var start = 0; start + segment <= n; start += step)
        {
            Prepare(a.Slice(start, segment), win, reA, imA);
            Prepare(b.Slice(start, segment), win, reB, imB);

            Fft.Forward(reA, imA);
            Fft.Forward(reB, imB);

            for (var k = 0; k < half; k++)
            {
                paa[k] += reA[k] * reA[k] + imA[k] * imA[k];
                pbb[k] += reB[k] * reB[k] + imB[k] * imB[k];

                // A · konjugiert(B)
                pabRe[k] += reA[k] * reB[k] + imA[k] * imB[k];
                pabIm[k] += imA[k] * reB[k] - reA[k] * imB[k];
            }

            segments++;
        }

        // Unter drei Abschnitten ist die Kohärenz nicht aussagekräftig.
        if (segments < 3) return new CrossSpectrumResult(0, null, []);

        var bands = new List<CoherenceBand>();
        double cohSum = 0;
        var cohCount = 0;

        for (var k = 1; k < half; k++)
        {
            var freq = (double)k / padded;
            var period = 1.0 / freq;

            if (period < minPeriod || period > maxPeriod) continue;

            var denom = paa[k] * pbb[k];
            if (denom <= 0) continue;

            var coh = (pabRe[k] * pabRe[k] + pabIm[k] * pabIm[k]) / denom;
            coh = Math.Clamp(coh, 0, 1);

            cohSum += coh;
            cohCount++;

            if (coh < minCoherence) continue;

            var phase = Math.Atan2(pabIm[k], pabRe[k]);

            /* Phase in Bars umrechnen: Ein voller Umlauf entspricht einer
               Periode. Der Wert ist deshalb nur innerhalb dieser Periode
               eindeutig — ein Vorlauf von 30 Bars bei einer Periode von 20 ist
               von einem Vorlauf von 10 nicht zu unterscheiden. */
            var lead = phase / (2 * Math.PI) * period;

            bands.Add(new CoherenceBand(
                Math.Round(period, 2),
                Math.Round(coh, 4),
                Math.Round(phase * 180 / Math.PI, 1),
                Math.Round(lead, 2)));
        }

        var strongest = bands.Count > 0 ? bands.MaxBy(x => x.Coherence) : null;

        return new CrossSpectrumResult(
            cohCount > 0 ? Math.Round(cohSum / cohCount, 4) : 0,
            strongest,
            bands.OrderByDescending(x => x.Coherence).Take(12).ToList());
    }

    private static void Prepare(ReadOnlySpan<double> src, double[] win,
                                double[] re, double[] im)
    {
        Array.Clear(re);
        Array.Clear(im);

        double mean = 0;
        foreach (var v in src) mean += v;
        mean /= src.Length;

        for (var i = 0; i < src.Length; i++) re[i] = (src[i] - mean) * win[i];
    }
}
