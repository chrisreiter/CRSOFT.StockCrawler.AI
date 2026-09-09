namespace Ingest.Core.Analysis;

/// <summary>Ein Merkmal mit seinem Beitrag, wenn die anderen mitgerechnet werden.</summary>
/// <param name="Coefficient">
/// Wirkung einer Streuungseinheit des Merkmals auf die Folgerendite, in
/// Log-Punkten.
/// </param>
/// <param name="TStat">
/// Koeffizient geteilt durch seinen Standardfehler. Erst diese Zahl entscheidet.
/// </param>
public sealed record FeatureEffect(string Name, double Coefficient, double StdError, double TStat);

/// <summary>Ergebnis einer Kontrollrechnung.</summary>
public sealed record ControlResult(
    int Observations,
    double Correlation,
    IReadOnlyList<FeatureEffect> Univariate,
    IReadOnlyList<FeatureEffect> Joint,
    double[,] DoubleSort,
    int[,] DoubleSortCounts,
    string Note);

/// <summary>
/// Prüft, ob ein Merkmal etwas beiträgt, das ein bekanntes nicht schon erklärt.
///
/// <b>Warum das nötig ist.</b> Der geschätzte Buchgewinn der Halter ist der
/// Abstand des Kurses von einem gleitenden, umsatzgewichteten Mittel. Das ist
/// der Bauplan eines Momentum-Indikators. Dass er die Folgeentwicklung
/// vorhersagt, wäre also möglicherweise nur die Wiederentdeckung eines seit
/// Jahrzehnten bekannten Effekts unter neuem Namen — und ein neues Merkmal, das
/// nichts Neues sagt, ist keines.
///
/// Die Frage lautet deshalb nicht „wirkt es", sondern „wirkt es <i>zusätzlich</i>".
/// Beantwortet wird sie zweifach: über eine gemeinsame Regression und über eine
/// doppelte Sortierung. Die Regression sagt es genauer, die Sortierung
/// anschaulicher — und wenn beide dasselbe sagen, ist die Antwort belastbar.
///
/// <b>Der Fehler, den diese Klasse vermeidet.</b> Werden überlappende
/// Zeitfenster ausgewertet — also die Folgerendite über zwanzig Tage an jedem
/// einzelnen Tag —, geht dieselbe Kursbewegung zwanzigfach in die Rechnung ein.
/// Die Zahl der Beobachtungen sieht dann zwanzigmal größer aus, als sie ist,
/// und jeder t-Wert ist um etwa den Faktor 4,5 zu hoch. Hier wird deshalb nur
/// jede <c>horizon</c>-te Bar verwendet.
/// </summary>
public static class FeatureControl
{
    /// <summary>
    /// Vergleicht zwei Merkmale gegen dieselbe Folgerendite.
    /// </summary>
    /// <param name="data">
    /// Je Wert: das zu prüfende Merkmal, das Kontrollmerkmal und die
    /// marktbereinigten Renditen.
    /// </param>
    public static ControlResult Compare(
        IReadOnlyList<(double?[] Candidate, double?[] Control, double[] Returns)> data,
        string candidateName, string controlName,
        int horizonBars = 20)
    {
        var xs = new List<double>();
        var zs = new List<double>();
        var ys = new List<double>();

        foreach (var (cand, ctrl, ret) in data)
        {
            var n = Math.Min(Math.Min(cand.Length, ctrl.Length), ret.Length);

            /* Nur jede horizon-te Bar: So überlappen sich die Reaktionsfenster
               nicht, und jede Beobachtung ist tatsächlich eine eigene. */
            for (var t = 0; t + horizonBars < n; t += horizonBars)
            {
                if (cand[t] is not { } a || ctrl[t] is not { } b) continue;
                if (double.IsNaN(a) || double.IsNaN(b)) continue;

                double sum = 0;
                for (var k = t + 1; k <= t + horizonBars; k++) sum += ret[k];

                xs.Add(a);
                zs.Add(b);
                ys.Add(sum);
            }
        }

        var m = xs.Count;

        if (m < 200)
            return new ControlResult(m, 0, [], [], new double[5, 5], new int[5, 5],
                "Zu wenige überlappungsfreie Beobachtungen");

        // Beide Merkmale auf Streuung eins, damit die Koeffizienten vergleichbar sind.
        var x = Standardize(xs);
        var z = Standardize(zs);
        var y = ys.ToArray();

        var corr = Dot(x, z) / m;

        var uni = new List<FeatureEffect>
        {
            Simple(candidateName, x, y),
            Simple(controlName, z, y)
        };

        var joint = Bivariate(candidateName, controlName, x, z, y);

        var (grid, counts) = DoubleSort(x, z, y);

        return new ControlResult(
            m, Math.Round(corr, 4), uni, joint, grid, counts,
            $"Nur jede {horizonBars}. Bar ausgewertet — überlappende Fenster würden "
            + "dieselbe Kursbewegung mehrfach zählen und die t-Werte um rund das "
            + $"{Math.Sqrt(horizonBars):F1}-fache überhöhen.");
    }

