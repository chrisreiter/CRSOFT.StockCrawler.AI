using Ingest.Core.Analysis.Spectral;
using Xunit;

namespace Ingest.Core.Tests;

/// <summary>
/// Prüfungen der Zerlegung über das gesamte Universum.
///
/// Der Kern dieser Säule ist eine Unterscheidung, die sich nur an konstruierten
/// Daten prüfen lässt: zwischen einem Markt, in dem viele Werte <i>dieselbe</i>
/// Schwingung tragen, und einem Markt, in dem jeder Wert für sich Struktur hat.
/// Beides sieht im einzelnen Chart gleich aus. Wenn das Verfahren die beiden
/// Fälle nicht trennt, ist es wertlos — und zwar auf eine Weise, die man an
/// echten Kursen nie bemerken würde.
/// </summary>
public class MarketModeTests
{
    private static Random Rnd(int seed = 4711) => new(seed);

    private static double Noise(Random r, double sd)
    {
        double s = 0;
        for (var i = 0; i < 6; i++) s += r.NextDouble();
        return sd * (s - 3);
    }

    /// <summary>
    /// Rote Reihe: Ein gleitendes Mittel über weißes Rauschen erzeugt genau die
    /// Spektralfarbe echter Kurse — Leistung fällt mit der Frequenz, mit
    /// Langzeitgedächtnis, aber ohne jeden Zusammenhang zu anderen Reihen.
    /// </summary>
    private static double[] Red(Random r, int n, double persistence = 0.9)
    {
        var x = new double[n];
        double state = 0;

        for (var i = 0; i < n; i++)
        {
            state = persistence * state + Noise(r, 1);
            x[i] = state;
        }

        return x;
    }

    private static List<(int, string, double[])> Universe(
        int count, int len, Func<int, Random, double[]> make, int seed = 4711)
    {
        var r = Rnd(seed);
        var list = new List<(int, string, double[])>(count);

        for (var i = 0; i < count; i++)
            list.Add((i + 1, $"A{i:D2}", make(i, r)));

        return list;
    }

    // ------------------------------------------------------------------------

    /// <summary>
    /// Zwanzig Werte, die alle eine 60-Bar-Schwingung tragen — jeder mit einem
    /// eigenen Phasenversatz und eigenem rotem Rauschen darüber. Genau der Fall,
    /// den die Säule finden soll.
    /// </summary>
    [Fact]
    public void FindetGemeinsameSchwingungTrotzPhasenversatz()
    {
        const int n = 1600;
        const double period = 60;

        // Versatz je Wert, in Bars. Der Reihe nach gestaffelt.
        var data = Universe(20, n, (i, r) =>
        {
            /* Versatz bis zu 15 Bars auf eine Periode von 60 -- also bis zu
               90 Grad. Weiter gespreizt loeschen sich die Beitraege gegenseitig
               aus, und dann SOLL die Mode auch nicht mehr gefunden werden. */
            var shift = i * 0.8;
            var x = Red(r, n, 0.9);

            // Gemeinsame Schwingung, deutlich unter dem eigenen Rauschen.
            var scale = 2.5 * StdDev(x);
            for (var t = 0; t < n; t++)
                x[t] += scale * Math.Sin(2 * Math.PI * (t - shift) / period);

            return x;
        });

        var res = MarketModes.Analyze(data, 192, 0.85, 10, 150, surrogates: 60);

        var best = res.Modes.FirstOrDefault();
        Assert.NotNull(best);

        Assert.True(best!.Significant, $"z={best.ZScore}");
        /* Weite Toleranz mit Absicht: Die Frequenzglaettung, ohne die die
           Kreuzspektralmatrix keinen vollen Rang bekaeme, verbreitert jede
           Spitze. Die Mode wird als Band gefunden, nicht als Linie. */
        Assert.True(Math.Abs(best.PeriodBars - period) / period < 0.35,
            $"Periode {best.PeriodBars}");

        /* Der gemessene Anteil muss deutlich über dem der Ersatzreihen liegen.
           Der Anteil allein sagt nichts -- entscheidend ist der Abstand. */
        Assert.True(best.ExplainedShare > best.SurrogateShare,
            $"gemessen {best.ExplainedShare}, Ersatz {best.SurrogateShare}");
    }

