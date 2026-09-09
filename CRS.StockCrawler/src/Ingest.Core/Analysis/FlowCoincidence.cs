namespace Ingest.Core.Analysis;

/// <summary>
/// Ein Zeitpunkt, an dem sich am Kapital im verfolgten Markt etwas
/// Ungewöhnliches getan hat.
/// </summary>
/// <param name="Kind">
/// <c>Zufluss</c> oder <c>Abfluss</c>, wenn sich der Gesamtumsatz auffällig
/// verändert hat; <c>Umschichtung</c>, wenn die Summe blieb und sich nur die
/// Anteile verschoben haben.
/// </param>
public sealed record FlowAnomaly(
    DateTime TsUtc,
    int Index,
    int Severity,
    string Kind,
    double TotalFlow,
    double ChangePct,
    double TurnoverPct);

/// <summary>
/// Ein Wert, der in zeitlicher Nähe zu Kapitalauffälligkeiten wiederholt selbst
/// auffällig wurde.
/// </summary>
/// <param name="Lift">
/// Wie viel häufiger als zufällig zu erwarten. 1,0 heißt: genau wie der Zufall
/// es hergäbe — also nichts. Erst deutlich darüber wird es interessant.
/// </param>
/// <param name="AvgLagBars">
/// Negativ heißt: der Wert war <i>vor</i> der Kapitalauffälligkeit dran — der
/// interessante Fall, denn nur solche Werte taugen als Frühindikator.
/// </param>
public sealed record CoincidingAsset(
    int AssetId,
    string Symbol,
    int Hits,
    double Lift,
    double AvgSeverity,
    int MaxSeverity,
    double AvgLagBars,
    double LeadSharePct);

/// <summary>
/// Verbindet die beiden Hälften: Wo hat sich am Kapital etwas getan, und
/// welche Kurse waren in unmittelbarer zeitlicher Nähe auffällig?
///
/// <b>Die Falle, die hier lauert:</b> Ein täglich stark schwankender Wert ist
/// in der Nähe von <i>allem</i> auffällig — auch in der Nähe zufällig
/// gewürfelter Zeitpunkte. Wer nur zählt, wie oft etwas zusammenfällt, findet
/// deshalb zuverlässig die unruhigsten Werte und hält das für einen
/// Zusammenhang. Deswegen wird jeder Treffer gegen die Erwartung gerechnet:
/// Wie oft <i>müsste</i> dieser Wert allein aufgrund seiner eigenen Unruhe in
/// diesem Zeitfenster auffällig sein? Erst der Überschuss darüber zählt.
///
/// Und auch der ist ein Hinweis, kein Nachweis. Gleichzeitigkeit ist keine
/// Ursache.
/// </summary>
public static class FlowCoincidence
{
    /// <summary>
    /// Findet Zeitpunkte auffälliger Kapitalbewegung.
    ///
    /// Bewertet werden zwei verschiedene Dinge: die Veränderung der
    /// <b>Gesamtsumme</b> — das ist Geld, das ins System kam oder es verließ —
    /// und der <b>Umschlag der Anteile</b> bei gleichbleibender Summe, also
    /// Kapital, das im System blieb und nur den Platz wechselte. Beides kann
    /// unabhängig voneinander auffällig sein.
    /// </summary>
    public static List<FlowAnomaly> FindAnomalies(
        DateTime[] grid,
        IReadOnlyDictionary<int, double[]> grossPerAsset,
        int window = EventDetector.DefaultWindow,
        int minSeverity = EventDetector.DefaultMinSeverity)
    {
        var result = new List<FlowAnomaly>();
        var n = grid.Length;
        if (n < window + 2) return result;

        var change = new double[n];
        var turnover = new double[n];
        var levels = new double[n];

        for (var i = 1; i < n; i++)
        {
            /* Wie überall in dieser Auswertung: verglichen werden nur Werte,
               die zu BEIDEN Zeitpunkten gehandelt haben.

               Ohne das misst man den Handelskalender. Der Gesamtumsatz eines
               Samstags liegt bei einem Zwanzigstel des Freitags, weil nur
               Krypto handelt — jedes Wochenende erschiene als
               Jahrhundertereignis, und die echten Ereignisse gingen zwischen
               104 Fehlalarmen pro Jahr unter. */
            double prevTotal = 0, currTotal = 0;
            var participants = 0;

            foreach (var g in grossPerAsset.Values)
            {
                if (g[i - 1] <= 0 || g[i] <= 0) continue;

                prevTotal += g[i - 1];
                currTotal += g[i];
                participants++;
            }

            if (participants < 2 || prevTotal <= 0 || currTotal <= 0) continue;

            levels[i] = currTotal;
            change[i] = Math.Log(currTotal / prevTotal);

            /* Anteilsumschlag: die Hälfte der Summe aller Anteilsänderungen.
               Der Faktor ein Halb, weil jede Verschiebung zweimal gezählt wird
               — einmal beim Abgeber, einmal beim Empfänger. Ergebnis: der
               Bruchteil des Marktes, der den Platz gewechselt hat. */
            double sum = 0;
            foreach (var g in grossPerAsset.Values)
            {
                if (g[i - 1] <= 0 || g[i] <= 0) continue;
                sum += Math.Abs(g[i] / currTotal - g[i - 1] / prevTotal);
            }

            turnover[i] = sum / 2;
        }

        var chBuf = new double[window];
        var toBuf = new double[window];

        for (var i = window + 1; i < n; i++)
        {
            if (levels[i] <= 0) continue;   // Schritt war nicht auswertbar

            for (var k = 0; k < window; k++)
            {
                chBuf[k] = Math.Abs(change[i - window + k]);
                toBuf[k] = turnover[i - window + k];
            }

            var typicalChange = Median(chBuf);
            var typicalTurnover = Median(toBuf);

            var zChange = typicalChange > 1e-9
                ? Math.Abs(change[i]) / (typicalChange * 1.4826) : 0;

            var zTurnover = typicalTurnover > 1e-9
                ? Math.Max(0, turnover[i] / typicalTurnover - 1) : 0;

            /* Welche der beiden Auffälligkeiten überwiegt, entscheidet die
               Benennung. Ein Zufluss verändert die Summe; eine Umschichtung
               lässt sie in Ruhe und mischt die Anteile durch. */
            var sevChange = EventDetector.ToScale(zChange);
            var sevTurnover = EventDetector.ToScale(zTurnover * 2);

            string kind;
            int severity;

            if (sevChange >= sevTurnover)
            {
                severity = sevChange;
                kind = change[i] > 0 ? "Zufluss" : "Abfluss";
            }
            else
            {
                severity = sevTurnover;
                kind = "Umschichtung";
            }

            if (severity < minSeverity) continue;

            result.Add(new FlowAnomaly(
                grid[i], i, severity, kind,
                Math.Round(levels[i], 2),
                Math.Round((Math.Exp(change[i]) - 1) * 100, 3),
                Math.Round(turnover[i] * 100, 3)));
        }

        return result;
    }

