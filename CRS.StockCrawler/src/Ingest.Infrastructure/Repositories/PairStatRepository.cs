using System.Data;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Models;
using Microsoft.Data.SqlClient;

namespace Ingest.Infrastructure.Repositories;

public sealed class PairStatRepository : IPairStatRepository
{
    private readonly ISqlConnectionFactory _factory;

    public PairStatRepository(ISqlConnectionFactory factory) => _factory = factory;

    /* Bei 300 verfolgten Werten fallen rund 45.000 Paare an, Kreuzungen können
       in die Hunderttausende gehen. Einzelne INSERT/MERGE-Aufrufe wären hier
       chancenlos, deshalb geht beides über SqlBulkCopy in eine Stage-Tabelle
       und von dort mit einer einzigen Anweisung in die Zieltabelle. */

    public async Task UpsertAsync(IEnumerable<PairStat> stats, CancellationToken ct = default)
    {
        var list = stats as IList<PairStat> ?? stats.ToList();
        if (list.Count == 0) return;

        await using var conn = await _factory.OpenAsync(ct);

        await ExecAsync(conn, """
            CREATE TABLE #pair_stage (
              asset_id_a INT, asset_id_b INT, interval_code VARCHAR(3), window_bars INT,
              corr0 FLOAT, best_lag_bars INT, best_lag_corr FLOAT, n_obs INT);
            """, ct);

        var table = new DataTable();
        table.Columns.Add("asset_id_a", typeof(int));
        table.Columns.Add("asset_id_b", typeof(int));
        table.Columns.Add("interval_code", typeof(string));
        table.Columns.Add("window_bars", typeof(int));
        table.Columns.Add("corr0", typeof(double));
        table.Columns.Add("best_lag_bars", typeof(int));
        table.Columns.Add("best_lag_corr", typeof(double));
        table.Columns.Add("n_obs", typeof(int));

        foreach (var s in list)
            table.Rows.Add(s.AssetIdA, s.AssetIdB, s.IntervalCode, s.WindowBars,
                           s.Corr0, s.BestLagBars, s.BestLagCorr, s.NObs);

        await BulkCopyAsync(conn, table, "#pair_stage", ct);

        await ExecAsync(conn, """
            MERGE dbo.pair_stat WITH (HOLDLOCK) AS t
            USING (SELECT asset_id_a, asset_id_b, interval_code, window_bars,
                          corr0, best_lag_bars, best_lag_corr, n_obs
                     FROM #pair_stage) AS s
               ON t.asset_id_a = s.asset_id_a AND t.asset_id_b = s.asset_id_b
              AND t.interval_code = s.interval_code AND t.window_bars = s.window_bars
            WHEN MATCHED THEN UPDATE SET
                  corr0 = s.corr0, best_lag_bars = s.best_lag_bars,
                  best_lag_corr = s.best_lag_corr, n_obs = s.n_obs,
                  computed_utc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
              INSERT (asset_id_a, asset_id_b, interval_code, window_bars,
                      corr0, best_lag_bars, best_lag_corr, n_obs)
              VALUES (s.asset_id_a, s.asset_id_b, s.interval_code, s.window_bars,
                      s.corr0, s.best_lag_bars, s.best_lag_corr, s.n_obs);

            DROP TABLE #pair_stage;
            """, ct);
    }

    public async Task UpsertCrossingsAsync(IEnumerable<Crossing> crossings, CancellationToken ct = default)
    {
        var list = crossings as IList<Crossing> ?? crossings.ToList();
        if (list.Count == 0) return;

        await using var conn = await _factory.OpenAsync(ct);

        await ExecAsync(conn, """
            CREATE TABLE #cross_stage (
              asset_id_a INT, asset_id_b INT, interval_code VARCHAR(3), ts_utc DATETIME2(0),
              direction TINYINT, spread_before FLOAT, spread_after FLOAT);
            """, ct);

        var table = new DataTable();
        table.Columns.Add("asset_id_a", typeof(int));
        table.Columns.Add("asset_id_b", typeof(int));
        table.Columns.Add("interval_code", typeof(string));
        table.Columns.Add("ts_utc", typeof(DateTime));
        table.Columns.Add("direction", typeof(byte));
        table.Columns.Add("spread_before", typeof(double));
        table.Columns.Add("spread_after", typeof(double));

        foreach (var c in list)
            table.Rows.Add(c.AssetIdA, c.AssetIdB, c.IntervalCode, c.TsUtc,
                           (byte)(c.Upward ? 1 : 0), c.SpreadBefore, c.SpreadAfter);

        await BulkCopyAsync(conn, table, "#cross_stage", ct);

        // Nur neue Kreuzungen einfügen; der eindeutige Index verträgt keine Dubletten.
        await ExecAsync(conn, """
            INSERT INTO dbo.crossing
              (asset_id_a, asset_id_b, interval_code, ts_utc, direction, spread_before, spread_after)
            SELECT s.asset_id_a, s.asset_id_b, s.interval_code, s.ts_utc,
                   s.direction, s.spread_before, s.spread_after
              FROM (SELECT *, ROW_NUMBER() OVER (
                       PARTITION BY asset_id_a, asset_id_b, interval_code, ts_utc
                       ORDER BY ts_utc) AS rn
                      FROM #cross_stage) s
             WHERE s.rn = 1
               AND NOT EXISTS (
                     SELECT 1 FROM dbo.crossing c
                      WHERE c.asset_id_a = s.asset_id_a AND c.asset_id_b = s.asset_id_b
                        AND c.interval_code = s.interval_code AND c.ts_utc = s.ts_utc);

            DROP TABLE #cross_stage;
            """, ct);
    }

