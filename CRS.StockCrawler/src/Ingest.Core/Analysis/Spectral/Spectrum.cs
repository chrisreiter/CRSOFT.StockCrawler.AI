namespace Ingest.Core.Analysis.Spectral;

/// <summary>Ein Punkt des Leistungsspektrums.</summary>
public sealed record SpectrumPoint(double PeriodBars, double Power);

/// <summary>Was die Spektralschätzung über eine Reihe sagt.</summary>
/// <param name="DominantPeriod">
/// Die stärkste Periode innerhalb des betrachteten Bandes, in Bars.
/// </param>
/// <param name="Prominence">
/// Wie deutlich diese Spitze aus ihrer Umgebung herausragt — das Verhältnis
/// ihrer Leistung zum Median des Bandes. Ohne diese Zahl ist die stärkste
/// Periode wertlos: In einem völlig flachen Spektrum gibt es immer eine
/// stärkste, und sie bedeutet nichts.
/// </param>
/// <param name="SpectralEntropy">
/// Wie gleichmäßig sich die Leistung auf die Frequenzen verteilt, von 0 bis 1.
/// Nahe 1 heißt: weißes Rauschen, keine Struktur. Niedrig heißt: die Energie
/// sitzt in wenigen Frequenzen.
/// </param>
public sealed record SpectrumResult(
    double DominantPeriod,
    double DominantPower,
    double Prominence,
    double SpectralEntropy,
    double LowBandShare,
    double HighBandShare,
    IReadOnlyList<SpectrumPoint> Points,

    /// <summary>
    /// Der örtliche Untergrund je Stützstelle. Erst das Verhältnis von Leistung
    /// zu Untergrund ist aussagekräftig — die rohe Leistung steigt bei
    /// Kursreihen ohne jeden Zyklus zu langen Perioden hin an.
    /// </summary>
    IReadOnlyList<double>? Background = null);

