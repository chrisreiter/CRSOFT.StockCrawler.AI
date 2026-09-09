using Ingest.Core.Analysis.Spectral;

namespace Ingest.Infrastructure.Services;

/// <summary>Was die Spektralsäule für einen Wert hergibt.</summary>
/// <param name="Skill">
/// Der <b>gemessene</b> Vorsprung gegenüber dem Stillstand, 0 bis 1. Null heisst: Die
/// Fortschreibung war im zurückgehaltenen Abschnitt nicht besser als die Annahme, der Kurs
/// bleibe stehen — dann trägt die Säule nichts bei.
/// </param>
/// <param name="GeprueftBis">
/// Bis zu wie vielen Bars voraus der Rückhalt gemessen wurde. Darüber hinaus gibt es
/// keinen Beitrag — man darf nicht weiter behaupten, als man geprüft hat.
/// </param>
/// <param name="Schwankung">
/// Die übliche Tagesschwankung dieser Reihe, als Riegel gegen davongelaufene Rekursionen.
/// </param>
public sealed record Spektrallage(
    double Skill, int Fenster, double ErklaerteStreuung,
    double LetzterLog, IReadOnlyList<double> Fortschreibung,
    int GeprueftBis = 40, double Schwankung = 0.02)
{
    /// <summary>Die fortgeschriebene Log-Rendite nach so vielen Tagen.</summary>
    /// <remarks>
    /// <c>null</c>, wenn so weit nicht fortgeschrieben wurde. Das ist kein Fehler, sondern
    /// die Grenze der Zerlegung: Über die Fensterlänge hinaus wiederholt eine SSA-Rekursion
    /// nur noch sich selbst, und das ist keine Aussage mehr.
    /// </remarks>
    public double? RenditeNach(int tage)
    {
        if (tage < 1 || tage > Fortschreibung.Count) return null;

        /* NICHT weiter behaupten, als geprüft wurde.

           Der Rückhalt misst 40 Bars voraus. Ihn für eine Aussage über 90 Tage zu
           benutzen ist ein Über-Anspruch, und er ist in der ersten Fassung sofort
           aufgetreten: Für ENA-USD schrieb die SSA über ein Vierteljahr +33,4 % fort,
           belegt mit einem Vorsprung, der über vierzig Tage gemessen war. Die Mischung
           zog die Prognose damit von −46 % auf −4 % — eine Änderung um 42 Prozentpunkte,
           gestützt auf nichts.

           Jenseits des geprüften Abschnitts gibt es deshalb keinen Beitrag. Das ist
           dieselbe Regel wie beim Deep-Learning: Was nicht im Sperrbereich gemessen ist,
           bekommt kein Gewicht. */
        if (tage > GeprueftBis) return null;

        var r = Fortschreibung[tage - 1] - LetzterLog;

        /* Zweiter Riegel: Der Betrag darf die eigene übliche Schwankung über diese
           Strecke nicht um mehr als das Dreifache überschreiten. Eine SSA-Rekursion
           kann davonlaufen, und eine davongelaufene Fortschreibung sieht wie eine
           mutige Prognose aus. */
        var grenze = 3 * Schwankung * Math.Sqrt(tage);

        return Math.Abs(r) > grenze ? null : r;
    }
}

/// <summary>
/// Die zweite Säule als Prognosebeitrag: singuläre Spektralanalyse mit Rückhalteprüfung.
///
/// <para><b>Warum diese Säule überhaupt eine Zahl liefern darf.</b> Von den Verfahren der
/// Mathematik-Säule ist die SSA das einzige, das die Struktur einer Reihe nicht nur
/// beschreibt, sondern aus ihr eine lineare Rekursion ableitet und damit fortschreibt.
/// Spektrum und Stabilität sagen, was in der Reihe steckt; die SSA sagt, wie es
/// weitergeht, wenn es weitergeht wie bisher.</para>
///
/// <para><b>Und warum sie trotzdem meistens nichts beiträgt.</b> „Erklärte Streuung" sagt
/// nichts über Prognosegüte: Auf Log-Kursen erklärt die erste Komponente über 95 %, weil sie
/// den Trend einfängt. Deshalb entscheidet allein die Rückhalteprüfung — der letzte
/// Abschnitt wird zurückgehalten, die Zerlegung auf dem Rest gerechnet und ihre
/// Fortschreibung gegen die Wirklichkeit gehalten. Wer die Latte „besser als Stillstand"
/// reisst, bekommt null. Auf echten Daten trägt die Säule bei drei von vier Werten nichts
/// bei, und das ist das erwartete Ergebnis.</para>
///
/// <para><b>Gerechnet wird auf Log-Kursen.</b> Sonst hängt die Fortschreibung am
/// Kursniveau: Ein Wert bei 5.000 bekäme dieselbe absolute Schwankung zugeschrieben wie
/// einer bei 5, und der Vergleich zwischen Werten wäre bedeutungslos.</para>
/// </summary>
public static class Spektralsaeule
{
    /// <summary>Wie viele Bars zurückgehalten werden, um den Rückhalt zu messen.</summary>
    private const int Rueckhalt = 40;

