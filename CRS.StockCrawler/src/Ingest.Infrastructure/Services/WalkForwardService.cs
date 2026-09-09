using System.Diagnostics;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Ingest.Infrastructure.Options;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Vollständiger Walk-Forward über die gesamte Historie — siehe
/// <see cref="IWalkForwardService"/> für die Begründung des Aufbaus.
/// </summary>
public sealed class WalkForwardService : IWalkForwardService
{
    private readonly IAssetRepository _assets;
    private readonly IPriceBarRepository _bars;
    private readonly ILearningRepository _learning;
    private readonly IForecastTrackRepository _track;
    private readonly IForecastRepository _forecasts;
    private readonly IngestOptions _opt;
    private readonly ILogger<WalkForwardService> _log;

    private readonly IReadOnlyList<IForecastModel> _models = Ensemble.DefaultModels();
    private readonly string[] _modelNames;

    /// <summary>Wie viele Bars Historie ein Wert braucht, bevor prognostiziert wird.</summary>
    private const int MinHistoryBars = 60;

    /// <summary>
    /// Länge des Rückblick-Fensters, das den Teilmodellen übergeben wird. Das
    /// längste Modell schaut 200 Bars zurück; 260 lässt Luft und hält den
    /// Aufwand je Schritt konstant statt mit der Historie wachsend.
    /// </summary>
    private const int WindowBars = 260;

    private const int MaxLeadersPerAsset = 5;

    public WalkForwardService(
        IAssetRepository assets,
        IPriceBarRepository bars,
        ILearningRepository learning,
        IForecastTrackRepository track,
        IForecastRepository forecasts,
        IOptions<IngestOptions> opt,
        ILogger<WalkForwardService> log)
    {
        _assets = assets;
        _bars = bars;
        _learning = learning;
        _track = track;
        _forecasts = forecasts;
        _opt = opt.Value;
        _log = log;
        _modelNames = _models.Select(m => m.Name).ToArray();
    }

    public async Task<WalkForwardResult> RunAsync(
        string? runLabel, IReadOnlyList<WalkForwardPass>? passes,
        int buckets, int pairRefreshEvery,
        int[]? persistHorizons = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var label = string.IsNullOrWhiteSpace(runLabel)
            ? $"wf-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
            : runLabel;

        passes ??= DefaultPasses();
        buckets = Math.Clamp(buckets, 4, 200);
        pairRefreshEvery = Math.Max(10, pairRefreshEvery);

        /* Die Gewichte überdauern die Durchläufe — genau darin besteht das
           fortlaufende Lernen über Tag, Stunde und noch einmal Tag. */
        var weights = new Dictionary<(int AssetId, int Horizon), double[]>();
        var modelStats = new Dictionary<(int AssetId, int Horizon, int ModelIdx), ModelRunning>();

        var epochs = new List<WalkForwardEpoch>();

        for (var p = 0; p < passes.Count; p++)
        {
            ct.ThrowIfCancellationRequested();

            var pass = passes[p];
            _log.LogInformation("Walk-Forward {Label}: Durchlauf {No}/{Total} — {Interval}, Horizonte {H}",
                label, p + 1, passes.Count, pass.IntervalCode, string.Join("/", pass.HorizonHours));

            var epoch = await RunPassAsync(
                label, p + 1, pass, buckets, pairRefreshEvery, weights, modelStats,
                persistHorizons, ct);

            epochs.Add(epoch);
        }

        // Gelernte Gewichte am Ende in die Datenbank übernehmen, damit der
        // Livebetrieb darauf aufsetzt.
        await PersistWeightsAsync(weights, modelStats, ct);

        _log.LogInformation("Walk-Forward {Label} abgeschlossen in {Elapsed:n1}s",
            label, sw.Elapsed.TotalSeconds);

        return new WalkForwardResult(label, epochs, sw.Elapsed);
    }

    /// <summary>
    /// Tag → Stunde → Tag. Im Tagesdurchlauf entfallen die Horizonte unter
    /// 24 Stunden: sie sind kürzer als eine Bar und dort nicht auflösbar.
    /// </summary>
    private WalkForwardPass[] DefaultPasses()
    {
        var all = _opt.EffectiveHorizons;
        var daily = all.Where(h => h >= 24).ToArray();

        return
        [
            new WalkForwardPass(BarInterval.Daily, daily),
            new WalkForwardPass(BarInterval.Hourly, all),
            new WalkForwardPass(BarInterval.Daily, daily)
        ];
    }

