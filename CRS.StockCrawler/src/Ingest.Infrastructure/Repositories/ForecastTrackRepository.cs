using System.Data;
using Dapper;
using Ingest.Core.Abstractions;
using Microsoft.Data.SqlClient;

namespace Ingest.Infrastructure.Repositories;

/// <summary>Eine Zeile des Prognoseverlaufs, bereits ausgewertet.</summary>
public sealed record TrackRow(
    int AssetId, int HorizonHours, string IntervalCode,
    DateTime TargetTsUtc, DateTime MadeAtUtc,
    decimal BaseClose, decimal PredictedClose,
    decimal? ActualClose, double? AbsPctError, bool? DirectionCorrect,
    double Confidence);

public interface IForecastTrackRepository
{
    /// <summary>
    /// Schreibt den Verlauf in Blöcken. Bei Millionen Zeilen ist SqlBulkCopy
    /// die einzige gangbare Form — einzelne INSERTs liefen Stunden.
    /// </summary>
    Task<int> BulkWriteAsync(IReadOnlyList<TrackRow> rows, string runLabel,
                             CancellationToken ct = default);

    Task<int> ClearAsync(string? runLabel, CancellationToken ct = default);

    Task<IReadOnlyList<TrackRow>> GetAsync(int assetId, int horizonHours, string intervalCode,
                                           DateTime fromUtc, DateTime toUtc,
                                           CancellationToken ct = default);

    /// <summary>
    /// Alle Prognosen, die zu einem Stichtag gestellt wurden — über sämtliche
    /// Horizonte. Ergibt zusammen den Pfad, den das Modell an diesem Tag in die
    /// Zukunft gezeichnet hat.
    /// </summary>
    Task<IReadOnlyList<TrackRow>> GetAsOfAsync(int assetId, string intervalCode,
                                               DateTime asOfUtc, TimeSpan tolerance,
                                               CancellationToken ct = default);
}

public sealed class ForecastTrackRepository : IForecastTrackRepository
{
    private readonly ISqlConnectionFactory _factory;

