using System.Globalization;
using System.Text.Json;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Ingest.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ingest.Infrastructure.Providers;

/// <summary>
/// CoinGecko liefert als einzige der evaluierten Quellen eine belastbare
/// Rangliste nach Marktkapitalisierung — genau das, was "die größten 100
/// Kryptowährungen" braucht. Kursdaten holen wir trotzdem woanders, weil
/// CoinGecko im Free-Tier keine sauberen Stundenbars über lange Zeiträume gibt.
/// </summary>
public sealed class CoinGeckoUniverseProvider : IUniverseProvider
{
    private readonly HttpClient _http;
    private readonly CoinGeckoOptions _opt;
    private readonly ILogger<CoinGeckoUniverseProvider> _log;

    /// <summary>
    /// Coins, deren Yahoo-Ticker vom Schema "SYMBOL-USD" abweicht oder dort
    /// mit einem anderen Papier kollidiert.
    /// </summary>
    private static readonly Dictionary<string, string?> YahooOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MIOTA"] = "IOTA-USD",
        ["LUNA"] = "LUNA1-USD",
        ["UNI"] = "UNI7083-USD",
        ["GRT"] = "GRT6719-USD",
        ["FTT"] = "FTT-USD",
        // Stablecoins: konstant bei 1 USD, für Korrelationsanalysen wertlos.
        ["USDT"] = null,
        ["USDC"] = null,
        ["BUSD"] = null,
        ["DAI"] = null,
        ["TUSD"] = null,
        ["USDE"] = null,
        ["FDUSD"] = null,
        ["USDS"] = null,
        ["PYUSD"] = null
    };

    public CoinGeckoUniverseProvider(HttpClient http, IOptions<CoinGeckoOptions> opt,
                                     ILogger<CoinGeckoUniverseProvider> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public ProviderId Id => ProviderId.CoinGecko;

    /// <summary>Der öffentliche Endpoint funktioniert ohne Key.</summary>
    public bool IsConfigured => true;

    public async Task<IReadOnlyList<Asset>> GetTopByMarketCapAsync(
        AssetClass assetClass, int limit, CancellationToken ct = default)
    {
        if (assetClass != AssetClass.Crypto) return [];

        var result = new List<Asset>();
        const int perPage = 250;
        var pages = (int)Math.Ceiling(limit / (double)perPage);

        for (var page = 1; page <= pages; page++)
        {
            var url = $"{_opt.BaseUrl}/coins/markets"
                    + $"?vs_currency={Uri.EscapeDataString(_opt.VsCurrency)}"
                    + $"&order=market_cap_desc&per_page={perPage}&page={page}&sparkline=false";

            var json = await GetJsonAsync(url, ct);
            if (json is null) break;
            if (json.Value.ValueKind != JsonValueKind.Array) break;

            var countBefore = result.Count;

            foreach (var c in json.Value.EnumerateArray())
            {
                var sym = Str(c, "symbol");
                if (string.IsNullOrWhiteSpace(sym)) continue;

                var upper = sym.ToUpperInvariant();

                // Nicht im Wörterbuch => Standardschema SYMBOL-USD.
                // Im Wörterbuch mit null => bewusst ausgeschlossen.
                string? yahoo;
                if (YahooOverrides.TryGetValue(upper, out var mapped))
                {
                    if (mapped is null) continue;
                    yahoo = mapped;
                }
                else
                {
                    yahoo = $"{upper}-USD";
                }

                result.Add(new Asset
                {
                    AssetClass = AssetClass.Crypto,
                    Symbol = yahoo,
                    ProviderSymbol = yahoo,
                    Provider = ProviderId.Yahoo,   // Kurse kommen von Yahoo
                    Name = Str(c, "name"),
                    Currency = _opt.VsCurrency.ToUpperInvariant(),
                    MarketCap = Dec(c, "market_cap"),
                    MarketCapRank = Int(c, "market_cap_rank"),

                    // Unabhängige Gegenprobe für die Symbolzuordnung.
                    ReferencePrice = Dec(c, "current_price")
                });

                if (result.Count >= limit) break;
            }

            if (result.Count >= limit || result.Count == countBefore) break;
        }

        _log.LogInformation("CoinGecko: {Count} Kryptowerte nach Marktkapitalisierung", result.Count);
        return result;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Dec(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        if (!v.TryGetDecimal(out var d)) return null;
        return d;
    }

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        // CoinGecko drosselt anonyme Clients hart; lieber langsam als gesperrt.
        await Task.Delay(_opt.DelayMs, ct);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(_opt.ApiKey) && !_opt.ApiKey.StartsWith("__", StringComparison.Ordinal))
                req.Headers.TryAddWithoutValidation("x-cg-demo-api-key", _opt.ApiKey);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("CoinGecko antwortete {Status}", (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "CoinGecko-Abruf fehlgeschlagen");
            return null;
        }
    }
}
