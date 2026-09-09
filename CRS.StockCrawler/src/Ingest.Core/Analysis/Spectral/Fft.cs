namespace Ingest.Core.Analysis.Spectral;

/// <summary>
/// Schnelle Fourier-Transformation, Radix-2, an Ort und Stelle.
///
/// <b>Warum selbst geschrieben:</b> Die Analysemathematik dieses Projekts ist
/// bewusst ohne Fremdbibliotheken gehalten, damit sie ohne Aufbau einer
/// Umgebung prüfbar bleibt. Eine Radix-2-FFT sind sechzig Zeilen und ein seit
/// sechzig Jahren unveränderter Algorithmus — dafür eine Abhängigkeit
/// aufzunehmen, die ihrerseits native Anteile mitbringt, stünde in keinem
/// Verhältnis. Bei den hier auftretenden Längen von 256 bis 4096 Punkten ist
/// die Laufzeit ohnehin bedeutungslos.
/// </summary>
public static class Fft
{
    /// <summary>
    /// Transformiert an Ort und Stelle. <paramref name="re"/> und
    /// <paramref name="im"/> müssen dieselbe Länge haben, und diese muss eine
    /// Zweierpotenz sein.
    /// </summary>
    public static void Forward(double[] re, double[] im, bool inverse = false)
    {
        var n = re.Length;
        if (n != im.Length) throw new ArgumentException("Real- und Imaginärteil müssen gleich lang sein");
        if (n <= 1) return;
        if ((n & (n - 1)) != 0) throw new ArgumentException($"Länge muss eine Zweierpotenz sein, war {n}");

        // ------------------------------------------------- Bitumkehr-Sortierung
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // ----------------------------------------------------- Schmetterlinge
        var sign = inverse ? 1.0 : -1.0;

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = sign * 2 * Math.PI / len;
            var wRe = Math.Cos(ang);
            var wIm = Math.Sin(ang);

            for (var i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;

                for (var k = 0; k < len / 2; k++)
                {
                    var uRe = re[i + k];
                    var uIm = im[i + k];

                    var vRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm;
                    var vIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe;

                    re[i + k] = uRe + vRe;
                    im[i + k] = uIm + vIm;
                    re[i + k + len / 2] = uRe - vRe;
                    im[i + k + len / 2] = uIm - vIm;

                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }

        if (!inverse) return;

        for (var i = 0; i < n; i++)
        {
            re[i] /= n;
            im[i] /= n;
        }
    }

    /// <summary>Nächste Zweierpotenz ab <paramref name="n"/>.</summary>
    public static int NextPow2(int n)
    {
        var p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>
    /// Kopiert ein reelles Signal in Puffer der nächsten Zweierpotenz und füllt
    /// den Rest mit Nullen.
    ///
    /// <b>Was das Auffüllen tut und was nicht:</b> Es erhöht die Zahl der
    /// Stützstellen im Spektrum, nicht die Auflösung. Zwei Frequenzen, die im
    /// ursprünglichen Fenster nicht zu trennen waren, sind es danach auch
    /// nicht — sie werden nur feiner abgetastet. Wer das verwechselt, liest
    /// aus dem Ergebnis eine Genauigkeit heraus, die in den Daten nicht steckt.
    /// </summary>
    public static (double[] Re, double[] Im) ToBuffers(ReadOnlySpan<double> signal)
    {
        var n = NextPow2(signal.Length);

        var re = new double[n];
        var im = new double[n];

        signal.CopyTo(re);
        return (re, im);
    }
}
