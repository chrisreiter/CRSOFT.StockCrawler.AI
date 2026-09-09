using Ingest.Core.Enums;
using Ingest.Core.Models;

namespace Ingest.Core.Analysis;

/// <summary>
/// Ein auffälliger Zeitpunkt in einer Kursreihe, bewertet auf einer Skala von
/// 1 bis 100.
/// </summary>
/// <param name="Severity">
/// 1 = kaum der Rede wert, 100 = so etwas kommt in Jahrzehnten kaum vor.
/// </param>
/// <param name="Kind">
/// Woran es lag: Kurssprung, Volumenschock, beides zusammen oder eine
/// ungewöhnlich weite Tagesspanne.
/// </param>
/// <param name="Suspect">
/// Verdacht auf Datenfehler statt Ereignis: ein gewaltiger Kurssprung ohne
/// jeden zusätzlichen Umsatz. Echte Ereignisse bewegen Geld; ein unbereinigter
/// Aktiensplit oder ein falsch zugeordnetes Symbol tut das nicht.
/// </param>
public sealed record MarketEvent(
    int AssetId,
    string Symbol,
    DateTime TsUtc,
    int Severity,
    string Kind,
    int Direction,
    double ReturnPct,
    double ReturnZ,
    double VolumeRatio,
    double RangePct,
    bool Suspect);

/// <summary>
/// Findet Ereignisse in Kursreihen und bewertet, wie gravierend sie waren.
///
/// <b>Warum eine Skala und nicht einfach der Prozentwert:</b> Fünf Prozent sind
/// für eine Versorgeraktie ein Erdbeben und für einen jungen Krypto-Token ein
/// ruhiger Dienstag. Vergleichbar wird das erst im Verhältnis zur eigenen
/// üblichen Schwankung. Deshalb wird jede Bewegung an der Streuung des
/// jeweiligen Wertes gemessen — und zwar an der <i>vorangegangenen</i>, nie an
/// einem Fenster, das das Ereignis selbst enthält.
///
/// <b>Warum robuste Kennzahlen:</b> Mittelwert und Standardabweichung werden
/// von genau den Ausschlägen aufgebläht, die wir suchen. Ein Absturz von 30 %
/// hebt die Standardabweichung so weit an, dass er selbst nur noch mäßig
/// auffällig wirkt — und die kleineren Nachbeben verschwinden ganz. Median und
/// mittlere absolute Abweichung haben dieses Problem nicht.
/// </summary>
public static class EventDetector
{
    /// <summary>Fenster für die Vergleichsstatistik, in Bars.</summary>
    public const int DefaultWindow = 60;

    /// <summary>
    /// Ab dieser Stufe gilt etwas als Ereignis. Darunter ist es normales
    /// Rauschen, von dem jede Reihe hunderte enthält.
    /// </summary>
    public const int DefaultMinSeverity = 40;