/// <summary>
/// Spektralschätzung für Kursreihen.
///
/// <b>Nicht der Rohpreis.</b> Kurse tragen einen Trend, und ein Trend schiebt
/// nahezu die gesamte Energie in die niedrigsten Frequenzen. Das Spektrum zeigt
/// dann verlässlich eine „Periode" in Länge des Betrachtungsfensters — ein
/// Artefakt des Trends, kein Zyklus. Gerechnet wird deshalb auf Log-Renditen
/// oder auf trendbereinigten Log-Preisen.
///
/// <b>Welch statt einfachem Periodogramm.</b> Ein Periodogramm über das ganze
/// Fenster ist ein bemerkenswert schlechter Schätzer: Seine Streuung sinkt
/// nicht, wenn man mehr Daten hinzunimmt — man bekommt nur mehr, ebenso
/// verrauschte Stützstellen. Welch mittelt über überlappende Teilfenster und
/// tauscht Frequenzauflösung gegen Stabilität. Für die Frage „gibt es hier
/// überhaupt einen Zyklus" ist das der richtige Tausch.
/// </summary>
public static class Spectrum
{
    /// <summary>Hann-Fenster. Dämpft die Ränder auf null.</summary>
    public static double[] Hann(int n)
    {
        var w = new double[n];
        if (n == 1) { w[0] = 1; return w; }

        for (var i = 0; i < n; i++)
            w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));

        return w;
    }

    /// <summary>
    /// Welch-Schätzung der spektralen Leistungsdichte.
    /// </summary>
    /// <param name="segment">
    /// Länge der Teilfenster in Bars. Sie begrenzt die längste auflösbare
    /// Periode: Wer nach einem Quartalszyklus sucht, braucht Teilfenster von
    /// deutlich mehr als 65 Tagen, sonst passt die gesuchte Schwingung gar
    /// nicht hinein.
    /// </param>
    public static SpectrumResult Welch(
        ReadOnlySpan<double> signal,
        int segment = 256,
        double overlap = 0.5,
        double minPeriod = 4,
        double maxPeriod = 0,
        int padFactor = 4)
    {
        var n = signal.Length;
        if (n < 16) return Empty();

        segment = Math.Min(segment, n);
        if (segment < 8) segment = Math.Min(8, n);

        var step = Math.Max(1, (int)(segment * (1 - Math.Clamp(overlap, 0, 0.95))));
        var win = Hann(segment);

        // Normierung: das Fenster entzieht dem Signal Leistung, die zurück muss.
        double winPower = 0;
        foreach (var v in win) winPower += v * v;

        /* Die Teilfenster werden mit Nullen aufgefuellt, bevor transformiert
           wird. Das erhoeht die Aufloesung nicht -- zwei benachbarte Frequenzen
           bleiben ununterscheidbar -- aber es verhindert, dass die gefundene
           Periode auf ein grobes Raster einrastet.

           Ohne das Auffuellen liegen bei einem Teilfenster von 128 Bars
           oberhalb von Periode 40 nur noch die Stuetzstellen 42,7, 64 und 128.
           Ein tatsaechlicher 50-Bar-Zyklus wuerde dann zuverlaessig als 42,7
           gemeldet -- und zwar so stabil, dass die Verwechslung wie ein Befund
           aussieht. */
        var padded = Fft.NextPow2(segment * Math.Clamp(padFactor, 1, 8));
        var half = padded / 2;

        var accum = new double[half];
        var segments = 0;

        var re = new double[padded];
        var im = new double[padded];

        for (var start = 0; start + segment <= n; start += step)
        {
            // Mittelwert je Teilfenster entfernen — sonst dominiert ein
            // Gleichanteil, der über das Fenster hinweg gar nicht konstant ist.
            double mean = 0;
            for (var i = 0; i < segment; i++) mean += signal[start + i];
            mean /= segment;

            Array.Clear(re);
            Array.Clear(im);

            for (var i = 0; i < segment; i++)
                re[i] = (signal[start + i] - mean) * win[i];

            Fft.Forward(re, im);

            for (var k = 0; k < half; k++)
                accum[k] += (re[k] * re[k] + im[k] * im[k]) / winPower;

            segments++;
        }

        if (segments == 0) return Empty();

        var points = new List<SpectrumPoint>(half);

        for (var k = 1; k < half; k++)
        {
            var freq = (double)k / padded;          // Zyklen je Bar
            points.Add(new SpectrumPoint(1.0 / freq, accum[k] / segments));
        }

        /* Die Obergrenze wird an das Teilfenster gekoppelt, egal was verlangt
           wurde.

           Grund, an echten Kursen aufgefallen: Eine lineare Trendbereinigung
           laesst bei gekruemmten Kursverlaeufen einen betraechtlichen Rest
           stehen, und dessen Energie sitzt bei der laengsten Periode, die das
           Teilfenster ueberhaupt hergibt. NVDA und BTC meldeten daraufhin beide
           eine dominante Periode von 85 beziehungsweise 114 Bars mit
           Prominenzen von 109 und 255 -- Werte, die wie ein zwingender Befund
           aussehen. Es war in beiden Faellen der Rest des Trends.

           Wer eine Periode schaetzen will, braucht mehrere vollstaendige
           Durchlaeufe im Fenster. Unterhalb von dreien ist die Schaetzung eine
           Aussage ueber die Fensterlaenge, nicht ueber den Markt. */
        var ceiling = segment / 3.0;
        var effectiveMax = maxPeriod <= 0 ? ceiling : Math.Min(maxPeriod, ceiling);

        return Summarize(points, minPeriod, effectiveMax);
    }

    /// <summary>
    /// Wertet ein fertiges Spektrum aus: stärkste Periode im Band, wie deutlich
    /// sie heraussticht, und wie strukturiert das Spektrum insgesamt ist.
    /// </summary>
    private static SpectrumResult Summarize(
        List<SpectrumPoint> points, double minPeriod, double maxPeriod)
    {
        if (points.Count == 0) return Empty();

        double total = 0;
        foreach (var p in points) total += p.Power;

        if (total <= 0) return Empty();

        /* Spektrale Entropie: die Leistungsverteilung als Wahrscheinlichkeit
           lesen und ihre Shannon-Entropie bilden, normiert auf den
           Maximalwert. Das ergibt ein Maß dafür, wie sehr sich die Reihe
           überhaupt von Rauschen unterscheidet. */
        double entropy = 0;
        foreach (var p in points)
        {
            var q = p.Power / total;
            if (q > 0) entropy -= q * Math.Log(q);
        }
        entropy /= Math.Log(points.Count);

        /* Anteil langer gegenüber kurzen Perioden. Ein hoher Anteil langer
           Perioden heißt: die Bewegung ist eher gerichtet als zappelig — ein
           brauchbarer Hinweis auf das vorherrschende Marktverhalten. */
        double low = 0, high = 0;
        foreach (var p in points)
        {
            if (p.PeriodBars >= 20) low += p.Power;
            else if (p.PeriodBars <= 8) high += p.Power;
        }

        var background = LocalBackground(points);

        double bestRatio = 0, bestPower = 0, bestPeriod = 0;

        for (var i = 0; i < points.Count; i++)
        {
            var pt = points[i];
            if (pt.PeriodBars < minPeriod || pt.PeriodBars > maxPeriod) continue;
            if (background[i] <= 0) continue;

            var ratio = pt.Power / background[i];
            if (ratio <= bestRatio) continue;

            bestRatio = ratio;
            bestPower = pt.Power;
            bestPeriod = pt.PeriodBars;
        }

        if (bestPeriod <= 0)
            return new SpectrumResult(0, 0, 0, Math.Round(entropy, 4),
                Math.Round(low / total, 4), Math.Round(high / total, 4), points, background);

        return new SpectrumResult(
            Math.Round(bestPeriod, 2),
            bestPower,
            Math.Round(bestRatio, 2),
            Math.Round(entropy, 4),
            Math.Round(low / total, 4),
            Math.Round(high / total, 4),
            points,
            background);
    }

    /// <summary>
    /// Der örtliche Untergrund des Spektrums: je Frequenz der Median ihrer
    /// Nachbarschaft.
    ///
    /// <b>Warum das nötig ist — und warum der erste Ansatz systematisch in die
    /// Irre führte.</b> Zunächst wurde die stärkste Spitze mit dem Median des
    /// gesamten Bandes verglichen. Das setzt voraus, dass ein strukturloses
    /// Spektrum flach ist. Kursspektren sind aber nicht flach, sondern rot: Die
    /// Leistung fällt mit steigender Frequenz, und zwar bei einem Zufallspfad
    /// mit dem Quadrat. In einem solchen Spektrum ist die längste betrachtete
    /// Periode <i>immer</i> die stärkste, und sie liegt <i>immer</i> weit über
    /// dem Median des Bandes.
    ///
    /// Genau das war zu sehen: NVDA, Bitcoin und BNB meldeten alle drei
    /// dieselbe dominante Periode mit dreistelliger Prominenz — nämlich exakt
    /// ein Drittel der Teilfensterlänge, also die längste im Band überhaupt
    /// zulässige. Drei völlig verschiedene Märkte mit identischem Befund: Das
    /// war kein Zyklus, das war die Farbe des Rauschens.
    ///
    /// Gegen den örtlichen Untergrund gemessen verschwindet dieser Effekt. Ein
    /// gleichmäßig abfallendes Spektrum ergibt überall ein Verhältnis nahe
    /// eins — also die richtige Antwort: kein Zyklus. Nur eine Spitze, die aus
    /// ihrer <i>unmittelbaren</i> Nachbarschaft heraussticht, überlebt.
    /// </summary>
    private static double[] LocalBackground(List<SpectrumPoint> points)
    {
        var n = points.Count;
        var bg = new double[n];

        var buf = new List<double>(64);

        for (var i = 0; i < n; i++)
        {
            /* Das Fenster wächst mit der Frequenz, statt fest zu sein. Die
               Stützstellen liegen gleichmäßig in der Frequenz, aber die
               interessanten Strukturen sind in der Periode gleichmäßig
               verteilt — bei niedrigen Frequenzen liegen deshalb wenige
               Stützstellen auf einem weiten Periodenbereich und bei hohen
               sehr viele auf einem engen. */
            /* Gemessen wird in einem Ring um die Stelle, nicht in einer
               Scheibe. Eine Spitze ist nie unendlich schmal -- das Fenster
               verbreitert sie, und die Nullauffuellung tastet sie feiner ab.
               Naehme man die unmittelbare Nachbarschaft mit in den Untergrund,
               hoebe die Spitze ihren eigenen Vergleichswert an, und ihr
               Verhaeltnis waere am Rand der Spitze groesser als in deren Mitte.
               Genau das war zu sehen: Die gefundene Periode sprang auf die
               Flanke statt auf den Gipfel. */
            var inLo = Math.Max(0, (int)(i / 2.5) - 2);
            var inHi = Math.Max(0, (int)(i / 1.3) - 2);
            var outLo = Math.Min(n - 1, (int)(i * 1.3) + 2);
            var outHi = Math.Min(n - 1, (int)(i * 2.5) + 2);

            buf.Clear();
            for (var k = inLo; k <= inHi; k++) buf.Add(points[k].Power);
            for (var k = outLo; k <= outHi; k++) buf.Add(points[k].Power);

            if (buf.Count == 0)
                for (var k = Math.Max(0, i - 5); k <= Math.Min(n - 1, i + 5); k++)
                    buf.Add(points[k].Power);

            buf.Sort();
            bg[i] = buf.Count % 2 == 1
                ? buf[buf.Count / 2]
                : (buf[buf.Count / 2 - 1] + buf[buf.Count / 2]) / 2;
        }

        return bg;
    }

    private static SpectrumResult Empty() =>
        new(0, 0, 0, 1, 0, 0, Array.Empty<SpectrumPoint>());

    /// <summary>
    /// Bandpass auf den Log-Preisen: laesst nur Schwingungen zwischen den
    /// beiden Periodengrenzen stehen.
    ///
    /// <b>Warum die lineare Trendbereinigung dafuer nicht taugt.</b> Sie zieht
    /// eine Gerade ab. Ein Kursverlauf ueber fuenf Jahre ist aber gekruemmt, und
    /// was nach dem Abzug uebrig bleibt, ist ein grosser, langsamer Rest. Dessen
    /// Energie sitzt bei der laengsten Periode, die das Fenster hergibt -- die
    /// Auswertung meldete daraufhin fuer NVDA wie fuer BTC dieselbe dominante
    /// Periode, naemlich genau ein Drittel der Teilfensterlaenge, mit
    /// dreistelliger Prominenz. Zwei voellig verschiedene Maerkte mit
    /// identischem "Zyklus": Das war der Rest des Trends, gemessen an der
    /// Fensterlaenge.
    ///
    /// Der Bandpass entfernt stattdessen alles, was langsamer schwingt als das
    /// gesuchte Band. Gebildet wird er aus der Differenz zweier exponentieller
    /// Mittel -- beide laufen ausschliesslich ueber zurueckliegende Werte, es
    /// kann also kein Zukunftswissen einsickern.
    /// </summary>
    public static double[] BandPass(ReadOnlySpan<double> closes,
                                    double minPeriod = 5, double maxPeriod = 120)
    {
        var n = closes.Length;
        if (n < 8) return [];

        var y = new double[n];
        for (var i = 0; i < n; i++) y[i] = closes[i] > 0 ? Math.Log(closes[i]) : 0;

        /* Die Glaettungsfaktoren aus den Periodengrenzen. Das schnelle Mittel
           entfernt das Zappeln unterhalb des Bandes, das langsame den Trend
           oberhalb -- die Differenz laesst das Band dazwischen stehen. */
        var aFast = 2.0 / (Math.Max(2, minPeriod / 2) + 1);
        var aSlow = 2.0 / (Math.Max(4, maxPeriod * 1.2) + 1);

        var fast = y[0];
        var slow = y[0];

        var outp = new double[n];

        for (var i = 0; i < n; i++)
        {
            fast += aFast * (y[i] - fast);
            slow += aSlow * (y[i] - slow);
            outp[i] = fast - slow;
        }

        /* Der Anlauf wird verworfen. Beide Mittel starten auf demselben Wert,
           die Differenz ist am Anfang also kuenstlich null und laeuft erst
           allmaehlich ein -- diese Einschwingphase saehe im Spektrum wie eine
           sehr lange Schwingung aus. */
        var warm = Math.Min(n / 4, (int)(maxPeriod * 2));
        return warm > 0 && n - warm > 32 ? outp[warm..] : outp;
    }

    /// <summary>Log-Renditen einer Kursreihe.</summary>
    public static double[] LogReturns(ReadOnlySpan<double> closes)
    {
        if (closes.Length < 2) return [];

        var r = new double[closes.Length - 1];
        for (var i = 1; i < closes.Length; i++)
            r[i - 1] = closes[i - 1] > 0 && closes[i] > 0
                ? Math.Log(closes[i] / closes[i - 1])
                : 0;

        return r;
    }

    /// <summary>
    /// Trendbereinigte Log-Preise: Log-Kurs abzüglich einer angepassten Geraden.
    ///
    /// Als Grundlage für die Zyklensuche oft ergiebiger als Renditen — das
    /// Differenzieren verstärkt hohe Frequenzen und drückt genau die
    /// mittelfristigen Schwingungen, nach denen gesucht wird.
    /// </summary>
    public static double[] DetrendedLog(ReadOnlySpan<double> closes)
    {
        var n = closes.Length;
        if (n < 3) return [];

        var y = new double[n];
        for (var i = 0; i < n; i++) y[i] = closes[i] > 0 ? Math.Log(closes[i]) : 0;

        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var i = 0; i < n; i++)
        {
            sx += i; sy += y[i];
            sxx += (double)i * i; sxy += i * y[i];
        }

        var den = n * sxx - sx * sx;
        var slope = den != 0 ? (n * sxy - sx * sy) / den : 0;
        var icept = (sy - slope * sx) / n;

        for (var i = 0; i < n; i++) y[i] -= slope * i + icept;
        return y;
    }
}
