namespace Ingest.Core.Analysis;

/// <summary>
/// Glättung und Ableitungen einer Reihe über eine lokale Polynomanpassung.
///
/// <b>Warum nicht einfach ein gleitendes Mittel und Differenzen.</b> Weil das
/// zwei verschiedene Fehler zugleich macht. Ein gleitendes Mittel dämpft Wenden
/// mit: Genau dort, wo die Kurve am meisten zu sagen hat, verschmiert es am
/// stärksten. Und die Ableitung als Differenz benachbarter Werte verstärkt
/// Rauschen — jede Differenz verdoppelt den Rauschanteil, die zweite Ableitung
/// vervierfacht ihn. Bei Kursdaten, deren Tagesrauschen ohnehin die Bewegung
/// überdeckt, bleibt davon nichts Verwertbares.
///
/// <b>Was hier stattdessen geschieht.</b> Um jeden Punkt wird ein Polynom
/// zweiten Grades durch die Nachbarschaft gelegt — kleinste Quadrate. Der
/// Funktionswert, die Steigung und die Krümmung an dieser Stelle sind dann die
/// Koeffizienten dieser Anpassung, nicht Differenzen verrauschter Zahlen:
///
/// <code>p(x) = a₀ + a₁x + a₂x²   →   f = a₀,  f' = a₁,  f'' = 2a₂</code>
///
/// Weil die Stützstellen gleichabständig sind, hängt die Anpassung nur von der
/// Fensterform ab, nicht von der Stelle. Die Lösung der kleinsten Quadrate
/// lässt sich deshalb einmal als drei Faltungskerne ausrechnen und danach für
/// die ganze Reihe anwenden — das ist der Kern des Verfahrens von Savitzky und
/// Golay und der Grund, warum es trotz der Anpassung an jedem Punkt schnell
/// ist.
///
/// <b>Zentriert oder kausal — und warum das kein Detail ist.</b> Das zentrierte
/// Fenster ist genauer und verschiebt nichts: Es nimmt gleich viele Werte von
/// links und rechts, die Wende liegt danach dort, wo sie wirklich war. Dafür
/// benutzt es Werte, die zum betrachteten Zeitpunkt noch nicht bekannt waren.
///
/// Für die <i>Beschreibung</i> der Vergangenheit ist das richtig — man will
/// wissen, wann der Hochpunkt lag, und nicht, wann man ihn frühestens hätte
/// bemerken können. Für alles, was in eine <i>Prognose</i> einfließt, ist es
/// verboten: Ein zentriert geglätteter Wert kennt die Zukunft und verrät sie
/// weiter. Deshalb gibt es beide Formen, und die Wahl muss an jeder Aufrufstelle
/// bewusst getroffen werden.
/// </summary>
public static class SavitzkyGolay
{
    /// <summary>Funktionswert, erste und zweite Ableitung an jeder Stelle.</summary>
    /// <param name="Value">Der geglättete Verlauf.</param>
    /// <param name="D1">Steigung je Bar.</param>
    /// <param name="D2">Krümmung je Bar².</param>
    public sealed record Result(double[] Value, double[] D1, double[] D2);

    /// <summary>
    /// Legt an jeder Stelle ein Polynom zweiten Grades an und gibt Wert,
    /// Steigung und Krümmung zurück.
    /// </summary>
    /// <param name="y">Die Reihe. Sinnvoll auf Log-Kursen — dann ist die
    /// Steigung eine Rendite je Bar und über Werte verschiedener Größenordnung
    /// vergleichbar.</param>
    /// <param name="halfWindow">
    /// Halbe Fensterbreite in Bars. Das Fenster umfasst 2m+1 Punkte im
    /// zentrierten und m+1 im kausalen Fall.
    /// </param>
    /// <param name="causal">
    /// Wahr: nur zurückblicken, Ableitung am rechten Rand ausgewertet. Falsch:
    /// zentriert — genauer, aber nicht für Prognosen verwendbar.
    /// </param>
    public static Result Fit(double[] y, int halfWindow, bool causal = false)
    {
        var n = y.Length;

        var value = new double[n];
        var d1 = new double[n];
        var d2 = new double[n];

        if (n == 0) return new Result(value, d1, d2);

        // Mindestens fünf Stützstellen — mit vier Punkten ist ein Polynom
        // zweiten Grades kaum überbestimmt und die Anpassung folgt dem Rauschen.
        var m = Math.Max(2, halfWindow);

        if (n < 2 * m + 1)
        {
            Array.Copy(y, value, n);
            return new Result(value, d1, d2);
        }

        var (k0, k1, k2) = causal ? CausalKernels(m) : CenteredKernels(m);

        // Verschiebung des Fensters gegenüber der bewerteten Stelle.
        var from = causal ? -m : -m;
        var to = causal ? 0 : m;

        for (var t = 0; t < n; t++)
        {
            /* Ränder: Wo das Fenster nicht vollständig in die Reihe passt,
               bleibt der Rohwert stehen und die Ableitungen bleiben null.

               Die Alternative — das Fenster am Rand einseitig zu stutzen —
               liefert dort systematisch zu kleine Ableitungen und damit
               Scheinereignisse genau an Anfang und Ende jeder Reihe. Lieber
               keine Aussage als eine verzerrte. */
            if (t + from < 0 || t + to >= n)
            {
                value[t] = y[t];
                continue;
            }

            double s0 = 0, s1 = 0, s2 = 0;

            for (var j = from; j <= to; j++)
            {
                var v = y[t + j];
                var idx = j - from;

                s0 += k0[idx] * v;
                s1 += k1[idx] * v;
                s2 += k2[idx] * v;
            }

            value[t] = s0;
            d1[t] = s1;
            d2[t] = s2;
        }

        return new Result(value, d1, d2);
    }

