namespace Ingest.Core.Analysis;

/// <summary>
/// Wie ein Wert auf die Ereignisse eines anderen reagiert.
/// </summary>
/// <param name="MeanResponsePct">
/// Mittlere Bewegung des Empfängers nach einem Ereignis des Senders, in Prozent
/// und <b>marktbereinigt</b>.
/// </param>
/// <param name="ConsistencyPct">
/// Anteil der Fälle, in denen die Reaktion in dieselbe Richtung ging wie im
/// Mittel. Fünfzig Prozent heißt: keine Regel, nur Streuung.
/// </param>
/// <param name="TStat">
/// Mittelwert geteilt durch seinen eigenen Standardfehler. Erst diese Zahl
/// verrät, ob der Mittelwert etwas anderes ist als Zufall bei kleiner Fallzahl.
/// </param>
public sealed record TransmissionLink(
    int FromAssetId, string FromSymbol,
    int ToAssetId, string ToSymbol,
    int HorizonBars,
    int Events,
    double MeanResponsePct,
    double ConsistencyPct,
    double TStat);

/// <summary>
/// Sucht wiederkehrende Übertragungen: Wenn sich Kurs A auffällig ändert, tut
/// Kurs B danach regelmäßig etwas Bestimmtes.
///
/// <b>Die Falle, ohne die jedes Ergebnis wertlos ist.</b> Fällt A stark, fällt
/// meistens auch B — weil der ganze Markt fällt. Die Modenanalyse hat gemessen,
/// wie groß dieser gemeinsame Anteil ist: über die Hälfte der gesamten
/// Bewegung. Wer die rohe Reaktion misst, findet deshalb für praktisch
/// <i>jedes</i> Paar eine starke Übertragung und hat nichts gefunden außer dem
/// Markt.
///
/// Gemessen wird deshalb die <b>marktbereinigte</b> Reaktion: die Bewegung des
/// Empfängers abzüglich dessen, was alle Werte im selben Zeitraum im Mittel
/// taten. Übrig bleibt nur, was diesen einen Wert von den anderen unterscheidet
/// — und nur das kann eine Übertragung von A nach B sein.
///
/// <b>Was das ausdrücklich nicht ist.</b> Eine gefundene Regelmäßigkeit ist
/// keine Ursache. A kann B bewegen; beide können von einem Dritten bewegt
/// werden; oder die Reihenfolge ist zufällig so herum ausgefallen. Deshalb
/// zählt am Ende nur, ob dieselbe Übertragung in einem Zeitraum wieder
/// auftaucht, den die Suche nie gesehen hat.
/// </summary>
public static class Transmission
{
    /// <summary>
    /// Marktbereinigte Renditen: von jeder Rendite wird der Querschnitt aller
    /// Werte zum selben Zeitpunkt abgezogen.
    /// </summary>
    public static Dictionary<int, double[]> MarketNeutral(
        IReadOnlyDictionary<int, double[]> closes)
    {
        var ids = closes.Keys.ToArray();
        if (ids.Length < 3) return [];

        var len = closes[ids[0]].Length;

        var ret = new Dictionary<int, double[]>(ids.Length);

        foreach (var id in ids)
        {
            var c = closes[id];
            var r = new double[len];

            for (var i = 1; i < len; i++)
                r[i] = c[i - 1] > 0 && c[i] > 0 ? Math.Log(c[i] / c[i - 1]) : 0;

            ret[id] = r;
        }

        /* Der Querschnitt je Zeitpunkt ist der Marktanteil der Bewegung. Sein
           Abzug ist die einfachste Form der Marktbereinigung und hier die
           richtige: Sie unterstellt nichts über Betas, die sich über
           fünfundzwanzig Jahre ohnehin ändern. */
        for (var i = 0; i < len; i++)
        {
            double sum = 0;
            foreach (var id in ids) sum += ret[id][i];

            var mean = sum / ids.Length;
            foreach (var id in ids) ret[id][i] -= mean;
        }

        return ret;
    }

