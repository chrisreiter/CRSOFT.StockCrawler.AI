namespace Ingest.Core.Analysis.Spectral;

/// <summary>Eine Einzelkomponente der Zerlegung.</summary>
/// <param name="Kind">
/// <c>Trend</c>, <c>Zyklus</c> oder <c>Rauschen</c> — abgeleitet aus der
/// dominanten Periode der Komponente selbst, nicht von Hand vergeben.
/// </param>
public sealed record SsaComponent(
    int Index,
    double VarianceShare,
    double DominantPeriod,
    string Kind,
    IReadOnlyList<double> Series);

/// <summary>Ergebnis einer Zerlegung samt Fortschreibung.</summary>
public sealed record SsaResult(
    int WindowLength,
    int UsedComponents,
    double ExplainedShare,
    IReadOnlyList<SsaComponent> Components,
    IReadOnlyList<double> Reconstructed,
    IReadOnlyList<double> Forecast,
    bool ForecastValid,
    string Note);

/// <summary>
/// Singuläre Spektralanalyse.
///
/// <b>Was sie besser macht als die Fourier-Zerlegung:</b> Fourier zerlegt in
/// Sinusschwingungen fester Frequenz und unterstellt damit, dass diese über das
/// gesamte Fenster gleich stark schwingen. An Märkten trifft das nicht zu — ein
/// Zyklus taucht auf, trägt ein halbes Jahr und verschwindet wieder. Die
/// singuläre Spektralanalyse legt keine Kurvenform fest: Sie liest die
/// Bausteine aus den Daten selbst ab. Trend, Schwingung und Rauschen fallen
/// dabei von allein auseinander, ohne dass man vorher sagen muss, wonach man
/// sucht.
///
/// <b>Und warum sie tatsächlich vorhersagen kann:</b> Aus den führenden
/// Komponenten lässt sich eine lineare Rekursion ableiten — jeder neue Wert als
/// gewichtete Summe der vorangegangenen. Das ist keine in die Zukunft
/// verlängerte Sinuskurve, sondern eine Fortschreibung der Struktur, die in den
/// Daten steckt. Untersuchungen an Finanzreihen zeigen, dass das auf kurze
/// Sicht mit ARIMA gleichzieht und auf mittlere Horizonte deutlich besser
/// abschneidet.
///
/// <b>Die Grenze, die bleibt:</b> Auch diese Rekursion unterstellt, dass die
/// gefundene Struktur weiter gilt. Bricht sie — und an Märkten bricht sie —,
/// läuft die Fortschreibung ins Leere. Deshalb ist der Vertrauenswert an den
/// Anteil der erklärten Streuung gekoppelt und nicht an die Schönheit der
/// Kurve.
/// </summary>
public static class Ssa
{
    /// <summary>
    /// Zerlegt und schreibt fort.
    /// </summary>
    /// <param name="windowLength">
    /// Die Fensterlänge bestimmt, welche Perioden überhaupt auffindbar sind:
    /// Erfassbar ist höchstens eine Schwingung der Länge des Fensters. Als
    /// Faustregel gilt ein Viertel bis die Hälfte der Reihenlänge, und für die
    /// Suche nach einer Periode T ein Vielfaches von T.
    /// </param>
    /// <param name="components">
    /// Wie viele führende Komponenten als Struktur gelten. Zu wenige lassen
    /// Signal im Rest zurück, zu viele holen das Rauschen mit in die Prognose.
    /// </param>
    /// <param name="horizon">Wie viele Bars fortgeschrieben werden sollen.</param>
    public static SsaResult Decompose(
        ReadOnlySpan<double> series,
        int windowLength = 0,
        int components = 4,
        int horizon = 0)
    {
        var n = series.Length;

        var L = windowLength > 0 ? windowLength : Math.Max(12, Math.Min(120, n / 4));
        L = Math.Clamp(L, 4, n / 2);

        if (n < 24 || L < 4)
            return Empty(L, "Reihe zu kurz für eine Zerlegung");

        var K = n - L + 1;

        /* Zentrieren, bevor zerlegt wird.

           Ohne das misst die Zerlegung nicht die Streuung, sondern das Niveau.
           Der Logarithmus eines Aktienkurses liegt bei etwa 4 bis 5, seine
           Schwankung bei Bruchteilen davon -- die erste Komponente faengt dann
           schlicht die Hoehe ein und meldet 99,9 Prozent erklaerte Streuung.
           Diese Zahl sieht nach einem hervorragenden Modell aus und bedeutet
           nur, dass der Kurs nicht null ist. An NVDA und BTC genau so
           aufgetreten. */
        double centre = 0;
        foreach (var v in series) centre += v;
        centre /= n;

        var x = new double[n];
        for (var i = 0; i < n; i++) x[i] = series[i] - centre;

        /* Die Bahnmatrix wird nicht aufgebaut. Gebraucht wird nur X·Xᵀ, und das
           lässt sich unmittelbar aus der Reihe summieren — bei 4000 Punkten
           spart das ein Feld von mehreren Megabyte, das ohnehin nur einmal
           gelesen würde. */
        var c = new double[L, L];

        for (var i = 0; i < L; i++)
        {
            for (var j = i; j < L; j++)
            {
                double sum = 0;
                for (var k = 0; k < K; k++) sum += x[i + k] * x[j + k];

                c[i, j] = sum;
                c[j, i] = sum;
            }
        }

        /* Nur die führenden Komponenten bestimmen, nicht alle.

           Gebraucht werden vier bis acht von bis zu zweihundert. Eine
           vollständige Jacobi-Zerlegung rechnet trotzdem alle — und ihr Aufwand
           wächst mit der dritten Potenz der Fensterlänge. Bei der
           Parametersuche über zwanzigtausend Zerlegungen war das der
           Unterschied zwischen Minuten und einem halben Tag.

           Potenziteration mit Deflation liefert dieselben führenden Paare bei
           einem Bruchteil der Rechnung. Die vollständige Zerlegung bleibt für
           kleine Fenster als Rückfall, weil sie dort nicht langsamer und
           numerisch unempfindlicher ist. */
        double[] values;
        double[,] vectors;
        int[] order;

        var wanted = Math.Clamp(components, 1, Math.Min(L, 12));

        if (L <= 40 || wanted * 4 >= L)
        {
            (values, vectors) = JacobiEigen(c, L);
            order = Enumerable.Range(0, L).OrderByDescending(i => values[i]).ToArray();
        }
        else
        {
            (values, vectors) = PartialEigen(c, L, wanted);
            order = Enumerable.Range(0, wanted).ToArray();
        }

        /* Bezugsgröße ist die Spur der Matrix, nicht die Summe der berechneten
           Eigenwerte. Bei einer Teilzerlegung sind nur wenige bekannt; ihre
           Summe als Ganzes zu nehmen hieße, den erklärten Anteil auf eins zu
           normieren — er wäre dann immer eins und sagte nichts. */
        double totalVar = 0;
        for (var i = 0; i < L; i++) totalVar += c[i, i];

        if (totalVar <= 0) return Empty(L, "Reihe ohne Streuung");

        var r = Math.Clamp(components, 1, Math.Min(L, 12));

        var comps = new List<SsaComponent>(r);
        var reconstructed = new double[n];
        var u = new double[L];
        var v2 = new double[K];

        double explained = 0;

        for (var idx = 0; idx < r; idx++)
        {
            var e = order[idx];
            if (values[e] <= 1e-12) break;

            for (var i = 0; i < L; i++) u[i] = vectors[i, e];

            // vᵢ = Xᵀ·uᵢ — die Ausprägung dieser Komponente über die Zeit.
            for (var k = 0; k < K; k++)
            {
                double s = 0;
                for (var i = 0; i < L; i++) s += x[i + k] * u[i];
                v2[k] = s;
            }

            var g = DiagonalAverage(u, v2, L, K, n);

            for (var t = 0; t < n; t++) reconstructed[t] += g[t];

            var share = values[e] / totalVar;
            explained += share;

            /* Wofür die Komponente steht, entscheidet ihr eigenes Spektrum. Eine
               Trendkomponente hat ihre Energie bei sehr langen Perioden, eine
               Schwingung bei einer bestimmten, Rauschen bei keiner. */
            var sp = Spectrum.Welch(g, Math.Min(256, n / 2), 0.5, 3, n / 2.0);

            comps.Add(new SsaComponent(
                idx,
                Math.Round(share, 5),
                /* Die Periode einer Komponente wird aus ihren Nulldurchgaengen
                   geschaetzt, nicht aus ihrem Spektrum. Das Spektrum kann
                   hoechstens melden, was in sein Teilfenster passt, und meldete
                   deshalb fuer saemtliche Komponenten denselben Wert -- die
                   Fenstergrenze. Nulldurchgaenge kennen diese Grenze nicht. */
                /* Fuer eine Trendkomponente gibt es keine Periode. Die
                   Schaetzung aus Nulldurchgaengen liefert dort die Reihenlaenge
                   -- eine Zahl, die richtig aussieht und nichts bedeutet. */
                Classify(sp, g) == "Trend" ? 0 : CrossingPeriod(g),
                Classify(sp, g),
                g.Select(x => Math.Round(x, 8)).ToList()));
        }

        var (forecast, ok, note) = horizon > 0
            ? Recurrent(reconstructed, vectors, values, order, comps.Count, L, horizon)
            : ([], false, "keine Fortschreibung angefordert");

        return new SsaResult(
            L, comps.Count, Math.Round(explained, 5), comps,
            reconstructed.Select(v => Math.Round(v + centre, 8)).ToList(),
            forecast.Select(v => Math.Round(v + centre, 8)).ToList(), ok, note);
    }

