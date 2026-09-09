using System.Diagnostics;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Ingest.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Walk-Forward-Backtest über die vorhandene Historie.
///
/// Für jeden Prüfzeitpunkt sehen die Teilmodelle ausschließlich die Bars davor;
/// der tatsächliche Kurs am Zielzeitpunkt wird erst danach zur Bewertung
/// herangezogen. Nach jeder Bewertung greift dieselbe Hedge-Regel wie im
/// Livebetrieb, sodass die Gewichte über den Testlauf hinweg dazulernen.
///
/// Eine Einschränkung bleibt und ist bewusst in Kauf genommen: die
/// Vorlauf-Beziehungen (welcher Wert welchem vorausläuft) stammen aus der
/// Analyse über den gesamten Zeitraum. Das Signal selbst wird zwar nur aus
/// Vergangenheitsdaten gebildet, die Auswahl der Frühindikatoren kennt aber den
/// ganzen Zeitraum. Für das leadlag-Teilmodell ist das Ergebnis daher eher
/// optimistisch; die übrigen Teilmodelle sind davon nicht betroffen.
/// </summary>
public sealed class BacktestService : IBacktestService
{
    private readonly IAssetRepository _assets;
    private readonly IPriceBarRepository _bars;
    private readonly IPairStatRepository _pairs;
    private readonly IForecastRepository _forecasts;
    private readonly IIngestRunRepository _runs;
    private readonly IngestOptions _opt;
    private readonly ILogger<BacktestService> _log;

    private readonly IReadOnlyList<IForecastModel> _models = Ensemble.DefaultModels();

    private const int DailySwitchHours = 96;
    private const int MinHistoryBars = 60;
    private const int MaxLeaders = 5;

    public BacktestService(
        IAssetRepository assets,
        IPriceBarRepository bars,
        IPairStatRepository pairs,
        IForecastRepository forecasts,
        IIngestRunRepository runs,
        IOptions<IngestOptions> opt,
        ILogger<BacktestService> log)
    {
        _assets = assets;
        _bars = bars;
        _pairs = pairs;
        _forecasts = forecasts;
        _runs = runs;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<BacktestResult> RunAsync(
        int? assetId, int steps, int strideBars, bool persist, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var runId = await _runs.StartAsync($"backtest:{steps}x{strideBars}", null, ct);

        try
        {
            var targets = assetId is null
                ? await _assets.GetTrackedAsync(ct)
                : [await _assets.GetAsync(assetId.Value, ct)
                   ?? throw new InvalidOperationException($"Asset {assetId} unbekannt.")];

            var horizons = _opt.EffectiveHorizons;

            // Statistik über alle Assets hinweg.
            var perHorizon = new Dictionary<int, (int N, double ErrSum, int Hits)>();
            var perModel = new Dictionary<string, (int N, double ErrSum, int Hits)>();
            var finalWeights = new Dictionary<string, (double Sum, int N)>();

            var totalForecasts = 0;
            var totalScored = 0;
            var assetsDone = 0;

            foreach (var asset in targets)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var made = await RunForAssetAsync(
                        asset, horizons, steps, strideBars, persist,
                        perHorizon, perModel, finalWeights, ct);

                    totalForecasts += made.Forecasts;
                    totalScored += made.Scored;
                    assetsDone++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Backtest für {Symbol} fehlgeschlagen", asset.Symbol);
                }
            }

            var byHorizon = perHorizon
                .OrderBy(kv => kv.Key)
                .Select(kv => new HorizonScore(kv.Key, kv.Value.N,
                    kv.Value.N > 0 ? kv.Value.ErrSum / kv.Value.N : 0,
                    kv.Value.N > 0 ? (double)kv.Value.Hits / kv.Value.N : 0))
                .ToList();

            var byModel = perModel
                .OrderBy(kv => kv.Key)
                .Select(kv => new ModelScore(kv.Key, kv.Value.N,
                    kv.Value.N > 0 ? kv.Value.ErrSum / kv.Value.N : 0,
                    kv.Value.N > 0 ? (double)kv.Value.Hits / kv.Value.N : 0,
                    finalWeights.TryGetValue(kv.Key, out var w) && w.N > 0 ? w.Sum / w.N : 0))
                .ToList();

            var allN = perHorizon.Values.Sum(v => v.N);
            var mape = allN > 0 ? perHorizon.Values.Sum(v => v.ErrSum) / allN : 0;
            var hit = allN > 0 ? (double)perHorizon.Values.Sum(v => v.Hits) / allN : 0;

            await _runs.FinishAsync(runId, totalScored, 0, totalForecasts,
                $"{assetsDone} Werte, Ø Fehler {mape:P2}, Richtung {hit:P1}", ct);

            _log.LogInformation(
                "Backtest: {Assets} Werte, {Forecasts} Prognosen, {Scored} bewertet, " +
                "Ø Fehler {Mape:P2}, Richtung {Hit:P1} in {Elapsed:n1}s",
                assetsDone, totalForecasts, totalScored, mape, hit, sw.Elapsed.TotalSeconds);

            return new BacktestResult(assetsDone, totalForecasts, totalScored, mape, hit,
                byHorizon, byModel, sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _runs.FinishAsync(runId, 0, 1, 0, ex.Message, ct);
            throw;
        }
    }

