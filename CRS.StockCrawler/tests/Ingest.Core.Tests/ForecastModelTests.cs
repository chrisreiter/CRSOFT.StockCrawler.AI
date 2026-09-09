using Ingest.Core.Analysis;
using Xunit;

namespace Ingest.Core.Tests;

public class ForecastModelTests
{
    private static List<decimal> Trend(int n, double start, double stepPct)
    {
        var list = new List<decimal>(n);
        var v = start;
        for (var i = 0; i < n; i++)
        {
            list.Add((decimal)v);
            v *= 1 + stepPct;
        }
        return list;
    }

    [Fact]
    public void Naive_SagtImmerKeineAenderung()
    {
        var input = new ForecastInput(Trend(100, 100, 0.01), 10);
        Assert.Equal(0.0, new NaiveModel().PredictReturn(input));
    }

    [Fact]
    public void Drift_SteigenderKurs_SagtAnstieg()
    {
        var input = new ForecastInput(Trend(250, 100, 0.002), 10);
        Assert.True(new DriftModel().PredictReturn(input) > 0);
    }

    [Fact]
    public void Drift_FallenderKurs_SagtRueckgang()
    {
        var input = new ForecastInput(Trend(250, 100, -0.002), 10);
        Assert.True(new DriftModel().PredictReturn(input) < 0);
    }

    [Fact]
    public void Drift_SkaliertMitDemHorizont()
    {
        var closes = Trend(250, 100, 0.002);
        var kurz = new DriftModel().PredictReturn(new ForecastInput(closes, 1));
        var lang = new DriftModel().PredictReturn(new ForecastInput(closes, 10));

        Assert.Equal(kurz * 10, lang, 8);
    }

    [Fact]
    public void Momentum_IstUeberLangeHorizonteGedaempft()
    {
        // Der Effekt darf nicht linear mitwachsen, sondern muss abflachen.
        var closes = Trend(200, 100, 0.003);
        var m = new MomentumModel();

        var h1 = m.PredictReturn(new ForecastInput(closes, 1));
        var h20 = m.PredictReturn(new ForecastInput(closes, 20));

        Assert.True(h20 > h1);
        Assert.True(h20 < h1 * 20, "Momentum darf nicht linear mit dem Horizont wachsen");
    }

    [Fact]
    public void MeanReversion_KursWeitUeberMittel_SagtRueckgang()
    {
        // Lange flach, dann ein Sprung nach oben: die Rückkehr zum Mittel
        // muss negativ ausfallen.
        var closes = Enumerable.Repeat(100m, 60).ToList();
        closes.AddRange(Enumerable.Repeat(130m, 3));

        var r = new MeanReversionModel().PredictReturn(
            new ForecastInput(closes, 5));

        Assert.True(r < 0, $"Erwartet negativ, war {r}");
    }

    [Fact]
    public void MeanReversion_KursWeitUnterMittel_SagtAnstieg()
    {
        var closes = Enumerable.Repeat(100m, 60).ToList();
        closes.AddRange(Enumerable.Repeat(70m, 3));

        var r = new MeanReversionModel().PredictReturn(
            new ForecastInput(closes, 5));

        Assert.True(r > 0, $"Erwartet positiv, war {r}");
    }

    [Fact]
    public void LeadLag_OhneFruehindikatoren_IstNull()
    {
        var input = new ForecastInput(Trend(100, 100, 0.001), 5);
        Assert.Equal(0.0, new LeadLagModel().PredictReturn(input));
    }

    [Fact]
    public void LeadLag_UebertraegtBewegungDesVorlaeufers()
    {
        // Vorlauf 6 Bars deckt den Horizont von 3 Bars ab.
        var input = new ForecastInput(Trend(100, 100, 0.001), 3,
            [new LeadSignal(99, RecentReturn: 0.05, LagBars: 6, Beta: 1.0, Corr: 0.7)]);

        Assert.True(new LeadLagModel().PredictReturn(input) > 0);
    }

    [Fact]
    public void LeadLag_ZuKurzerVorlauf_WirdIgnoriert()
    {
        // Ein Vorlauf von 2 Bars sagt nichts über 24 Bars in der Zukunft.
        var input = new ForecastInput(Trend(100, 100, 0.001), 24, [new LeadSignal(99, RecentReturn: 0.05, LagBars: 2, Beta: 1.0, Corr: 0.9)]);

        Assert.Equal(0.0, new LeadLagModel().PredictReturn(input));
    }

    [Fact]
    public void LeadLag_SchwacheKorrelation_WirdIgnoriert()
    {
        var input = new ForecastInput(Trend(100, 100, 0.001), 3, [new LeadSignal(99, RecentReturn: 0.05, LagBars: 6, Beta: 1.0, Corr: 0.05)]);

        Assert.Equal(0.0, new LeadLagModel().PredictReturn(input));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(59)]
    public void AlleModelle_ZuWenigHistorie_LiefernNullStattAusnahme(int anzahl)
    {
        var input = new ForecastInput(Enumerable.Repeat(100m, anzahl).ToList(), 5);

        foreach (var m in Ensemble.DefaultModels())
        {
            var r = m.PredictReturn(input);
            Assert.False(double.IsNaN(r), $"{m.Name} lieferte NaN");
            Assert.False(double.IsInfinity(r), $"{m.Name} lieferte Unendlich");
        }
    }
}