    /// <summary>
    /// Schaetzt die Periode aus den Durchgaengen durch den eigenen Mittelwert.
    /// Eine Schwingung kreuzt ihn zweimal je Umlauf.
    /// </summary>
    private static double CrossingPeriod(double[] g)
    {
        var n = g.Length;
        if (n < 4) return 0;

        double mean = 0;
        foreach (var v in g) mean += v;
        mean /= n;

        var crossings = 0;
        for (var i = 1; i < n; i++)
            if ((g[i - 1] - mean) * (g[i] - mean) < 0) crossings++;

        return crossings < 2 ? 0 : Math.Round(2.0 * (n - 1) / crossings, 2);
    }

    /// <summary>
    /// Entscheidet anhand der Komponente selbst, wofuer sie steht.
    ///
    /// Die Trendfrage wird ueber Nulldurchgaenge beantwortet, nicht ueber das
    /// Spektrum. Grund: Das Spektrum einer Komponente kann nur Perioden melden,
    /// die in sein Teilfenster passen -- eine Trendkomponente, die ueber die
    /// ganze Reihe genau einmal ansteigt, hat eine Periode jenseits davon und
    /// wuerde als staerkste auffindbare Frequenz irgendeinen Wert am oberen
    /// Rand melden. Sie saehe damit aus wie ein sehr langer Zyklus.
    ///
    /// Nulldurchgaenge sind gegen dieses Problem immun: Ein Trend kreuzt seinen
    /// eigenen Mittelwert ein- bis zweimal, eine Schwingung zweimal je Periode.
    /// </summary>
    private static string Classify(SpectrumResult sp, double[] g)
    {
        var n = g.Length;
        if (n < 8) return "Rauschen";

        double mean = 0;
        foreach (var v in g) mean += v;
        mean /= n;

        var crossings = 0;
        for (var i = 1; i < n; i++)
            if ((g[i - 1] - mean) * (g[i] - mean) < 0) crossings++;

        if (crossings <= 3) return "Trend";

        // Ohne deutliche Spitze und mit gleichverteilter Energie: Rauschen.
        return sp.Prominence >= 3 && sp.SpectralEntropy < 0.9 ? "Zyklus" : "Rauschen";
    }