    private async Task<(int Forecasts, int Scored)> RunForAssetAsync(
        Asset asset, int[] horizons, int steps, int strideBars, bool persist,
        Dictionary<int, (int N, double ErrSum, int Hits)> perHorizon,
        Dictionary<string, (int N, double ErrSum, int Hits)> perModel,
        Dictionary<string, (double Sum, int N)> finalWeights,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var forecasts = 0;
        var scored = 0;

        // Historie und Vorlaufsignale je Intervall einmal laden.
        var history = new Dictionary<string, List<PriceBar>>();
        var leaderSeries = new Dictionary<string, List<(PairStat Stat, double[] Returns)>>();

        foreach (var interval in BarInterval.All)
        {
            var lookback = BarInterval.Duration(interval) * 3000;
            var bars = await _bars.GetAsync(asset.AssetId, interval, now - lookback, now, ct);
            history[interval] = bars.ToList();

            leaderSeries[interval] = await LoadLeadersAsync(asset.AssetId, interval, now, ct);
        }

        // Gewichte je Horizont über den ganzen Lauf mitführen — genau darin
        // besteht das Lernen.
        var weights = new Dictionary<int, Dictionary<string, ModelWeight>>();

        foreach (var h in horizons)
        {
            var stored = await _forecasts.GetWeightsAsync(asset.AssetId, h, ct);
            weights[h] = stored.ToDictionary(w => w.ModelName, w => w);
        }

        foreach (var horizonHours in horizons)
        {
            var interval = horizonHours >= DailySwitchHours ? BarInterval.Daily : BarInterval.Hourly;
            var bars = history[interval];

            var horizonBars = interval == BarInterval.Hourly
                ? horizonHours
                : Math.Max(1, horizonHours / 24);

            // Der jüngste Prüfzeitpunkt muss so weit zurückliegen, dass sein
            // Zielzeitpunkt bereits in den Daten steht.
            var lastCheckable = bars.Count - 1 - horizonBars;
            if (lastCheckable < MinHistoryBars) continue;

            var w = weights[horizonHours];

            // Von alt nach neu laufen, damit die Gewichte chronologisch lernen.
            var points = new List<int>();
            for (var s = 0; s < steps; s++)
            {
                var idx = lastCheckable - s * strideBars;
                if (idx < MinHistoryBars) break;
                points.Add(idx);
            }
            points.Reverse();

            foreach (var t in points)
            {
                // Nur Bars bis einschließlich t – kein Blick in die Zukunft.
                var closes = new List<decimal>(t + 1);
                for (var i = 0; i <= t; i++) closes.Add(bars[i].Close);

                var leaders = BuildSignalsAt(leaderSeries[interval], closes.Count);

                var input = new ForecastInput(closes, horizonBars, leaders);

                var wDict = w.ToDictionary(kv => kv.Key, kv => kv.Value.Weight);
                var hDict = w.ToDictionary(kv => kv.Key, kv => kv.Value.HitRate);

                var result = Ensemble.Combine(_models, input, wDict, hDict);

                var baseClose = bars[t].Close;
                var actualBar = bars[t + horizonBars];
                var actual = actualBar.Close;
                if (baseClose <= 0 || actual <= 0) continue;

                var predictedClose = baseClose * (decimal)Math.Exp(result.PredictedReturn);
                var actualReturn = Math.Log((double)actual / (double)baseClose);
                var absPctError = Math.Abs((double)(actual - predictedClose) / (double)baseClose);
                var directionCorrect = DirectionHit(result.PredictedReturn, actualReturn);

                forecasts++;
                scored++;

                Accumulate(perHorizon, horizonHours, absPctError, directionCorrect);

                foreach (var c in result.Components)
                    AccumulateModel(perModel, c.ModelName,
                        Math.Abs(c.PredictedReturn - actualReturn),
                        DirectionHit(c.PredictedReturn, actualReturn));

                // Lernschritt — identisch zur Regel im Livebetrieb.
                ApplyLearning(w, result.Components, asset.AssetId, horizonHours, actualReturn);

                if (persist)
                {
                    var madeAt = bars[t].TsUtc;
                    var forecast = new Forecast
                    {
                        AssetId = asset.AssetId,
                        HorizonHours = horizonHours,
                        MadeAtUtc = madeAt,
                        TargetTsUtc = actualBar.TsUtc,
                        BaseClose = baseClose,
                        PredictedClose = decimal.Round(predictedClose, 8),
                        PredictedReturn = result.PredictedReturn,
                        Confidence = result.Confidence,
                        ModelVersion = Ensemble.Version + "-bt",
                        Components = result.Components.Select(c => new ForecastComponent
                        {
                            ModelName = c.ModelName,
                            PredictedReturn = c.PredictedReturn,
                            Weight = c.Weight
                        }).ToList()
                    };

                    var fid = await _forecasts.InsertAsync(forecast, ct);

                    await _forecasts.ScoreAsync([new ForecastScore
                    {
                        ForecastId = fid,
                        ActualClose = actual,
                        ActualReturn = actualReturn,
                        AbsPctError = absPctError,
                        DirectionCorrect = directionCorrect
                    }], ct);
                }
            }

            foreach (var kv in w)
            {
                if (!finalWeights.TryGetValue(kv.Key, out var acc)) acc = (0, 0);
                finalWeights[kv.Key] = (acc.Sum + kv.Value.Weight, acc.N + 1);
            }

            await _forecasts.UpsertWeightsAsync(w.Values, ct);
        }

        return (forecasts, scored);
    }