    // ------------------------------------------------------------------ Lesen

    private const string PairColumns = """
        asset_id_a AS AssetIdA, asset_id_b AS AssetIdB, interval_code AS IntervalCode,
        window_bars AS WindowBars, corr0 AS Corr0, best_lag_bars AS BestLagBars,
        best_lag_corr AS BestLagCorr, n_obs AS NObs, computed_utc AS ComputedUtc
        """;

    public async Task<IReadOnlyList<PairStat>> GetForAsync(
        int assetId, string intervalCode, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<PairStat>(new CommandDefinition($"""
            SELECT {PairColumns}
              FROM dbo.pair_stat
             WHERE interval_code = @intervalCode AND (asset_id_a = @assetId OR asset_id_b = @assetId)
             ORDER BY ABS(corr0) DESC
            """, new { assetId, intervalCode }, commandTimeout: 120, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<PairStat>> GetTopLeadersAsync(
        int assetIdB, string intervalCode, int limit, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        /* Ein Vorläufer kann in beiden Spalten stehen: pair_stat legt immer
           (A,B) mit A < B ab. Steht das gesuchte Papier auf Seite A, dreht sich
           die Vorlaufrichtung um, deshalb die zweite Hälfte der Union. */
        var rows = await conn.QueryAsync<PairStat>(new CommandDefinition($"""
            SELECT TOP (@limit) * FROM (
              SELECT {PairColumns}
                FROM dbo.pair_stat
               WHERE asset_id_b = @assetId AND interval_code = @intervalCode
                 AND best_lag_bars > 0
              UNION ALL
              SELECT asset_id_b AS AssetIdA, asset_id_a AS AssetIdB, interval_code AS IntervalCode,
                     window_bars AS WindowBars, corr0 AS Corr0, -best_lag_bars AS BestLagBars,
                     best_lag_corr AS BestLagCorr, n_obs AS NObs, computed_utc AS ComputedUtc
                FROM dbo.pair_stat
               WHERE asset_id_a = @assetId AND interval_code = @intervalCode
                 AND best_lag_bars < 0
            ) x
            ORDER BY ABS(x.BestLagCorr) DESC
            """, new { assetId = assetIdB, intervalCode, limit },
            commandTimeout: 120, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<Crossing>> GetCrossingsAsync(
        DateTime fromUtc, string intervalCode, int limit, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<Crossing>(new CommandDefinition("""
            SELECT TOP (@limit)
                   crossing_id AS CrossingId, asset_id_a AS AssetIdA, asset_id_b AS AssetIdB,
                   interval_code AS IntervalCode, ts_utc AS TsUtc,
                   CAST(direction AS BIT) AS Upward,
                   spread_before AS SpreadBefore, spread_after AS SpreadAfter
              FROM dbo.crossing
             WHERE interval_code = @intervalCode AND ts_utc >= @fromUtc
             ORDER BY ts_utc DESC
            """, new { fromUtc, intervalCode, limit }, commandTimeout: 120, cancellationToken: ct));

        return rows.ToList();
    }

    // ------------------------------------------------------------------ Hilfen

    private static Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(sql, commandTimeout: 600, cancellationToken: ct));

    private static async Task BulkCopyAsync(SqlConnection conn, DataTable table, string target,
                                            CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = target,
            BatchSize = 10_000,
            BulkCopyTimeout = 600
        };

        foreach (DataColumn c in table.Columns)
            bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);

        await bulk.WriteToServerAsync(table, ct);
    }
}