    /// <summary>
    /// Misst für ein Paar, wie der Empfänger nach den Ereignissen des Senders
    /// reagiert.
    /// </summary>
    /// <param name="minGap">
    /// Mindestabstand zwischen berücksichtigten Ereignissen. Liegen zwei
    /// Ereignisse dichter als der Horizont beieinander, überlappen ihre
    /// Reaktionsfenster, und dieselbe Bewegung geht mehrfach in den Mittelwert
    /// ein — der sieht dann verlässlicher aus, als er ist.
    /// </param>
    public static TransmissionLink? Measure(
        int fromId, string fromSymbol,
        int toId, string toSymbol,
        IReadOnlyList<DerivativeEvent> events,
        double[] responseReturns,
        int horizonBars,
        int minEvents = 12,
        int minGap = 0)
    {
        if (events.Count < minEvents) return null;

        var gap = Math.Max(minGap, horizonBars);

        var samples = new List<double>(events.Count);

        /* Kein int.MinValue als Anfangswert: Die Prüfung lautet
           e.Index − lastUsed, und diese Differenz läuft dann über. Sie wird
           negativ, der Mindestabstand greift bei jedem Ereignis, und die
           Messung liefert für JEDES Paar null Beobachtungen. Genau dieser
           Fehler steckte kurz zuvor schon im Ereignisdetektor — dasselbe
           Muster, dieselbe Wirkung, zwei Dateien weiter. */
        var lastUsed = -gap - 1;

        foreach (var e in events)
        {
            if (e.Index - lastUsed < gap) continue;

            var start = e.Index + 1;
            var end = start + horizonBars;

            if (end > responseReturns.Length) break;

            double sum = 0;
            for (var i = start; i < end; i++) sum += responseReturns[i];

            /* Das Vorzeichen des Ereignisses wird eingerechnet. Sonst hebt eine
               Aufwärts- und eine Abwärtsbeschleunigung einander im Mittel auf,
               und eine völlig verlässliche Übertragung erschiene als null. */
            samples.Add(e.Sign * sum);
            lastUsed = e.Index;
        }

        if (samples.Count < minEvents) return null;

        var n = samples.Count;
        var mean = samples.Average();

        double var2 = 0;
        foreach (var s in samples) var2 += (s - mean) * (s - mean);

        var sd = Math.Sqrt(var2 / Math.Max(1, n - 1));
        var se = sd / Math.Sqrt(n);

        var same = samples.Count(s => Math.Sign(s) == Math.Sign(mean) && s != 0);

        return new TransmissionLink(
            fromId, fromSymbol, toId, toSymbol, horizonBars, n,
            Math.Round((Math.Exp(mean) - 1) * 100, 4),
            Math.Round(100.0 * same / n, 2),
            se > 1e-12 ? Math.Round(mean / se, 3) : 0);
    }

    /// <summary>
    /// Vergleicht zwei Läufe: Tauchen dieselben Übertragungen in einem zweiten,
    /// unabhängigen Zeitraum wieder auf?
    ///
    /// Verglichen wird nicht die Rangfolge, sondern das <b>Vorzeichen</b>. Bei
    /// einer Übertragung geht es nicht darum, wer am stärksten reagiert,
    /// sondern ob die Richtung dieselbe bleibt. Eine Übertragung, die einmal
    /// nach oben und einmal nach unten wirkt, ist keine.
    /// </summary>
    public static (int Checked, int SameSign, double SharePct, double MeanTrainT, double MeanTestPct)
        Confirm(IReadOnlyList<TransmissionLink> train,
                IReadOnlyList<TransmissionLink> test,
                int topN = 50)
    {
        var byKey = test.ToDictionary(l => (l.FromAssetId, l.ToAssetId, l.HorizonBars));

        var picked = train
            .Where(l => Math.Abs(l.TStat) >= 2)
            .OrderByDescending(l => Math.Abs(l.TStat))
            .Take(topN)
            .ToList();

        int checkedN = 0, same = 0;
        double sumT = 0, sumTest = 0;

        foreach (var l in picked)
        {
            if (!byKey.TryGetValue((l.FromAssetId, l.ToAssetId, l.HorizonBars), out var t)) continue;

            checkedN++;
            sumT += Math.Abs(l.TStat);

            // Auf die Richtung des Trainings bezogen, damit sich Vorzeichen nicht aufheben.
            var aligned = Math.Sign(l.MeanResponsePct) * t.MeanResponsePct;
            sumTest += aligned;

            if (aligned > 0) same++;
        }

        return (checkedN, same,
            checkedN > 0 ? Math.Round(100.0 * same / checkedN, 2) : 0,
            checkedN > 0 ? Math.Round(sumT / checkedN, 3) : 0,
            checkedN > 0 ? Math.Round(sumTest / checkedN, 4) : 0);
    }
}