    /// <summary>
    /// Bewertet alle Bars einer Reihe und gibt zurück, was die Schwelle
    /// überschreitet.
    /// </summary>
    public static List<MarketEvent> Detect(
        int assetId,
        string symbol,
        AssetClass assetClass,
        IReadOnlyList<PriceBar> bars,
        int window = DefaultWindow,
        int minSeverity = DefaultMinSeverity)
    {
        var result = new List<MarketEvent>();
        if (bars.Count < window + 2) return result;

        var n = bars.Count;

        // Log-Returns und Geldumsätze einmal vorab.
        var ret = new double[n];
        var vol = new double[n];

        for (var i = 1; i < n; i++)
        {
            var prev = (double)bars[i - 1].Close;
            var cur = (double)bars[i].Close;
            ret[i] = prev > 0 && cur > 0 ? Math.Log(cur / prev) : 0;
            vol[i] = NetFlow.Gross(assetClass, bars[i]);
        }

        // Tagesspannen relativ zum Schlusskurs, für den Vergleich mit der Historie.
        var range = new double[n];
        for (var i = 0; i < n; i++)
        {
            var b = bars[i];
            if (b.High is { } hi && b.Low is { } lo && b.Close > 0 && hi > lo)
                range[i] = (double)((hi - lo) / b.Close);
        }

        var absBuf = new double[window];
        var volBuf = new double[window];
        var rngBuf = new double[window];

        for (var i = window + 1; i < n; i++)
        {
            /* Das Vergleichsfenster endet VOR der bewerteten Bar. Nähme man sie
               hinein, bewertete sich das Ereignis teilweise an sich selbst. */
            for (var k = 0; k < window; k++)
            {
                var j = i - window + k;
                absBuf[k] = Math.Abs(ret[j]);
                volBuf[k] = vol[j];
                rngBuf[k] = range[j];
            }

            var typicalMove = Median(absBuf);
            var typicalVol = Median(volBuf);
            var typicalRange = Median(rngBuf);

            // Eine Reihe ohne jede Bewegung im Fenster lässt keinen Vergleich zu.
            if (typicalMove <= 1e-9) continue;

            /* Der Faktor 1,4826 rechnet die mittlere absolute Abweichung auf
               das Niveau einer Standardabweichung um — nur eben unempfindlich
               gegen Ausreißer. */
            var z = Math.Abs(ret[i]) / (typicalMove * 1.4826);

            var volRatio = typicalVol > 0 ? vol[i] / typicalVol : 1;

            /* Volumen geht logarithmisch ein: doppelter Umsatz ist bemerkenswert,
               zwanzigfacher nicht zehnmal so bemerkenswert wie doppelter. */
            var volZ = volRatio > 0 ? Math.Log(volRatio) / 0.6931 : 0;   // in Verdopplungen

            /* Die Spanne wird an der Historie der Spannen gemessen, nicht an
               der Bewegung. Der frühere Umweg über die Bewegung ließ jeden
               Stablecoin auf Stufe 100 laufen: dessen übliche Bewegung liegt
               bei praktisch null, jede noch so harmlose Tagesspanne war
               dagegen ein Vielfaches. */
            var rangeZ = typicalRange > 1e-6
                ? Math.Max(0, range[i] / typicalRange - 1)
                : 0;

            /* Der Kursausschlag trägt die Bewertung; Volumen und Spanne dürfen
               sie nur verstärken. Beide sind gedeckelt, damit kein Nebenkriterium
               allein ein Ereignis erzeugt — ein ruhiger Tag mit viel Umsatz ist
               kein Ereignis, sondern ein ruhiger Tag mit viel Umsatz. */
            var magnitude = z
                          + 0.6 * Math.Clamp(volZ, 0, 4)
                          + 0.3 * Math.Clamp(rangeZ, 0, 3);

            var severity = ToScale(magnitude);
            if (severity < minSeverity) continue;

            // Ohne nennenswerte Kursbewegung ist es kein Ereignis dieser Reihe.
            if (z < 2 && volZ < 2) continue;

            /* Ein Sprung um ein Vielfaches der üblichen Bewegung, bei dem
               niemand mehr gehandelt hat als sonst, ist mit hoher
               Wahrscheinlichkeit ein Datenfehler — ein unbereinigter Split etwa.
               Echte Ereignisse ziehen Umsatz nach sich. */
            var suspect = z >= 10 && volRatio < 1.5;

            var bar = bars[i];

            result.Add(new MarketEvent(
                assetId, symbol, bar.TsUtc, severity,
                Classify(z, volZ, rangeZ),
                Math.Sign(ret[i]),
                Math.Round((Math.Exp(ret[i]) - 1) * 100, 3),
                Math.Round(z, 2),
                Math.Round(volRatio, 2),
                Math.Round(range[i] * 100, 3),
                suspect));
        }

        return result;
    }

    /// <summary>
    /// Bildet eine unbegrenzte Auffälligkeit auf 1 bis 100 ab.
    ///
    /// Die Kurve sättigt: sie wächst anfangs schnell und nähert sich dann
    /// hundert an, ohne es zu erreichen. Dadurch bleibt der interessante
    /// Bereich — zwischen „ungewöhnlich" und „außergewöhnlich" — gespreizt,
    /// während der Abstand zwischen zwei Jahrhundertereignissen klein bleibt,
    /// wo er auch hingehört.
    /// </summary>
    public static int ToScale(double magnitude)
    {
        if (magnitude <= 0) return 1;

        var s = 100 * (1 - Math.Exp(-magnitude / 4.0));
        return Math.Clamp((int)Math.Round(s), 1, 100);
    }

    private static string Classify(double z, double volZ, double rangeZ)
    {
        var kursSprung = z >= 3;
        var volumenSchock = volZ >= 1.5;

        if (kursSprung && volumenSchock) return "Kurs+Volumen";
        if (kursSprung) return "Kurssprung";
        if (volumenSchock) return "Volumenschock";
        return rangeZ > 0.5 ? "Spanne" : "Auffälligkeit";
    }

    private static double Median(double[] buf)
    {
        var copy = buf.AsSpan().ToArray();
        Array.Sort(copy);

        var m = copy.Length / 2;
        return copy.Length % 2 == 1 ? copy[m] : (copy[m - 1] + copy[m]) / 2;
    }
}
