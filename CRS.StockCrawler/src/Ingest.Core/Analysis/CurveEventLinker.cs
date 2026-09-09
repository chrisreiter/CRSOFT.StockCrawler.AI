namespace Ingest.Core.Analysis;

/// <summary>Ein Ereignis, so wie die Verknüpfung es braucht.</summary>
public sealed record LinkableEvent(
    int AssetId, DateTime TsUtc, string Type, int Sign, double Severity);

/// <summary>
/// Zwei Werte, deren Ereignisse auffällig oft dicht beieinanderliegen.
/// </summary>
/// <param name="Pairs">Wie oft es zusammen auftrat.</param>
/// <param name="Expected">Wie oft es bei Unabhängigkeit zu erwarten wäre.</param>
/// <param name="Lift">Verhältnis der beiden. Eins heißt: nichts Besonderes.</param>
/// <param name="MedianLagBars">
/// Positiv, wenn B typischerweise NACH A kommt. Das ist die einzige Zahl hier,
/// die eine Richtung behauptet — und die einzige, die sich für eine Prognose
/// verwenden ließe.
/// </param>
/// <param name="LeadShare">
/// Anteil der Paare, bei denen B tatsächlich nach A kam. Bei 0,5 gibt es keine
/// Ordnung, nur Gleichzeitigkeit.
/// </param>
public sealed record EventLink(
    int AssetA, int AssetB,
    string TypeA, string TypeB,
    int Pairs, double Expected, double Lift,
    double MedianLagBars, double LeadShare, double MeanSeverity);

