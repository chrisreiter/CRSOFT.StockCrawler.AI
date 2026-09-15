namespace Ingest.Core.Analysis.Spectral;

/// <summary>
/// Der Akkord eines Wertes in einer Epoche: seine ein bis drei stärksten
/// Grundperioden, absteigend nach Periode, mit relativer Amplitude und Phase.
/// </summary>
/// <param name="Perioden">Absteigend, in Bars. Die erste ist der Grundton.</param>
/// <param name="Amplituden">Relativ zum Grundton, der Grundton selbst ist 1.</param>
/// <param name="Phasen">Grad, 0 bis 360, Lage am Epochenende, je Periode.</param>
public sealed record Akkord(
    int AssetId, string Symbol, DateTime EpochFrom, DateTime EpochTo,
    double[] Perioden, double[] Amplituden, double[] Phasen,
    double Prominenz, double Stabilitaet);

/// <summary>
/// Eine Klasse des Katalogs: alle Akkorde, deren Perioden paarweise innerhalb
/// der Toleranz beieinanderliegen. Die Perioden der Klasse sind die Mediane.
/// </summary>
/// <param name="Harmonik">
/// <c>rein</c> (eine Periode), <c>oberton</c> (Nebenperioden sind ganzzahlige
/// Teiler des Grundtons, ±8 %), <c>schwebung</c> (zwei nahe, nicht harmonische
/// Perioden), <c>dreiklang</c> (drei Perioden ohne einfaches Verhältnis).
/// </param>
public sealed record Katalogklasse(
    int Nr, double[] Perioden, double[] Amplituden, string Harmonik,
    int Akkorde, int Werte, int Epochen, double MittlereProminenz,
    double MittlereStabilitaet, List<Akkord> Mitglieder);

/// <summary>
/// Ein Paar von Werten, das dieselbe Klasse in mehreren Epochen teilt — mit dem
/// Versatz ihrer Grundtöne in Bars und dessen Streuung über die Epochen.
/// </summary>
public sealed record Klassenpaar(
    int AssetA, string SymbolA, int AssetB, string SymbolB,
    int GemeinsameEpochen, double MittlererVersatzBars, double VersatzStreuung,
    bool Bestaendig);

