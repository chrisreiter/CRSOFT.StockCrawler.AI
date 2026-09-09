using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Xunit;

namespace Ingest.Core.Tests;

public class CrossingDetectorTests
{
    private static DateTime[] Tage(int n)
        => Enumerable.Range(0, n)
            .Select(i => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i))
            .ToArray();

    [Fact]
    public void Detect_RelativeStaerkeKippt_FindetGenauEineKreuzung()
    {
        /* Nach der Normalisierung starten beide Kurven zwangsläufig bei 100.
           Eine Kreuzung ist deshalb nicht "die eine überholt die andere im
           Preis", sondern "die relative Wertentwicklung kippt".

           Hier zieht A zuerst davon und fällt dann zurück, B macht es
           umgekehrt — der Abstand wechselt genau einmal das Vorzeichen. */
        var a = new List<decimal>();
        var b = new List<decimal>();

        for (var i = 0; i <= 10; i++)          // A vorn
        {
            a.Add(100m + i);
            b.Add(100m - i * 0.5m);
        }
        for (var i = 1; i <= 10; i++)          // B überholt
        {
            a.Add(110m - i * 2m);
            b.Add(95m + i * 2m);
        }

        var result = CrossingDetector.Detect(1, 2, BarInterval.Daily, Tage(a.Count), a, b);

        Assert.Single(result);
        Assert.False(result[0].Upward);        // A liegt danach unter B
        Assert.True(result[0].SpreadBefore > 0);
        Assert.True(result[0].SpreadAfter < 0);
    }

    [Fact]
    public void Detect_AuseinanderlaufendeKurven_SindKeineKreuzung()
    {
        // Beide starten normalisiert bei 100 und driften auseinander, ohne die
        // Plätze je zu tauschen. Das darf keine Kreuzung ergeben.
        var n = 21;
        var a = Enumerable.Range(0, n).Select(i => (decimal)(100 - i)).ToList();
        var b = Enumerable.Range(0, n).Select(i => (decimal)(100 + i)).ToList();

        var result = CrossingDetector.Detect(1, 2, BarInterval.Daily, Tage(n), a, b);

        Assert.Empty(result);
    }

    [Fact]
    public void Detect_ParalleleKurven_FindetKeineKreuzung()
    {
        var n = 30;
        var a = Enumerable.Range(0, n).Select(i => (decimal)(100 + i)).ToList();
        var b = Enumerable.Range(0, n).Select(i => (decimal)(200 + 2 * i)).ToList();

        // Beide auf 100 normalisiert laufen sie identisch — kein Vorzeichenwechsel.
        var result = CrossingDetector.Detect(1, 2, BarInterval.Daily, Tage(n), a, b);

        Assert.Empty(result);
    }

    [Fact]
    public void Detect_MinimalesRauschen_ErzeugtKeineScheinkreuzungen()
    {
        /* Zwei fast identische Kurven pendeln um die Nulllinie. Ohne die
           Mindestabstand-Schwelle würde jede Mikrobewegung als Kreuzung
           gezählt und die Tabelle mit Rauschen fluten. */
        var n = 100;
        var rnd = new Random(1);
        var a = new List<decimal>();
        var b = new List<decimal>();

        for (var i = 0; i < n; i++)
        {
            a.Add(100m + (decimal)(rnd.NextDouble() * 0.02));
            b.Add(100m + (decimal)(rnd.NextDouble() * 0.02));
        }

        var result = CrossingDetector.Detect(1, 2, BarInterval.Daily, Tage(n), a, b, minSpread: 0.5);
        Assert.Empty(result);
    }

    [Fact]
    public void MovingAverageCross_TrendwechselWirdErkannt()
    {
        // Erst fallend, dann steigend: die schnelle MA muss die langsame
        // irgendwann nach oben kreuzen.
        var closes = new List<decimal>();
        for (var i = 0; i < 80; i++) closes.Add(100m - i * 0.5m);
        for (var i = 0; i < 80; i++) closes.Add(60m + i * 0.8m);

        var result = CrossingDetector.MovingAverageCross(
            Tage(closes.Count), closes, fast: 10, slow: 30);

        Assert.Contains(result, r => r.GoldenCross);
    }

    [Fact]
    public void MovingAverageCross_ZuWenigDaten_LiefertLeer()
    {
        var closes = Enumerable.Repeat(100m, 20).ToList();
        var result = CrossingDetector.MovingAverageCross(Tage(20), closes, fast: 10, slow: 50);

        Assert.Empty(result);
    }
}