    /// <summary>Wie viele Bars höchstens einfliessen.</summary>
    private const int MaxBars = 400;

    public static Spektrallage? Rechne(IReadOnlyList<decimal> closes, int horizontTage)
    {
        if (closes.Count < 120 || horizontTage < 1) return null;

        var roh = closes.Count > MaxBars
            ? closes.Skip(closes.Count - MaxBars).ToArray()
            : closes.ToArray();

        var logs = new double[roh.Length];

        for (var i = 0; i < roh.Length; i++)
        {
            var c = (double)roh[i];
            if (c <= 0) return null;      // Ein toter Wert -- keine Fortschreibung.
            logs[i] = Math.Log(c);
        }

        /* Die übliche Tagesschwankung -- Grundlage für den Riegel gegen davongelaufene
           Fortschreibungen. Standardabweichung der Log-Differenzen. */
        double sigma;
        {
            var d = new double[logs.Length - 1];
            for (var i = 1; i < logs.Length; i++) d[i - 1] = logs[i] - logs[i - 1];

            var m = d.Average();
            sigma = Math.Sqrt(d.Sum(x => (x - m) * (x - m)) / Math.Max(1, d.Length - 1));

            if (sigma <= 1e-9) sigma = 0.02;
        }

        /* Fenster: ein Viertel der Reihe, gedeckelt. Zu klein findet keine langen
           Schwingungen, zu gross lässt zu wenige Fenster für die Zerlegung übrig. */
        var L = Math.Clamp(logs.Length / 4, 12, 120);

        /* Die Fortschreibung wird auf die Fensterlänge begrenzt. Darüber hinaus
           wiederholt die Rekursion nur noch sich selbst -- sie sähe wie eine Prognose
           aus und wäre keine. */
        var schritte = Math.Min(horizontTage, L);

        // ------------------------------------------------------ Rückhalt ----
        var skill = Pruefe(logs, L);
        if (skill <= 0)
        {
            /* Ohne Rückhalt wird gar nicht erst fortgeschrieben: Die Zahl käme
               ohnehin mit Verdienst null in die Mischung und kostete nur Rechenzeit. */
            return new Spektrallage(0, L, 0, logs[^1], [], Rueckhalt, sigma);
        }

        // ---------------------------------------------------- Fortschreiben --
        SsaResult voll;

        try { voll = Ssa.Decompose(logs, L, 4, schritte); }
        catch { return null; }

        if (!voll.ForecastValid || voll.Forecast.Count == 0)
            return new Spektrallage(0, L, voll.ExplainedShare, logs[^1], [], Rueckhalt, sigma);

        return new Spektrallage(skill, L, voll.ExplainedShare, logs[^1],
                                voll.Forecast.ToArray(), Rueckhalt, sigma);
    }

    /// <summary>
    /// Der zurückgehaltene Abschnitt entscheidet.
    ///
    /// <para>Gemessen wird gegen den Stillstand — den letzten bekannten Wert über den
    /// ganzen Abschnitt fortgeschrieben. Das ist dieselbe Latte wie überall in dieser
    /// Anwendung, und sie ist tiefer, als sie klingt: Über kurze Strecken ist Stillstand
    /// eine ausgesprochen gute Prognose.</para>
    /// </summary>
    private static double Pruefe(double[] logs, int fenster)
    {
        if (logs.Length < Rueckhalt * 3) return 0;

        var schnitt = logs.Length - Rueckhalt;
        var training = logs[..schnitt];

        SsaResult t;

        try { t = Ssa.Decompose(training, Math.Min(fenster, training.Length / 2), 4, Rueckhalt); }
        catch { return 0; }

        if (!t.ForecastValid || t.Forecast.Count < Rueckhalt) return 0;

        var grundlinie = training[^1];
        double fehlerModell = 0, fehlerStillstand = 0;

        for (var i = 0; i < Rueckhalt; i++)
        {
            var wahr = logs[schnitt + i];
            fehlerModell += Math.Abs(t.Forecast[i] - wahr);
            fehlerStillstand += Math.Abs(grundlinie - wahr);
        }

        if (fehlerStillstand <= 0) return 0;

        return Math.Clamp(1 - fehlerModell / fehlerStillstand, 0, 1);
    }
}