/// <summary>
/// Vom einzelnen Spektralmuster zum Katalog der Grundschwingungen.
///
/// <para><b>Was ein „Grundmuster" hier ist.</b> Ein Chart ist in einer Epoche
/// nicht durch <i>eine</i> Frequenz beschrieben, sondern durch das Zusammenspiel
/// seiner stärksten. Zwei Werte mit Grundton 63 Bars sind sich ähnlich; zwei
/// Werte mit 63 Bars <i>und</i> dem Oberton 31,5 Bars sind sich ähnlicher, und
/// einer, der stattdessen 63 und 47 Bars trägt, ist ein anderes Muster — er
/// schwebt. Der Katalog fasst deshalb <b>Akkorde</b> zusammen, nicht Perioden.</para>
///
/// <para><b>Warum Toleranz relativ und die Klassenbildung greedy ist.</b> Fünf
/// Bars Unterschied sind bei Periode 10 eine andere Welt und bei Periode 100
/// dieselbe Spitze; verglichen wird deshalb als Anteil. Die Bildung der Klassen
/// ist absichtlich einfach: Akkorde absteigend nach Prominenz, jeder tritt der
/// ersten Klasse bei, deren Perioden alle passen, sonst gründet er eine. Das
/// ist reihenfolgeabhängig, aber die prominentesten Akkorde bilden die Kerne —
/// und sie sind auch das, was man sehen will. Ein k-Means hätte dieselbe
/// Willkür bei der Wahl von k und liesse sich schlechter nachrechnen.</para>
///
/// <para><b>Was ein Fund ist und was nicht.</b> Dass zwei Werte in einer Epoche
/// in derselben Klasse landen, ist bei hunderten Werten und einigen Dutzend
/// Klassen Zufall. Ein Paar, das über mehrere Epochen dieselbe Klasse mit
/// gleichbleibendem Versatz teilt, ist etwas anderes — und nur ein Versatz
/// ungleich null wäre prognostisch überhaupt von Wert: Zwei Werte, die
/// dieselbe Schwingung <i>gleichzeitig</i> tragen, sagen einander nichts voraus.
/// Der zentrale Befund dieses Projekts steht dem entgegen (Kurse bewegen sich
/// gemeinsam, nicht nacheinander), und die Auswertung hier ist gebaut, ihn zu
/// prüfen, nicht zu umgehen: Der Versatz wird <i>gemessen</i> und mit seiner
/// Streuung ausgewiesen.</para>
/// </summary>
public static class Grundschwingungen
{
    /// <summary>
    /// Fasst die Spektralmuster eines Wertes und einer Epoche zu einem Akkord
    /// zusammen: die bis zu <paramref name="stimmen"/> prominentesten Perioden.
    /// </summary>
    /// <param name="mindestAnteil">Eine Nebenstimme zählt nur, wenn ihre
    /// Prominenz mindestens diesen Anteil der stärksten erreicht. Ohne diese
    /// Schwelle hätte JEDER Akkord drei Stimmen, weil die Spitzenliste immer
    /// drei hergibt — und der Katalog zerfiele in lauter Dreiklänge, die sich
    /// in der dritten, schwachen Stimme unterscheiden. Gemessen an 60 Werten:
    /// 127 Akkorde, alle dreistimmig, neun Klassen, die 36 davon fassen.</param>
    public static List<Akkord> Akkorde(IReadOnlyList<FreqPattern> muster, int stimmen = 3,
                                       double mindestAnteil = 0.6)
    {
        var result = new List<Akkord>();

        foreach (var g in muster.GroupBy(p => (p.AssetId, p.EpochTo)))
        {
            var nachStaerke = g.OrderByDescending(p => p.Prominence).ToList();
            if (nachStaerke.Count == 0) continue;
            var schwelle = nachStaerke[0].Prominence * mindestAnteil;
            var top = nachStaerke.Where(p => p.Prominence >= schwelle).Take(stimmen)
                                 .OrderByDescending(p => p.PeriodBars).ToList();

            /*  Amplituden relativ zum Grundton. Absolut waeren sie Kurs-
                niveau: Eine Schwingung von 3 Dollar ist bei einem Kurs von 30
                gewaltig und bei 3000 unsichtbar. Ist die Amplitude des
                Grundtons nicht bestimmbar, bleiben alle bei 1.               */
            var basis = top[0].Amplitude;
            var amps = top.Select(p => basis > 0 && !double.IsNaN(p.Amplitude)
                                        ? Math.Round(p.Amplitude / basis, 3) : 1.0).ToArray();

            result.Add(new Akkord(
                g.Key.AssetId, top[0].Symbol, top[0].EpochFrom, g.Key.EpochTo,
                top.Select(p => p.PeriodBars).ToArray(),
                amps,
                top.Select(p => p.PhaseDeg).ToArray(),
                Math.Round(top.Average(p => p.Prominence), 2),
                Math.Round(top.Average(p => p.Stability), 4)));
        }

        return result;
    }

    /// <summary>
    /// Bildet den Katalog: Akkorde gleicher Stimmenzahl, deren Perioden alle
    /// innerhalb der Toleranz beieinanderliegen, werden eine Klasse.
    /// </summary>
    /// <param name="toleranz">Zulässige relative Abweichung je Periode.</param>
    /// <param name="mindestAkkorde">Kleinere Klassen werden verworfen — sie
    /// sind Einzelfälle, kein Muster.</param>
    public static List<Katalogklasse> Katalog(
        IReadOnlyList<Akkord> akkorde, double toleranz = 0.12, int mindestAkkorde = 3)
    {
        var klassen = new List<(double[] Perioden, List<Akkord> Mitglieder)>();

        foreach (var a in akkorde.OrderByDescending(x => x.Prominenz))
        {
            var ziel = klassen.FirstOrDefault(k => Passt(k.Perioden, a.Perioden, toleranz));
            if (ziel.Mitglieder is null)
            {
                klassen.Add(((double[])a.Perioden.Clone(), new List<Akkord> { a }));
                continue;
            }
            ziel.Mitglieder.Add(a);

            /*  Der Kern wandert mit dem Median der Mitglieder. Ohne das
                bestimmte der erste Akkord die Klasse fuer immer, und ein
                zufaellig am Rand liegender Gruender zoege eine schiefe Klasse
                nach sich.                                                     */
            for (var i = 0; i < ziel.Perioden.Length; i++)
                ziel.Perioden[i] = Median(ziel.Mitglieder.Select(m => m.Perioden[i]));
        }

        var nr = 0;
        var result = new List<Katalogklasse>();
        foreach (var k in klassen.Where(k => k.Mitglieder.Count >= mindestAkkorde)
                                 .OrderByDescending(k => k.Mitglieder.Select(m => m.AssetId).Distinct().Count())
                                 .ThenByDescending(k => k.Mitglieder.Count))
        {
            var perioden = k.Perioden.Select(p => Math.Round(p, 1)).ToArray();
            var amps = Enumerable.Range(0, perioden.Length)
                .Select(i => Math.Round(Median(k.Mitglieder.Select(m => m.Amplituden[i])), 3))
                .ToArray();

            result.Add(new Katalogklasse(
                ++nr, perioden, amps, Harmonik(perioden),
                k.Mitglieder.Count,
                k.Mitglieder.Select(m => m.AssetId).Distinct().Count(),
                k.Mitglieder.Select(m => m.EpochTo).Distinct().Count(),
                Math.Round(k.Mitglieder.Average(m => m.Prominenz), 2),
                Math.Round(k.Mitglieder.Average(m => m.Stabilitaet), 4),
                k.Mitglieder));
        }

        return result;
    }

