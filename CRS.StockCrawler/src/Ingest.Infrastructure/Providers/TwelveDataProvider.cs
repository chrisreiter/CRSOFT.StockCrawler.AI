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
/// Twelve Data. Ein <c>/time_series</c>-Endpoint bedient Aktien, ETFs und
/// Krypto mit identischem Schema und unterstützt 1h — inhaltlich der beste
/// Fit, braucht aber einen API-Key. Ist keiner gesetzt, meldet der Provider
/// sich über <see cref="IsConfigured"/> ab und Yahoo übernimmt.
/// </summary>
public sealed class TwelveDataProvider : IMarketDataProvider
{
    private readonly HttpClient _http;
    private readonly TwelveDataOptions _opt;
    private readonly ILogger<TwelveDataProvider> _log;

    private static readonly SemaphoreSlim Throttle = new(1, 1);
    private static DateTime _lastCall = DateTime.MinValue;

    public TwelveDataProvider(HttpClient http, IOptions<TwelveDataOptions> opt,
                              ILogger<TwelveDataProvider> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public ProviderId Id => ProviderId.TwelveData;

    public IReadOnlyCollection<AssetClass> SupportedClasses =>
        [AssetClass.Stock, AssetClass.Etf, AssetClass.Crypto, AssetClass.Index];

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_opt.ApiKey) && !_opt.ApiKey.StartsWith("__", StringComparison.Ordinal);

    public bool SupportsInterval(string intervalCode)
        => intervalCode is BarInterval.Hourly or BarInterval.Daily;

    public async Task<IReadOnlyList<PriceBar>> GetBarsAsync(
        string providerSymbol, AssetClass assetClass, string intervalCode,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];

        var iv = intervalCode == BarInterval.Hourly ? "1h" : "1day";
        var all = new List<PriceBar>();

        // Pro Aufruf gibt es maximal outputsize Bars. Reicht das nicht für den
        // Zeitraum, wird rückwärts in Blöcken geblättert.
        var cursorTo = toUtc;
        for (var page = 0; page < 50; page++)
        {
            var url = $"/time_series?symbol={Uri.EscapeDataString(providerSymbol)}"
                    + $"&interval={iv}"
                    + $"&start_date={fromUtc:yyyy-MM-dd HH:mm:ss}"
                    + $"&end_date={cursorTo:yyyy-MM-dd HH:mm:ss}"
                    + $"&outputsize={_opt.MaxOutputSize}"
                    + "&timezone=UTC&order=ASC"
                    + $"&apikey={Uri.EscapeDataString(_opt.ApiKey!)}";

            var json = await GetJsonAsync(url, ct);
            if (json is null) break;

            var batch = Parse(json.Value, providerSymbol);
            if (batch.Count == 0) break;

            all.AddRange(batch);

            // Weniger als eine volle Seite bedeutet: Zeitraum vollständig abgedeckt.
            if (batch.Count < _opt.MaxOutputSize) break;

            var earliest = batch[0].TsUtc;
            if (earliest <= fromUtc) break;
            cursorTo = earliest.AddSeconds(-1);
        }

        return all
            .GroupBy(b => b.TsUtc)
            .Select(g => g.First())
            .OrderBy(b => b.TsUtc)
            .ToList();
    }

    public async Task<Asset?> ResolveAsync(
        string providerSymbol, AssetClass assetClass, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        var url = $"/time_series?symbol={Uri.EscapeDataString(providerSymbol)}"
                + $"&interval=1day&outputsize=1&timezone=UTC"
                + $"&apikey={Uri.EscapeDataString(_opt.ApiKey!)}";

        var json = await GetJsonAsync(url, ct);
        if (json is null || !json.Value.TryGetProperty("meta", out var meta)) return null;

        return new Asset
        {
            AssetClass = assetClass,
            Symbol = providerSymbol,
            ProviderSymbol = providerSymbol,
            Provider = ProviderId.TwelveData,
            Currency = Str(meta, "currency"),
            Exchange = Str(meta, "exchange")
        };
    }

    // ------------------------------------------------------------------ Parsing

    private List<PriceBar> Parse(JsonElement root, string symbol)
    {
        var bars = new List<PriceBar>();

        // Fehler kommen mit HTTP 200 und status=error zurück.
        if (root.TryGetProperty("status", out var st)
            && st.ValueKind == JsonValueKind.String
            && st.GetString() == "error")
        {
            _log.LogWarning("TwelveData {Symbol}: {Message}", symbol, Str(root, "message"));
            return bars;
        }

        if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            return bars;

        foreach (var v in values.EnumerateArray())
        {
            if (!TryDate(Str(v, "datetime"), out var ts)) continue;

            var close = Num(v, "close");
            if (close is null || close <= 0) continue;

            var high = Num(v, "high");
            var low = Num(v, "low");
            if (high is not null && low is not null && high < low) continue;

            bars.Add(new PriceBar
            {
                TsUtc = ts,
                Open = Num(v, "open"),
                High = high,
                Low = low,
                Close = close.Value,
                Volume = Num(v, "volume")
            });
        }

        bars.Sort((a, b) => a.TsUtc.CompareTo(b.TsUtc));
        return bars;
    }

    private static bool TryDate(string? s, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        // Tagesbars kommen als "2026-08-20", Intraday als "2026-08-20 14:00:00".
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    }

    private static decimal? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;

        // Twelve Data liefert Zahlen als Strings.
        var raw = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return null;
        return d;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        await Throttle.WaitAsync(ct);
        try
        {
            var wait = TimeSpan.FromMilliseconds(_opt.DelayMs) - (DateTime.UtcNow - _lastCall);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastCall = DateTime.UtcNow;
        }
        finally
        {
            Throttle.Release();
        }

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("TwelveData antwortete {Status}", (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "TwelveData-Abruf fehlgeschlagen");
            return null;
        }
    }
}
