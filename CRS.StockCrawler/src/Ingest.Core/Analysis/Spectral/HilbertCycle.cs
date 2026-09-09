namespace Ingest.Core.Analysis.Spectral;

/// <summary>Momentanzustand der Schwingung am rechten Rand der Reihe.</summary>
/// <param name="Phase">Lage im Zyklus in Grad, 0 bis 360.</param>
/// <param name="CyclePeriod">
/// Aus dem Phasenfortschritt geschätzte Zykluslänge in Bars.
/// </param>
/// <param name="PhaseQuality">
/// Wie ruhig die Längenschätzung zuletzt lag, von 0 bis 1. Bei einer echten
/// Schwingung ändert sie sich kaum; bei Rauschen springt sie von Bar zu Bar.
/// Ohne diese Zahl wäre die Zykluslänge eine Zahl ohne Aussage.
/// </param>
public sealed record HilbertState(
    double Phase,
    double Amplitude,
    double CyclePeriod,
    double PhaseQuality,
    bool Valid);

/// <summary>
/// Momentanphase und Momentanzykluslänge über einen kausalen
/// Hilbert-Diskriminator.
///
/// <b>Der Gedanke:</b> Eine Fourier-Zerlegung sagt, welche Frequenzen im
/// gesamten Fenster stecken. Für eine Prognose interessiert etwas anderes: wo
/// die Schwingung <i>gerade jetzt</i> steht und wie schnell sie fortschreitet.
/// Dafür braucht es zur Reihe eine um 90 Grad verschobene Fassung; aus beiden
/// ergeben sich zu jedem Zeitpunkt Amplitude und Phasenwinkel, und aus dem
/// Phasenzuwachs je Bar die aktuelle Zykluslänge.
///
/// <b>Warum nicht über die FFT:</b> Der naheliegende Weg — negative Frequenzen
/// streichen, zurücktransformieren — war der erste Versuch und funktioniert
/// nicht. Die Fourier-Transformation setzt das Fenster periodisch fort, und an
/// der Nahtstelle entsteht ein Sprung, dessen Nachwirkungen weit ins Fenster
/// hineinreichen. Betroffen ist ausgerechnet der rechte Rand — also genau die
/// Gegenwart, um die es geht. Bei einem sauberen Sinus mit Periode 36 lag die
/// gemessene Länge zwar richtig, die Güte der Phase aber bei null: Der
/// Phasenzuwachs schwankte stärker als sein eigener Mittelwert.
///
/// Der hier verwendete Weg umgeht das Problem, statt es zu dämpfen. Die
/// Quadraturkomponente entsteht aus kurzen Filtern, die ausschließlich auf
/// zurückliegende Werte greifen. Es gibt keine Fortsetzung, keine Naht und
/// keinen Randbereich, der verworfen werden müsste — und nebenbei ist damit
/// ausgeschlossen, dass Zukunftswissen in die Rechnung sickert. Der Aufbau
/// stammt von John Ehlers, der ihn als Homodyn-Diskriminator in die technische
/// Analyse eingeführt hat.
///
/// <b>Wo die Grenze verläuft:</b> Die Phase fortzuschreiben ist zulässig — sie
/// wächst, solange die Schwingung anhält. Die Amplitude fortzuschreiben ist es
/// nicht: Dass eine Schwingung gerade stark ist, sagt nichts darüber, ob sie es
/// bleibt. In der Fortschreibung wird sie deshalb gedämpft, und zwar umso
/// stärker, je weiter man hinausgeht.
/// </summary>
public static class HilbertCycle
{
    // Koeffizienten des Quadraturfilters. Sie stammen aus Ehlers' Entwurf und
    // bilden über den hier interessanten Längenbereich eine Verschiebung um
    // 90 Grad nach, mit einer Verzögerung von drei Bars.
    private const double C0 = 0.0962;
    private const double C1 = 0.5769;

