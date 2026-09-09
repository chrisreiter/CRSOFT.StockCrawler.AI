namespace Ingest.Core.Analysis;

/// <summary>
/// Die Kennzahlen einer Kursreihe über einen langen Zeitraum.
///
/// <para>Bewusst abhängigkeitsfrei und auf Log-Renditen gerechnet: Nur so
/// addieren sich Perioden, und nur so ist die Jahresrendite einer Reihe die
/// mittlere Tagesrendite mal der Zahl der Handelstage.</para>
/// </summary>
public static class Langfristmass
{
    /// <summary>
    /// Handelstage je Jahr. 252 ist die Zahl für Aktien; Krypto handelt an 365
    /// Tagen. Wer beides mit derselben Zahl hochrechnet, überschätzt die
    /// Krypto-Schwankung um rund zwanzig Prozent.
    /// </summary>
    public const double TageJeJahrAktie = 252;
    public const double TageJeJahrKrypto = 365;

    /// <summary>Log-Renditen aus einer Kursreihe.</summary>
    public static double[] Renditen(IReadOnlyList<double> kurse)
    {
        if (kurse.Count < 2) return [];

        var r = new double[kurse.Count - 1];
        for (var i = 1; i < kurse.Count; i++)
            r[i - 1] = kurse[i] > 0 && kurse[i - 1] > 0
                ? Math.Log(kurse[i] / kurse[i - 1]) : 0;

        return r;
    }

    /// <summary>
    /// Geometrische Jahresrendite. Gerechnet über die Log-Renditen und erst am
    /// Ende zurückverwandelt — der arithmetische Mittelwert von Renditen
    /// überschätzt systematisch, und zwar umso mehr, je stärker die Reihe
    /// schwankt. Genau bei Krypto also am meisten.
    /// </summary>
    public static double RenditeProJahr(double[] renditen, double tageJeJahr)
    {
        if (renditen.Length == 0) return 0;

        var summe = 0.0;
        foreach (var x in renditen) summe += x;

        return Math.Exp(summe / renditen.Length * tageJeJahr) - 1;
    }

    /// <summary>Auf ein Jahr hochgerechnete Streuung der Tagesrenditen.</summary>
    public static double SchwankungProJahr(double[] renditen, double tageJeJahr)
    {
        if (renditen.Length < 2) return 0;

        var mittel = 0.0;
        foreach (var x in renditen) mittel += x;
        mittel /= renditen.Length;

        var quadrate = 0.0;
        foreach (var x in renditen) quadrate += (x - mittel) * (x - mittel);

        return Math.Sqrt(quadrate / (renditen.Length - 1)) * Math.Sqrt(tageJeJahr);
    }

    /// <summary>
    /// Der tiefste Rückgang vom bisherigen Höchststand, als positiver Anteil.
    ///
    /// <para>Diese Zahl entscheidet in der Praxis mehr als die Schwankung: Sie
    /// sagt, wie viel jemand zwischenzeitlich verloren hätte — und damit, ob er
    /// die Anlage durchgehalten hätte oder am Tiefpunkt verkauft.</para>
    /// </summary>
    public static double GroessterRueckgang(IReadOnlyList<double> kurse)
    {
        if (kurse.Count < 2) return 0;

        var hoch = kurse[0];
        var tiefster = 0.0;

        foreach (var k in kurse)
        {
            if (k > hoch) hoch = k;
            if (hoch <= 0) continue;

            var rueckgang = 1 - k / hoch;
            if (rueckgang > tiefster) tiefster = rueckgang;
        }

        return tiefster;
    }

    /// <summary>
    /// Gleitende Ein-Jahres-Fenster: Anteil positiver, schlechtestes, bestes.
    ///
    /// <para>Die Fenster überlappen — als Fallzahl taugen sie deshalb nicht,
    /// und ein t-Wert darüber wäre um rund die Wurzel der Fensterlänge zu hoch.
    /// Als <i>Beschreibung</i> („in wie vielen Zwölfmonatsstrecken war man im
    /// Plus") sind sie richtig, und als solche stehen sie hier.</para>
    /// </summary>
    public static (double AnteilPositiv, double Schlechtestes, double Bestes)
        Jahresfenster(IReadOnlyList<double> kurse, int fensterTage)
    {
        if (kurse.Count <= fensterTage) return (0, 0, 0);

        var positiv = 0;
        var n = 0;
        var min = double.MaxValue;
        var max = double.MinValue;

        for (var i = fensterTage; i < kurse.Count; i++)
        {
            if (kurse[i - fensterTage] <= 0) continue;

            var r = kurse[i] / kurse[i - fensterTage] - 1;
            n++;
            if (r > 0) positiv++;
            if (r < min) min = r;
            if (r > max) max = r;
        }

        return n == 0 ? (0, 0, 0) : ((double)positiv / n, min, max);
    }

    /// <summary>Korrelation zweier gleich langer Renditereihen.</summary>
    public static double Korrelation(double[] a, double[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n < 3) return 0;

        double ma = 0, mb = 0;
        for (var i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;

        double sab = 0, saa = 0, sbb = 0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - ma;
            var db = b[i] - mb;
            sab += da * db; saa += da * da; sbb += db * db;
        }

        return saa > 0 && sbb > 0 ? sab / Math.Sqrt(saa * sbb) : 0;
    }
}