    /// <summary>
    /// Paare von Werten innerhalb einer Klasse, die sie in mindestens
    /// <paramref name="mindestEpochen"/> gemeinsamen Epochen tragen — mit dem
    /// Versatz ihrer Grundtöne.
    /// </summary>
    public static List<Klassenpaar> Paare(Katalogklasse klasse, int mindestEpochen = 3)
    {
        var jeEpoche = klasse.Mitglieder.GroupBy(m => m.EpochTo).ToList();
        var versaetze = new Dictionary<(int, int), List<double>>();
        var namen = new Dictionary<int, string>();

        foreach (var e in jeEpoche)
        {
            var list = e.OrderBy(m => m.AssetId).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                namen[list[i].AssetId] = list[i].Symbol;
                for (var j = i + 1; j < list.Count; j++)
                {
                    var a = list[i]; var b = list[j];
                    if (a.AssetId == b.AssetId) continue;

                    /*  Versatz aus der Phasendifferenz der Grundtoene, auf
                        -180..180 gebracht und in Bars umgerechnet. Positiv
                        heisst: b liegt hinter a zurueck, a laeuft voraus.     */
                    var d = b.Phasen[0] - a.Phasen[0];
                    while (d > 180) d -= 360;
                    while (d < -180) d += 360;
                    var lag = d / 360.0 * (a.Perioden[0] + b.Perioden[0]) / 2;

                    var key = (a.AssetId, b.AssetId);
                    if (!versaetze.TryGetValue(key, out var l)) versaetze[key] = l = new List<double>();
                    l.Add(lag);
                }
            }
        }

        var result = new List<Klassenpaar>();
        foreach (var ((a, b), lags) in versaetze)
        {
            if (lags.Count < mindestEpochen) continue;
            var mean = lags.Average();
            var sd = lags.Count > 1
                ? Math.Sqrt(lags.Sum(l => (l - mean) * (l - mean)) / (lags.Count - 1)) : 0;

            /*  Bestaendig heisst: Der Versatz ist ueber die Epochen derselbe
                UND ungleich null. Ein Versatz, der um null streut, ist
                Gleichzeitigkeit; einer, der springt, war Zufall in huebscher
                Form. Beides ist kein Fund.                                    */
            var bestaendig = sd > 0 && Math.Abs(mean) > 2 * sd && Math.Abs(mean) >= 1;

            result.Add(new Klassenpaar(a, namen[a], b, namen[b], lags.Count,
                Math.Round(mean, 2), Math.Round(sd, 2), bestaendig));
        }

