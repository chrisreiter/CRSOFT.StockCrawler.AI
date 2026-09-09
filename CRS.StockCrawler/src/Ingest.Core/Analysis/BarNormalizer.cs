using Ingest.Core.Enums;
using Ingest.Core.Models;

namespace Ingest.Core.Analysis;

/// <summary>
/// Rastert Bar-Zeitstempel auf ein einheitliches Gitter.
///
/// Ohne diesen Schritt scheitert jeder klassenübergreifende Vergleich: Yahoo
/// stempelt Tagesbars von US-Aktien auf den Handelsbeginn (13:30 UTC), Krypto
/// dagegen auf Mitternacht. Stundenbars laufen bei Aktien auf :30, bei Krypto
/// auf :00. Die Schnittmenge zweier solcher Reihen ist leer, obwohl beide
/// denselben Tag beziehungsweise dieselbe Stunde meinen.
///
/// Deshalb wird beim Ingest abgerundet: Tagesbars auf 00:00 des Handelstages,
/// Stundenbars auf den Beginn der Stunde.
/// </summary>
public static class BarNormalizer
{
    public static DateTime Snap(DateTime tsUtc, string intervalCode) => intervalCode switch
    {
        BarInterval.Daily => new DateTime(tsUtc.Year, tsUtc.Month, tsUtc.Day, 0, 0, 0, DateTimeKind.Utc),
        BarInterval.Hourly => new DateTime(tsUtc.Year, tsUtc.Month, tsUtc.Day, tsUtc.Hour, 0, 0, DateTimeKind.Utc),
        _ => DateTime.SpecifyKind(tsUtc, DateTimeKind.Utc)
    };

    /// <summary>
    /// Rastert eine ganze Reihe. Fallen dabei zwei Bars auf denselben
    /// Zeitpunkt, gewinnt die spätere — sie ist die aktuellere Fassung.
    /// </summary>
    public static List<PriceBar> Normalize(IReadOnlyList<PriceBar> bars, string intervalCode)
    {
        var byTs = new Dictionary<DateTime, PriceBar>(bars.Count);

        foreach (var b in bars)
        {
            var snapped = Snap(b.TsUtc, intervalCode);

            if (byTs.TryGetValue(snapped, out var existing))
            {
                // Kollision: Werte zusammenführen statt eine Bar wegzuwerfen.
                existing.High = Max(existing.High, b.High);
                existing.Low = Min(existing.Low, b.Low);
                existing.Close = b.Close;
                existing.AdjClose = b.AdjClose ?? existing.AdjClose;
                existing.Volume = (existing.Volume ?? 0) + (b.Volume ?? 0);
                continue;
            }

            byTs[snapped] = new PriceBar
            {
                TsUtc = snapped,
                Open = b.Open,
                High = b.High,
                Low = b.Low,
                Close = b.Close,
                AdjClose = b.AdjClose,
                Volume = b.Volume
            };
        }

        return byTs.Values.OrderBy(b => b.TsUtc).ToList();
    }

    private static decimal? Max(decimal? a, decimal? b)
        => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static decimal? Min(decimal? a, decimal? b)
        => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}