    /// <summary>
    /// Die drei Faltungskerne für ein zentriertes Fenster.
    ///
    /// Für gleichabständige Stützstellen x = −m … m verschwinden alle ungeraden
    /// Potenzsummen, und das Normalgleichungssystem zerfällt: a₁ hängt nur von
    /// Σx·y ab, a₀ und a₂ hängen aneinander. Damit lassen sich die Kerne in
    /// geschlossener Form hinschreiben, ohne eine Matrix zu invertieren.
    /// </summary>
    private static (double[] K0, double[] K1, double[] K2) CenteredKernels(int m)
    {
        var len = 2 * m + 1;

        double s0 = len;
        double s2 = 0, s4 = 0;

        for (var j = -m; j <= m; j++)
        {
            double x2 = (double)j * j;
            s2 += x2;
            s4 += x2 * x2;
        }

        var k0 = new double[len];
        var k1 = new double[len];
        var k2 = new double[len];

        // Nenner der 2x2-Lösung für (a₀, a₂).
        var det = s0 * s4 - s2 * s2;

        for (var j = -m; j <= m; j++)
        {
            var i = j + m;
            double x = j, x2 = x * x;

            k0[i] = (s4 - s2 * x2) / det;
            k1[i] = x / s2;

            // f'' = 2·a₂
            k2[i] = 2 * (s0 * x2 - s2) / det;
        }

        return (k0, k1, k2);
    }

    /// <summary>
    /// Dieselbe Anpassung über ein rückwärts gerichtetes Fenster, ausgewertet am
    /// rechten Rand.
    ///
    /// Hier verschwinden die ungeraden Summen nicht, also wird das volle
    /// 3×3-System gelöst. Es ist klein und wird einmal je Fensterbreite
    /// gebraucht — eine geschlossene Form wäre hier nur schwerer zu lesen, nicht
    /// schneller.
    /// </summary>
    private static (double[] K0, double[] K1, double[] K2) CausalKernels(int m)
    {
        var len = m + 1;

        // Potenzsummen der Stützstellen x = −m … 0.
        var s = new double[5];

        for (var j = -m; j <= 0; j++)
        {
            double p = 1;
            for (var k = 0; k < 5; k++) { s[k] += p; p *= j; }
        }

        // Normalgleichungen: A·a = Σ xᵏ·y, mit A[i,j] = s[i+j].
        var a = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                a[i, j] = s[i + j];

        var inv = Invert3(a);

        var k0 = new double[len];
        var k1 = new double[len];
        var k2 = new double[len];

        for (var j = -m; j <= 0; j++)
        {
            var i = j + m;
            double x = j;

            // Beitrag dieser Stützstelle zu jedem Koeffizienten.
            var p = new[] { 1.0, x, x * x };

            double a0 = 0, a1 = 0, a2 = 0;

            for (var q = 0; q < 3; q++)
            {
                a0 += inv[0, q] * p[q];
                a1 += inv[1, q] * p[q];
                a2 += inv[2, q] * p[q];
            }

            // Ausgewertet an der Stelle x = 0, also am rechten Rand.
            k0[i] = a0;
            k1[i] = a1;
            k2[i] = 2 * a2;
        }

        return (k0, k1, k2);
    }

    /// <summary>Inverse einer 3×3-Matrix über die Adjunkte.</summary>
    private static double[,] Invert3(double[,] a)
    {
        var c = new double[3, 3];

        c[0, 0] = a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1];
        c[0, 1] = a[0, 2] * a[2, 1] - a[0, 1] * a[2, 2];
        c[0, 2] = a[0, 1] * a[1, 2] - a[0, 2] * a[1, 1];

        c[1, 0] = a[1, 2] * a[2, 0] - a[1, 0] * a[2, 2];
        c[1, 1] = a[0, 0] * a[2, 2] - a[0, 2] * a[2, 0];
        c[1, 2] = a[0, 2] * a[1, 0] - a[0, 0] * a[1, 2];

        c[2, 0] = a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0];
        c[2, 1] = a[0, 1] * a[2, 0] - a[0, 0] * a[2, 1];
        c[2, 2] = a[0, 0] * a[1, 1] - a[0, 1] * a[1, 0];

        var det = a[0, 0] * c[0, 0] + a[0, 1] * c[1, 0] + a[0, 2] * c[2, 0];

        if (Math.Abs(det) < 1e-30)
            throw new InvalidOperationException(
                "Normalgleichungen singulär — Fenster zu klein für ein Polynom zweiten Grades");

        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                c[i, j] /= det;

        return c;
    }
}