        return result.OrderByDescending(p => p.Bestaendig)
                     .ThenByDescending(p => p.GemeinsameEpochen)
                     .ThenBy(p => p.VersatzStreuung)
                     .ToList();
    }

    /// <summary>Benennt das Verhältnis der Perioden eines Akkords.</summary>
    public static string Harmonik(double[] perioden)
    {
        if (perioden.Length == 1) return "rein";

        var grund = perioden[0];
        var harmonisch = perioden.Skip(1).All(p =>
        {
            var v = grund / p;
            var n = Math.Round(v);
            return n >= 2 && Math.Abs(v - n) / n < 0.08;
        });
        if (harmonisch) return "oberton";

        return perioden.Length == 2 ? "schwebung" : "dreiklang";
    }

    /// <summary>
    /// Die Wellenform einer Klasse als Skizze: Summe der Kosinusschwingungen
    /// mit den Amplituden der Klasse über zwei Grundperioden, auf ±1 normiert.
    /// Phase null für alle — die Skizze zeigt die Form, nicht die Lage.
    /// </summary>
    public static double[] Skizze(double[] perioden, double[] amplituden, int punkte = 96)
    {
        var laenge = 2 * perioden[0];
        var y = new double[punkte];
        for (var i = 0; i < punkte; i++)
        {
            var t = laenge * i / (punkte - 1);
            double v = 0;
            for (var k = 0; k < perioden.Length; k++)
                v += amplituden[k] * Math.Cos(2 * Math.PI * t / perioden[k]);
            y[i] = v;
        }
        var max = y.Max(Math.Abs);
        if (max > 0) for (var i = 0; i < punkte; i++) y[i] = Math.Round(y[i] / max, 3);
        return y;
    }

    /// <summary>
    /// Auslenkung der Akkord-Summe nach <paramref name="bars"/> Bars, relativ zu
    /// heute (Bar 0). Phasenkonvention wie <see cref="FrequencyPatterns.Projektion"/>:
    /// <c>x(τ) = Σ aₖ · cos(2π τ / Pₖ + φₖ)</c>, τ vom Fensterende gezählt.
    /// </summary>
    public static double Fortschreibung(double[] perioden, double[] amplituden, double[] phasen, int bars)
    {
        double heute = 0, dann = 0;
        for (var k = 0; k < perioden.Length; k++)
        {
            var w = 2 * Math.PI / perioden[k];
            var phi = phasen[k] * Math.PI / 180;
            heute += amplituden[k] * Math.Cos(phi);
            dann  += amplituden[k] * Math.Cos(w * bars + phi);
        }
        return dann - heute;
    }

    private static bool Passt(double[] kern, double[] kandidat, double toleranz)
    {
        if (kern.Length != kandidat.Length) return false;
        for (var i = 0; i < kern.Length; i++)
            if (Math.Abs(kandidat[i] - kern[i]) / kern[i] > toleranz) return false;
        return true;
    }

    private static double Median(IEnumerable<double> werte)
    {
        var s = werte.OrderBy(x => x).ToArray();
        if (s.Length == 0) return double.NaN;
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }
}

/// <summary>
/// Der Beitrag der Grundschwingungen eines Wertes zur Prognose — mit
/// Rückhalteprüfung, nach demselben Muster wie die SSA-Fortschreibung der
/// Spektralsäule.
///
/// <para><b>Was fortgeschrieben wird.</b> Der Akkord des jüngsten Fensters
/// (1024 Bars), also die ein bis drei stärksten Perioden mit Amplitude und
/// Lage am Fensterende. Die Summe dieser Kosinusschwingungen wird über den
/// Horizont weitergerechnet; die Differenz zum Stand heute ist der Beitrag.
/// Das ist die reinste Form einer Zyklusprognose — und genau deshalb auch die
/// angreifbarste: Eine Schwingung, die im Fenster stabil war, muss es morgen
/// nicht sein.</para>
///
/// <para><b>Deshalb der Rückhalt.</b> Dasselbe Verfahren wird auf ein Fenster
/// angewandt, das 40 Bars vor dem Ende aufhört, und gegen die tatsächlichen 40
/// Bars gemessen — Fehler gegen den Stillstand, wie überall in dieser
/// Anwendung. Ohne Vorsprung: Verdienst null, kein Beitrag. Und über die
/// geprüften 40 Bars hinaus gibt es keinen Beitrag, egal wie schön die
/// Schwingung weiterliefe.</para>
/// </summary>
public sealed record Grundschwingungslage(
    double Skill, double[] Perioden, double[] Amplituden, double[] Phasen,
    double LetzterKurs, double Schwankung, int GeprueftBis = 40)
{
    /// <summary>Fortgeschriebene Log-Rendite nach so vielen Bars, oder <c>null</c>
    /// jenseits des geprüften Abschnitts bzw. bei davongelaufenem Betrag.</summary>
    public double? RenditeNach(int bars)
    {
        if (bars < 1 || bars > GeprueftBis || Perioden.Length == 0) return null;

        /*  Die Amplituden sind LOG-Einheiten: Der Bandpass, aus dem die Spitzen
            stammen, laeuft auf den Log-Kursen. Die Auslenkungsdifferenz ist
            damit unmittelbar die Log-Rendite -- kein Kursniveau noetig. Der
            erste Entwurf addierte sie auf den Kurs und bekam +0,000 %.       */
        var r = Grundschwingungen.Fortschreibung(Perioden, Amplituden, Phasen, bars);

        // Derselbe Riegel wie bei der SSA: nicht mehr als das Dreifache der
        // üblichen Schwankung über diese Strecke.
        var grenze = 3 * Schwankung * Math.Sqrt(bars);
        return Math.Abs(r) > grenze ? null : r;
    }
}

