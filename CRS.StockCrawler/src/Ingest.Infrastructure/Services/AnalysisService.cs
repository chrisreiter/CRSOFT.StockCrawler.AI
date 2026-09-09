using System.Diagnostics;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Untersucht die Wechselwirkungen zwischen den verfolgten Kursen: gleichzeitige
/// Korrelation, Vorlauf-Beziehungen und Kreuzungen der normalisierten Kurven.
///
/// Die eigentliche Rechnung steckt in <see cref="PairAnalyzer"/> — dieselbe
/// Komponente nutzt der Walk-Forward, der die Struktur wiederholt aus einem
/// wachsenden Zeitfenster neu bestimmt.
/// </summary>
public sealed class AnalysisService : IAnalysisService
{
    private readonly IAssetRepository _assets;
    private readonly IPriceBarRepository _bars;
    private readonly IPairStatRepository _pairs;
    private readonly IIngestRunRepository _runs;
    private readonly ILogger<AnalysisService> _log;

    private static readonly PairAnalyzerOptions Options = new();

    public AnalysisService(
        IAssetRepository assets,
        IPriceBarRepository bars,
        IPairStatRepository pairs,
        IIngestRunRepository runs,
        ILogger<AnalysisService> log)
    {
        _assets = assets;
        _bars = bars;
        _pairs = pairs;
        _runs = runs;
        _log = log;
    }

    public async Task<AnalysisResult> RecomputeAsync(
        string intervalCode, int windowBars, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var runId = await _runs.StartAsync($"analysis:{intervalCode}:{windowBars}", null, ct);

        try
        {
            var tracked = await _assets.GetTrackedAsync(ct);
            if (tracked.Count < 2)
            {
                await _runs.FinishAsync(runId, 0, 0, 0, "Zu wenige verfolgte Werte", ct);
                return new AnalysisResult(0, 0, tracked.Count, sw.Elapsed);
            }

            var to = DateTime.UtcNow;
            var from = to - BarInterval.Duration(intervalCode) * windowBars;

            var series = await _bars.GetManyAsync(
                tracked.Select(a => a.AssetId).ToArray(), intervalCode, from, to, ct);

            var usable = series
                .Where(kv => kv.Value.Count >= Options.MinObservations)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            if (usable.Count < 2)
            {
                await _runs.FinishAsync(runId, 0, 0, 0,
                    $"Nur {usable.Count} Reihen mit mindestens {Options.MinObservations} Bars", ct);
                return new AnalysisResult(0, 0, usable.Count, sw.Elapsed);
            }

            var (grid, dense) = PairAnalyzer.Densify(usable);
            var assetIds = usable.Keys.OrderBy(i => i).ToArray();

            var result = PairAnalyzer.Analyze(
                assetIds, dense, grid, 0, grid.Length - 1, intervalCode, windowBars, Options);

            _log.LogInformation(
                "Analyse {Interval}: {Pairs} Paare, {Cross} Kreuzungen berechnet, schreibe …",
                intervalCode, result.Stats.Count, result.Crossings.Count);

            await _pairs.UpsertAsync(result.Stats, ct);
            await _pairs.UpsertCrossingsAsync(result.Crossings, ct);

            await _runs.FinishAsync(runId, result.Stats.Count, 0, result.Crossings.Count,
                $"{usable.Count} Werte, {grid.Length} Zeitpunkte", ct);

            _log.LogInformation("Analyse {Interval} fertig in {Elapsed:n1}s",
                intervalCode, sw.Elapsed.TotalSeconds);

            return new AnalysisResult(
                result.Stats.Count, result.Crossings.Count, usable.Count, sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _runs.FinishAsync(runId, 0, 1, 0, ex.Message, ct);
            throw;
        }
    }
}
