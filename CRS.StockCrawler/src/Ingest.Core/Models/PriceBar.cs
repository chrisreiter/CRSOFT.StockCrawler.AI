namespace Ingest.Core.Models;

/// <summary>
/// Eine OHLCV-Bar. Gleiche Form für Aktie, ETF und Krypto, für 1h und 1d.
/// <see cref="TsUtc"/> ist immer der Bar-BEGINN in UTC.
/// </summary>
public sealed class PriceBar
{
    public DateTime TsUtc { get; set; }
    public decimal? Open { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public decimal Close { get; set; }
    public decimal? AdjClose { get; set; }
    public decimal? Volume { get; set; }
}