    private static FeatureEffect Simple(string name, double[] x, double[] y)
    {
        var n = x.Length;
        var my = y.Average();

        double sxy = 0, sxx = 0;
        for (var i = 0; i < n; i++)
        {
            sxy += x[i] * (y[i] - my);
            sxx += x[i] * x[i];
        }

        var b = sxx > 0 ? sxy / sxx : 0;

        double rss = 0;
        for (var i = 0; i < n; i++)
        {
            var r = y[i] - my - b * x[i];
            rss += r * r;
        }

        var se = sxx > 0 ? Math.Sqrt(rss / (n - 2) / sxx) : 0;

        return new FeatureEffect(name, Math.Round(b, 6), Math.Round(se, 6),
            se > 1e-15 ? Math.Round(b / se, 2) : 0);
    }

    /// <summary>
    /// Zwei Merkmale gleichzeitig. Gelöst über die Normalengleichungen — bei
    /// zwei Einflussgrößen ist das eine 2×2-Inverse und braucht kein Verfahren.
    /// </summary>
    private static List<FeatureEffect> Bivariate(
        string n1, string n2, double[] x, double[] z, double[] y)
    {
        var n = x.Length;
        var my = y.Average();

        double sxx = 0, szz = 0, sxz = 0, sxy = 0, szy = 0;

        for (var i = 0; i < n; i++)
        {
            var yc = y[i] - my;
            sxx += x[i] * x[i];
            szz += z[i] * z[i];
            sxz += x[i] * z[i];
            sxy += x[i] * yc;
            szy += z[i] * yc;
        }

        var det = sxx * szz - sxz * sxz;

        // Sind beide Merkmale nahezu dasselbe, ist das Gleichungssystem entartet.
        if (Math.Abs(det) < 1e-12)
            return [new FeatureEffect(n1, 0, 0, 0), new FeatureEffect(n2, 0, 0, 0)];

        var b1 = (szz * sxy - sxz * szy) / det;
        var b2 = (sxx * szy - sxz * sxy) / det;

        double rss = 0;
        for (var i = 0; i < n; i++)
        {
            var r = y[i] - my - b1 * x[i] - b2 * z[i];
            rss += r * r;
        }

        var s2 = rss / (n - 3);

        var se1 = Math.Sqrt(s2 * szz / det);
        var se2 = Math.Sqrt(s2 * sxx / det);

        return
        [
            new FeatureEffect(n1, Math.Round(b1, 6), Math.Round(se1, 6),
                se1 > 1e-15 ? Math.Round(b1 / se1, 2) : 0),
            new FeatureEffect(n2, Math.Round(b2, 6), Math.Round(se2, 6),
                se2 > 1e-15 ? Math.Round(b2 / se2, 2) : 0)
        ];
    }

    /// <summary>
    /// Doppelte Sortierung: erst nach dem Kontrollmerkmal, dann innerhalb jeder
    /// Gruppe nach dem geprüften. Bleibt innerhalb der Zeilen ein Gefälle, trägt
    /// das geprüfte Merkmal etwas bei, was das Kontrollmerkmal nicht erklärt.
    /// </summary>
    private static (double[,] Means, int[,] Counts) DoubleSort(double[] x, double[] z, double[] y)
    {
        var n = x.Length;
        var means = new double[5, 5];
        var counts = new int[5, 5];

        var zOrder = Enumerable.Range(0, n).OrderBy(i => z[i]).ToArray();
        var perRow = n / 5;

        for (var row = 0; row < 5; row++)
        {
            var from = row * perRow;
            var to = row == 4 ? n : (row + 1) * perRow;

            var slice = zOrder[from..to].OrderBy(i => x[i]).ToArray();
            var perCol = Math.Max(1, slice.Length / 5);

            for (var col = 0; col < 5; col++)
            {
                var a = col * perCol;
                var b = col == 4 ? slice.Length : Math.Min(slice.Length, (col + 1) * perCol);

                if (b <= a) continue;

                double sum = 0;
                for (var k = a; k < b; k++) sum += y[slice[k]];

                counts[row, col] = b - a;
                means[row, col] = sum / (b - a);
            }
        }

        return (means, counts);
    }

    private static double[] Standardize(List<double> v)
    {
        var n = v.Count;
        var mean = v.Average();

        var x = new double[n];
        double var2 = 0;

        for (var i = 0; i < n; i++)
        {
            x[i] = v[i] - mean;
            var2 += x[i] * x[i];
        }

        var sd = Math.Sqrt(var2 / n);
        if (sd < 1e-15) return x;

        for (var i = 0; i < n; i++) x[i] /= sd;
        return x;
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>
    /// Einfaches Momentum: die Rendite über die zurückliegenden
    /// <paramref name="lookback"/> Bars.
    ///
    /// Bewusst ohne jede Verfeinerung — es ist die Kontrolle, nicht der
    /// Kandidat. Je einfacher sie ist, desto überzeugender ist ein Beitrag, der
    /// sie überlebt.
    /// </summary>
    public static double?[] Momentum(double[] closes, int lookback = 120)
    {
        var n = closes.Length;
        var outp = new double?[n];

        for (var t = lookback; t < n; t++)
        {
            if (closes[t] <= 0 || closes[t - lookback] <= 0) continue;
            outp[t] = Math.Log(closes[t] / closes[t - lookback]);
        }

        return outp;
    }
}
