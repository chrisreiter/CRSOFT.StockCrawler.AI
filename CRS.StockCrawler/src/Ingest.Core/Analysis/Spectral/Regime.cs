namespace Ingest.Core.Analysis.Spectral;

/// <summary>Wie sich die Reihe zurzeit verhält.</summary>
/// <param name="Hurst">
/// Über 0,5: Bewegungen setzen sich eher fort. Unter 0,5: sie kehren eher um.
/// Genau 0,5 wäre ein Zufallspfad, bei dem weder das eine noch das andere gilt.
/// </param>
/// <param name="Label">
/// <c>trendfolgend</c>, <c>rückkehrend</c> oder <c>richtungslos</c>.
/// </param>
/// <param name="Confidence">
/// Wie deutlich der Befund ist, von 0 bis 1 — aus dem Abstand zu 0,5 und der
/// Güte der Anpassung.
/// </param>
public sealed record RegimeState(
    double Hurst,
    string Label,
    double Confidence,
    double FitQuality,
    int Points);

/// <summary>
/// Erkennt, ob eine Reihe gerade zum Weiterlaufen oder zum Umkehren neigt.
///
/// <b>Warum das hier steht und nicht bei den Prognosemodellen:</b> Diese
/// Auswertung sagt selbst nichts über den nächsten Kurs. Sie beantwortet die
/// vorgelagerte Frage — welches der vorhandenen Modelle in der jetzigen Lage
/// überhaupt sinnvoll ist. In einer trendfolgenden Phase hat das
/// Momentum-Modell recht und das Rückkehr-Modell irrt; in einer rückkehrenden
/// Phase ist es umgekehrt. Bislang findet die Gewichtung das nur heraus, indem
/// sie sich irrt und aus den Fehlern nachregelt. Ein Vorabhinweis erspart der
/// Gewichtung diesen Umweg.
///
/// Gemessen wird über die trendbereinigte Fluktuationsanalyse: Die Reihe wird
/// aufsummiert, in Abschnitte verschiedener Länge zerlegt, in jedem Abschnitt
/// von seinem eigenen Trend befreit, und aus dem verbleibenden Rest wird die
/// Schwankung bestimmt. Wächst diese Schwankung mit der Abschnittslänge
/// schneller als mit deren Wurzel, verstärken sich Bewegungen; wächst sie
/// langsamer, gleichen sie sich aus.
///
/// Gegenüber der älteren Spannweitenanalyse hat dieses Verfahren den
/// entscheidenden Vorteil, dass ein überlagerter Trend das Ergebnis nicht
/// verfälscht — und Kursreihen haben immer einen.
/// </summary>
public static class Regime
{
    /// <summary>
    /// Schätzt den Hurst-Exponenten aus den Renditen.
    /// </summary>
    public static RegimeState Analyze(ReadOnlySpan<double> returns, int minBox = 8, int maxBox = 0)
    {
        var n = returns.Length;
        if (n < 64) return new RegimeState(0.5, "richtungslos", 0, 0, n);

        double mean = 0;
        foreach (var v in returns) mean += v;
        mean /= n;

        // Kumulierte Abweichung vom Mittel — das „Profil" der Reihe.
        var y = new double[n];
        double run = 0;
        for (var i = 0; i < n; i++)
        {
            run += returns[i] - mean;
            y[i] = run;
        }

        var top = maxBox > 0 ? maxBox : n / 4;
        if (top <= minBox) return new RegimeState(0.5, "richtungslos", 0, 0, n);

        var xs = new List<double>();
        var ys = new List<double>();

        /* Abschnittslängen geometrisch abgestuft, nicht linear: Der Zusammenhang
           wird in doppelt logarithmischer Auftragung ausgewertet, und dort
           liegen geometrische Stufen gleichmäßig. Lineare Stufen häuften alle
           Punkte am rechten Rand. */
        for (double s = minBox; s <= top; s *= 1.35)
        {
            var box = (int)Math.Round(s);
            if (box < 4) continue;

            var boxes = n / box;
            if (boxes < 4) break;

            double sumSq = 0;

            for (var b = 0; b < boxes; b++)
            {
                var off = b * box;

                // Gerade an den Abschnitt anpassen und abziehen.
                double sx = 0, sy = 0, sxx = 0, sxy = 0;
                for (var i = 0; i < box; i++)
                {
                    sx += i; sy += y[off + i];
                    sxx += (double)i * i; sxy += i * y[off + i];
                }

                var den = box * sxx - sx * sx;
                var slope = den != 0 ? (box * sxy - sx * sy) / den : 0;
                var icept = (sy - slope * sx) / box;

                for (var i = 0; i < box; i++)
                {
                    var res = y[off + i] - (slope * i + icept);
                    sumSq += res * res;
                }
            }

            var f = Math.Sqrt(sumSq / (boxes * box));
            if (f <= 0) continue;

            xs.Add(Math.Log(box));
            ys.Add(Math.Log(f));
        }

        if (xs.Count < 4) return new RegimeState(0.5, "richtungslos", 0, 0, n);

        // Steigung der Ausgleichsgeraden ist der gesuchte Exponent.
        var m = xs.Count;
        double mx = xs.Average(), my = ys.Average();
        double num = 0, den2 = 0;

        for (var i = 0; i < m; i++)
        {
            num += (xs[i] - mx) * (ys[i] - my);
            den2 += (xs[i] - mx) * (xs[i] - mx);
        }

        var h = den2 != 0 ? num / den2 : 0.5;

        // Bestimmtheitsmaß: Liegt der Zusammenhang überhaupt auf einer Geraden?
        double ssTot = 0, ssRes = 0;
        var b0 = my - h * mx;

        for (var i = 0; i < m; i++)
        {
            var fit = h * xs[i] + b0;
            ssRes += (ys[i] - fit) * (ys[i] - fit);
            ssTot += (ys[i] - my) * (ys[i] - my);
        }

        var r2 = ssTot > 0 ? Math.Clamp(1 - ssRes / ssTot, 0, 1) : 0;

        h = Math.Clamp(h, 0, 1.2);

        /* Vertrauen aus zwei Bedingungen: Der Wert muss deutlich von 0,5
           abweichen UND die Anpassung muss taugen. Ein Exponent von 0,8 aus
           einer Punktwolke ohne erkennbare Gerade ist keine Aussage. */
        var conf = Math.Clamp(Math.Abs(h - 0.5) / 0.25, 0, 1) * r2;

        var label = h > 0.55 ? "trendfolgend"
                  : h < 0.45 ? "rückkehrend"
                  : "richtungslos";

        return new RegimeState(
            Math.Round(h, 4), label, Math.Round(conf, 4), Math.Round(r2, 4), n);
    }

