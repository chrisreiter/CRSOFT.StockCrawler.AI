using System.Text.Json;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Ingest.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ingest.Infrastructure.Providers;

/// <summary>
/// Ermittelt das Anlageuniversum über Yahoos Screener — keylos und mit echten
/// Kennzahlen, statt einer gepflegten Symbolliste, die sofort veraltet.
///
/// Aktien werden nach Marktkapitalisierung sortiert, Fonds/ETFs nach
/// Fondsvermögen. Da ein einzelner Screener nur einen Ausschnitt liefert,
/// werden für Aktien mehrere zusammengeführt und anschließend gemeinsam
/// gerankt.
/// </summary>
public sealed class YahooScreenerUniverseProvider : IUniverseProvider
{
    private readonly HttpClient _http;
    private readonly YahooOptions _opt;
    private readonly ILogger<YahooScreenerUniverseProvider> _log;

    private const int PageSize = 250;

    /// <summary>
    /// Aktien-Screener, die zusammen die großen US-Werte abdecken. Überschneidungen
    /// sind gewollt und werden beim Zusammenführen entfernt.
    /// </summary>
    private static readonly string[] EquityScreeners =
    [
        "most_actives",
        "undervalued_large_caps",
        "growth_technology_stocks",
        "day_gainers",
        "day_losers",
        "aggressive_small_caps"
    ];

    public YahooScreenerUniverseProvider(HttpClient http, IOptions<YahooOptions> opt,
                                          ILogger<YahooScreenerUniverseProvider> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public ProviderId Id => ProviderId.Yahoo;
    public bool IsConfigured => true;

    public async Task<IReadOnlyList<Asset>> GetTopByMarketCapAsync(
        AssetClass assetClass, int limit, CancellationToken ct = default)
    {
        return assetClass switch
        {
            AssetClass.Stock => await GetStocksAsync(limit, ct),
            AssetClass.Etf => await GetEtfsAsync(limit, ct),
            _ => []
        };
    }

    private async Task<IReadOnlyList<Asset>> GetStocksAsync(int limit, CancellationToken ct)
    {
        var merged = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);

        foreach (var scr in EquityScreeners)
        {
            var quotes = await FetchAllAsync(scr, "intradaymarketcap", limit * 3, ct);

            foreach (var q in quotes)
            {
                var asset = MapEquity(q);
                if (asset is null) continue;

                // Bei Dubletten die Variante mit Kennzahl behalten.
                if (merged.TryGetValue(asset.Symbol, out var existing)
                    && existing.MarketCap >= asset.MarketCap) continue;

                merged[asset.Symbol] = asset;
            }
        }

        var ranked = merged.Values
            .Where(a => a.MarketCap is > 0)
            .OrderByDescending(a => a.MarketCap)
            .Take(limit)
            .ToList();

        for (var i = 0; i < ranked.Count; i++) ranked[i].MarketCapRank = i + 1;

        _log.LogInformation("Yahoo-Screener: {Count} Aktien aus {Pool} Kandidaten gerankt",
            ranked.Count, merged.Count);

        return ranked;
    }

    private async Task<IReadOnlyList<Asset>> GetEtfsAsync(int limit, CancellationToken ct)
    {
        var quotes = await FetchAllAsync("top_etfs_us", "fundnetassets", limit * 2, ct);

        var ranked = quotes
            .Select(MapFund)
            .Where(a => a is { MarketCap: > 0 })
            .Select(a => a!)
            .GroupBy(a => a.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(a => a.MarketCap)
            .Take(limit)
            .ToList();

        for (var i = 0; i < ranked.Count; i++) ranked[i].MarketCapRank = i + 1;

        _log.LogInformation("Yahoo-Screener: {Count} Fonds/ETFs nach Fondsvermögen", ranked.Count);
        return ranked;
    }

    // -------------------------------------------------------------------- Mapping

    private static Asset? MapEquity(JsonElement q)
    {
        var symbol = Str(q, "symbol");
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        // Der Screener mischt gelegentlich andere Typen unter.
        if (Str(q, "quoteType") is { } t && !t.Equals("EQUITY", StringComparison.OrdinalIgnoreCase))
            return null;

        return new Asset
        {
            AssetClass = AssetClass.Stock,
            Symbol = symbol,
            ProviderSymbol = symbol,
            Provider = ProviderId.Yahoo,
            Name = Str(q, "longName") ?? Str(q, "shortName"),
            Currency = Str(q, "currency"),
            Exchange = Str(q, "fullExchangeName") ?? Str(q, "exchange"),
            MarketCap = Dec(q, "marketCap")
        };
    }

    private static Asset? MapFund(JsonElement q)
    {
        var symbol = Str(q, "symbol");
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        return new Asset
        {
            AssetClass = AssetClass.Etf,
            Symbol = symbol,
            ProviderSymbol = symbol,
            Provider = ProviderId.Yahoo,
            Name = Str(q, "longName") ?? Str(q, "shortName"),
            Currency = Str(q, "currency"),
            Exchange = Str(q, "fullExchangeName") ?? Str(q, "exchange"),

            // Für Fonds ist das Fondsvermögen die Größenkennzahl; wir legen es
            // in dieselbe Spalte, damit die Rangfolge klassenübergreifend gleich
            // funktioniert.
            MarketCap = Dec(q, "netAssets")
        };
    }

    // --------------------------------------------------------------------- HTTP

    private async Task<List<JsonElement>> FetchAllAsync(
        string scrId, string sortField, int want, CancellationToken ct)
    {
        var all = new List<JsonElement>();

        for (var start = 0; start < want; start += PageSize)
        {
            var count = Math.Min(PageSize, want - start);
            var url = $"/v1/finance/screener/predefined/saved?scrIds={Uri.EscapeDataString(scrId)}"
                    + $"&count={count}&start={start}"
                    + $"&sortField={Uri.EscapeDataString(sortField)}&sortType=DESC";

            var json = await GetJsonAsync(url, ct);
            if (json is null) break;

            if (!json.Value.TryGetProperty("finance", out var fin)) break;
            if (fin.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            {
                _log.LogWarning("Screener {Scr}: {Err}", scrId, err.ToString());
                break;
            }

            if (!fin.TryGetProperty("result", out var res)
                || res.ValueKind != JsonValueKind.Array
                || res.GetArrayLength() == 0) break;

            if (!res[0].TryGetProperty("quotes", out var quotes)
                || quotes.ValueKind != JsonValueKind.Array) break;

            var n = quotes.GetArrayLength();
            if (n == 0) break;

            foreach (var q in quotes.EnumerateArray()) all.Add(q.Clone());

            // Kürzere Seite als angefragt => Ende der Liste erreicht.
            if (n < count) break;
        }

        return all;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Dec(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;

        // Yahoo liefert Fondsvermögen als Fließkommazahl; über den Umweg double
        // bleibt der Wert auch bei Billionenbeträgen im decimal-Bereich.
        if (!v.TryGetDouble(out var d) || double.IsNaN(d) || double.IsInfinity(d)) return null;
        if (Math.Abs(d) > 1e28) return null;

        return (decimal)d;
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        await Task.Delay(_opt.DelayMs, ct);

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Yahoo-Screener antwortete {Status}", (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Yahoo-Screener-Abruf fehlgeschlagen");
            return null;
        }
    }
}
