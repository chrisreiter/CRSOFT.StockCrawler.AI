using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Xunit;

namespace Ingest.Core.Tests;

/// <summary>
/// Diese Tests sichern die Voraussetzung dafür ab, dass Aktien und Krypto
/// überhaupt vergleichbar sind. Ohne Rasterung liegt eine US-Aktie täglich auf
/// 13:30 UTC, Bitcoin auf 00:00 — die Schnittmenge wäre leer.
/// </summary>
public class BarNormalizerTests
{
    [Fact]
    public void Snap_Tagesbar_AufMitternacht()
    {
        var handelsbeginn = new DateTime(2026, 8, 20, 13, 30, 0, DateTimeKind.Utc);
        var erwartet = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(erwartet, BarNormalizer.Snap(handelsbeginn, BarInterval.Daily));
    }

    [Fact]
    public void Snap_Stundenbar_AufVolleStunde()
    {
        var halbeStunde = new DateTime(2026, 8, 20, 14, 30, 0, DateTimeKind.Utc);
        var erwartet = new DateTime(2026, 8, 20, 14, 0, 0, DateTimeKind.Utc);

        Assert.Equal(erwartet, BarNormalizer.Snap(halbeStunde, BarInterval.Hourly));
    }

    [Fact]
    public void Snap_AktieUndKrypto_LandenAufDemselbenTag()
    {
        var aktie = new DateTime(2026, 8, 20, 13, 30, 0, DateTimeKind.Utc);
        var krypto = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            BarNormalizer.Snap(aktie, BarInterval.Daily),
            BarNormalizer.Snap(krypto, BarInterval.Daily));
    }

    [Fact]
    public void Snap_AktieUndKrypto_LandenAufDerselbenStunde()
    {
        var aktie = new DateTime(2026, 8, 20, 14, 30, 0, DateTimeKind.Utc);
        var krypto = new DateTime(2026, 8, 20, 14, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            BarNormalizer.Snap(aktie, BarInterval.Hourly),
            BarNormalizer.Snap(krypto, BarInterval.Hourly));
    }

    [Fact]
    public void Normalize_SortiertUndRastert()
    {
        var bars = new List<PriceBar>
        {
            new() { TsUtc = new DateTime(2026, 8, 21, 13, 30, 0, DateTimeKind.Utc), Close = 101m },
            new() { TsUtc = new DateTime(2026, 8, 20, 13, 30, 0, DateTimeKind.Utc), Close = 100m }
        };

        var result = BarNormalizer.Normalize(bars, BarInterval.Daily);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].TsUtc < result[1].TsUtc);
        Assert.Equal(new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc), result[0].TsUtc);
    }

    [Fact]
    public void Normalize_KollidierendeBars_WerdenZusammengefuehrt()
    {
        // Zwei Stundenbars derselben Stunde: High/Low müssen sich erweitern,
        // Close von der späteren stammen, Volumen sich addieren.
        var stunde = new DateTime(2026, 8, 20, 14, 0, 0, DateTimeKind.Utc);

        var bars = new List<PriceBar>
        {
            new() { TsUtc = stunde.AddMinutes(10), Open = 10m, High = 12m, Low = 9m, Close = 11m, Volume = 100m },
            new() { TsUtc = stunde.AddMinutes(40), Open = 11m, High = 15m, Low = 8m, Close = 14m, Volume = 250m }
        };

        var result = BarNormalizer.Normalize(bars, BarInterval.Hourly);

        Assert.Single(result);
        var b = result[0];
        Assert.Equal(stunde, b.TsUtc);
        Assert.Equal(15m, b.High);
        Assert.Equal(8m, b.Low);
        Assert.Equal(14m, b.Close);
        Assert.Equal(350m, b.Volume);
    }
}
