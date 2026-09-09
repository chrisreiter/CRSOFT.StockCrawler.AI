using System.Text.Json;
using Ingest.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ingest.Infrastructure.Providers;

/// <summary>Branche und Land eines Symbols.</summary>
public sealed record SectorInfo(string Symbol, string Sector, string? Country, string? Exchange);

/// <summary>
/// Ordnet Aktien einer Branche zu.
///
/// Yahoos Kursdatensatz enthält kein Branchenfeld — wohl aber elf eigene
/// Screener, je einer pro Branche. Die Zuordnung entsteht deshalb, indem diese
/// Listen durchlaufen und die enthaltenen Symbole markiert werden.
///
/// Auflösung ist die Branchenebene. Feineres wie „Rüstung" oder „Halbleiter"
/// gibt es dort nicht: Rüstungswerte erscheinen unter Industrials, Chiphersteller
/// unter Technology. Für eine feinere Einteilung bräuchte es eine Quelle mit
/// Schlüssel, etwa die Stammdaten von Twelve Data.
/// </summary>
public sealed class YahooSectorProvider
{
    private readonly HttpClient _http;
    private readonly YahooOptions _opt;
    private readonly ILogger<YahooSectorProvider> _log;

    private const int PageSize = 250;

    /// <summary>Screener-Kennung und die zugehörige deutsche Bezeichnung.</summary>
    public static readonly (string ScrId, string Name)[] Sectors =
    [
        ("ms_technology", "Technologie"),
        ("ms_healthcare", "Gesundheit"),
        ("ms_financial_services", "Finanzen"),
        ("ms_consumer_cyclical", "Konsum zyklisch"),
        ("ms_consumer_defensive", "Konsum defensiv"),
        ("ms_industrials", "Industrie"),
        ("ms_energy", "Energie"),
        ("ms_utilities", "Versorger"),
        ("ms_real_estate", "Immobilien"),
        ("ms_basic_materials", "Rohstoffe"),
        ("ms_communication_services", "Kommunikation")
    ];

    public YahooSectorProvider(HttpClient http, IOptions<YahooOptions> opt,
                               ILogger<YahooSectorProvider> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    /// <summary>
    /// Läuft alle Branchen-Screener durch. Erscheint ein Symbol in mehreren
    /// Listen, gewinnt die erste — die Reihenfolge oben ist danach gewählt,
    /// welche Zuordnung am aussagekräftigsten ist.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SectorInfo>> GetSectorsAsync(
        int perSector = 500, CancellationToken ct = default)
    {
        var result = new Dictionary<string, SectorInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (scrId, name) in Sectors)
        {
            ct.ThrowIfCancellationRequested();

            var found = 0;

            for (var start = 0; start < perSector; start += PageSize)
            {
                var count = Math.Min(PageSize, perSector - start);
                var url = $"/v1/finance/screener/predefined/saved?scrIds={Uri.EscapeDataString(scrId)}"
                        + $"&count={count}&start={start}&sortField=intradaymarketcap&sortType=DESC";

                var json = await GetJsonAsync(url, ct);
                if (json is null) break;

                if (!json.Value.TryGetProperty("finance", out var fin)) break;
                if (fin.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null) break;
                if (!fin.TryGetProperty("result", out var res)
                    || res.ValueKind != JsonValueKind.Array || res.GetArrayLength() == 0) break;

                if (!res[0].TryGetProperty("quotes", out var quotes)
                    || quotes.ValueKind != JsonValueKind.Array) break;

                var n = quotes.GetArrayLength();
                if (n == 0) break;

                foreach (var q in quotes.EnumerateArray())
                {
                    var symbol = Str(q, "symbol");
                    if (string.IsNullOrWhiteSpace(symbol)) continue;

                    // Erste Zuordnung gewinnt.
                    if (result.ContainsKey(symbol)) continue;

                    result[symbol] = new SectorInfo(
                        symbol, name, Str(q, "region"),
                        Str(q, "fullExchangeName") ?? Str(q, "exchange"));

                    found++;
                }

                if (n < count) break;
            }

            _log.LogInformation("Branche {Name}: {Count} Symbole", name, found);
        }

        return result;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        await Task.Delay(_opt.DelayMs, ct);

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Branchen-Screener antwortete {Status}", (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Branchen-Abruf fehlgeschlagen");
            return null;
        }
    }
}
