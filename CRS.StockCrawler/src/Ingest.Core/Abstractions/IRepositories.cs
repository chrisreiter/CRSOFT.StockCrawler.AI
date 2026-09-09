using Ingest.Core.Enums;
using Ingest.Core.Models;

namespace Ingest.Core.Abstractions;

public interface IAssetRepository
{
    Task<int> UpsertAsync(Asset asset, CancellationToken ct = default);
    Task<Asset?> GetAsync(int assetId, CancellationToken ct = default);
    Task<Asset?> FindAsync(AssetClass cls, string symbol, CancellationToken ct = default);

    /// <summary>Alle Assets, optional gefiltert. Basis für die Backend-Auswahl.</summary>
    Task<IReadOnlyList<Asset>> ListAsync(
        AssetClass? cls = null, bool? trackedOnly = null, string? search = null,
        int limit = 500, string? sector = null, string? country = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<Asset>> GetTrackedAsync(CancellationToken ct = default);

    /// <summary>Tracking-Flag setzen – steuert, was ingestiert/prognostiziert wird.</summary>
    Task<int> SetTrackedAsync(IEnumerable<int> assetIds, bool tracked, CancellationToken ct = default);

    /// <summary>Top-N je Klasse als getrackt markieren.</summary>
    Task<int> TrackTopByMarketCapAsync(AssetClass cls, int limit, CancellationToken ct = default);
}

public interface IPriceBarRepository
{
    /// <summary>Idempotenter Bulk-Upsert. Liefert die Zahl betroffener Zeilen.</summary>
    Task<int> UpsertAsync(int assetId, string intervalCode, ProviderId provider,
                          IReadOnlyList<PriceBar> bars, CancellationToken ct = default);

    Task<IReadOnlyList<PriceBar>> GetAsync(int assetId, string intervalCode,
                                           DateTime fromUtc, DateTime toUtc,
                                           CancellationToken ct = default);

    /// <summary>Letzte gespeicherte Bar-Zeit – Startpunkt für inkrementelle Updates.</summary>
    Task<DateTime?> GetLastTsAsync(int assetId, string intervalCode, CancellationToken ct = default);

    /// <summary>Schlusskurse mehrerer Assets, auf gemeinsame Zeitachse ausgerichtet.</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<PriceBar>>> GetManyAsync(
        IEnumerable<int> assetIds, string intervalCode, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default);

    Task<decimal?> GetCloseAtOrBeforeAsync(int assetId, string intervalCode, DateTime tsUtc,
                                           CancellationToken ct = default);

    /// <summary>
    /// Wie <see cref="GetCloseAtOrBeforeAsync"/>, liefert aber auch den
    /// Zeitstempel der gefundenen Bar. Nötig, um zu erkennen, ob überhaupt
    /// eine NEUE Bar vorliegt — bei geschlossener Börse wäre es sonst
    /// dieselbe, mit der die Prognose gestellt wurde.
    /// </summary>
    Task<(DateTime TsUtc, decimal Close)?> GetBarAtOrBeforeAsync(
        int assetId, string intervalCode, DateTime tsUtc, CancellationToken ct = default);
}

public interface IIngestRunRepository
{
    Task<long> StartAsync(string jobName, ProviderId? provider, CancellationToken ct = default);
    Task FinishAsync(long runId, int ok, int err, int rows, string? note, CancellationToken ct = default);
    Task<IReadOnlyList<IngestRun>> GetRecentAsync(int last, CancellationToken ct = default);
}

public interface IForecastRepository
{
    Task<long> InsertAsync(Forecast forecast, CancellationToken ct = default);
    Task<IReadOnlyList<Forecast>> GetDueAsync(DateTime nowUtc, int maxRows, CancellationToken ct = default);

    /// <summary>
    /// Stellt Prognosen zurück, deren Zielzeitpunkt in einem geschlossenen
    /// Marktfenster liegt — dort kann nie eine neue Bar erscheinen. Ohne das
    /// blockieren sie als älteste den Kopf der Warteschlange, und die
    /// Lernschleife steht still.
    /// </summary>
    Task<int> MarkUnscoreableAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ForecastComponent>> GetComponentsAsync(IEnumerable<long> forecastIds, CancellationToken ct = default);
    Task ScoreAsync(IEnumerable<ForecastScore> scores, CancellationToken ct = default);

    /// <summary>Bewertet die über alle Säulen gemischte Zahl — getrennt von Säule 1.</summary>
    Task ScoreCombinedAsync(
        IEnumerable<(long Id, decimal Ist, double IstRendite, double Fehler, bool? Richtung)> scores,
        CancellationToken ct = default);

    Task<IReadOnlyList<Forecast>> GetLatestAsync(int assetId, CancellationToken ct = default);

    /// <summary>
    /// Vergangene Prognosen eines Horizonts samt ihrer Auswertung — die
    /// Grundlage dafür, im Chart Prognose und eingetretene Wirklichkeit
    /// nebeneinander zu zeigen.
    /// </summary>
    Task<IReadOnlyList<ForecastVsActual>> GetHistoryAsync(
        int assetId, int horizonHours, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default);

    /// <summary>Welche Horizonte liegen für diesen Wert gespeichert vor?</summary>
    Task<IReadOnlyList<int>> GetAvailableHorizonsAsync(int assetId, CancellationToken ct = default);

    Task<IReadOnlyList<ModelWeight>> GetWeightsAsync(int assetId, int horizonHours, CancellationToken ct = default);
    Task UpsertWeightsAsync(IEnumerable<ModelWeight> weights, CancellationToken ct = default);

    /// <summary>Genauigkeits-Historie für das Dashboard.</summary>
    Task<IReadOnlyList<(int HorizonHours, int N, double Mape, double HitRate)>>
        GetAccuracyAsync(int? assetId, CancellationToken ct = default);
}

public interface IPairStatRepository
{
    Task UpsertAsync(IEnumerable<PairStat> stats, CancellationToken ct = default);
    Task<IReadOnlyList<PairStat>> GetForAsync(int assetId, string intervalCode, CancellationToken ct = default);
    Task<IReadOnlyList<PairStat>> GetTopLeadersAsync(int assetIdB, string intervalCode, int limit, CancellationToken ct = default);
    Task UpsertCrossingsAsync(IEnumerable<Crossing> crossings, CancellationToken ct = default);
    Task<IReadOnlyList<Crossing>> GetCrossingsAsync(DateTime fromUtc, string intervalCode, int limit, CancellationToken ct = default);
}