    // ================================================================ Durchlauf

    private async Task<WalkForwardEpoch> RunPassAsync(
        string label, int passNo, WalkForwardPass pass, int buckets, int pairRefreshEvery,
        Dictionary<(int, int), double[]> weights,
        Dictionary<(int, int, int), ModelRunning> modelStats,
        int[]? persistHorizons,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var interval = pass.IntervalCode;

        var epochId = await _learning.StartEpochAsync(
            label, passNo, interval, string.Join(",", pass.HorizonHours), ct);

        try
        {
            var tracked = await _assets.GetTrackedAsync(ct);
            var ids = tracked.Select(a => a.AssetId).ToArray();

            // Gesamte Historie laden — der Durchlauf beginnt bei der ersten Bar.
            var series = await _bars.GetManyAsync(
                ids, interval, new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                DateTime.UtcNow, ct);

            var usable = series
                .Where(kv => kv.Value.Count >= MinHistoryBars + 2)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            if (usable.Count < 2)
            {
                await _learning.FinishEpochAsync(epochId, usable.Count, 0, 0, 0, 0, 0, 0,
                    "Zu wenige Reihen mit ausreichender Historie", ct);

                return Empty(epochId, passNo, pass, sw.Elapsed);
            }

            var (grid, dense) = PairAnalyzer.Densify(usable);
            var assetIds = usable.Keys.OrderBy(i => i).ToArray();

            var states = BuildStates(assetIds, usable, grid, pass.HorizonHours, interval, weights);

            // Horizonte in Bars dieser Auflösung.
            var horizonBars = pass.HorizonHours
                .Select(h => interval == BarInterval.Hourly ? h : Math.Max(1, h / 24))
                .ToArray();

            var agg = new PassAggregate(pass.HorizonHours, _modelNames.Length, buckets);
            var leadMap = new Dictionary<int, LeadSignal[]>();
            var pairRefreshes = 0;

            /* Nur die verlangten Horizonte mitschreiben. Der Puffer wird
               zwischendurch geleert, damit bei Millionen Zeilen nicht der
               gesamte Verlauf im Speicher steht. */
            var persist = persistHorizons is { Length: > 0 }
                ? persistHorizons.ToHashSet()
                : [];

            var trackBuffer = new List<TrackRow>(persist.Count > 0 ? 200_000 : 0);
            long trackWritten = 0;

            long forecasts = 0, scored = 0;
            var bucketSize = Math.Max(1, grid.Length / buckets);

            // Wiederverwendete Puffer — im heißen Pfad wird nichts alloziert.
            var window = new double[WindowBars];
            var comps = new double[_models.Count];

            for (var t = 0; t < grid.Length; t++)
            {
                if ((t & 255) == 0) ct.ThrowIfCancellationRequested();

                /* Vorlaufstruktur periodisch neu bestimmen — ausschließlich aus
                   Daten bis t. Ohne das kennte leadlag Zusammenhänge, die sich
                   erst später ergeben haben. */
                if (t >= MinHistoryBars && (t - MinHistoryBars) % pairRefreshEvery == 0)
                {
                    RefreshLeaders(leadMap, assetIds, dense, grid, t, interval);
                    pairRefreshes++;
                }

                var bucket = Math.Min(buckets - 1, t / bucketSize);

                foreach (var st in states)
                {
                    var i = st.GridToLocal[t];
                    if (i < 0) continue;

                    // 1) Fällig gewordene Prognosen auswerten und daraus lernen.
                    for (var hIdx = 0; hIdx < horizonBars.Length; hIdx++)
                    {
                        var q = st.Pending[hIdx];
                        while (q.Count > 0 && q.Peek().TargetLocal <= i)
                        {
                            var pend = q.Dequeue();
                            var horizonHours = pass.HorizonHours[hIdx];

                            var outcome = Resolve(st, hIdx, pend, i, horizonHours,
                                                  agg, bucket, modelStats);
                            scored++;

                            if (outcome is not null && persist.Contains(horizonHours))
                            {
                                trackBuffer.Add(outcome with { IntervalCode = interval });

                                if (trackBuffer.Count >= 200_000)
                                {
                                    await _track.BulkWriteAsync(trackBuffer, label, ct);
                                    trackWritten += trackBuffer.Count;
                                    trackBuffer.Clear();
                                }
                            }
                        }
                    }

                    // 2) Neue Prognosen stellen — die Modelle sehen nur [0..i].
                    if (i < MinHistoryBars) continue;

                    var take = Math.Min(WindowBars, i + 1);
                    Array.Copy(st.Closes, i + 1 - take, window, 0, take);

                    var input = new ForecastInput(
                        take == WindowBars ? window : window[..take],
                        0,
                        leadMap.TryGetValue(st.AssetId, out var ls) ? ls : []);

                    for (var hIdx = 0; hIdx < horizonBars.Length; hIdx++)
                    {
                        var target = i + horizonBars[hIdx];
                        if (target >= st.Closes.Length) continue;

                        input.HorizonBars = horizonBars[hIdx];

                        var w = st.Weights[hIdx];
                        var pred = Ensemble.CombineInPlace(_models, input, w, comps);

                        st.Pending[hIdx].Enqueue(new Pending(
                            target, i, st.Closes[i], pred, (double[])comps.Clone()));

                        forecasts++;
                    }
                }
            }

            if (trackBuffer.Count > 0)
            {
                await _track.BulkWriteAsync(trackBuffer, label, ct);
                trackWritten += trackBuffer.Count;
                trackBuffer.Clear();
            }

            if (trackWritten > 0)
                _log.LogInformation("Durchlauf {No}: {Rows} Verlaufszeilen gespeichert",
                    passNo, trackWritten);

            var (mape, hit) = agg.Overall();

            await _learning.SaveCurveAsync(epochId, agg.BuildCurve(grid, bucketSize),
                agg.BuildModelCurve(_modelNames), ct);

            await _learning.FinishEpochAsync(epochId, usable.Count, grid.Length,
                forecasts, scored, mape, hit, pairRefreshes, null, ct);

            _log.LogInformation(
                "Durchlauf {No} ({Interval}): {Steps} Schritte, {Fc} Prognosen, {Sc} bewertet, " +
                "Ø Fehler {Mape:P2}, Richtung {Hit:P1}, {Refresh} Struktur-Neuberechnungen, {Sec:n1}s",
                passNo, interval, grid.Length, forecasts, scored, mape, hit,
                pairRefreshes, sw.Elapsed.TotalSeconds);

            return new WalkForwardEpoch(
                epochId, passNo, interval, pass.HorizonHours, usable.Count, grid.Length,
                forecasts, scored, mape, hit, pairRefreshes, sw.Elapsed,
                agg.ByHorizon(), agg.ByModel(_modelNames), agg.BuildCurve(grid, bucketSize));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _learning.FinishEpochAsync(epochId, 0, 0, 0, 0, 0, 0, 0, ex.Message, ct);
            throw;
        }
    }

