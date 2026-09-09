using System.Text.Json;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Ingest.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ingest.Infrastructure.Providers;

/// <summary>
/// Yahoo Finance Chart-API. Deckt Aktien, ETFs und Krypto über denselben
/// Endpoint mit identischem Schema ab und braucht keinen API-Key — deshalb der
/// Standard-Provider. Die Schnittstelle ist inoffiziell, also wird durchweg
/// defensiv geparst: fehlende Felder führen zu übersprungenen Bars, nie zu
/// einer Exception.
/// </summary>
public sealed class YahooProvider : IMarketDataProvider
{
    private readonly HttpClient _http;
    private readonly YahooOptions _opt;
    private readonly ILogger<YahooProvider> _log;

    private static readonly SemaphoreSlim Throttle = new(1, 1);
    private static DateTime _lastCall = DateTime.MinValue;

    public YahooProvider(HttpClient http, IOptions<YahooOptions> opt, ILogger<YahooProvider> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public ProviderId Id => ProviderId.Yahoo;

    public IReadOnlyCollection<AssetClass> SupportedClasses =>
        [AssetClass.Stock, AssetClass.Etf, AssetClass.Crypto, AssetClass.Index];

    public bool IsConfigured => true;

    public bool SupportsInterval(string intervalCode)
        => intervalCode is BarInterval.Hourly or BarInterval.Daily;

    public async Task<IReadOnlyList<PriceBar>> GetBarsAsync(
        string providerSymbol, AssetClass assetClass, string intervalCode,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var iv = intervalCode == BarInterval.Hourly ? "1h" : "1d";

        // period1/period2 statt range: bei range=max liefert Yahoo stillschweigend
        // monatliche statt täglicher Bars zurück.
        var url = $"/v8/finance/chart/{Uri.EscapeDataString(providerSymbol)}"
                + $"?interval={iv}&period1={ToUnix(fromUtc)}&period2={ToUnix(toUtc)}";

        var json = await GetJsonAsync(url, ct);
        return json is null ? [] : ParseChart(json.Value, providerSymbol);
    }

    public async Task<Asset?> ResolveAsync(
        string providerSymbol, AssetClass assetClass, CancellationToken ct = default)
    {
        var url = $"/v8/finance/chart/{Uri.EscapeDataString(providerSymbol)}?interval=1d&range=5d";
        var json = await GetJsonAsync(url, ct);
        if (json is null || !TryResult(json.Value, out var result)) return null;
        if (!result.TryGetProperty("meta", out var meta)) return null;

        return new Asset
        {
            AssetClass = assetClass,
            Symbol = providerSymbol,
            ProviderSymbol = providerSymbol,
            Provider = ProviderId.Yahoo,
            Name = Str(meta, "longName") ?? Str(meta, "shortName"),
            Currency = Str(meta, "currency"),
            Exchange = Str(meta, "fullExchangeName") ?? Str(meta, "exchangeName")
        };
    }

    // ------------------------------------------------------------------ Parsing

    private List<PriceBar> ParseChart(JsonElement root, string symbol)
    {
        var bars = new List<PriceBar>();
        if (!TryResult(root, out var result)) return bars;

        if (!result.TryGetProperty("timestamp", out var tsArr) || tsArr.ValueKind != JsonValueKind.Array)
            return bars;

        if (!result.TryGetProperty("indicators", out var ind)
            || !ind.TryGetProperty("quote", out var quoteArr)
            || quoteArr.ValueKind != JsonValueKind.Array
            || quoteArr.GetArrayLength() == 0)
            return bars;

        var q = quoteArr[0];
        var open = Arr(q, "open");
        var high = Arr(q, "high");
        var low = Arr(q, "low");
        var close = Arr(q, "close");
        var vol = Arr(q, "volume");

        // adjclose hängt in einem eigenen Zweig und fehlt bei Krypto ganz.
        double?[]? adj = null;
        if (ind.TryGetProperty("adjclose", out var adjArr)
            && adjArr.ValueKind == JsonValueKind.Array
            && adjArr.GetArrayLength() > 0)
        {
            adj = Arr(adjArr[0], "adjclose");
        }

        var n = tsArr.GetArrayLength();
        var skipped = 0;

        for (var i = 0; i < n; i++)
        {
            var c = At(close, i);

            // Für noch laufende oder illiquide Bars liefert Yahoo null.
            if (c is null || c <= 0)
            {
                skipped++;
                continue;
            }

            var o = At(open, i);
            var h = At(high, i);
            var l = At(low, i);

            // Plausibilität: High darf nie unter Low liegen.
            if (h is not null && l is not null && h < l)
            {
                skipped++;
                continue;
            }

            // Kleinere Inkonsistenzen glattziehen statt die Bar zu verwerfen.
            if (h is not null && h < c) h = c;
            if (l is not null && l > c) l = c;

            bars.Add(new PriceBar
            {
                TsUtc = DateTimeOffset.FromUnixTimeSeconds(tsArr[i].GetInt64()).UtcDateTime,
                Open = Dec(o),
                High = Dec(h),
                Low = Dec(l),
                Close = Dec(c)!.Value,
                AdjClose = Dec(At(adj, i)),
                Volume = Dec(At(vol, i))
            });
        }

        if (skipped > 0)
            _log.LogDebug("Yahoo {Symbol}: {Skipped} von {Total} Bars verworfen", symbol, skipped, n);

        bars.Sort((a, b) => a.TsUtc.CompareTo(b.TsUtc));
        return bars;
    }

    private static bool TryResult(JsonElement root, out JsonElement result)
    {
        result = default;
        if (!root.TryGetProperty("chart", out var chart)) return false;
        if (chart.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null) return false;
        if (!chart.TryGetProperty("result", out var arr)
            || arr.ValueKind != JsonValueKind.Array
            || arr.GetArrayLength() == 0) return false;

        result = arr[0];
        return true;
    }

    private static double?[]? Arr(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return null;

        var res = new double?[a.GetArrayLength()];
        for (var i = 0; i < res.Length; i++)
            res[i] = a[i].ValueKind == JsonValueKind.Number ? a[i].GetDouble() : null;
        return res;
    }

    private static double? At(double?[]? a, int i) => a is not null && i < a.Length ? a[i] : null;

    private static decimal? Dec(double? v)
    {
        if (v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return null;

        // Jenseits dieser Grenze sprengt der Wert DECIMAL(19,8) und ist ohnehin Müll.
        if (Math.Abs(v.Value) > 1e11) return null;

        return (decimal)Math.Round(v.Value, 8);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long ToUnix(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        await WaitForSlotAsync(ct);

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Yahoo antwortete {Status} auf {Url}", (int)resp.StatusCode, url);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Yahoo-Abruf fehlgeschlagen: {Url}", url);
            return null;
        }
    }

    /// <summary>Serialisiert die Calls und hält den Mindestabstand ein.</summary>
    private async Task WaitForSlotAsync(CancellationToken ct)
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
    }
}
