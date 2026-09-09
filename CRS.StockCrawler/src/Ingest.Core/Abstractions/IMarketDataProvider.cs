using Ingest.Core.Enums;
using Ingest.Core.Models;

namespace Ingest.Core.Abstractions;

/// <summary>
/// Einheitlicher Zugriff auf einen Kursdaten-Anbieter. Jede Implementierung
/// übersetzt ihr hauseigenes Format in <see cref="PriceBar"/>, damit oberhalb
/// dieser Schicht kein Provider mehr sichtbar ist.
/// </summary>
public interface IMarketDataProvider
{
    ProviderId Id { get; }

    /// <summary>Anlageklassen, die dieser Provider bedienen kann.</summary>
    IReadOnlyCollection<AssetClass> SupportedClasses { get; }

    /// <summary>Ob der Provider einsatzbereit ist (z. B. API-Key vorhanden).</summary>
    bool IsConfigured { get; }

    /// <summary>Unterstützt der Provider dieses Intervall?</summary>
    bool SupportsInterval(string intervalCode);

    /// <summary>
    /// OHLCV-Bars für einen Zeitraum. Rückgabe ist aufsteigend nach Zeit
    /// sortiert und enthält nur validierte Bars.
    /// </summary>
    Task<IReadOnlyList<PriceBar>> GetBarsAsync(
        string providerSymbol,
        AssetClass assetClass,
        string intervalCode,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Stammdaten zu einem Symbol (Name, Währung, Börse). Null, wenn unbekannt.
    /// </summary>
    Task<Asset?> ResolveAsync(
        string providerSymbol,
        AssetClass assetClass,
        CancellationToken ct = default);
}

/// <summary>
/// Provider, der zusätzlich ein nach Marktkapitalisierung sortiertes
/// Universum liefern kann – Basis für "die größten 100".
/// </summary>
public interface IUniverseProvider
{
    ProviderId Id { get; }
    bool IsConfigured { get; }

    /// <summary>Top-N Assets einer Klasse, absteigend nach Marktkapitalisierung.</summary>
    Task<IReadOnlyList<Asset>> GetTopByMarketCapAsync(
        AssetClass assetClass,
        int limit,
        CancellationToken ct = default);
}
