namespace Ingest.Core.Models;

/// <summary>Eine abgegebene Prognose für ein Asset auf einen Horizont.</summary>
public sealed class Forecast
{
    public long ForecastId { get; set; }
    public int AssetId { get; set; }
    public int HorizonHours { get; set; }
    public DateTime MadeAtUtc { get; set; }
    public DateTime TargetTsUtc { get; set; }
    public decimal BaseClose { get; set; }
    public decimal PredictedClose { get; set; }

    /// <summary>Log-Return gegenüber <see cref="BaseClose"/>.</summary>
    public double PredictedReturn { get; set; }

    /// <summary>0..1, abgeleitet aus der bisherigen Trefferquote der Teilmodelle.</summary>
    public double Confidence { get; set; }

    public string ModelVersion { get; set; } = "";

    /// <summary>
    /// Die Schätzung aus ALLEN Säulen, gewichtet nach Gewicht × gemessenem Verdienst.
    ///
    /// <para><c>PredictedClose</c> daneben bleibt die erste Säule allein. Die Trennung ist
    /// nicht Zierde: An <c>PredictedClose</c> hängt die Rückkopplung, die die Gewichte der
    /// fünf Teilmodelle verschiebt. Stünde dort die gemischte Zahl, lernte Säule 1 aus
    /// einem Fehler, den sie nicht gemacht hat.</para>
    /// </summary>
    public decimal? CombinedClose { get; set; }

    public double? CombinedReturn { get; set; }

    /// <summary>Welche Säule mit welchem Anteil einging — als JSON, zum Nachlesen.</summary>
    public string? PillarMix { get; set; }
    public List<ForecastComponent> Components { get; set; } = [];
}

/// <summary>Beitrag eines einzelnen Teilmodells – Grundlage der Lernschleife.</summary>
public sealed class ForecastComponent
{
    public long ForecastId { get; set; }
    public string ModelName { get; set; } = "";
    public double PredictedReturn { get; set; }
    public double Weight { get; set; }
}

/// <summary>Nachträgliche Bewertung: lag die Prognose richtig?</summary>
public sealed class ForecastScore
{
    public long ForecastId { get; set; }
    public decimal ActualClose { get; set; }
    public double ActualReturn { get; set; }
    public double AbsPctError { get; set; }
    public bool DirectionCorrect { get; set; }
    public DateTime ScoredAtUtc { get; set; }
}

/// <summary>
/// Eine vergangene Prognose neben dem, was tatsächlich eintrat. Ist
/// <see cref="ActualClose"/> null, war der Zielzeitpunkt noch nicht erreicht
/// oder die Auswertung steht aus.
/// </summary>
public sealed class ForecastVsActual
{
    public long ForecastId { get; set; }
    public DateTime MadeAtUtc { get; set; }
    public DateTime TargetTsUtc { get; set; }
    public decimal BaseClose { get; set; }
    public decimal PredictedClose { get; set; }
    public double Confidence { get; set; }

    public decimal? ActualClose { get; set; }
    public double? AbsPctError { get; set; }
    public bool? DirectionCorrect { get; set; }
}

/// <summary>Adaptives Gewicht je Asset × Horizont × Teilmodell.</summary>
public sealed class ModelWeight
{
    public int AssetId { get; set; }
    public int HorizonHours { get; set; }
    public string ModelName { get; set; } = "";
    public double Weight { get; set; }
    public int NObs { get; set; }
    public double MeanAbsPctErr { get; set; }
    public double HitRate { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
