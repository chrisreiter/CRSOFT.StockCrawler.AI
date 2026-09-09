using Ingest.Core.Enums;

namespace Ingest.Core.Models;

/// <summary>
/// Ein handelbares Instrument – Aktie, ETF/Fonds oder Kryptowährung.
/// Bewusst identisch für alle Klassen, damit Analysen keine Sonderfälle brauchen.
/// </summary>
public sealed class Asset
{
    public int AssetId { get; set; }
    public AssetClass AssetClass { get; set; }

    /// <summary>Kanonisches Kürzel, z. B. AAPL, SPY, BTC-USD.</summary>
    public string Symbol { get; set; } = "";

    public string? Name { get; set; }
    public string? Currency { get; set; }
    public string? Exchange { get; set; }

    public ProviderId Provider { get; set; }

    /// <summary>Symbol in der Schreibweise des Providers (kann abweichen).</summary>
    public string ProviderSymbol { get; set; } = "";

    public decimal? MarketCap { get; set; }
    public int? MarketCapRank { get; set; }

    /// <summary>
    /// Kurs, den der Universum-Provider zuletzt gemeldet hat. Dient als
    /// unabhängige Gegenprobe für die Symbolzuordnung: weicht der Kursdaten-
    /// Provider stark davon ab, zeigt das Symbol auf ein anderes Papier.
    /// </summary>
    /// <summary>Branche (nur Aktien). Elf Kategorien, siehe YahooSectorProvider.</summary>
    public string? Sector { get; set; }

    /// <summary>Länderkennung der Notierung, etwa US oder DE.</summary>
    public string? Country { get; set; }

    public decimal? ReferencePrice { get; set; }

    public DateTime? ReferencePriceUtc { get; set; }

    /// <summary>Nur getrackte Assets werden ingestiert und prognostiziert.</summary>
    public bool IsTracked { get; set; }

    public DateTime FirstSeenUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