    public ForecastTrackRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<int> BulkWriteAsync(IReadOnlyList<TrackRow> rows, string runLabel,
                                          CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;

        await using var conn = await _factory.OpenAsync(ct);

        // Über eine Stage-Tabelle, damit ein erneuter Lauf vorhandene Zeilen
        // ersetzt statt am Primärschlüssel zu scheitern.
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE TABLE #track_stage (
              asset_id INT, horizon_hours INT, interval_code VARCHAR(3),
              target_ts_utc DATETIME2(0), made_at_utc DATETIME2(0),
              base_close DECIMAL(19,8), predicted_close DECIMAL(19,8),
              actual_close DECIMAL(19,8) NULL, abs_pct_error FLOAT NULL,
              direction_correct BIT NULL, confidence FLOAT, run_label NVARCHAR(64));
            """, commandTimeout: 300, cancellationToken: ct));

        var table = new DataTable();
        table.Columns.Add("asset_id", typeof(int));
        table.Columns.Add("horizon_hours", typeof(int));
        table.Columns.Add("interval_code", typeof(string));
        table.Columns.Add("target_ts_utc", typeof(DateTime));
        table.Columns.Add("made_at_utc", typeof(DateTime));
        table.Columns.Add("base_close", typeof(decimal));
        table.Columns.Add("predicted_close", typeof(decimal));
        table.Columns.Add("actual_close", typeof(decimal));
        table.Columns.Add("abs_pct_error", typeof(double));
        table.Columns.Add("direction_correct", typeof(bool));
        table.Columns.Add("confidence", typeof(double));
        table.Columns.Add("run_label", typeof(string));

        foreach (var r in rows)
        {
            table.Rows.Add(
                r.AssetId, r.HorizonHours, r.IntervalCode, r.TargetTsUtc, r.MadeAtUtc,
                r.BaseClose, r.PredictedClose,
                (object?)r.ActualClose ?? DBNull.Value,
                (object?)r.AbsPctError ?? DBNull.Value,
                (object?)r.DirectionCorrect ?? DBNull.Value,
                r.Confidence, runLabel);
        }

        using (var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "#track_stage",
            BatchSize = 20_000,
            BulkCopyTimeout = 900
        })
        {
            foreach (DataColumn c in table.Columns)
                bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);

            await bulk.WriteToServerAsync(table, ct);
        }

        var affected = await conn.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.forecast_track WITH (HOLDLOCK) AS t
            USING (SELECT asset_id, horizon_hours, interval_code, target_ts_utc,
                          MAX(made_at_utc) AS made_at_utc,
                          MAX(base_close) AS base_close,
                          MAX(predicted_close) AS predicted_close,
                          MAX(actual_close) AS actual_close,
                          MAX(abs_pct_error) AS abs_pct_error,
                          MAX(CAST(direction_correct AS TINYINT)) AS direction_correct,
                          MAX(confidence) AS confidence,
                          MAX(run_label) AS run_label
                     FROM #track_stage
                    GROUP BY asset_id, horizon_hours, interval_code, target_ts_utc) AS s
               ON t.asset_id = s.asset_id AND t.horizon_hours = s.horizon_hours
              AND t.interval_code = s.interval_code AND t.target_ts_utc = s.target_ts_utc
            WHEN MATCHED THEN UPDATE SET
                  made_at_utc = s.made_at_utc, base_close = s.base_close,
                  predicted_close = s.predicted_close, actual_close = s.actual_close,
                  abs_pct_error = s.abs_pct_error,
                  direction_correct = CAST(s.direction_correct AS BIT),
                  confidence = s.confidence, run_label = s.run_label
            WHEN NOT MATCHED THEN
              INSERT (asset_id, horizon_hours, interval_code, target_ts_utc, made_at_utc,
                      base_close, predicted_close, actual_close, abs_pct_error,
                      direction_correct, confidence, run_label)
              VALUES (s.asset_id, s.horizon_hours, s.interval_code, s.target_ts_utc, s.made_at_utc,
                      s.base_close, s.predicted_close, s.actual_close, s.abs_pct_error,
                      CAST(s.direction_correct AS BIT), s.confidence, s.run_label);

            DROP TABLE #track_stage;
            """, commandTimeout: 900, cancellationToken: ct));

        return affected;
    }

    public async Task<int> ClearAsync(string? runLabel, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.forecast_track WHERE @runLabel IS NULL OR run_label = @runLabel",
            new { runLabel }, commandTimeout: 600, cancellationToken: ct));
    }


    public async Task<IReadOnlyList<TrackRow>> GetAsOfAsync(
        int assetId, string intervalCode, DateTime asOfUtc, TimeSpan tolerance,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        /* Der Stichtag muss nicht exakt getroffen werden: an Wochenenden und
           Feiertagen gibt es keine Bar, und der Nutzer soll trotzdem ein
           Ergebnis sehen. Je Horizont zählt die Prognose, die dem Stichtag am
           nächsten liegt. */
        var from = asOfUtc - tolerance;
        var to = asOfUtc + tolerance;

        var rows = await conn.QueryAsync<TrackRow>(new CommandDefinition("""
            WITH nearest AS (
              SELECT *,
                     ROW_NUMBER() OVER (PARTITION BY horizon_hours
                                        ORDER BY ABS(DATEDIFF(SECOND, made_at_utc, @asOfUtc))) AS rn
                FROM dbo.forecast_track
               WHERE asset_id = @assetId AND interval_code = @intervalCode
                 AND made_at_utc >= @from AND made_at_utc <= @to
            )
            SELECT asset_id AS AssetId, horizon_hours AS HorizonHours,
                   interval_code AS IntervalCode, target_ts_utc AS TargetTsUtc,
                   made_at_utc AS MadeAtUtc, base_close AS BaseClose,
                   predicted_close AS PredictedClose, actual_close AS ActualClose,
                   abs_pct_error AS AbsPctError, direction_correct AS DirectionCorrect,
                   confidence AS Confidence
              FROM nearest WHERE rn = 1
             ORDER BY horizon_hours
            """, new { assetId, intervalCode, asOfUtc, from, to },
            commandTimeout: 120, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<TrackRow>> GetAsync(
        int assetId, int horizonHours, string intervalCode,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<TrackRow>(new CommandDefinition("""
            SELECT asset_id AS AssetId, horizon_hours AS HorizonHours,
                   interval_code AS IntervalCode, target_ts_utc AS TargetTsUtc,
                   made_at_utc AS MadeAtUtc, base_close AS BaseClose,
                   predicted_close AS PredictedClose, actual_close AS ActualClose,
                   abs_pct_error AS AbsPctError, direction_correct AS DirectionCorrect,
                   confidence AS Confidence
              FROM dbo.forecast_track
             WHERE asset_id = @assetId AND horizon_hours = @horizonHours
               AND interval_code = @intervalCode
               AND target_ts_utc >= @fromUtc AND target_ts_utc <= @toUtc
             ORDER BY target_ts_utc
            """, new { assetId, horizonHours, intervalCode, fromUtc, toUtc },
            commandTimeout: 120, cancellationToken: ct));

        return rows.ToList();
    }
}