/// <summary>
/// Setzt die Ereignisse verschiedener Werte zueinander in Beziehung.
///
/// <b>Die Falle, um die es hier geht.</b> Zählt man einfach, wie oft zwei Werte
/// am selben Tag ein Ereignis haben, gewinnen immer die Paare mit den meisten
/// Ereignissen. Ein Wert mit 400 Ereignissen und einer mit 300 treffen sich in
/// zehn Jahren zwangsläufig oft — ohne dass das irgendetwas bedeutet. Gemessen
/// wird deshalb gegen die Erwartung bei Unabhängigkeit:
///
/// <code>erwartet ≈ n_A · n_B · (2·Fenster+1) / Gesamtzahl der Bars</code>
///
/// Erst das Verhältnis von beobachtet zu erwartet ist eine Aussage.
///
/// <b>Gemeinsame Handelszeitpunkte.</b> Die Gesamtzahl der Bars ist die der
/// Zeitpunkte, an denen BEIDE Werte gehandelt haben. Nimmt man stattdessen das
/// gemeinsame Zeitraster, ist die Erwartung für jedes Aktien-Krypto-Paar zu
/// klein, weil die Aktie an einem Drittel der Tage gar nicht handeln konnte —
/// und jedes solche Paar sähe auffällig aus. Dieselbe Falle wie an drei anderen
/// Stellen dieses Systems.
///
/// <b>Was ein Vorlauf ist und was nicht.</b> Ein hoher Lift bei einem
/// Median-Abstand von null heißt: Die beiden bewegen sich gemeinsam. Das ist
/// eine Aussage über Struktur, keine über Vorhersagbarkeit. Erst ein Abstand
/// deutlich über null, bei dem der Anteil der Fälle mit B-nach-A klar über der
/// Hälfte liegt, wäre ein Vorlauf — und selbst dann muss er sich in einem
/// getrennten Zeitraum wiederholen, bevor man ihm glauben darf.
/// </summary>
public static class CurveEventLinker
{
    /// <param name="events">Alle Ereignisse aller Werte, beliebige Reihenfolge.</param>
    /// <param name="tradingDays">
    /// Je Wert die Zeitpunkte, an denen er gehandelt hat. Grundlage der
    /// Erwartungsrechnung.
    /// </param>
    /// <param name="windowBars">Wie weit „dicht beieinander" reicht.</param>
    /// <param name="barDuration">Dauer einer Bar, für die Umrechnung in Zeit.</param>
    /// <param name="minPairs">Weniger Treffer als das ist Zufall, nicht Befund.</param>
    /// <param name="minLift">Ab welchem Vielfachen der Erwartung berichtet wird.</param>
    public static List<EventLink> Link(
        IReadOnlyList<LinkableEvent> events,
        IReadOnlyDictionary<int, HashSet<DateTime>> tradingDays,
        int windowBars,
        TimeSpan barDuration,
        int minPairs = 8,
        double minLift = 1.3)
    {
        var byAsset = events.GroupBy(e => e.AssetId)
                            .ToDictionary(g => g.Key,
                                          g => g.OrderBy(e => e.TsUtc).ToList());

        var assets = byAsset.Keys.OrderBy(x => x).ToList();
        var window = barDuration * windowBars;

        var result = new List<EventLink>();

        for (var ia = 0; ia < assets.Count; ia++)
        {
            for (var ib = ia + 1; ib < assets.Count; ib++)
            {
                var a = assets[ia];
                var b = assets[ib];

                if (!tradingDays.TryGetValue(a, out var daysA)) continue;
                if (!tradingDays.TryGetValue(b, out var daysB)) continue;

                /* Nur Zeitpunkte, an denen beide gehandelt haben — sowohl für
                   die Ereignisse als auch für den Nenner der Erwartung. */
                var common = daysA.Count < daysB.Count
                    ? daysA.Where(daysB.Contains).ToHashSet()
                    : daysB.Where(daysA.Contains).ToHashSet();

                if (common.Count < 200) continue;

                var evA = byAsset[a].Where(e => common.Contains(e.TsUtc)).ToList();
                var evB = byAsset[b].Where(e => common.Contains(e.TsUtc)).ToList();

                if (evA.Count == 0 || evB.Count == 0) continue;

                // Je Kombination von Ereignisarten getrennt zählen: Dass ein
                // Hochpunkt bei A mit einem Tiefpunkt bei B zusammenfällt, ist
                // etwas anderes als zwei Hochpunkte.
                foreach (var ga in evA.GroupBy(e => e.Type))
                {
                    foreach (var gb in evB.GroupBy(e => e.Type))
                    {
                        var la = ga.ToList();
                        var lb = gb.ToList();

                        if (la.Count < minPairs || lb.Count < minPairs) continue;

                        var lags = new List<double>();
                        double sevSum = 0;
                        var pairs = 0;
                        var after = 0;

                        var j0 = 0;

                        foreach (var x in la)
                        {
                            // Fenster mitwandern lassen statt jedes Mal neu zu
                            // suchen — sonst ist der Aufwand quadratisch in der
                            // Ereigniszahl, und die ist bei 300 Werten groß.
                            while (j0 < lb.Count && lb[j0].TsUtc < x.TsUtc - window) j0++;

                            for (var j = j0; j < lb.Count; j++)
                            {
                                var dt = lb[j].TsUtc - x.TsUtc;
                                if (dt > window) break;

                                pairs++;
                                sevSum += (x.Severity + lb[j].Severity) / 2;

                                var lag = dt.TotalMinutes / barDuration.TotalMinutes;
                                lags.Add(lag);

                                if (lag > 0) after++;
                            }
                        }

                        if (pairs < minPairs) continue;

                        /* Erwartung bei Unabhängigkeit. Ein Ereignis von A hat
                           2w+1 Bars, in denen ein Ereignis von B als Treffer
                           zählt; die Wahrscheinlichkeit dafür ist der Anteil
                           der B-Ereignisse an allen gemeinsamen Bars. */
                        var expected = la.Count * (double)lb.Count
                                     * (2 * windowBars + 1) / common.Count;

                        if (expected <= 0) continue;

                        var lift = pairs / expected;
                        if (lift < minLift) continue;

                        lags.Sort();

                        result.Add(new EventLink(
                            a, b, ga.Key, gb.Key,
                            pairs,
                            Math.Round(expected, 2),
                            Math.Round(lift, 3),
                            Math.Round(lags[lags.Count / 2], 2),
                            Math.Round(after / (double)pairs, 3),
                            Math.Round(sevSum / pairs, 1)));
                    }
                }
            }
        }

        return result.OrderByDescending(r => r.Lift).ToList();
    }
}
