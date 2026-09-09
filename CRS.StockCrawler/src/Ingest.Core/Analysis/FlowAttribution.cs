namespace Ingest.Core.Analysis;

/// <summary>Eine zugeordnete Umverteilung: von wem zu wem, wie viel.</summary>
public sealed record AttributedFlow(int FromAssetId, int ToAssetId, double Amount);

/// <summary>
/// Zerlegung der Umsatzentwicklung in <b>Umverteilung</b> und <b>Zu-/Abfluss</b>.
///
/// Der Gedanke dahinter: Betrachtet man die verfolgten Werte als ein System,
/// zerfällt jede Veränderung in zwei Teile.
///
/// <list type="number">
/// <item><b>Der Kuchen wird größer oder kleiner.</b> Steigt der Gesamtumsatz,
/// kam Geld von außen dazu; fällt er, wurde abgezogen. Das ist der Teil, der
/// das System als Ganzes betrifft.</item>
/// <item><b>Die Stücke werden neu verteilt.</b> Bleibt die Summe gleich und
/// verschieben sich nur die Anteile, ist das Kapital im System geblieben und
/// hat nur den Platz gewechselt.</item>
/// </list>
///
/// Der zweite Teil lässt sich zuordnen, und zwar exakt: Da alle Anteile
/// zusammen stets eins ergeben, ist die Summe dessen, was die Verlierer
/// abgeben, <i>gleich</i> der Summe dessen, was die Gewinner aufnehmen. Man
/// verteilt den Verlust jedes Verlierers anteilig auf die Gewinner.
///
/// <b>Was das ist und was nicht:</b> Es ist ein Modell unter einer klar
/// benannten Annahme — dass das verfolgte Universum geschlossen ist und
/// Anteilsverschiebungen Umschichtung bedeuten. Es ist kein beobachteter
/// Zahlungsstrom. Wer bei einem konkreten Geschäft von wem gekauft hat, steht
/// in Kursdaten nicht. Verlässt Kapital den Markt in etwas, das wir nicht
/// verfolgen, sieht das Modell nur den schrumpfenden Kuchen, nicht das Ziel.
/// </summary>
public static class FlowAttribution
{
    /// <summary>Ergebnis einer Zerlegung über einen Zeitraum.</summary>
    /// <param name="StepsUsed">Wie viele Zeitschritte tatsächlich auswertbar waren.</param>
    /// <param name="AvgParticipants">
    /// Wie viele Werte im Schnitt an einem Schritt teilnahmen. Liegt die Zahl
    /// weit unter dem Betrachtungsraum, dominiert ein Teilmarkt die Rechnung.
    /// </param>
    public sealed record Result(
        double TotalRedistributed,
        double ExternalInflow,
        double ExternalOutflow,
        int StepsUsed,
        double AvgParticipants,
        IReadOnlyList<AttributedFlow> Flows);