    /// <summary>
    /// Bestimmt Amplitude, Phase und Zykluslänge am Ende der Reihe.
    /// </summary>
    /// <param name="lookback">
    /// Über wie viele Bars die Ruhe der Längenschätzung beurteilt wird.
    /// </param>
    public static HilbertState Analyze(
        ReadOnlySpan<double> signal,
        int lookback = 20,
        double minPeriod = 6,
        double maxPeriod = 120)
    {
        var n = signal.Length;
        if (n < 64) return new HilbertState(0, 0, 0, 0, false);

        var smooth = new double[n];
        var detrend = new double[n];
        var q1 = new double[n];
        var i1 = new double[n];
        var period = new double[n];
        var smoothPeriod = new double[n];

        double i2p = 0, q2p = 0, rep = 0, imp = 0;

        // Vor dem ersten belastbaren Wert dient eine mittlere Länge als Anlauf.
        var seed = Math.Clamp((minPeriod + maxPeriod) / 2, minPeriod, maxPeriod);
        for (var i = 0; i < n; i++) period[i] = seed;

        for (var i = 3; i < n; i++)
            smooth[i] = (4 * signal[i] + 3 * signal[i - 1] + 2 * signal[i - 2] + signal[i - 3]) / 10;

        for (var i = 6; i < n; i++)
        {
            /* Der Faktor gleicht aus, dass die Filter über den Längenbereich
               nicht gleich stark durchlassen: Bei langen Zyklen dämpfen sie
               mehr, bei kurzen weniger. Ohne die Anpassung hinge die geschätzte
               Amplitude an der Zykluslänge statt am Kurs. */
            var adj = 0.075 * period[i - 1] + 0.54;

            detrend[i] = Quad(smooth, i) * adj;
            q1[i] = Quad(detrend, i) * adj;
            i1[i] = detrend[i - 3];

            var jI = Quad(i1, i) * adj;
            var jQ = Quad(q1, i) * adj;

            // Zeigeraddition: verschiebt die Komponenten auf eine gemeinsame Lage.
            var i2 = i1[i] - jQ;
            var q2 = q1[i] + jI;

            i2 = 0.2 * i2 + 0.8 * i2p;
            q2 = 0.2 * q2 + 0.8 * q2p;

            /* Homodyn-Diskriminator: das Produkt aus aktuellem und um eine Bar
               verzögertem Zeiger. Sein Winkel IST der Phasenzuwachs je Bar —
               und damit unmittelbar die Zykluslänge, ohne dass Phasen entrollt
               oder Sprünge behandelt werden müssten. */
            var re = i2 * i2p + q2 * q2p;
            var im = i2 * q2p - q2 * i2p;

            re = 0.2 * re + 0.8 * rep;
            im = 0.2 * im + 0.8 * imp;

            i2p = i2; q2p = q2; rep = re; imp = im;

            var p = period[i - 1];

            if (im != 0 && re != 0)
            {
                var delta = Math.Abs(Math.Atan2(im, re));
                if (delta > 1e-9) p = 2 * Math.PI / delta;
            }

            /* Sprungbegrenzung: Die Zykluslänge eines Marktes ändert sich nicht
               von einer Bar zur nächsten um ein Vielfaches. Was so aussieht,
               ist ein Aussetzer der Schätzung, und ihn ungebremst zu übernehmen
               würde die Schätzung für die nächsten Bars mitreißen. */
            p = Math.Clamp(p, 0.67 * period[i - 1], 1.5 * period[i - 1]);
            p = Math.Clamp(p, minPeriod, maxPeriod);

            period[i] = 0.2 * p + 0.8 * period[i - 1];
            smoothPeriod[i] = 0.33 * period[i] + 0.67 * (i > 6 ? smoothPeriod[i - 1] : period[i]);
        }

        var last = n - 1;
        var dc = smoothPeriod[last];

        if (dc < minPeriod || dc > maxPeriod || double.IsNaN(dc))
            return new HilbertState(0, 0, 0, 0, false);

        /* Güte: wie ruhig die Längenschätzung zuletzt lag. Der Diskriminator
           liefert bei Rauschen keine wilden Ausschläge — die Glättung fängt sie
           ab —, aber er wandert. Ein wandernder Wert ist kein Zyklus. */
        var from = Math.Max(7, last - lookback);
        double mean = 0;
        var cnt = 0;

        for (var i = from; i <= last; i++) { mean += smoothPeriod[i]; cnt++; }
        mean /= cnt;

        double dev = 0;
        for (var i = from; i <= last; i++) dev += Math.Abs(smoothPeriod[i] - mean);
        dev /= cnt;

        var quality = mean > 0 ? Math.Clamp(1 - dev / mean * 4, 0, 1) : 0;

        /* Phase: die geglättete Reihe über genau einen Zyklus mit Sinus und
           Kosinus verglichen. Das ist die diskrete Fassung einer Projektion auf
           die Grundschwingung — und sie sagt, an welcher Stelle des Umlaufs die
           Reihe im Moment steht. */
        var span = Math.Min((int)Math.Round(dc), last - 6);
        if (span < 4) return new HilbertState(0, 0, Math.Round(dc, 2), Math.Round(quality, 4), false);

        double realPart = 0, imagPart = 0, energy = 0;

        for (var k = 0; k < span; k++)
        {
            var w = 2 * Math.PI * k / dc;
            var v = smooth[last - k];

            realPart += Math.Sin(w) * v;
            imagPart += Math.Cos(w) * v;
            energy += v * v;
        }

        var deg = Math.Atan2(imagPart, realPart) * 180 / Math.PI;
        if (deg < 0) deg += 360;

        // Amplitude als Scheitelwert: der quadratische Mittelwert mal Wurzel zwei.
        var amp = span > 0 ? Math.Sqrt(energy / span) * Math.Sqrt(2) : 0;

        return new HilbertState(
            Math.Round(deg, 1),
            Math.Round(amp, 8),
            Math.Round(dc, 2),
            Math.Round(quality, 4),
            quality > 0);
    }

    /// <summary>Das Quadraturfilter auf eine Reihe an der Stelle i.</summary>
    private static double Quad(double[] x, int i) =>
        C0 * x[i] + C1 * x[i - 2] - C1 * x[i - 4] - C0 * x[i - 6];

    /// <summary>
    /// Schreibt die Schwingung fort: Die Phase läuft weiter, die Amplitude
    /// wird gedämpft.
    /// </summary>
    /// <param name="halfLife">
    /// Nach wie vielen Bars die unterstellte Amplitude auf die Hälfte
    /// zurückgeht. Klein heißt vorsichtig — die Prognose verebbt dann rasch
    /// gegen null, was bei einem Zyklus, dessen Fortbestand niemand kennt, die
    /// ehrlichere Annahme ist.
    /// </param>
    public static double[] Project(HilbertState state, int horizon, double halfLife = 20)
    {
        if (!state.Valid || horizon <= 0 || state.CyclePeriod <= 0) return [];

        var outp = new double[horizon];

        var step = 2 * Math.PI / state.CyclePeriod;
        var phase = state.Phase * Math.PI / 180;

        // Auch die Güte dämpft: eine unsaubere Schwingung darf weniger behaupten.
        var scale = state.Amplitude * state.PhaseQuality;

        for (var h = 1; h <= horizon; h++)
        {
            var decay = Math.Pow(0.5, h / Math.Max(1, halfLife));
            outp[h - 1] = scale * decay * Math.Cos(phase + step * h);
        }

        return outp;
    }
}
