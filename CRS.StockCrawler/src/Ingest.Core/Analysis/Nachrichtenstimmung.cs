using System.Text.RegularExpressions;

namespace Ingest.Core.Analysis;

/// <summary>Die Stimmung eines Textes: Richtung und wie deutlich.</summary>
/// <param name="Wert">−1 bis +1. Null heisst neutral oder nichts erkannt.</param>
/// <param name="Treffer">Wie viele Stimmungswörter gefunden wurden.</param>
/// <param name="Woerter">Die gefundenen Wörter — damit eine Zahl nachprüfbar bleibt.</param>
public readonly record struct Stimmung(double Wert, int Treffer, IReadOnlyList<string> Woerter)
{
    public static readonly Stimmung Keine = new(0, 0, []);
}

/// <summary>
/// Liest aus Nachrichtentext eine Richtung — aufwärts, abwärts oder nichts.
///
/// <para><b>Warum ein Wörterbuch und kein Sprachmodell.</b> Die Stimmung wird für jeden
/// verfolgten Wert und jeden Lauf gebraucht; bei 600 Werten und stündlichem Takt sind das
/// Zehntausende Bewertungen am Tag. Ein Sprachmodell dafür kostet Stunden Rechenzeit und
/// liefert bei jedem Lauf leicht andere Zahlen — eine Prognose, die sich ohne neue
/// Nachrichten ändert, ist nicht nachvollziehbar. Das Wörterbuch ist stumpf, aber
/// wiederholbar, und es lässt sich Wort für Wort nachlesen.</para>
///
/// <para><b>Deutsch und Englisch gemischt.</b> Die Feeds sind es auch — Handelsblatt neben
/// Reuters, NZZ neben CNBC. Zwei getrennte Wörterbücher hätten eine Spracherkennung
/// vorausgesetzt, die bei Überschriften unzuverlässig ist.</para>
///
/// <para><b>Die Verneinung wird beachtet.</b> „keine Erholung" ist das Gegenteil von
/// „Erholung", und in Überschriften steht das häufig. Ohne diese Regel zählte der Satz als
/// Kaufsignal. Geprüft werden die drei Wörter davor.</para>
///
/// <para><b>Was diese Klasse NICHT tut: behaupten, dass es wirkt.</b> Sie liefert eine Zahl.
/// Ob diese Zahl einen Kurs vorhersagt, entscheidet allein die Kalibrierung gegen
/// eingetroffene Renditen — gemessen an GDELT lag der prognostische Zusammenhang von
/// Nachrichtenton und SPY bei 0,0419 gegen eine Signifikanzschwelle von 0,077. Wer diese
/// Zahl ohne Kalibrierung in eine Prognose gibt, erfindet Signal.</para>
/// </summary>
public static class Nachrichtenstimmung
{
    /* Gewichte statt blossem +1/−1: „Insolvenz" ist ein anderes Kaliber als „nachgeben".
       Die Werte sind nicht gemessen, sondern gesetzt -- sie ordnen nur die Wörter
       untereinander. Was daraus für eine Prognose folgt, bestimmt die Kalibrierung. */