    /// <summary>
    /// Zerlegt die Reihe der Umsätze.
    ///
    /// <paramref name="gross"/> hält je Wert den Bruttoumsatz über das
    /// gemeinsame Zeitraster. <paramref name="lagBars"/> erlaubt es, den
    /// Gewinnern einen Vorlauf zuzugestehen: gibt A heute ab und steigt B erst
    /// morgen, wird das mit Lag 1 erfasst.
    /// </summary>
    public static Result Attribute(
        int[] assetIds,
        IReadOnlyDictionary<int, double[]> gross,
        int gridLength,
        int lagBars = 0,
        double minShareChange = 0.0002)
    {
        var flows = new Dictionary<(int From, int To), double>();

        double redistributed = 0, inflow = 0, outflow = 0;
        int steps = 0;
        long participantSum = 0;

        // Puffer für Anteilsänderungen je Schritt.
        var losers = new List<(int Id, double Amount)>();
        var gainers = new List<(int Id, double Amount)>();

        for (var t = 1; t < gridLength; t++)
        {
            var targetIdx = Math.Min(gridLength - 1, t + lagBars);

            /* Verglichen werden ausschließlich Werte, die zu BEIDEN Zeitpunkten
               gehandelt haben.

               Ohne diese Einschränkung misst die Rechnung den Handelskalender
               statt Kapitalbewegung: samstags handelt nur Krypto, der
               Aktienanteil ist null. Freitag → Samstag sähe damit wie ein
               vollständiger Abzug aus Aktien aus, Sonntag → Montag wie die
               Rückkehr — und da montags weit mehr Umsatz stattfindet als
               samstags, bliebe unterm Strich ein gewaltiger Scheinstrom von
               Krypto in Aktien stehen. Gemessen an einer Kalenderregel, nicht
               an einer Kapitalbewegung.

               Die Anteile werden zusätzlich AUF DIESE Teilmenge bezogen, sonst
               schleppte man die Gesamtsumme aus Werten mit, die zu einem der
               beiden Zeitpunkte gar nicht handelten. */
            double prevTotal = 0, currTotal = 0;
            var participants = 0;

            foreach (var id in assetIds)
            {
                if (!gross.TryGetValue(id, out var g)) continue;
                if (g[t - 1] <= 0 || g[targetIdx] <= 0) continue;

                prevTotal += g[t - 1];
                currTotal += g[targetIdx];
                participants++;
            }

            // Unter zwei Teilnehmern gibt es nichts umzuverteilen.
            if (participants < 2 || prevTotal <= 0 || currTotal <= 0) continue;

            steps++;
            participantSum += participants;

            /* Teil eins: die Größe des Kuchens. Wächst der Umsatz derselben
               Werte, kam Geld dazu; schrumpft er, wurde abgezogen. */
            var delta = currTotal - prevTotal;
            if (delta > 0) inflow += delta; else outflow += -delta;

            /* Teil zwei: die Verteilung. Anteile beider Zeitpunkte vergleichen —
               dadurch fällt die Größenänderung heraus und übrig bleibt reine
               Umschichtung. */
            losers.Clear();
            gainers.Clear();

            double lossSum = 0, gainSum = 0;

            foreach (var id in assetIds)
            {
                if (!gross.TryGetValue(id, out var g)) continue;
                if (g[t - 1] <= 0 || g[targetIdx] <= 0) continue;

                var d = g[targetIdx] / currTotal - g[t - 1] / prevTotal;

                if (d < -minShareChange) { losers.Add((id, -d)); lossSum += -d; }
                else if (d > minShareChange) { gainers.Add((id, d)); gainSum += d; }
            }

            if (lossSum <= 0 || gainSum <= 0) continue;

            /* Der zuzuordnende Betrag: der kleinere der beiden Ströme, bewertet
               mit dem Umsatzniveau. Mehr als abgegeben wurde kann nirgends
               ankommen, und mehr als aufgenommen wurde nirgends herkommen. */
            var moved = Math.Min(lossSum, gainSum) * currTotal;
            redistributed += moved;

            foreach (var (fromId, lost) in losers)
            {
                var fromShare = lost / lossSum;

                foreach (var (toId, gained) in gainers)
                {
                    var amount = moved * fromShare * (gained / gainSum);
                    if (amount <= 0) continue;

                    var key = (fromId, toId);
                    flows[key] = flows.GetValueOrDefault(key) + amount;
                }
            }
        }

        var list = flows
            .Select(kv => new AttributedFlow(kv.Key.From, kv.Key.To, kv.Value))
            .OrderByDescending(f => f.Amount)
            .ToList();

        return new Result(
            redistributed, inflow, outflow, steps,
            steps > 0 ? (double)participantSum / steps : 0,
            list);
    }

    /// <summary>
    /// Fasst zugeordnete Ströme zu Gruppen zusammen — etwa je Anlageklasse
    /// oder Branche. Erst dadurch wird das Bild lesbar: 300 Werte ergeben
    /// 90.000 Paare, drei Klassen dagegen neun Zellen.
    /// </summary>
    public static Dictionary<(string From, string To), double> GroupBy(
        IEnumerable<AttributedFlow> flows,
        IReadOnlyDictionary<int, string> groupOf)
    {
        var result = new Dictionary<(string, string), double>();

        foreach (var f in flows)
        {
            if (!groupOf.TryGetValue(f.FromAssetId, out var from)) continue;
            if (!groupOf.TryGetValue(f.ToAssetId, out var to)) continue;

            // Umschichtung innerhalb derselben Gruppe sagt über die Gruppe nichts.
            if (from == to) continue;

            var key = (from, to);
            result[key] = result.GetValueOrDefault(key) + f.Amount;
        }

        return result;
    }
}