    /// <summary>
    /// Übersetzt die Lage in Vorabgewichte für die vorhandenen Teilmodelle.
    ///
    /// Bewusst zurückhaltend: Die Gewichte weichen höchstens um den halben
    /// Vertrauenswert von der Gleichverteilung ab. Diese Auswertung ist ein
    /// Hinweis, keine Anweisung — die Gewichtung lernt weiterhin aus den
    /// tatsächlichen Fehlern, und die soll sie nicht überstimmen.
    /// </summary>
    public static Dictionary<string, double> Prior(RegimeState r)
    {
        var w = new Dictionary<string, double>
        {
            ["naive"] = 1, ["drift"] = 1, ["momentum"] = 1,
            ["meanrev"] = 1, ["leadlag"] = 1
        };

        if (r.Confidence <= 0) return w;

        var k = 1 + Math.Clamp(r.Confidence, 0, 1) * 0.5;

        if (r.Label == "trendfolgend")
        {
            w["momentum"] *= k;
            w["drift"] *= k;
            w["meanrev"] /= k;
        }
        else if (r.Label == "rückkehrend")
        {
            w["meanrev"] *= k;
            w["momentum"] /= k;
        }

        var sum = w.Values.Sum();
        foreach (var key in w.Keys.ToList()) w[key] = Math.Round(w[key] / sum, 5);

        return w;
    }
}