    /// <summary>
    /// Ordnet den Kapitalauffälligkeiten die Kursauffälligkeiten zu, die in
    /// ihrer zeitlichen Nähe lagen.
    ///
    /// <paramref name="windowBars"/> spannt das Fenster nach beiden Seiten auf.
    /// Was davor lag, ist ein möglicher Frühindikator; was danach kam, eine
    /// mögliche Folge.
    /// </summary>
    public static List<CoincidingAsset> Match(
        IReadOnlyList<FlowAnomaly> anomalies,
        IReadOnlyList<MarketEvent> events,
        DateTime[] grid,
        int windowBars = 3)
    {
        var result = new List<CoincidingAsset>();
        if (anomalies.Count == 0 || events.Count == 0 || grid.Length == 0) return result;

        var index = new Dictionary<DateTime, int>(grid.Length);
        for (var i = 0; i < grid.Length; i++) index[grid[i]] = i;

        var anomalyIdx = anomalies.Select(a => a.Index).ToArray();
        Array.Sort(anomalyIdx);

        // Ereignisse je Wert, auf Rasterpositionen übersetzt.
        var perAsset = new Dictionary<int, (string Symbol, List<(int Idx, int Sev)> Items)>();

        foreach (var e in events)
        {
            if (!index.TryGetValue(e.TsUtc, out var idx)) continue;

            if (!perAsset.TryGetValue(e.AssetId, out var entry))
            {
                entry = (e.Symbol, []);
                perAsset[e.AssetId] = entry;
            }
            entry.Items.Add((idx, e.Severity));
        }

        var span = windowBars * 2 + 1;

        foreach (var (assetId, (symbol, items)) in perAsset)
        {
            if (items.Count == 0) continue;

            int hits = 0, maxSev = 0, leads = 0;
            double sevSum = 0, lagSum = 0;

            foreach (var (idx, sev) in items)
            {
                /* Nächstgelegene Kapitalauffälligkeit suchen. Liegt sie im
                   Fenster, zählt das als Treffer — und zwar genau einmal, auch
                   wenn mehrere Auffälligkeiten in Reichweite sind. */
                var pos = Array.BinarySearch(anomalyIdx, idx);
                if (pos < 0) pos = ~pos;

                var best = int.MaxValue;
                for (var p = Math.Max(0, pos - 1); p <= Math.Min(anomalyIdx.Length - 1, pos); p++)
                {
                    var d = idx - anomalyIdx[p];
                    if (Math.Abs(d) < Math.Abs(best)) best = d;
                }

                if (best == int.MaxValue || Math.Abs(best) > windowBars) continue;

                hits++;
                sevSum += sev;
                lagSum += best;
                if (sev > maxSev) maxSev = sev;
                if (best < 0) leads++;
            }

            if (hits == 0) continue;

            /* Die Erwartung bei Zufall: Anteil der Bars, an denen dieser Wert
               ohnehin auffällig ist, mal der Zahl der überwachten Bars rund um
               die Kapitalauffälligkeiten. Die Fenster können sich überlappen —
               deshalb ist das eine Obergrenze der abgedeckten Bars, und der
               ausgewiesene Faktor damit eher zu vorsichtig als zu großzügig. */
            var eventRate = (double)items.Count / grid.Length;
            var covered = Math.Min(grid.Length, anomalies.Count * span);
            var expected = eventRate * covered;

            result.Add(new CoincidingAsset(
                assetId, symbol, hits,
                expected > 0 ? Math.Round(hits / expected, 2) : 0,
                Math.Round(sevSum / hits, 1),
                maxSev,
                Math.Round(lagSum / hits, 2),
                Math.Round(leads * 100.0 / hits, 1)));
        }

        return result;
    }

    private static double Median(double[] buf)
    {
        var copy = buf.AsSpan().ToArray();
        Array.Sort(copy);

        var m = copy.Length / 2;
        return copy.Length % 2 == 1 ? copy[m] : (copy[m - 1] + copy[m]) / 2;
    }
}