    /// <summary>
    /// Lädt die Frühindikatoren samt ihrer Renditereihen. Die Reihen werden
    /// später positionsweise angezapft, damit zum Prüfzeitpunkt nur
    /// zurückliegende Werte einfließen.
    /// </summary>
    private async Task<List<(PairStat, double[])>> LoadLeadersAsync(
        int assetId, string interval, DateTime now, CancellationToken ct)
    {
        var top = await _pairs.GetTopLeadersAsync(assetId, interval, MaxLeaders, ct);
        var result = new List<(PairStat, double[])>();
        if (top.Count == 0) return result;

        var lookback = BarInterval.Duration(interval) * 3000;
        var series = await _bars.GetManyAsync(
            top.Select(t => t.AssetIdA).Distinct().ToArray(), interval, now - lookback, now, ct);

        foreach (var p in top)
        {
            if (!series.TryGetValue(p.AssetIdA, out var bars) || bars.Count < 60) continue;
            result.Add((p, Statistics.LogReturns(bars.Select(b => b.Close).ToList())));
        }

        return result;
    }

    /// <summary>Vorlaufsignale, wie sie zum Zeitpunkt <paramref name="upTo"/> aussahen.</summary>
    private static List<LeadSignal> BuildSignalsAt(
        List<(PairStat Stat, double[] Returns)> leaders, int upTo)
    {
        var signals = new List<LeadSignal>(leaders.Count);

        foreach (var (stat, returns) in leaders)
        {
            var lag = Math.Max(1, stat.BestLagBars);

            // Position in der Reihe des Vorläufers, die dem Prüfzeitpunkt entspricht.
            var end = Math.Min(upTo - 1, returns.Length);
            if (end < lag + 20) continue;

            double recent = 0;
            for (var k = end - lag; k < end; k++) recent += returns[k];

            // Beta aus den Daten vor dem Prüfzeitpunkt.
            var n = Math.Min(120, end - lag);
            if (n < 20) continue;

            var beta = EstimateBeta(returns, end, lag, n);
            signals.Add(new LeadSignal(stat.AssetIdA, recent, lag, beta, stat.BestLagCorr));
        }

        return signals;
    }

