namespace Ingest.Core.Analysis;

/// <summary>
/// Schätzt, welcher Anteil der Halter eines Wertes gerade im Gewinn liegt.
///
/// <b>Woher der Gedanke stammt.</b> Der Dispositionseffekt — Anleger
/// realisieren Gewinne zu früh und halten Verluste zu lange — ist seit
/// Shefrin/Statman (1985) beschrieben und von Odean an Kursdaten bestätigt.
/// Der Bezugspunkt ist dabei stets der eigene Kaufpreis.
///
/// <b>Was daraus folgt.</b> Liegt der Kurs deutlich über dem, was die Halter
/// bezahlt haben, entsteht Verkaufsdruck: Viele sitzen auf Buchgewinnen und
/// wollen sie sichern. Liegt er darunter, entsteht das Gegenteil — es wird
/// gehalten und ausgesessen. Der durchschnittliche Einstandspreis ist damit
/// eine Größe mit Wirkung auf den Kurs, und er lässt sich näherungsweise aus
/// dem rekonstruieren, was wir haben: Kurs und Umsatz je Bar.
///
/// <b>Wie geschätzt wird.</b> Jede vergangene Bar steht für Anteile, die zu
/// ihrem Kurs den Besitzer wechselten; ihr Gewicht ist ihr Umsatz. Ältere Bars
/// zählen weniger, weil diese Anteile inzwischen mit hoher Wahrscheinlichkeit
/// weiterverkauft wurden. Daraus ergibt sich ein umsatzgewichteter,
/// abklingender Durchschnittskurs — der geschätzte Einstand der heutigen
/// Halter.
///
/// <b>Wo die Grenze liegt.</b> Sauber wäre die Gewichtung über den Umschlag,
/// also Umsatz geteilt durch die Zahl ausstehender Anteile (so bei
/// Grinblatt/Han 2005). Die Zahl der ausstehenden Anteile haben wir nicht. Die
/// Abklingzeit tritt an ihre Stelle: Sie unterstellt für alle Werte denselben
/// Umschlag, was für einen viel gehandelten Wert zu langsam und für einen
/// trägen zu schnell ist. Das ist eine Näherung und als solche zu behandeln.
/// </summary>
public static class GainsOverhang
{
    /// <summary>
    /// Berechnet je Bar den Überhang: wie weit der Kurs über dem geschätzten
    /// Einstand der Halter liegt.
    ///
    /// Positiv heißt: Die Halter sind im Schnitt im Gewinn. Der Wert ist ein
    /// Anteil, keine Prozentzahl — 0,15 bedeutet 15 Prozent über dem Einstand.
    /// </summary>
    /// <param name="halfLifeBars">
    /// Nach wie vielen Bars das Gewicht einer alten Bar auf die Hälfte fällt.
    /// Sie steht für die Umschlagsgeschwindigkeit des Bestands.
    /// </param>
    /// <param name="lookback">
    /// Wie weit zurückgeschaut wird. Weiter als etwa das Zehnfache der
    /// Halbwertszeit trägt nichts mehr bei und kostet nur Rechenzeit.
    /// </param>
    public static double?[] Compute(
        double[] closes, double?[] volumes,
        int halfLifeBars = 60, int lookback = 0)
    {
        var n = closes.Length;
        var result = new double?[n];

        if (n < 32) return result;

        if (lookback <= 0) lookback = Math.Min(n, halfLifeBars * 8);

        var decay = Math.Pow(0.5, 1.0 / Math.Max(1, halfLifeBars));

        for (var t = 1; t < n; t++)
        {
            var from = Math.Max(0, t - lookback);

            // Zu wenig Vorgeschichte ergibt keinen belastbaren Einstand.
            if (t - from < 20) continue;

            double num = 0, den = 0;
            var w = 1.0;

            /* Rückwärts laufen, damit das Gewicht einfach fortgeschrieben
               werden kann statt für jede Bar neu potenziert zu werden. */
            for (var i = t - 1; i >= from; i--)
            {
                var v = volumes[i] ?? 0;
                if (v > 0 && closes[i] > 0)
                {
                    num += w * v * closes[i];
                    den += w * v;
                }

                w *= decay;
            }

            if (den <= 0 || closes[t] <= 0) continue;

            var basis = num / den;
            if (basis <= 0) continue;

            result[t] = (closes[t] - basis) / basis;
        }

        return result;
    }

    /// <summary>
    /// Prüft, ob der Überhang etwas über die weitere Entwicklung sagt.
    ///
    /// Verglichen wird gegen die <b>marktbereinigte</b> Rendite: Ohne diesen
    /// Abzug misst man den Marktfaktor, der über die Hälfte aller Bewegung
    /// erklärt, und findet einen Zusammenhang, wo keiner ist.
    ///
    /// Zurückgegeben wird die mittlere Folgerendite je Fünftel des Überhangs.
    /// Besteht ein Zusammenhang, ordnen sich die Fünftel; besteht keiner,
    /// springen sie.
    /// </summary>
    public static (double[] MeanByQuintile, int[] CountByQuintile, double Spread, double TStat)
        Evaluate(
            IReadOnlyList<(double?[] Overhang, double[] NeutralReturns)> data,
            int horizonBars = 20)
    {
        var buckets = new List<double>[5];
        for (var i = 0; i < 5; i++) buckets[i] = [];

        // Alle Beobachtungen einsammeln, um die Fünftelgrenzen zu bestimmen.
        var all = new List<(double Value, double Forward)>();

        foreach (var (over, ret) in data)
        {
            var n = Math.Min(over.Length, ret.Length);

            for (var t = 0; t < n - horizonBars; t++)
            {
                if (over[t] is not { } v) continue;

                double sum = 0;
                for (var k = t + 1; k <= t + horizonBars; k++) sum += ret[k];

                all.Add((v, sum));
            }
        }

        if (all.Count < 500)
            return (new double[5], new int[5], 0, 0);

        var sorted = all.Select(x => x.Value).OrderBy(v => v).ToArray();

        double Q(double p) => sorted[Math.Clamp((int)(p * sorted.Length), 0, sorted.Length - 1)];

        var edges = new[] { Q(0.2), Q(0.4), Q(0.6), Q(0.8) };

        foreach (var (v, f) in all)
        {
            var b = v <= edges[0] ? 0
                  : v <= edges[1] ? 1
                  : v <= edges[2] ? 2
                  : v <= edges[3] ? 3
                  : 4;

            buckets[b].Add(f);
        }

        var means = new double[5];
        var counts = new int[5];

        for (var i = 0; i < 5; i++)
        {
            counts[i] = buckets[i].Count;
            means[i] = counts[i] > 0 ? buckets[i].Average() : 0;
        }

        /* Der Abstand zwischen dem obersten und dem untersten Fünftel ist die
           eigentliche Aussage — und sein t-Wert entscheidet, ob er mehr ist als
           die Streuung zweier großer Stichproben. */
        var spread = means[4] - means[0];

        double Var(List<double> x)
        {
            if (x.Count < 2) return 0;
            var m = x.Average();
            return x.Sum(v => (v - m) * (v - m)) / (x.Count - 1);
        }

        var se = Math.Sqrt(Var(buckets[4]) / Math.Max(1, counts[4])
                         + Var(buckets[0]) / Math.Max(1, counts[0]));

        return (means, counts, spread, se > 1e-15 ? spread / se : 0);
    }
}