    private static readonly Dictionary<string, double> Positiv = new(StringComparer.OrdinalIgnoreCase)
    {
        // deutsch
        ["rekord"] = 1.0, ["rekordhoch"] = 1.0, ["allzeithoch"] = 1.0,
        ["durchbruch"] = 0.9, ["rallye"] = 0.9, ["hausse"] = 0.9,
        ["gewinnsprung"] = 0.9, ["übernahmeangebot"] = 0.8, ["uebernahmeangebot"] = 0.8,
        ["steigt"] = 0.6, ["steigen"] = 0.6, ["stieg"] = 0.6, ["gestiegen"] = 0.6,
        ["zulegen"] = 0.6, ["zulegt"] = 0.6, ["legt zu"] = 0.6, ["klettert"] = 0.7,
        ["erholung"] = 0.6, ["erholt"] = 0.6, ["aufschwung"] = 0.7, ["aufwärtstrend"] = 0.7,
        ["gewinn"] = 0.5, ["gewinne"] = 0.5, ["überschuss"] = 0.5,
        ["optimistisch"] = 0.6, ["zuversichtlich"] = 0.6, ["hochgestuft"] = 0.8,
        ["kaufempfehlung"] = 0.8, ["übergewichten"] = 0.7, ["anhebung"] = 0.6,
        ["angehoben"] = 0.6, ["prognose angehoben"] = 0.9, ["besser als erwartet"] = 0.9,
        ["übertrifft"] = 0.8, ["uebertrifft"] = 0.8, ["stark"] = 0.4, ["robust"] = 0.5,
        ["wachstum"] = 0.5, ["expansion"] = 0.5, ["dividendenerhöhung"] = 0.7,

        // englisch
        ["record"] = 1.0, ["record high"] = 1.0, ["all-time high"] = 1.0,
        ["surge"] = 0.9, ["surges"] = 0.9, ["soar"] = 0.9, ["soars"] = 0.9,
        ["rally"] = 0.9, ["rallies"] = 0.9, ["breakout"] = 0.8, ["bullish"] = 0.9,
        ["jumps"] = 0.7, ["jumped"] = 0.7, ["climbs"] = 0.7, ["gains"] = 0.6,
        ["rises"] = 0.6, ["rose"] = 0.6, ["higher"] = 0.4, ["advance"] = 0.5,
        ["upgrade"] = 0.8, ["upgraded"] = 0.8, ["outperform"] = 0.7, ["overweight"] = 0.6,
        ["beats"] = 0.8, ["beat expectations"] = 0.9, ["tops estimates"] = 0.9,
        ["raised guidance"] = 0.9, ["strong"] = 0.4, ["robust"] = 0.5, ["growth"] = 0.5,
        ["profit"] = 0.5, ["buyback"] = 0.6, ["approval"] = 0.5, ["partnership"] = 0.4,
        ["adoption"] = 0.5, ["inflow"] = 0.5, ["inflows"] = 0.5
    };

    private static readonly Dictionary<string, double> Negativ = new(StringComparer.OrdinalIgnoreCase)
    {
        // deutsch
        ["insolvenz"] = 1.0, ["pleite"] = 1.0, ["crash"] = 1.0, ["absturz"] = 1.0,
        ["einbruch"] = 0.9, ["talfahrt"] = 0.9, ["baisse"] = 0.9, ["ausverkauf"] = 0.8,
        ["fällt"] = 0.6, ["faellt"] = 0.6, ["fallen"] = 0.6, ["fiel"] = 0.6,
        ["sinkt"] = 0.6, ["sinken"] = 0.6, ["verliert"] = 0.6, ["verluste"] = 0.6,
        ["nachgeben"] = 0.5, ["rutscht"] = 0.7, ["abwärtstrend"] = 0.7,
        ["gewinnwarnung"] = 1.0, ["prognose gesenkt"] = 0.9, ["gesenkt"] = 0.6,
        ["herabgestuft"] = 0.8, ["verkaufsempfehlung"] = 0.8, ["untergewichten"] = 0.7,
        ["schlechter als erwartet"] = 0.9, ["verfehlt"] = 0.8, ["enttäuscht"] = 0.7,
        ["ermittlungen"] = 0.7, ["klage"] = 0.6, ["strafe"] = 0.6, ["skandal"] = 0.8,
        ["rezession"] = 0.8, ["krise"] = 0.7, ["schwach"] = 0.4, ["stellenabbau"] = 0.6,
        ["hack"] = 0.8, ["gehackt"] = 0.9, ["betrug"] = 0.9,

        // englisch
        ["bankruptcy"] = 1.0, ["insolvency"] = 1.0, ["crash"] = 1.0, ["plunge"] = 0.9,
        ["plunges"] = 0.9, ["slump"] = 0.8, ["slumps"] = 0.8, ["tumble"] = 0.8,
        ["tumbles"] = 0.8, ["selloff"] = 0.8, ["sell-off"] = 0.8, ["bearish"] = 0.9,
        ["falls"] = 0.6, ["fell"] = 0.6, ["drops"] = 0.6, ["declines"] = 0.6,
        ["slides"] = 0.6, ["lower"] = 0.4, ["losses"] = 0.6, ["loss"] = 0.5,
        ["downgrade"] = 0.8, ["downgraded"] = 0.8, ["underperform"] = 0.7,
        ["misses"] = 0.8, ["missed estimates"] = 0.9, ["cut guidance"] = 0.9,
        ["profit warning"] = 1.0, ["weak"] = 0.4, ["recession"] = 0.8, ["crisis"] = 0.7,
        ["lawsuit"] = 0.6, ["probe"] = 0.6, ["investigation"] = 0.7, ["fraud"] = 0.9,
        ["hacked"] = 0.9, ["exploit"] = 0.8, ["outflow"] = 0.5, ["outflows"] = 0.5,
        ["layoffs"] = 0.6, ["halted"] = 0.7, ["delisting"] = 0.9
    };