    private WalkForwardEpoch Empty(int epochId, int passNo, WalkForwardPass pass, TimeSpan d)
        => new(epochId, passNo, pass.IntervalCode, pass.HorizonHours,
               0, 0, 0, 0, 0, 0, 0, d, [], [], []);

    // ------------------------------------------------------------ Auswertung

    private TrackRow? Resolve(
        AssetWalkState st, int hIdx, Pending pend, int currentLocal, int horizonHours,
        PassAggregate agg, int bucket,
        Dictionary<(int, int, int), ModelRunning> modelStats)
    {
        var actual = st.Closes[Math.Min(currentLocal, st.Closes.Length - 1)];
        if (pend.BaseClose <= 0 || actual <= 0) return null;

        var actualReturn = Math.Log(actual / pend.BaseClose);
        var predictedClose = pend.BaseClose * Math.Exp(pend.PredictedReturn);
        var absPctError = Math.Abs(actual - predictedClose) / pend.BaseClose;
        var hit = DirectionHit(pend.PredictedReturn, actualReturn);

        agg.AddHorizon(hIdx, bucket, absPctError, hit);

        for (var m = 0; m < pend.Components.Length; m++)
        {
            var cErr = Math.Abs(pend.Components[m] - actualReturn);
            var cHit = DirectionHit(pend.Components[m], actualReturn);

            agg.AddModel(m, bucket, cErr, cHit);

            var key = (st.AssetId, horizonHours, m);
            if (!modelStats.TryGetValue(key, out var ms)) ms = new ModelRunning();
            ms.Add(cErr, cHit);
            modelStats[key] = ms;
        }

        /* Der Lernschritt. Die Fehlerskala richtet sich nach der tatsächlichen
           Bewegung: bei einem ruhigen Wert wiegt derselbe absolute Fehler
           schwerer als bei einem volatilen. */
        var scale = Math.Max(0.002, Math.Abs(actualReturn) * 2);
        Ensemble.UpdateWeightsInPlace(st.Weights[hIdx], pend.Components, actualReturn, scale: scale);

        agg.SnapshotWeights(bucket, st.Weights[hIdx]);

        return new TrackRow(
            st.AssetId, horizonHours, "",
            st.Timestamps[Math.Min(currentLocal, st.Timestamps.Length - 1)],
            st.Timestamps[Math.Max(0, currentLocal - (pend.TargetLocal - pend.MadeLocal))],
            (decimal)pend.BaseClose, (decimal)predictedClose,
            (decimal)actual, absPctError, hit, 0.5);
    }