public static class GrundschwingungenPrognose
{
    private const int Fenster = 1024;
    private const int Rueckhalt = 40;

    /// <summary>
    /// Bestimmt Akkord und Rückhalt eines Wertes aus seinen Tagesschlüssen.
    /// <c>null</c>, wenn die Reihe zu kurz ist (unter 1064 Bars).
    /// </summary>
    public static Grundschwingungslage? Rechne(IReadOnlyList<double> closes)
    {
        if (closes.Count < Fenster + Rueckhalt) return null;

        var arr = closes as double[] ?? closes.ToArray();
        if (arr.Any(c => c <= 0)) return null;

        // Übliche Tagesschwankung, für den Riegel gegen davongelaufene Beiträge.
        double sigma;
        {
            var d = new double[arr.Length - 1];
            for (var i = 1; i < arr.Length; i++) d[i - 1] = Math.Log(arr[i] / arr[i - 1]);
            var m = d.Average();
            sigma = Math.Sqrt(d.Sum(x => (x - m) * (x - m)) / Math.Max(1, d.Length - 1));
            if (sigma <= 1e-9) sigma = 0.02;
        }

        // ------------------------------------------------------- Rückhalt --
        var schnitt = arr.Length - Rueckhalt;
        var training = arr[(schnitt - Fenster)..schnitt];
        var (tp, ta, tph) = Akkord(training);

        double skill = 0;
        if (tp.Length > 0)
        {
            // Im Log-Raum, wie die Amplituden (siehe RenditeNach).
            var grundlinie = Math.Log(arr[schnitt - 1]);
            double fehlerModell = 0, fehlerStillstand = 0;
            for (var i = 1; i <= Rueckhalt; i++)
            {
                var wahr = Math.Log(arr[schnitt - 1 + i]);
                var modell = grundlinie + Grundschwingungen.Fortschreibung(tp, ta, tph, i);
                fehlerModell += Math.Abs(modell - wahr);
                fehlerStillstand += Math.Abs(grundlinie - wahr);
            }
            if (fehlerStillstand > 0)
                skill = Math.Clamp(1 - fehlerModell / fehlerStillstand, 0, 1);
        }

        // ------------------------------------------------------ Heute -------
        var (p, a, ph) = Akkord(arr[^Fenster..]);
        return new Grundschwingungslage(Math.Round(skill, 4), p, a, ph, arr[^1], sigma, Rueckhalt);
    }

    /// <summary>Die Stimmen eines Fensters: Perioden, absolute Amplituden, Phasen.</summary>
    private static (double[] Perioden, double[] Amplituden, double[] Phasen) Akkord(double[] fenster)
    {
        var stamps = new DateTime[fenster.Length];   // Extract braucht Stempel, hier ohne Belang
        var muster = FrequencyPatterns.Extract(0, "", fenster, stamps,
            epochBars: fenster.Length, stepBars: fenster.Length);
        if (muster.Count == 0) return ([], [], []);

        var schwelle = muster.Max(m => m.Prominence) * 0.6;
        var top = muster.Where(m => m.Prominence >= schwelle && !double.IsNaN(m.Amplitude))
                        .OrderByDescending(m => m.Prominence).Take(3)
                        .OrderByDescending(m => m.PeriodBars).ToList();

        return (top.Select(m => m.PeriodBars).ToArray(),
                top.Select(m => m.Amplitude).ToArray(),
                top.Select(m => m.PhaseDeg).ToArray());
    }
}
