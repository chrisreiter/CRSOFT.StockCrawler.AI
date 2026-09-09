using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Xunit;

namespace Ingest.Core.Tests;

/// <summary>
/// Diese Tests sichern eine real aufgetretene Fehlerquelle ab: bei kleineren
/// Kryptowerten trifft die Zuordnung CoinGecko-Symbol → Yahoo-Ticker
/// gelegentlich ein ganz anderes Papier. Beobachtet wurde ein Tagessprung um
/// den Faktor 1254, der die Prognosefehler auf über 100 % trieb.
/// </summary>
public class BarQualityFilterTests
{
    private static List<PriceBar> Reihe(params decimal[] closes)
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return closes.Select((c, i) => new PriceBar { TsUtc = t0.AddDays(i), Close = c }).ToList();
    }

    [Fact]
    public void Clean_NormaleReihe_BleibtUnveraendert()
    {
        var bars = Reihe(100m, 102m, 99m, 105m, 103m);
        var (kept, report) = BarQualityFilter.Clean(bars, BarInterval.Daily);

        Assert.Equal(5, kept.Count);
        Assert.Equal(0, report.Rejected);
    }

    [Fact]
    public void Clean_VolatilesKrypto_WirdNichtUebermaessigGefiltert()
    {
        // Bis zum Faktor 10 pro Tag gilt als möglich, wenn auch selten.
        var bars = Reihe(100m, 150m, 90m, 200m, 120m);
        var (kept, report) = BarQualityFilter.Clean(bars, BarInterval.Daily);

        Assert.Equal(5, kept.Count);
        Assert.Equal(0, report.Rejected);
    }

    [Fact]
    public void Clean_UnmoeglicherSprung_WirdVerworfen()
    {
        // 1254-facher Sprung wie bei GRAM-USD beobachtet.
        var bars = Reihe(0.005m, 0.0053m, 6.65m, 0.0054m, 0.0055m);
        var (kept, report) = BarQualityFilter.Clean(bars, BarInterval.Daily);

        Assert.Equal(4, kept.Count);
        Assert.Equal(1, report.Rejected);
        Assert.DoesNotContain(kept, b => b.Close == 6.65m);
    }

    [Fact]
    public void Clean_NachAusreisser_BleibtDieGesundeFolgebarErhalten()
    {
        /* Wichtig: der Vergleichswert darf nach einem verworfenen Ausreißer
           nicht fortgeschrieben werden — sonst würde die völlig gesunde
           Folgebar ebenfalls als Sprung gewertet und die Reihe risse ab. */
        var bars = Reihe(100m, 5000m, 101m, 102m);
        var (kept, _) = BarQualityFilter.Clean(bars, BarInterval.Daily);

        Assert.Equal(3, kept.Count);
        Assert.Equal([100m, 101m, 102m], kept.Select(b => b.Close));
    }

    [Fact]
    public void Clean_NullUndNegativKurse_WerdenVerworfen()
    {
        var bars = Reihe(100m, 0m, 101m, -5m, 102m);
        var (kept, report) = BarQualityFilter.Clean(bars, BarInterval.Daily);

        Assert.Equal(3, kept.Count);
        Assert.Equal(2, report.Rejected);
        Assert.All(kept, b => Assert.True(b.Close > 0));
    }

    [Fact]
    public void Clean_StundenbarsSindStrenger()
    {
        // Faktor 8 als EINZELNER Ausschlag: bei Tagesbars noch zulässig,
        // bei Stundenbars nicht.
        var bars = Reihe(100m, 800m, 101m);

        var (taeglich, _) = BarQualityFilter.Clean(bars, BarInterval.Daily);
        var (stuendlich, _) = BarQualityFilter.Clean(bars, BarInterval.Hourly);

        Assert.Equal(3, taeglich.Count);
        Assert.Equal(2, stuendlich.Count);
    }

    [Fact]
    public void Clean_DauerhafteNeubewertung_BleibtErhalten()
    {
        /* Ein Sprung, der auf dem neuen Niveau bleibt, ist keine Fehlmessung,
           sondern eine Neubewertung — etwa eine Token-Umstellung. Würde die
           Reihe ab da verworfen, bliebe der Wert dauerhaft ohne Kurse. */
        var bars = Reihe(100m, 5000m, 5050m, 4980m, 5100m);
        var (kept, report) = BarQualityFilter.Clean(bars, BarInterval.Hourly);

        Assert.Equal(5, kept.Count);
        Assert.Equal(0, report.Rejected);

        // Der Sprung wird trotzdem gemeldet, damit er auffällt.
        Assert.True(report.WorstFactor > 40);
    }

    [Fact]
    public void RejectRatio_ZeigtFalscheSymbolzuordnungAn()
    {
        // Eine Reihe, die ständig zwischen zwei Größenordnungen springt, kommt
        // von zwei verschiedenen Papieren.
        var closes = new List<decimal>();
        for (var i = 0; i < 20; i++) closes.Add(i % 2 == 0 ? 0.001m : 100m);

        var (_, report) = BarQualityFilter.Clean(Reihe([.. closes]), BarInterval.Daily);

        Assert.True(report.RejectRatio > BarQualityFilter.SuspiciousRatio,
            $"Anteil war nur {report.RejectRatio:P1}");
    }

    [Fact]
    public void RejectRatio_LeereReihe_IstNullStattDivisionDurchNull()
    {
        var (kept, report) = BarQualityFilter.Clean([], BarInterval.Daily);

        Assert.Empty(kept);
        Assert.Equal(0.0, report.RejectRatio);
    }

    [Fact]
    public void Clean_ErsteBarWirdNieVerworfen()
    {
        // Ohne Vorgänger gibt es keinen Sprung zu bewerten.
        var bars = Reihe(0.00001m, 0.000011m);
        var (kept, _) = BarQualityFilter.Clean(bars, BarInterval.Daily);

        Assert.Equal(2, kept.Count);
    }
}
