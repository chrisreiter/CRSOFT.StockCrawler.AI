using Ingest.Core.Analysis;
using Xunit;

namespace Ingest.Core.Tests;

public class StatisticsTests
{
    [Fact]
    public void Correlation_IdentischeReihen_IstEins()
    {
        double[] a = [1, 2, 3, 4, 5, 6, 7, 8];
        Assert.Equal(1.0, Statistics.Correlation(a, a), 6);
    }

    [Fact]
    public void Correlation_GegenlaeufigeReihen_IstMinusEins()
    {
        double[] a = [1, 2, 3, 4, 5, 6, 7, 8];
        double[] b = [8, 7, 6, 5, 4, 3, 2, 1];
        Assert.Equal(-1.0, Statistics.Correlation(a, b), 6);
    }

    [Fact]
    public void Correlation_KonstanteReihe_IstNullStattNaN()
    {
        // Ohne Sonderbehandlung wäre der Nenner null und das Ergebnis NaN —
        // das würde beim Schreiben in die FLOAT-Spalte scheitern.
        double[] a = [1, 2, 3, 4, 5];
        double[] konstant = [7, 7, 7, 7, 7];

        var r = Statistics.Correlation(a, konstant);
        Assert.False(double.IsNaN(r));
        Assert.Equal(0.0, r);
    }

    [Fact]
    public void Correlation_ZuWenigePunkte_IstNull()
    {
        double[] a = [1, 2];
        double[] b = [3, 4];
        Assert.Equal(0.0, Statistics.Correlation(a, b));
    }

    [Fact]
    public void BestLag_VerschobeneReihe_FindetDieVerschiebung()
    {
        // b ist a um 3 Positionen nach hinten verschoben: a läuft b voraus.
        var rnd = new Random(42);
        var a = new double[200];
        for (var i = 0; i < a.Length; i++) a[i] = rnd.NextDouble() - 0.5;

        const int shift = 3;
        var b = new double[a.Length];
        for (var i = shift; i < b.Length; i++) b[i] = a[i - shift];

        var (lag, corr) = Statistics.BestLag(a, b, maxLag: 12);

        Assert.Equal(shift, lag);
        Assert.True(corr > 0.9, $"Korrelation am besten Lag war nur {corr:F3}");
    }

    [Fact]
    public void BestLag_GleichzeitigeReihen_LiefertNull()
    {
        var rnd = new Random(7);
        var a = new double[150];
        for (var i = 0; i < a.Length; i++) a[i] = rnd.NextDouble() - 0.5;

        // b folgt a ohne Versatz, mit etwas Rauschen.
        var b = a.Select(v => v * 0.8 + (rnd.NextDouble() - 0.5) * 0.05).ToArray();

        var (lag, _) = Statistics.BestLag(a, b, maxLag: 10);
        Assert.Equal(0, lag);
    }

    [Fact]
    public void LogReturns_KonstanterKurs_IstNull()
    {
        var closes = new List<decimal> { 100m, 100m, 100m, 100m };
        var r = Statistics.LogReturns(closes);

        Assert.Equal(3, r.Length);
        Assert.All(r, v => Assert.Equal(0.0, v, 10));
    }

    [Fact]
    public void LogReturns_Verdopplung_IstLogZwei()
    {
        var closes = new List<decimal> { 100m, 200m };
        var r = Statistics.LogReturns(closes);

        Assert.Single(r);
        Assert.Equal(Math.Log(2), r[0], 8);
    }

    [Fact]
    public void LogReturns_NullOderNegativerKurs_LiefertNullStattNaN()
    {
        // Kurse <= 0 sind Datenmüll; sie dürfen die Reihe nicht mit NaN vergiften.
        var closes = new List<decimal> { 100m, 0m, 50m };
        var r = Statistics.LogReturns(closes);

        Assert.All(r, v => Assert.False(double.IsNaN(v)));
    }

    [Fact]
    public void Beta_DoppelteBewegung_IstZwei()
    {
        double[] x = [0.01, -0.02, 0.03, -0.01, 0.02, 0.005, -0.015];
        var y = x.Select(v => v * 2.0).ToArray();

        Assert.Equal(2.0, Statistics.Beta(x, y), 6);
    }

    [Fact]
    public void Ewma_GewichtetJuengereWerteStaerker()
    {
        double[] x = [0, 0, 0, 0, 10];
        var mitGedaechtnis = Statistics.Ewma(x, alpha: 0.9);
        var traege = Statistics.Ewma(x, alpha: 0.1);

        // Hohes Alpha reagiert stärker auf den letzten Wert.
        Assert.True(mitGedaechtnis > traege);
    }

    [Fact]
    public void StdDev_EinzelnerWert_IstNull()
    {
        double[] x = [5];
        Assert.Equal(0.0, Statistics.StdDev(x));
    }
}
