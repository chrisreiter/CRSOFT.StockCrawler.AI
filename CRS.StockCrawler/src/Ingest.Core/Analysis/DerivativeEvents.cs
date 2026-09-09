namespace Ingest.Core.Analysis;

/// <summary>Eine auffällig starke Änderung der geglätteten Kurve.</summary>
/// <param name="Sign">+1 bei Beschleunigung nach oben, −1 nach unten.</param>
/// <param name="Strength">
/// Stärke in Vielfachen der üblichen Änderung dieser Reihe — das Gegenstück zur
/// Ereignisstufe, nur auf der Ableitung statt auf dem Tagessprung.
/// </param>
public sealed record DerivativeEvent(int Index, int Sign, double Strength);

/// <summary>
/// Findet Stellen, an denen sich eine Kursreihe auffällig stark ändert —
/// gemessen an der Ableitung der <b>geglätteten</b> Kurve.
///
/// <b>Warum geglättet und nicht roh.</b> Der vorhandene Ereignisdetektor misst
/// den Sprung von einer Bar zur nächsten. Damit findet er Tagesausreißer:
/// Quartalszahlen, Meldungen, gelegentlich Datenfehler. Das ist eine andere
/// Frage als die hier gestellte. Die Ableitung einer geglätteten Kurve schlägt
/// nicht bei einem einzelnen Tag aus, sondern dort, wo eine Bewegung
/// <i>einsetzt</i> oder kippt — wo also aus einer Seitwärtsbewegung ein Trend
/// wird oder ein Trend bricht.
///
/// Ein einzelner Ausreißer verschwindet in der Glättung fast vollständig; eine
/// beginnende Bewegung dagegen hebt die Ableitung über mehrere Bars an. Genau
/// diese Trennung ist der Zweck.
///
/// <b>Kausal geglättet.</b> Ein gleitendes Mittel um den Punkt herum wäre
/// glatter und wäre falsch: Es nähme künftige Werte in die Bestimmung einer
/// Stelle auf, die später als Auslöser gelten soll. Verwendet wird deshalb ein
/// exponentielles Mittel, das ausschließlich zurückblickt.
/// </summary>
public static class DerivativeEvents
{
    /// <summary>
    /// Erkennt die auffälligen Änderungsstellen einer Reihe.
    /// </summary>
    /// <param name="smoothSpan">
    /// Wie stark geglättet wird, in Bars. Kurz reagiert schnell und findet
    /// mehr; lang findet nur die größeren Wenden.
    /// </param>
    /// <param name="window">
    /// Vergleichsfenster für „üblich". Endet stets VOR der bewerteten Stelle.
    /// </param>
    /// <param name="minZ">
    /// Ab wie vielen Vielfachen der üblichen Änderung etwas als Ereignis gilt.
    /// </param>
    /// <param name="refractoryBars">
    /// Sperrzeit nach einem Ereignis. Ohne sie meldet eine einzige kräftige
    /// Bewegung zehn Ereignisse in Folge — und jede spätere Auswertung zählt
    /// dieselbe Sache zehnmal, was jede Häufigkeitsaussage wertlos macht.
    /// </param>
    public static List<DerivativeEvent> Detect(
        double[] closes,
        int smoothSpan = 10,
        int window = 120,
        double minZ = 3.0,
        int refractoryBars = 10)
    {
        var result = new List<DerivativeEvent>();
        var n = closes.Length;

        if (n < window + smoothSpan + 4) return result;

        // Geglättete Log-Kurse, ausschließlich rückblickend.
        var alpha = 2.0 / (Math.Max(2, smoothSpan) + 1);

        var sm = new double[n];
        var prev = closes[0] > 0 ? Math.Log(closes[0]) : 0;

        for (var i = 0; i < n; i++)
        {
            var y = closes[i] > 0 ? Math.Log(closes[i]) : prev;
            prev += alpha * (y - prev);
            sm[i] = prev;
        }

        // Erste Ableitung: Änderung der geglätteten Kurve je Bar.
        var d = new double[n];
        for (var i = 1; i < n; i++) d[i] = sm[i] - sm[i - 1];

        var buf = new double[window];

        /* Der Anfangswert ist bewusst kein int.MinValue.

           Die Sperrzeit wird als i − lastHit geprüft. Mit int.MinValue als
           Anfangswert läuft diese Differenz beim ersten Durchgang über und
           wird negativ — die Sperre greift dann bei JEDEM Punkt, lastHit wird
           nie gesetzt, und der Detektor findet über die gesamte Reihe kein
           einziges Ereignis. Aufgefallen an einer Kunstreihe mit eingebautem
           Trendbeginn: Die z-Werte reichten bis 5,4, gemeldet wurde nichts. */
        var lastHit = -refractoryBars - 1;

        for (var i = window + 1; i < n; i++)
        {
            for (var k = 0; k < window; k++) buf[k] = Math.Abs(d[i - window + k]);

            var typical = Median(buf);
            if (typical <= 1e-12) continue;

            /* Wie beim Ereignisdetektor über den Median und den Faktor 1,4826:
               Mittelwert und Streuung würden von genau den Ausschlägen
               aufgebläht, die gesucht werden. */
            var z = Math.Abs(d[i]) / (typical * 1.4826);

            if (z < minZ) continue;
            if (i - lastHit < refractoryBars) continue;

            lastHit = i;
            result.Add(new DerivativeEvent(i, Math.Sign(d[i]), Math.Round(z, 3)));
        }

        return result;
    }

    private static double Median(double[] buf)
    {
        var c = (double[])buf.Clone();
        Array.Sort(c);

        var m = c.Length / 2;
        return c.Length % 2 == 1 ? c[m] : (c[m - 1] + c[m]) / 2;
    }
}