    /// <summary>
    /// Fortschreibung über die lineare Rekursion aus den führenden Komponenten.
    /// </summary>
    private static (IReadOnlyList<double> Values, bool Ok, string Note) Recurrent(
        double[] reconstructed, double[,] vectors, double[] values,
        int[] order, int r, int L, int horizon)
    {
        if (r < 1) return ([], false, "keine tragfähige Komponente");

        /* Der Kern des Verfahrens: Die letzten Einträge der Eigenvektoren
           entscheiden, ob sich die Struktur überhaupt fortschreiben lässt.
           Summieren sie sich zu eins oder mehr, ist die Rekursion nicht
           definiert — die Zerlegung sagt dann nichts über den nächsten Wert. */
        double nu2 = 0;
        for (var idx = 0; idx < r; idx++)
        {
            var pi = vectors[L - 1, order[idx]];
            nu2 += pi * pi;
        }

        if (nu2 >= 0.999)
            return ([], false, "Rekursion nicht definiert — Struktur trägt nicht bis an den Rand");

        var coef = new double[L - 1];

        for (var idx = 0; idx < r; idx++)
        {
            var e = order[idx];
            var pi = vectors[L - 1, e];

            for (var j = 0; j < L - 1; j++)
                coef[j] += pi * vectors[j, e];
        }

        for (var j = 0; j < L - 1; j++) coef[j] /= 1 - nu2;

        /* Fortgeschrieben wird auf der rekonstruierten Reihe, nicht auf der
           rohen: Die Rekursion beschreibt die Struktur, und das Rauschen
           gehört nicht dazu. Es mitzuschleppen hieße, Zufall fortzusetzen. */
        var buf = new List<double>(reconstructed);
        var outp = new List<double>(horizon);

        for (var h = 0; h < horizon; h++)
        {
            double next = 0;
            for (var j = 0; j < L - 1; j++)
                next += coef[j] * buf[buf.Count - (L - 1) + j];

            if (double.IsNaN(next) || double.IsInfinity(next))
                return (outp, false, "Rekursion divergiert");

            buf.Add(next);
            outp.Add(Math.Round(next, 8));
        }

        return (outp, true, "ok");
    }

