using Ingest.Core.Analysis;
using Xunit;

namespace Ingest.Core.Tests;

/// <summary>
/// Prüfungen der Suche nach geschlossenen Gruppen.
///
/// Die entscheidende Frage ist nicht, ob das Verfahren eine Gruppe findet — die
/// schrittweise Suche liefert immer eine. Sondern ob sie eine eingebaute
/// wiederfindet und bei reinem Zufall keine behauptet.
/// </summary>
public class ConservedGroupTests
{
    private static double Noise(Random r, double sd)
    {
        double s = 0;
        for (var i = 0; i < 6; i++) s += r.NextDouble();
        return sd * (s - 3);
    }

    /// <summary>
    /// Erzeugt Anteilsreihen. Werte, die eine Gruppe bilden sollen, teilen sich
    /// eine feste Summe: Was der eine gewinnt, verlieren die anderen anteilig.
    /// Genau das beschreibt eine geschlossene Gruppe.
    /// </summary>
    private static List<(int, string, double[])> Universe(
        int n, int len, int groupSize, Random r, bool withGroup)
    {
        var s = new double[n][];

        for (var i = 0; i < n; i++)
        {
            s[i] = new double[len];
            s[i][0] = 0.06;
        }

        for (var t = 1; t < len; t++)
        {
            for (var i = 0; i < n; i++) s[i][t] = s[i][t - 1] + Noise(r, 0.0003);

            if (!withGroup) continue;

            /* Umschichtung INNERHALB der ersten groupSize Werte: Ein zufällig
               gewählter Betrag wandert von einem zum anderen. Die Summe der
               Gruppe bleibt dabei exakt gleich -- alles andere bewegt sich
               unabhängig weiter. */
            var from = r.Next(groupSize);
            var to = r.Next(groupSize);
            if (from == to) continue;

            var amount = Math.Abs(Noise(r, 0.0015));

            s[from][t] -= amount;
            s[to][t] += amount;
        }

        var list = new List<(int, string, double[])>(n);
        for (var i = 0; i < n; i++) list.Add((i + 1, $"A{i:D2}", s[i]));

        return list;
    }

    [Fact]
    public void FindetEingebauteGeschlosseneGruppe()
    {
        var r = new Random(4711);
        const int n = 14, len = 2400, groupSize = 5;

        var shares = Universe(n, len, groupSize, r, withGroup: true);

        var res = ConservedGroups.Find(shares, splitAt: 1400, groups: 1, maxSize: 10);

        Assert.NotEmpty(res);

        var g = res[0];
        var found = g.Members.Select(m => m.Symbol).ToHashSet();

        /* Die Gruppe muss überwiegend aus den gebauten Mitgliedern bestehen.
           Nicht exakt: Die schrittweise Suche darf einen Wert danebengreifen,
           solange sie im Kern richtig liegt. */
        var hits = Enumerable.Range(0, groupSize).Count(i => found.Contains($"A{i:D2}"));

        Assert.True(hits >= 4, $"nur {hits} der {groupSize} gebauten Mitglieder gefunden: "
                             + string.Join(", ", found));

        Assert.True(g.Holds, $"außerhalb {g.OutOfSampleClosure}, Zufall {g.NullClosure}");

        // Und der Ausgleich muss deutlich sein, nicht knapp.
        Assert.True(g.OutOfSampleClosure < 0.7, $"Geschlossenheit {g.OutOfSampleClosure}");
    }

    /// <summary>
    /// Der wichtigere Test. Vierzehn unabhängige Reihen. Die schrittweise Suche
    /// findet auch hier eine Gruppe — sie ist an das Rauschen des
    /// Suchzeitraums angepasst. Im Prüfzeitraum darf davon nichts übrig sein.
    /// </summary>
    [Fact]
    public void UnabhaengigeReihenHaltenNicht()
    {
        var r = new Random(4711);

        var shares = Universe(14, 2400, 5, r, withGroup: false);

        var res = ConservedGroups.Find(shares, splitAt: 1400, groups: 3, maxSize: 10);

        Assert.DoesNotContain(res, g => g.Holds);
    }

    /// <summary>
    /// Die Anpassung an den Suchzeitraum muss sichtbar bleiben: Innerhalb ist
    /// die Gruppe stets geschlossener als außerhalb. Wäre sie es nicht, täte
    /// die Suche nicht, was sie soll.
    /// </summary>
    [Fact]
    public void AnpassungAnDenSuchzeitraumIstSichtbar()
    {
        var r = new Random(99);

        var shares = Universe(16, 2400, 5, r, withGroup: false);
        var res = ConservedGroups.Find(shares, splitAt: 1400, groups: 1, maxSize: 8);

        if (res.Count == 0) return;   // keine Gruppe gebildet ist auch ein Ergebnis

        Assert.True(res[0].Closure <= res[0].OutOfSampleClosure + 1e-9,
            $"innen {res[0].Closure}, außen {res[0].OutOfSampleClosure}");
    }
}