    private static bool DirectionHit(double predicted, double actual)
        => Math.Abs(actual) < 1e-6
            ? Math.Abs(predicted) < 1e-3
            : Math.Sign(predicted) == Math.Sign(actual);

    // ---------------------------------------------------------- Vorbereitung

    private List<AssetWalkState> BuildStates(
        int[] assetIds,
        Dictionary<int, IReadOnlyList<PriceBar>> series,
        DateTime[] grid,
        int[] horizons,
        string interval,
        Dictionary<(int, int), double[]> weights)
    {
        var gridIndex = new Dictionary<DateTime, int>(grid.Length);
        for (var i = 0; i < grid.Length; i++) gridIndex[grid[i]] = i;

        var states = new List<AssetWalkState>(assetIds.Length);

        foreach (var id in assetIds)
        {
            var bars = series[id];

            var closes = new double[bars.Count];
            var stamps = new DateTime[bars.Count];
            var gridToLocal = new int[grid.Length];
            Array.Fill(gridToLocal, -1);

            for (var k = 0; k < bars.Count; k++)
            {
                closes[k] = (double)bars[k].Close;
                stamps[k] = bars[k].TsUtc;
                if (gridIndex.TryGetValue(bars[k].TsUtc, out var g)) gridToLocal[g] = k;
            }

            var w = new double[horizons.Length][];
            var pending = new Queue<Pending>[horizons.Length];

            for (var h = 0; h < horizons.Length; h++)
            {
                var key = (id, horizons[h]);
                if (!weights.TryGetValue(key, out var arr))
                {
                    arr = new double[_models.Count];
                    Array.Fill(arr, 1.0 / _models.Count);
                    weights[key] = arr;
                }
                w[h] = arr;                    // dieselbe Instanz: Lernen wirkt über Durchläufe hinweg
                pending[h] = new Queue<Pending>();
            }

            states.Add(new AssetWalkState(id, closes, stamps, gridToLocal, w, pending));
        }

        return states;
    }

