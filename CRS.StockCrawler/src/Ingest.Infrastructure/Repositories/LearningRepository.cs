using System.Data;
using Dapper;
using Ingest.Core.Abstractions;
using Microsoft.Data.SqlClient;

namespace Ingest.Infrastructure.Repositories;

public interface ILearningRepository
{
    Task<int> StartEpochAsync(string runLabel, int passNo, string intervalCode,
                              string horizons, CancellationToken ct = default);

    Task FinishEpochAsync(int epochId, int assets, int steps, long forecasts, long scored,
                          double mape, double hitRate, int pairRefreshes, string? note,
                          CancellationToken ct = default);

    Task SaveCurveAsync(int epochId, IReadOnlyList<CurvePoint> curve,
                        IReadOnlyList<ModelCurvePoint> modelCurve, CancellationToken ct = default);

    Task<IReadOnlyList<EpochSummary>> GetEpochsAsync(string? runLabel, int limit,
                                                     CancellationToken ct = default);

    Task<(IReadOnlyList<CurveRow> Curve, IReadOnlyList<ModelCurveRow> ModelCurve)>
        GetCurveAsync(int epochId, CancellationToken ct = default);
}

public sealed record EpochSummary(
    int EpochId, string RunLabel, int PassNo, string IntervalCode, string Horizons,
    DateTime StartedUtc, DateTime? FinishedUtc, int? Assets, int? Steps,
    long? Forecasts, long? Scored, double? Mape, double? HitRate, int? PairRefreshes, string? Note);

public sealed record CurveRow(
    int BucketNo, int HorizonHours, DateTime FromUtc, DateTime ToUtc,
    int N, double Mape, double HitRate);

public sealed record ModelCurveRow(
    int BucketNo, string ModelName, double AvgWeight, double HitRate, double MeanError, int N);

public sealed class LearningRepository : ILearningRepository
{
    private readonly ISqlConnectionFactory _factory;

    public LearningRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<int> StartEpochAsync(string runLabel, int passNo, string intervalCode,
                                           string horizons, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO dbo.learning_epoch (run_label, pass_no, interval_code, horizons)
            OUTPUT INSERTED.epoch_id
            VALUES (@runLabel, @passNo, @intervalCode, @horizons)
            """, new { runLabel, passNo, intervalCode, horizons }, cancellationToken: ct));
    }

    public async Task FinishEpochAsync(int epochId, int assets, int steps, long forecasts,
                                       long scored, double mape, double hitRate, int pairRefreshes,
                                       string? note, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.learning_epoch
               SET finished_utc = SYSUTCDATETIME(), assets = @assets, steps = @steps,
                   forecasts = @forecasts, scored = @scored, mape = @mape,
                   hit_rate = @hitRate, pair_refreshes = @pairRefreshes, note = @note
             WHERE epoch_id = @epochId
            """,
            new
            {
                epochId, assets, steps, forecasts, scored,
                mape = Sane(mape), hitRate = Sane(hitRate), pairRefreshes,
                note = note?.Length > 900 ? note[..900] : note
            }, cancellationToken: ct));
    }

    public async Task SaveCurveAsync(int epochId, IReadOnlyList<CurvePoint> curve,
                                     IReadOnlyList<ModelCurvePoint> modelCurve,
                                     CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        if (curve.Count > 0)
        {
            var t = new DataTable();
            t.Columns.Add("epoch_id", typeof(int));
            t.Columns.Add("bucket_no", typeof(int));
            t.Columns.Add("horizon_hours", typeof(int));
            t.Columns.Add("from_utc", typeof(DateTime));
            t.Columns.Add("to_utc", typeof(DateTime));
            t.Columns.Add("n", typeof(int));
            t.Columns.Add("mape", typeof(double));
            t.Columns.Add("hit_rate", typeof(double));

            foreach (var c in curve)
                t.Rows.Add(epochId, c.Bucket, c.HorizonHours, c.FromUtc, c.ToUtc,
                           c.N, Sane(c.Mape), Sane(c.HitRate));

            await BulkAsync(conn, t, "dbo.learning_curve", ct);
        }

        if (modelCurve.Count > 0)
        {
            var t = new DataTable();
            t.Columns.Add("epoch_id", typeof(int));
            t.Columns.Add("bucket_no", typeof(int));
            t.Columns.Add("model_name", typeof(string));
            t.Columns.Add("avg_weight", typeof(double));
            t.Columns.Add("hit_rate", typeof(double));
            t.Columns.Add("mean_error", typeof(double));
            t.Columns.Add("n", typeof(int));

            foreach (var m in modelCurve)
                t.Rows.Add(epochId, m.Bucket, m.ModelName, Sane(m.AvgWeight),
                           Sane(m.HitRate), Sane(m.MeanError), m.N);

            await BulkAsync(conn, t, "dbo.learning_model_curve", ct);
        }
    }

    public async Task<IReadOnlyList<EpochSummary>> GetEpochsAsync(
        string? runLabel, int limit, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<EpochSummary>(new CommandDefinition("""
            SELECT TOP (@limit)
                   epoch_id AS EpochId, run_label AS RunLabel, pass_no AS PassNo,
                   interval_code AS IntervalCode, horizons AS Horizons,
                   started_utc AS StartedUtc, finished_utc AS FinishedUtc,
                   assets AS Assets, steps AS Steps, forecasts AS Forecasts, scored AS Scored,
                   mape AS Mape, hit_rate AS HitRate, pair_refreshes AS PairRefreshes,
                   note AS Note
              FROM dbo.learning_epoch
             WHERE (@runLabel IS NULL OR run_label = @runLabel)
             ORDER BY epoch_id DESC
            """, new { runLabel, limit }, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<(IReadOnlyList<CurveRow>, IReadOnlyList<ModelCurveRow>)> GetCurveAsync(
        int epochId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var curve = await conn.QueryAsync<CurveRow>(new CommandDefinition("""
            SELECT bucket_no AS BucketNo, horizon_hours AS HorizonHours,
                   from_utc AS FromUtc, to_utc AS ToUtc,
                   n AS N, mape AS Mape, hit_rate AS HitRate
              FROM dbo.learning_curve
             WHERE epoch_id = @epochId
             ORDER BY horizon_hours, bucket_no
            """, new { epochId }, cancellationToken: ct));

        var models = await conn.QueryAsync<ModelCurveRow>(new CommandDefinition("""
            SELECT bucket_no AS BucketNo, model_name AS ModelName, avg_weight AS AvgWeight,
                   hit_rate AS HitRate, mean_error AS MeanError, n AS N
              FROM dbo.learning_model_curve
             WHERE epoch_id = @epochId
             ORDER BY model_name, bucket_no
            """, new { epochId }, cancellationToken: ct));

        return (curve.ToList(), models.ToList());
    }

    /// <summary>NaN und Unendlich würden beim Schreiben in FLOAT-Spalten stören.</summary>
    private static double Sane(double v)
        => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;

    private static async Task BulkAsync(SqlConnection conn, DataTable table, string target,
                                        CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = target,
            BatchSize = 5000,
            BulkCopyTimeout = 300
        };

        foreach (DataColumn c in table.Columns)
            bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);

        await bulk.WriteToServerAsync(table, ct);
    }
}
