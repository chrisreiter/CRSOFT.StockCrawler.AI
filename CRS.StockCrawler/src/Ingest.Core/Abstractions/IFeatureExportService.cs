namespace Ingest.Core.Abstractions;

public sealed record FeatureExportResult(
    string FilePath,
    string IntervalCode,
    int Assets,
    int GridPoints,
    long Rows,
    int[] HorizonBars,
    string FeatureVersion,
    TimeSpan Duration);

/// <summary>
/// Erzeugt die Querschnitts-Merkmalsmatrix als CSV für das Training.
///
/// Eine Zeile je Zeitpunkt und Wert, mit eigenen Kurs- und Flussmerkmalen,
/// dem Zustand des Gesamtmarkts, der eigenen Anlageklasse und der verwandten
/// Werte — plus den Zielgrößen für die verlangten Horizonte.
/// </summary>
/// <summary>
/// Das jüngste Merkmalsfenster eines Wertes — die Eingabe für ein trainiertes
/// Modell.
/// </summary>
public sealed record FeatureWindow(
    IReadOnlyList<double[]> Rows, double LastClose, DateTime LastTsUtc);

/// <summary>
/// Der gesamte verwertbare Verlauf eines Wertes — alle Merkmalszeilen mit
/// zugehörigem Schlusskurs und Zeitpunkt, in der Reihenfolge, in der auch das
/// Training sie gesehen hat.
///
/// Für den Rückblick gebraucht: Er stellt an jedem Tag der Vergangenheit
/// dieselbe Frage, die die Säule heute beantwortet, und legt die Antwort neben
/// das, was tatsächlich eintrat.
/// </summary>
public sealed record FeatureHistory(
    IReadOnlyList<double[]> Rows,
    IReadOnlyList<double> Close,
    IReadOnlyList<DateTime> TsUtc);

public interface IFeatureExportService
{
    Task<FeatureExportResult> ExportAsync(
        string intervalCode,
        int[] horizonBars,
        string? outputPath,
        CancellationToken ct = default);

    /// <summary>
    /// Baut das jüngste Merkmalsfenster für einen einzelnen Wert.
    ///
    /// <b>Warum hier und nicht im Endpunkt.</b> Ein Modell rechnet mit genau den
    /// Merkmalen, mit denen es trainiert wurde — in genau dieser Reihenfolge und
    /// genau dieser Bauart. Würde der Endpunkt sie ein zweites Mal
    /// zusammensetzen, gäbe es zwei Quellen der Wahrheit, und eine Abweichung in
    /// einem einzigen Merkmal ließe das Modell stillschweigend falsch rechnen.
    /// Es gibt daher nur einen Aufbau, und er steht dort, wo auch der Export ihn
    /// benutzt.
    /// </summary>
    /// <summary>Lädt den vollständigen verwertbaren Verlauf eines Wertes.</summary>
    Task<FeatureHistory?> BuildHistoryAsync(
        int assetId, string intervalCode, CancellationToken ct = default);

    Task<FeatureWindow?> BuildWindowAsync(
        int assetId, string intervalCode, int seqLen, CancellationToken ct = default);
}
