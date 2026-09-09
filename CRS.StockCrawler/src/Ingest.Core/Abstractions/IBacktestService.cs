namespace Ingest.Core.Abstractions;

public sealed record BacktestResult(
    int Assets,
    int Forecasts,
    int Scored,
    double MeanAbsPctError,
    double HitRate,
    IReadOnlyList<HorizonScore> ByHorizon,
    IReadOnlyList<ModelScore> ByModel,
    TimeSpan Duration);

public sealed record HorizonScore(int HorizonHours, int N, double Mape, double HitRate);

public sealed record ModelScore(string ModelName, int N, double MeanAbsError, double HitRate, double FinalWeight);

/// <summary>
/// Walk-Forward-Backtest: stellt Prognosen zu Zeitpunkten in der Vergangenheit,
/// wertet sie sofort gegen den tatsächlich eingetretenen Kurs aus und zieht die
/// Modellgewichte nach.
///
/// Zweck ist zweierlei. Erstens belegt er, dass die Rückkopplung funktioniert,
/// ohne wochenlang auf fällige Prognosen zu warten. Zweitens startet das System
/// dadurch nicht bei Gleichgewichtung, sondern mit bereits gelernten Gewichten.
/// </summary>
public interface IBacktestService
{
    Task<BacktestResult> RunAsync(int? assetId, int steps, int strideBars, bool persist,
                                  CancellationToken ct = default);
}