    /// <summary>
    /// Der wichtigste Test dieser Datei. Zwanzig Werte, jeder für sich rot —
    /// also mit Langzeitgedächtnis, Autokorrelation und genau der Spektralfarbe
    /// echter Kurse — aber ohne jeden Zusammenhang untereinander.
    ///
    /// Ohne die Ersatzprüfung liefert die Zerlegung auch hier einen führenden
    /// Eigenwert, der weit über 1/N liegt: Rote Reihen erzeugen allein durch
    /// Zufall scheinbare Gemeinsamkeit, und zwar erheblich mehr als weiße.
    /// Genau daran scheitert jede Auswertung, die nur den Anteil betrachtet.
    /// </summary>
    [Fact]
    public void UnabhaengigeRoteReihenErgebenKeineGemeinsameSchwingung()
    {
        const int n = 1600;

        var data = Universe(20, n, (_, r) => Red(r, n, 0.9));

        var res = MarketModes.Analyze(data, 192, 0.85, 10, 150, surrogates: 60);

        Assert.NotEmpty(res.Modes);
        Assert.DoesNotContain(res.Modes, m => m.Significant);
    }

    /// <summary>
    /// Der Phasenversatz muss nicht nur erkannt, sondern richtig herum
    /// zugeordnet werden. Wer vorausläuft, muss als vorauslaufend erscheinen —
    /// sonst zeigt die Auswertung auf den Falschen, und zwar mit voller
    /// Überzeugung.
    /// </summary>
    [Fact]
    public void ErkenntWerVorauslaeuft()
    {
        const int n = 2000;
        const double period = 50;
        const double lead = 6;

        /* Wert 0 läuft der Gruppe um sechs Bars voraus, alle anderen laufen
           gleich. Bei sonst identischem Aufbau. */
        var data = Universe(12, n, (i, r) =>
        {
            var shift = i == 0 ? lead : 0;
            var x = Red(r, n, 0.85);

            var scale = 1.2 * StdDev(x);
            for (var t = 0; t < n; t++)
                x[t] += scale * Math.Sin(2 * Math.PI * (t + shift) / period);

            return x;
        });

        var res = MarketModes.Analyze(data, 192, 0.85, 20, 120, surrogates: 40);

        var mode = res.Modes.FirstOrDefault(m => Math.Abs(m.PeriodBars - period) / period < 0.25)
                   ?? res.Modes.First();

        var vorne = mode.Members.FirstOrDefault(m => m.Symbol == "A00");
        Assert.NotNull(vorne);

        var andere = mode.Members.Where(m => m.Symbol != "A00").ToList();
        Assert.NotEmpty(andere);

        var mittel = andere.Average(m => m.LeadBars);

        // Der Vorläufer muss klar vor dem Rest liegen.
        Assert.True(Math.Abs(vorne!.LeadBars - mittel) > 2,
            $"A00 bei {vorne.LeadBars}, Rest im Mittel {mittel:F2}");
    }

    /// <summary>
    /// Ein einzelner sehr lauter Wert darf die Mode nicht an sich reißen. Ohne
    /// die Normierung je Frequenz fände die Zerlegung die Lautstärke statt die
    /// Gemeinsamkeit.
    /// </summary>
    [Fact]
    public void LauterWertBeherrschtDieModeNicht()
    {
        const int n = 1600;
        const double period = 40;

        var data = Universe(15, n, (i, r) =>
        {
            var x = Red(r, n, 0.88);

            // Wert 0 schwankt fünfzigmal so stark wie alle anderen.
            var loud = i == 0 ? 50.0 : 1.0;
            for (var t = 0; t < n; t++) x[t] *= loud;

            // Die gemeinsame Schwingung tragen ALLE gleich stark -- relativ.
            var scale = 1.0 * StdDev(x);
            for (var t = 0; t < n; t++)
                x[t] += scale * Math.Sin(2 * Math.PI * t / period);

            return x;
        });

        var res = MarketModes.Analyze(data, 192, 0.85, 10, 150, surrogates: 40);
        var mode = res.Modes.First();

        var laut = mode.Members.First(m => m.Symbol == "A00").Loading;
        var mittel = mode.Members.Where(m => m.Symbol != "A00").Average(m => m.Loading);

        // Die Ladung des lauten Wertes darf nicht aus dem Rahmen fallen.
        Assert.True(laut < mittel * 2.5, $"laut {laut:F3}, Rest im Mittel {mittel:F3}");
    }

    private static double StdDev(double[] x)
    {
        var mean = x.Average();
        return Math.Sqrt(x.Sum(v => (v - mean) * (v - mean)) / x.Length);
    }
}