    /// <summary>
    /// Mittelt die Nebendiagonalen der Komponentenmatrix u·vᵀ und macht daraus
    /// wieder eine Zeitreihe. Ohne diesen Schritt bliebe die Komponente eine
    /// Matrix, in der derselbe Zeitpunkt mehrfach und mit verschiedenen Werten
    /// vorkommt.
    /// </summary>
    private static double[] DiagonalAverage(double[] u, double[] v, int L, int K, int n)
    {
        var g = new double[n];
        var cnt = new int[n];

        for (var i = 0; i < L; i++)
        {
            var ui = u[i];
            for (var k = 0; k < K; k++)
            {
                g[i + k] += ui * v[k];
                cnt[i + k]++;
            }
        }

        for (var t = 0; t < n; t++)
            if (cnt[t] > 0) g[t] /= cnt[t];

        return g;
    }

    /// <summary>
    /// Die führenden <paramref name="k"/> Eigenpaare über Potenziteration mit
    /// Deflation.
    ///
    /// Jede Iteration ist eine Matrix-Vektor-Multiplikation — quadratisch in
    /// der Fenstergröße statt kubisch. Nach jedem gefundenen Paar wird sein
    /// Beitrag aus der Matrix genommen, damit der nächste Durchlauf das nächste
    /// findet.
    ///
    /// Die Deflation verändert eine ARBEITSKOPIE. Ohne sie wäre die
    /// Ausgangsmatrix nach dem Aufruf zerstört, und die Spur — die als
    /// Bezugsgröße gebraucht wird — stünde nicht mehr zur Verfügung.
    /// </summary>
    private static (double[] Values, double[,] Vectors) PartialEigen(double[,] a, int n, int k)
    {
        var m = (double[,])a.Clone();

        var values = new double[k];
        var vectors = new double[n, k];

        var v = new double[n];
        var t = new double[n];

        var rnd = new Random(20260821);

        for (var idx = 0; idx < k; idx++)
        {
            // Zufälliger Start: Ein gleichmäßiger Vektor kann auf einem
            // Eigenvektor senkrecht stehen und dann nie zu ihm konvergieren.
            for (var i = 0; i < n; i++) v[i] = rnd.NextDouble() - 0.5;

            Normalize(v, n);

            double lambda = 0;

            for (var it = 0; it < 300; it++)
            {
                for (var i = 0; i < n; i++)
                {
                    double s = 0;
                    for (var j = 0; j < n; j++) s += m[i, j] * v[j];
                    t[i] = s;
                }

                var norm = Norm(t, n);
                if (norm < 1e-300) break;

                for (var i = 0; i < n; i++) v[i] = t[i] / norm;

                double lam = 0;
                for (var i = 0; i < n; i++)
                {
                    double s = 0;
                    for (var j = 0; j < n; j++) s += m[i, j] * v[j];
                    lam += v[i] * s;
                }

                if (it > 3 && Math.Abs(lam - lambda) < 1e-11 * Math.Max(1, Math.Abs(lam)))
                {
                    lambda = lam;
                    break;
                }

                lambda = lam;
            }

            values[idx] = lambda;
            for (var i = 0; i < n; i++) vectors[i, idx] = v[i];

            if (idx + 1 >= k) break;

            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                    m[i, j] -= lambda * v[i] * v[j];
        }

        return (values, vectors);
    }

