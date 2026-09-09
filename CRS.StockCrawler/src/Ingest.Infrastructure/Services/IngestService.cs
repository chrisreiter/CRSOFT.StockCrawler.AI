using System.Diagnostics;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Holt Kursdaten über die Provider-Abstraktion und schreibt sie in
/// <c>price_bar</c>. Welcher Provider zum Zug kommt, entscheidet sich pro
/// Asset: der bevorzugte, sofern er konfiguriert ist und Klasse und Intervall
/// unterstützt, sonst der erste passende.
/// </summary>
public sealed class IngestService : IIngestService
{
    private readonly IEnumerable<IMarketDataProvider> _providers;
    private readonly IAssetRepository _assets;
    private readonly IPriceBarRepository _bars;
    private readonly IIngestRunRepository _runs;
    private readonly ILogger<IngestService> _log;

    public IngestService(
        IEnumerable<IMarketDataProvider> providers,
        IAssetRepository assets,
        IPriceBarRepository bars,
        IIngestRunRepository runs,
        ILogger<IngestService> log)
    {
        _providers = providers;
        _assets = assets;
        _bars = bars;
        _runs = runs;
        _log = log;
    }

    public async Task<IngestResult> BackfillAsync(int months, string intervalCode, CancellationToken ct = default)
    {
        var to = DateTime.UtcNow;
        var from = to.AddMonths(-Math.Max(1, months));
        return await RunAsync($"backfill:{intervalCode}:{months}m", intervalCode,
            _ => from, to, ct);
    }

    public async Task<IngestResult> UpdateIncrementalAsync(string intervalCode, CancellationToken ct = default)
    {
        var to = DateTime.UtcNow;

        return await RunAsync($"update:{intervalCode}", intervalCode, last =>
        {
            // Ohne Vorlauf würde die letzte, eventuell noch unfertige Bar nie
            // korrigiert. Deshalb bewusst überlappend nachladen.
            if (last is null)
                return to.AddMonths(intervalCode == BarInterval.Hourly ? -2 : -24);

            var overlap = intervalCode == BarInterval.Hourly
                ? TimeSpan.FromHours(6)
                : TimeSpan.FromDays(5);

            return last.Value - overlap;
        }, to, ct);
    }

    private async Task<IngestResult> RunAsync(
        string jobName, string intervalCode, Func<DateTime?, DateTime> fromSelector,
        DateTime to, CancellationToken ct)
    {
        if (!BarInterval.IsValid(intervalCode))
            throw new ArgumentException($"Unbekanntes Intervall: {intervalCode}", nameof(intervalCode));

        var sw = Stopwatch.StartNew();
        var runId = await _runs.StartAsync(jobName, null, ct);

        var tracked = await _assets.GetTrackedAsync(ct);
        int ok = 0, failed = 0, rows = 0;
        var errors = new List<string>();

        foreach (var asset in tracked)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var last = await _bars.GetLastTsAsync(asset.AssetId, intervalCode, ct);
                var from = fromSelector(last);

                if (from >= to)
                {
                    ok++;
                    continue;
                }

                var written = await IngestOneAsync(asset.AssetId, intervalCode, from, to, ct);
                rows += written;
                ok++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                errors.Add($"{asset.Symbol}: {ex.Message}");
                _log.LogWarning(ex, "Ingest für {Symbol} fehlgeschlagen", asset.Symbol);
            }
        }

        var note = errors.Count > 0 ? string.Join("; ", errors.Take(30)) : null;
        await _runs.FinishAsync(runId, ok, failed, rows, note, ct);

        _log.LogInformation(
            "{Job}: {Ok} ok, {Failed} Fehler, {Rows} Zeilen in {Elapsed:n1}s",
            jobName, ok, failed, rows, sw.Elapsed.TotalSeconds);

        return new IngestResult(tracked.Count, ok, failed, rows, sw.Elapsed);
    }

    public async Task<int> IngestOneAsync(
        int assetId, string intervalCode, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var asset = await _assets.GetAsync(assetId, ct)
                    ?? throw new InvalidOperationException($"Asset {assetId} existiert nicht.");

        var provider = SelectProvider(asset, intervalCode)
                       ?? throw new InvalidOperationException(
                           $"Kein Provider für {asset.Symbol} ({asset.AssetClass}, {intervalCode}).");

        var bars = await provider.GetBarsAsync(
            asset.ProviderSymbol, asset.AssetClass, intervalCode, fromUtc, toUtc, ct);

        if (bars.Count == 0)
        {
            _log.LogDebug("{Symbol}: keine Bars für {Interval} von {From:d} bis {To:d}",
                asset.Symbol, intervalCode, fromUtc, toUtc);
            return 0;
        }

        // Zeitstempel auf ein gemeinsames Raster ziehen. Erst dadurch liegen
        // Aktien und Krypto auf derselben Zeitachse und lassen sich vergleichen.
        var normalized = BarNormalizer.Normalize(bars, intervalCode);

        // Unmögliche Sprünge aussortieren, bevor sie Analyse und Prognose vergiften.
        var (clean, quality) = BarQualityFilter.Clean(normalized, intervalCode);

        if (quality.Rejected > 0)
        {
            var level = quality.RejectRatio >= BarQualityFilter.SuspiciousRatio
                ? LogLevel.Warning
                : LogLevel.Debug;

            _log.Log(level,
                "{Symbol} ({Interval}): {Rejected} von {Total} Bars verworfen " +
                "(schlimmster Sprung Faktor {Worst:N1}). " +
                "Ein hoher Anteil deutet auf eine falsche Symbolzuordnung hin.",
                asset.Symbol, intervalCode, quality.Rejected,
                quality.Rejected + quality.Kept, quality.WorstFactor);
        }

        if (clean.Count == 0) return 0;

        return await _bars.UpsertAsync(assetId, intervalCode, provider.Id, clean, ct);
    }

    /// <summary>
    /// Bevorzugt den am Asset hinterlegten Provider. Ist der nicht einsatzbereit
    /// (etwa fehlender API-Key), wird der erste passende genommen — so bleibt das
    /// System auch ohne Schlüssel arbeitsfähig.
    /// </summary>
    private IMarketDataProvider? SelectProvider(Asset asset, string intervalCode)
    {
        var candidates = _providers
            .Where(p => p.IsConfigured
                        && p.SupportedClasses.Contains(asset.AssetClass)
                        && p.SupportsInterval(intervalCode))
            .ToList();

        return candidates.FirstOrDefault(p => p.Id == asset.Provider) ?? candidates.FirstOrDefault();
    }
}