public class SeriesAlignerTests
{
    private static PriceBar Bar(DateTime ts, decimal close) => new() { TsUtc = ts, Close = close };

    [Fact]
    public void Intersect_NimmtNurGemeinsameZeitpunkte()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var series = new Dictionary<int, IReadOnlyList<PriceBar>>
        {
            [1] = [Bar(t0, 10m), Bar(t0.AddDays(1), 11m), Bar(t0.AddDays(2), 12m)],
            [2] = [Bar(t0.AddDays(1), 20m), Bar(t0.AddDays(2), 21m), Bar(t0.AddDays(3), 22m)]
        };

        var (ts, closes) = SeriesAligner.Intersect(series);

        Assert.Equal(2, ts.Length);
        Assert.Equal(t0.AddDays(1), ts[0]);
        Assert.Equal([11m, 12m], closes[1]);
        Assert.Equal([20m, 21m], closes[2]);
    }

    [Fact]
    public void Intersect_KeineUeberschneidung_LiefertLeer()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var series = new Dictionary<int, IReadOnlyList<PriceBar>>
        {
            [1] = [Bar(t0, 10m)],
            [2] = [Bar(t0.AddDays(5), 20m)]
        };

        var (ts, _) = SeriesAligner.Intersect(series);
        Assert.Empty(ts);
    }

    [Fact]
    public void ForwardFill_LueckenUebernehmenDenLetztenWert()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = new List<PriceBar> { Bar(t0, 10m), Bar(t0.AddDays(3), 13m) };
        var grid = Enumerable.Range(0, 5).Select(i => t0.AddDays(i)).ToArray();

        var filled = SeriesAligner.ForwardFill(bars, grid);

        Assert.Equal(10m, filled[0]);
        Assert.Equal(10m, filled[1]);   // Wochenendlücke
        Assert.Equal(10m, filled[2]);
        Assert.Equal(13m, filled[3]);
        Assert.Equal(13m, filled[4]);
    }

    [Fact]
    public void ForwardFill_VorDerErstenBar_BleibtLeer()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = new List<PriceBar> { Bar(t0.AddDays(2), 10m) };
        var grid = Enumerable.Range(0, 4).Select(i => t0.AddDays(i)).ToArray();

        var filled = SeriesAligner.ForwardFill(bars, grid);

        Assert.Null(filled[0]);
        Assert.Null(filled[1]);
        Assert.Equal(10m, filled[2]);
    }

    [Fact]
    public void RebaseTo100_StartetBeiHundert()
    {
        var closes = new List<decimal> { 250m, 275m, 300m };
        var r = SeriesAligner.RebaseTo100(closes);

        Assert.Equal(100.0, r[0], 6);
        Assert.Equal(110.0, r[1], 6);
        Assert.Equal(120.0, r[2], 6);
    }

    [Fact]
    public void RebaseTo100_MachtGroessenordnungenVergleichbar()
    {
        // Genau dafür ist die Normalisierung da: eine 300-Dollar-Aktie und ein
        // 70.000-Dollar-Bitcoin mit gleicher prozentualer Bewegung müssen
        // deckungsgleiche Kurven ergeben.
        var aktie = new List<decimal> { 300m, 330m };
        var krypto = new List<decimal> { 70000m, 77000m };

        var ra = SeriesAligner.RebaseTo100(aktie);
        var rk = SeriesAligner.RebaseTo100(krypto);

        Assert.Equal(ra[1], rk[1], 6);
    }

    [Fact]
    public void RebaseTo100_StartwertNull_FaelltAufErstenPositivenWertZurueck()
    {
        var closes = new List<decimal> { 0m, 50m, 100m };
        var r = SeriesAligner.RebaseTo100(closes);

        Assert.All(r, v => Assert.False(double.IsNaN(v)));
        Assert.All(r, v => Assert.False(double.IsInfinity(v)));
    }
}