    /// <summary>
    /// Grobe Beta-Schätzung aus der Streuung des Vorläufers. Eine saubere
    /// Regression bräuchte die ausgerichtete Zielreihe an jedem Prüfpunkt und
    /// wäre im Backtest zu teuer; 1.0 als Ausgangswert ist neutral.
    /// </summary>
    private static double EstimateBeta(double[] returns, int end, int lag, int n)
    {
        var slice = new double[n];
        Array.Copy(returns, Math.Max(0, end - lag - n), slice, 0, n);

        var sd = Statistics.StdDev(slice);
        return sd <= double.Epsilon ? 0 : 1.0;
    }

    private static bool DirectionHit(double predicted, double actual)
        => Math.Abs(actual) < 1e-6
            ? Math.Abs(predicted) < 1e-3
            : Math.Sign(predicted) == Math.Sign(actual);

    private static void Accumulate(
        Dictionary<int, (int N, double ErrSum, int Hits)> map, int key, double err, bool hit)
    {
        map.TryGetValue(key, out var v);
        map[key] = (v.N + 1, v.ErrSum + err, v.Hits + (hit ? 1 : 0));
    }

    private static void AccumulateModel(
        Dictionary<string, (int N, double ErrSum, int Hits)> map, string key, double err, bool hit)
    {
        map.TryGetValue(key, out var v);
        map[key] = (v.N + 1, v.ErrSum + err, v.Hits + (hit ? 1 : 0));
    }

    private static void ApplyLearning(
        Dictionary<string, ModelWeight> w, IReadOnlyList<ComponentPrediction> comps,
        int assetId, int horizonHours, double actualReturn)
    {
        var currentWeights = w.ToDictionary(kv => kv.Key, kv => kv.Value.Weight);
        var componentReturns = comps.ToDictionary(c => c.ModelName, c => c.PredictedReturn);

        var scale = Math.Max(0.002, Math.Abs(actualReturn) * 2);
        var updated = Ensemble.UpdateWeights(currentWeights, componentReturns, actualReturn, scale: scale);

        foreach (var c in comps)
        {
            if (!w.TryGetValue(c.ModelName, out var mw))
            {
                mw = new ModelWeight
                {
                    AssetId = assetId,
                    HorizonHours = horizonHours,
                    ModelName = c.ModelName,
                    Weight = 1.0 / Math.Max(1, comps.Count),
                    NObs = 0,
                    MeanAbsPctErr = 0,
                    HitRate = 0.5
                };
                w[c.ModelName] = mw;
            }

            var err = Math.Abs(c.PredictedReturn - actualReturn);
            var hit = DirectionHit(c.PredictedReturn, actualReturn);

            var (newErr, n) = Ensemble.RunningMean(mw.MeanAbsPctErr, mw.NObs, err);
            var (newHit, _) = Ensemble.RunningMean(mw.HitRate, mw.NObs, hit ? 1.0 : 0.0);

            mw.MeanAbsPctErr = newErr;
            mw.HitRate = newHit;
            mw.NObs = n;
            mw.Weight = updated.TryGetValue(c.ModelName, out var nw) ? nw : mw.Weight;
        }
    }
}