    /// <summary>
    /// Bestimmt die Frühindikatoren neu — nur aus Daten bis <paramref name="upTo"/>.
    /// </summary>
    private void RefreshLeaders(
        Dictionary<int, LeadSignal[]> leadMap, int[] assetIds,
        Dictionary<int, double[]> dense, DateTime[] grid, int upTo, string interval)
    {
        // Gleitendes Fenster: ältere Zusammenhänge sind für die Gegenwart
        // wenig aussagekräftig, und das Fenster hält den Aufwand konstant.
        var from = Math.Max(0, upTo - 500);

        var result = PairAnalyzer.Analyze(
            assetIds, dense, grid, from, upTo, interval, upTo - from + 1,
            new PairAnalyzerOptions { DetectCrossings = false });

        leadMap.Clear();
        var byTarget = new Dictionary<int, List<LeadSignal>>();

        foreach (var s in result.Stats)
        {
            if (s.BestLagBars == 0) continue;

            // Positiver Lag: A läuft B voraus. Negativer: umgekehrt.
            var leader = s.BestLagBars > 0 ? s.AssetIdA : s.AssetIdB;
            var target = s.BestLagBars > 0 ? s.AssetIdB : s.AssetIdA;
            var lag = Math.Abs(s.BestLagBars);

            if (!dense.TryGetValue(leader, out var lead)) continue;

            // Jüngste Bewegung des Vorläufers über die Dauer seines Vorlaufs —
            // ausschließlich aus der Vergangenheit.
            double recent = 0;
            var seen = 0;
            for (var k = upTo; k >= 0 && seen < lag; k--)
            {
                var cur = lead[k];
                if (double.IsNaN(cur)) continue;

                var prevIdx = k - 1;
                while (prevIdx >= 0 && double.IsNaN(lead[prevIdx])) prevIdx--;
                if (prevIdx < 0) break;

                var prev = lead[prevIdx];
                if (prev > 0 && cur > 0) recent += Math.Log(cur / prev);
                seen++;
            }

            if (!byTarget.TryGetValue(target, out var list))
            {
                list = [];
                byTarget[target] = list;
            }

            // Beta neutral bei 1.0: eine saubere Regression an jedem Prüfpunkt
            // wäre im vollen Durchlauf zu teuer, und 1.0 unterstellt nichts.
            list.Add(new LeadSignal(leader, recent, lag, 1.0, s.BestLagCorr));
        }

        foreach (var (target, list) in byTarget)
        {
            leadMap[target] = list
                .OrderByDescending(l => Math.Abs(l.Corr))
                .Take(MaxLeadersPerAsset)
                .ToArray();
        }
    }

    // ----------------------------------------------------------- Speicherung

    private async Task PersistWeightsAsync(
        Dictionary<(int AssetId, int Horizon), double[]> weights,
        Dictionary<(int AssetId, int Horizon, int ModelIdx), ModelRunning> stats,
        CancellationToken ct)
    {
        var list = new List<ModelWeight>(weights.Count * _models.Count);

        foreach (var ((assetId, horizon), w) in weights)
        {
            for (var m = 0; m < w.Length; m++)
            {
                stats.TryGetValue((assetId, horizon, m), out var ms);

                list.Add(new ModelWeight
                {
                    AssetId = assetId,
                    HorizonHours = horizon,
                    ModelName = _modelNames[m],
                    Weight = w[m],
                    NObs = ms.N,
                    MeanAbsPctErr = ms.MeanError,
                    HitRate = ms.N > 0 ? ms.HitRate : 0.5
                });
            }
        }

        await _forecasts.UpsertWeightsAsync(list, ct);
        _log.LogInformation("Walk-Forward: {Count} gelernte Gewichte gespeichert", list.Count);
    }

    // -------------------------------------------------------- Hilfsstrukturen

    private readonly record struct Pending(
        int TargetLocal, int MadeLocal, double BaseClose, double PredictedReturn, double[] Components);

    private sealed class AssetWalkState(
        int assetId, double[] closes, DateTime[] timestamps, int[] gridToLocal,
        double[][] weights, Queue<Pending>[] pending)
    {
        public int AssetId { get; } = assetId;
        public double[] Closes { get; } = closes;

        /// <summary>Zeitstempel der eigenen Bars — für den gespeicherten Verlauf.</summary>
        public DateTime[] Timestamps { get; } = timestamps;

        public int[] GridToLocal { get; } = gridToLocal;
        public double[][] Weights { get; } = weights;
        public Queue<Pending>[] Pending { get; } = pending;
    }

    private struct ModelRunning
    {
        public int N;
        public double MeanError;
        public double HitRate;

        public void Add(double error, bool hit)
        {
            var (e, n) = Ensemble.RunningMean(MeanError, N, error);
            var (h, _) = Ensemble.RunningMean(HitRate, N, hit ? 1.0 : 0.0);
            MeanError = e;
            HitRate = h;
            N = n;
        }
    }
}
