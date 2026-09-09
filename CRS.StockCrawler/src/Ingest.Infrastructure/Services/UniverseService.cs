using System.Diagnostics;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Baut die Gesamtliste der verfügbaren Instrumente auf und markiert die
/// größten N je Klasse als "getrackt". Alle entdeckten Werte bleiben in der
/// Tabelle stehen, damit im Backend nachträglich andere ausgewählt werden können.
/// </summary>
public sealed class UniverseService : IUniverseService
{
    private readonly IEnumerable<IUniverseProvider> _providers;
    private readonly IAssetRepository _assets;
    private readonly IIngestRunRepository _runs;
    private readonly IngestOptions _opt;
    private readonly ILogger<UniverseService> _log;

    public UniverseService(
        IEnumerable<IUniverseProvider> providers,
        IAssetRepository assets,
        IIngestRunRepository runs,
        IOptions<IngestOptions> opt,
        ILogger<UniverseService> log)
    {
        _providers = providers;
        _assets = assets;
        _runs = runs;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<UniverseResult> RefreshAsync(
        AssetClass cls, int topN, bool autoTrack, CancellationToken ct = default)
    {
        var runId = await _runs.StartAsync($"universe:{cls}", null, ct);
        var sw = Stopwatch.StartNew();
        var discovered = 0;
        var errors = new List<string>();

        try
        {
            foreach (var p in _providers.Where(p => p.IsConfigured))
            {
                IReadOnlyList<Core.Models.Asset> found;
                try
                {
                    found = await p.GetTopByMarketCapAsync(cls, topN, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Universum-Provider {Provider} fehlgeschlagen", p.Id);
                    errors.Add($"{p.Id}: {ex.Message}");
                    continue;
                }

                foreach (var a in found)
                {
                    try
                    {
                        await _assets.UpsertAsync(a, ct);
                        discovered++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        errors.Add($"{a.Symbol}: {ex.Message}");
                    }
                }

                // Der erste Provider, der für diese Klasse liefert, gewinnt.
                if (found.Count > 0) break;
            }

            var tracked = autoTrack ? await _assets.TrackTopByMarketCapAsync(cls, topN, ct) : 0;

            var note = errors.Count > 0 ? string.Join("; ", errors.Take(20)) : null;
            await _runs.FinishAsync(runId, discovered, errors.Count, 0, note, ct);

            _log.LogInformation(
                "Universum {Class}: {Discovered} entdeckt, {Tracked} neu getrackt ({Elapsed:n1}s)",
                cls, discovered, tracked, sw.Elapsed.TotalSeconds);

            return new UniverseResult(discovered, tracked,
                $"{cls}: {discovered} entdeckt, {tracked} neu aktiviert");
        }
        catch (Exception ex)
        {
            await _runs.FinishAsync(runId, discovered, 1, 0, ex.Message, ct);
            throw;
        }
    }

    public async Task<UniverseResult> RefreshAllAsync(CancellationToken ct = default)
    {
        var total = 0;
        var tracked = 0;
        var notes = new List<string>();

        foreach (var (cls, top) in new[]
                 {
                     (AssetClass.Stock, _opt.TopStocks),
                     (AssetClass.Etf, _opt.TopEtfs),
                     (AssetClass.Crypto, _opt.TopCrypto)
                 })
        {
            var r = await RefreshAsync(cls, top, autoTrack: true, ct);
            total += r.Discovered;
            tracked += r.Tracked;
            notes.Add(r.Note);
        }

        return new UniverseResult(total, tracked, string.Join(" | ", notes));
    }
}