    /// <summary>Wörter, die eine nachfolgende Aussage umdrehen.</summary>
    private static readonly HashSet<string> Verneinung = new(StringComparer.OrdinalIgnoreCase)
    {
        "kein", "keine", "keinen", "keiner", "nicht", "nie", "ohne", "kaum",
        "no", "not", "never", "without", "fails", "failed", "unlikely", "denies"
    };

    private static readonly Regex Zerteiler = new(@"[^\p{L}\p{Nd}\-']+", RegexOptions.Compiled);

    /// <summary>
    /// Bewertet einen Text.
    ///
    /// <para>Der Wert ist der Mittelwert der gefundenen Stimmungswörter, nicht ihre Summe.
    /// Sonst hinge das Ergebnis an der Textlänge: Ein langer Artikel mit zwanzig schwachen
    /// Wörtern schlüge eine Überschrift mit einem starken, obwohl die Überschrift
    /// deutlicher ist.</para>
    /// </summary>
    public static Stimmung Bewerte(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Stimmung.Keine;

        var w = Zerteiler.Split(text).Where(x => x.Length > 1).ToArray();
        if (w.Length == 0) return Stimmung.Keine;

        double summe = 0;
        var treffer = 0;
        var gefunden = new List<string>();

        for (var i = 0; i < w.Length; i++)
        {
            double wert;

            if (Positiv.TryGetValue(w[i], out var p)) wert = p;
            else if (Negativ.TryGetValue(w[i], out var n)) wert = -n;
            else continue;

            /* Drei Wörter zurück nach einer Verneinung. Zwei sind zu wenig -- „nicht
               so recht erholt" -- und fünf greifen über den Satz hinaus. */
            for (var k = Math.Max(0, i - 3); k < i; k++)
                if (Verneinung.Contains(w[k])) { wert = -wert; break; }

            summe += wert;
            treffer++;
            if (gefunden.Count < 8) gefunden.Add(w[i]);
        }

        if (treffer == 0) return Stimmung.Keine;

        return new Stimmung(Math.Clamp(summe / treffer, -1, 1), treffer, gefunden);
    }

    /// <summary>
    /// Fasst mehrere Texte zusammen — jüngere zählen mehr.
    ///
    /// <para><b>Warum eine Abklingzeit.</b> Eine Meldung von vorgestern beschreibt einen
    /// Kurs, der sie längst verdaut hat. Ohne Abklingen zöge eine alte Schlagzeile die
    /// Stimmung tagelang in eine Richtung, und die Zahl beschriebe die Vergangenheit statt
    /// die Gegenwart.</para>
    ///
    /// <para><b>Warum die Anzahl mitgeführt wird.</b> Eine Stimmung aus einer einzigen
    /// Meldung ist etwas anderes als dieselbe Zahl aus vierzig. Der Aufrufer braucht
    /// beides, um zu entscheiden, ob er ihr etwas zutraut.</para>
    /// </summary>
    /// <param name="halbwertszeitStunden">Nach dieser Zeit zählt eine Meldung halb.</param>
    public static Stimmung Fasse(
        IEnumerable<(string Text, DateTime AlsUtc)> texte, DateTime jetztUtc,
        double halbwertszeitStunden = 24)
    {
        double zaehler = 0, nenner = 0;
        var treffer = 0;
        var woerter = new List<string>();

        foreach (var (text, als) in texte)
        {
            var s = Bewerte(text);
            if (s.Treffer == 0) continue;

            var alter = Math.Max(0, (jetztUtc - als).TotalHours);
            var gewicht = Math.Pow(0.5, alter / Math.Max(1, halbwertszeitStunden));

            zaehler += s.Wert * gewicht;
            nenner += gewicht;
            treffer += s.Treffer;

            foreach (var x in s.Woerter)
                if (woerter.Count < 12 && !woerter.Contains(x)) woerter.Add(x);
        }

        return nenner <= 1e-9
            ? Stimmung.Keine
            : new Stimmung(Math.Clamp(zaehler / nenner, -1, 1), treffer, woerter);
    }
}