    private static double Norm(double[] x, int n)
    {
        double s = 0;
        for (var i = 0; i < n; i++) s += x[i] * x[i];
        return Math.Sqrt(s);
    }

    private static void Normalize(double[] x, int n)
    {
        var norm = Norm(x, n);
        if (norm < 1e-300) return;
        for (var i = 0; i < n; i++) x[i] /= norm;
    }

    /// <summary>
    /// Eigenwerte und Eigenvektoren einer symmetrischen Matrix nach Jacobi.
    ///
    /// Für die hier auftretenden Größen — das Fenster liegt bei einigen
    /// Dutzend bis gut hundert — ist das Verfahren schnell genug und zugleich
    /// das numerisch gutmütigste: Es arbeitet ausschließlich mit Drehungen,
    /// erhält die Symmetrie in jedem Schritt und braucht weder Startwerte noch
    /// Abbruchheuristiken.
    /// </summary>
    private static (double[] Values, double[,] Vectors) JacobiEigen(double[,] a, int n)
    {
        var m = (double[,])a.Clone();
        var v = new double[n, n];

        for (var i = 0; i < n; i++) v[i, i] = 1;

        for (var sweep = 0; sweep < 100; sweep++)
        {
            // Wie weit ist die Matrix noch von der Diagonalgestalt entfernt?
            double off = 0;
            for (var p = 0; p < n; p++)
                for (var q = p + 1; q < n; q++)
                    off += m[p, q] * m[p, q];

            if (off < 1e-20) break;

            for (var p = 0; p < n - 1; p++)
            {
                for (var q = p + 1; q < n; q++)
                {
                    if (Math.Abs(m[p, q]) < 1e-18) continue;

                    var theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;

                    var c = 1 / Math.Sqrt(t * t + 1);
                    var s = t * c;

                    for (var k = 0; k < n; k++)
                    {
                        var mkp = m[k, p];
                        var mkq = m[k, q];
                        m[k, p] = c * mkp - s * mkq;
                        m[k, q] = s * mkp + c * mkq;
                    }

                    for (var k = 0; k < n; k++)
                    {
                        var mpk = m[p, k];
                        var mqk = m[q, k];
                        m[p, k] = c * mpk - s * mqk;
                        m[q, k] = s * mpk + c * mqk;
                    }

                    for (var k = 0; k < n; k++)
                    {
                        var vkp = v[k, p];
                        var vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
            }
        }

        var values = new double[n];
        for (var i = 0; i < n; i++) values[i] = m[i, i];

        return (values, v);
    }

    private static SsaResult Empty(int L, string note) =>
        new(L, 0, 0, [], [], [], false, note);
}
