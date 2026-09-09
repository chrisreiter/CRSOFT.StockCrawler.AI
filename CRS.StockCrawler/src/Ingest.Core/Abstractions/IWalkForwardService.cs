namespace Ingest.Core.Abstractions;

/// <summary>Ein Durchlauf: eine Auflösung, ein Satz Horizonte.</summary>
public sealed record WalkForwardPass(string IntervalCode, int[] HorizonHours);

public sealed record CurvePoint(
    int Bucket, int HorizonHours, DateTime FromUtc, DateTime ToUtc,
    int N, double Mape, double HitRate);

public sealed record ModelCurvePoint(
    int Bucket, string ModelName, double AvgWeight, double HitRate, double MeanError, int N);

public sealed record WalkForwardEpoch(
    int EpochId,
    int PassNo,
    string IntervalCode,
    int[] Horizons,
    int Assets,
    int Steps,
    long Forecasts,
    long Scored,
    double Mape,
    double HitRate,
    int PairRefreshes,
    TimeSpan Duration,
    IReadOnlyList<HorizonScore> ByHorizon,
    IReadOnlyList<ModelScore> ByModel,
    IReadOnlyList<CurvePoint> Curve);

public sealed record WalkForwardResult(
    string RunLabel,
    IReadOnlyList<WalkForwardEpoch> Epochs,
    TimeSpan Duration);

/// <summary>
/// Vollständiger Walk-Forward über die gesamte Historie.
///
/// Anders als <see cref="IBacktestService"/>, der eine Stichprobe von
/// Prüfzeitpunkten zieht, läuft dieser Dienst Bar für Bar durch: von der
/// ersten verfügbaren bis zur letzten. An jedem Schritt wird prognostiziert,
/// und die Bewertung erfolgt <b>erst dann</b>, wenn der Zielzeitpunkt im
/// Durchlauf tatsächlich erreicht ist. Offene Prognosen warten so lange in
/// einer Warteschlange.
///
/// Das ist der entscheidende Unterschied zu einer sofortigen Bewertung: würde
/// direkt nach der Prognose gelernt, kennten die Gewichte an Tag n+1 bereits
/// das Ergebnis von Tag n+30. Genau diese Rückkopplung aus der Zukunft
/// vermeidet die verzögerte Auswertung.
///
/// Auch die Vorlauf-Beziehungen werden periodisch neu bestimmt, jeweils nur
/// aus den bis dahin bekannten Daten — sonst wüsste das leadlag-Teilmodell von
/// Korrelationen, die sich erst später ergeben haben.
/// </summary>
public interface IWalkForwardService
{
    Task<WalkForwardResult> RunAsync(
        string? runLabel,
        IReadOnlyList<WalkForwardPass>? passes,
        int buckets,
        int pairRefreshEvery,

        /// <summary>
        /// Horizonte, deren Verlauf Zeile für Zeile gespeichert wird. Leer =
        /// nur Kennzahlen. Ein voller Tagesdurchlauf über 25 Jahre erzeugt je
        /// Horizont rund 1,1 Millionen Zeilen — deshalb bewusst auswählbar.
        /// </summary>
        int[]? persistHorizons = null,
        CancellationToken ct = default);
}
