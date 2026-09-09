using Ingest.Core.Enums;
using Ingest.Core.Models;

namespace Ingest.Core.Analysis;

public sealed record QualityReport(int Kept, int Rejected, double WorstFactor)
{
    /// <summary>
    /// Anteil verworfener Bars. Ein hoher Wert deutet nicht auf einzelne
    /// Ausreißer hin, sondern darauf, dass das Symbol auf das falsche Papier
    /// zeigt.
    /// </summary>
    public double RejectRatio => Kept + Rejected == 0 ? 0 : (double)Rejected / (Kept + Rejected);
}

/// <summary>
/// Verwirft Bars mit unmöglichen Sprüngen gegenüber der Vorgängerbar.
///
/// Hintergrund: die Zuordnung eines CoinGecko-Coins auf einen Yahoo-Ticker über
/// das Schema <c>SYMBOL-USD</c> trifft bei kleineren Token gelegentlich ein
/// ganz anderes Papier. Das fällt nicht durch fehlende Daten auf, sondern durch
/// Kursreihen, die um Größenordnungen springen — beobachtet wurden Tagessprünge
/// um den Faktor 1254. Solche Reihen ruinieren jede Korrelation und lassen die
/// Prognosefehler explodieren.
///
/// Eine gepflegte Ausnahmeliste würde dem Problem immer hinterherlaufen,
/// deshalb wird stattdessen die Plausibilität der Reihe selbst geprüft.
/// </summary>
public static class BarQualityFilter
{
    /// <summary>
    /// Höchster noch akzeptierter Faktor zwischen zwei aufeinanderfolgenden
    /// Schlusskursen. Auch sehr volatile Kryptowerte bewegen sich an einem Tag
    /// nicht um das Zehnfache; an einer Stunde erst recht nicht.
    /// </summary>
    public static double MaxFactor(string intervalCode)
        => intervalCode == BarInterval.Hourly ? 5.0 : 10.0;

    /// <summary>
    /// Ab diesem Anteil verworfener Bars ist von einer falschen Symbolzuordnung
    /// auszugehen, nicht von einzelnen Fehlkursen.
    /// </summary>
    public const double SuspiciousRatio = 0.02;

    public static (List<PriceBar> Bars, QualityReport Report) Clean(
        IReadOnlyList<PriceBar> bars, string intervalCode)
    {
        var max = MaxFactor(intervalCode);
        var min = 1.0 / max;

        var kept = new List<PriceBar>(bars.Count);
        var rejected = 0;
        var worst = 1.0;

        decimal? previous = null;

        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];

            if (b.Close <= 0)
            {
                rejected++;
                continue;
            }

            if (previous is > 0)
            {
                var factor = (double)(b.Close / previous.Value);

                if (factor > max || factor < min)
                {
                    /* Zwei Fälle sehen an dieser Stelle gleich aus:
                       ein einzelner Fehlkurs, der danach wieder verschwindet —
                       und eine echte Neubewertung, die auf dem neuen Niveau
                       bleibt. Unterscheiden lassen sie sich nur am Folgekurs.
                       Bleibt der beim neuen Niveau, wird darauf neu verankert;
                       sonst gilt die Bar als Ausreißer. */
                    var levelHolds = i + 1 < bars.Count
                                     && bars[i + 1].Close > 0
                                     && Within((double)(bars[i + 1].Close / b.Close), max, min);

                    if (!levelHolds)
                    {
                        rejected++;
                        worst = Math.Max(worst, Math.Max(factor, 1.0 / factor));

                        // Vorgänger bewusst NICHT fortschreiben: sonst würde
                        // nach einem Ausreißer auch die gesunde Folgebar
                        // verworfen.
                        continue;
                    }

                    // Neubewertung: Sprung festhalten, Reihe aber fortführen.
                    worst = Math.Max(worst, Math.Max(factor, 1.0 / factor));
                }
            }

            kept.Add(b);
            previous = b.Close;
        }

        return (kept, new QualityReport(kept.Count, rejected, worst));
    }

    private static bool Within(double factor, double max, double min)
        => factor <= max && factor >= min;
}
