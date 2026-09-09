using System.Data;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Core.Models;

namespace Ingest.Infrastructure.Repositories;

public sealed class PriceBarRepository : IPriceBarRepository
{
    private readonly ISqlConnectionFactory _factory;

    /// <summary>
    /// Obergrenze je MERGE-Aufruf. Größere Blöcke sprengen die Grenze für
    /// Table-Valued Parameters und lassen Sperren zu lange stehen.
    /// </summary>
    private const int ChunkSize = 2000;

    public PriceBarRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<int> UpsertAsync(int assetId, string intervalCode, ProviderId provider,
                                       IReadOnlyList<PriceBar> bars, CancellationToken ct = default)
    {
        if (bars.Count == 0) return 0;

        await using var conn = await _factory.OpenAsync(ct);
        var total = 0;

        // Dubletten müssen vorher raus: der TVP hat ts_utc als Primärschlüssel.
        var distinct = bars
            .GroupBy(b => b.TsUtc)
            .Select(g => g.Last())
            .OrderBy(b => b.TsUtc)
            .ToList();

        for (var offset = 0; offset < distinct.Count; offset += ChunkSize)
        {
            var chunk = distinct.Skip(offset).Take(ChunkSize).ToList();

            var table = new DataTable();
            table.Columns.Add("ts_utc", typeof(DateTime));
            table.Columns.Add("open", typeof(decimal));
            table.Columns.Add("high", typeof(decimal));
            table.Columns.Add("low", typeof(decimal));
            table.Columns.Add("close", typeof(decimal));
            table.Columns.Add("adj_close", typeof(decimal));
            table.Columns.Add("volume", typeof(decimal));

            foreach (var b in chunk)
            {
                table.Rows.Add(
                    b.TsUtc,
                    (object?)b.Open ?? DBNull.Value,
                    (object?)b.High ?? DBNull.Value,
                    (object?)b.Low ?? DBNull.Value,
                    b.Close,
                    (object?)b.AdjClose ?? DBNull.Value,
                    (object?)b.Volume ?? DBNull.Value);
            }

            var p = new DynamicParameters();
            p.Add("@asset_id", assetId);
            p.Add("@interval_code", intervalCode);
            p.Add("@provider", (byte)provider);
            p.Add("@bars", table.AsTableValuedParameter("dbo.PriceBarList"));

            total += await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "dbo.upsert_price_bars", p,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 120,
                cancellationToken: ct));
        }

        return total;
    }

    private const string BarColumns = """
        ts_utc AS TsUtc, [open] AS [Open], [high] AS [High], [low] AS [Low],
        [close] AS [Close], adj_close AS AdjClose, volume AS Volume
        """;

    public async Task<IReadOnlyList<PriceBar>> GetAsync(
        int assetId, string intervalCode, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<PriceBar>(new CommandDefinition($"""
            SELECT {BarColumns}
              FROM dbo.price_bar
             WHERE asset_id = @assetId AND interval_code = @intervalCode
               AND ts_utc >= @fromUtc AND ts_utc <= @toUtc
             ORDER BY ts_utc
            """, new { assetId, intervalCode, fromUtc, toUtc }, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<DateTime?> GetLastTsAsync(int assetId, string intervalCode, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(ts_utc) FROM dbo.price_bar WHERE asset_id = @assetId AND interval_code = @intervalCode",
            new { assetId, intervalCode }, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<PriceBar>>> GetManyAsync(
        IEnumerable<int> assetIds, string intervalCode, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default)
    {
        var ids = assetIds.Distinct().ToArray();
        var result = new Dictionary<int, IReadOnlyList<PriceBar>>(ids.Length);
        if (ids.Length == 0) return result;

        await using var conn = await _factory.OpenAsync(ct);

        /* Ein Roundtrip je Block statt einer Abfrage je Wert — bei 100 Werten
           sonst 100 Abfragen. Die Blockgröße kommt aus der Parametergrenze von
           SQL Server; heute reicht ein Block, aber der Bestand soll wachsen
           dürfen, ohne dass diese Stelle stillschweigend bricht. */
        var rows = new List<(int AssetId, DateTime TsUtc, decimal? Open, decimal? High,
                             decimal? Low, decimal Close, decimal? AdjClose, decimal? Volume)>();

        foreach (var chunk in SqlBatching.Chunks(ids))
        {
            var part = await conn.QueryAsync<(int AssetId, DateTime TsUtc, decimal? Open, decimal? High,
                                              decimal? Low, decimal Close, decimal? AdjClose, decimal? Volume)>(
                new CommandDefinition("""
                    SELECT asset_id, ts_utc, [open], [high], [low], [close], adj_close, volume
                      FROM dbo.price_bar
                     WHERE asset_id IN @ids AND interval_code = @intervalCode
                       AND ts_utc >= @fromUtc AND ts_utc <= @toUtc
                     ORDER BY asset_id, ts_utc
                    """, new { ids = chunk, intervalCode, fromUtc, toUtc },
                    commandTimeout: 180, cancellationToken: ct));

            rows.AddRange(part);
        }

        foreach (var g in rows.GroupBy(r => r.AssetId))
        {
            result[g.Key] = g.Select(r => new PriceBar
            {
                TsUtc = r.TsUtc,
                Open = r.Open,
                High = r.High,
                Low = r.Low,
                Close = r.Close,
                AdjClose = r.AdjClose,
                Volume = r.Volume
            }).ToList();
        }

        return result;
    }


    public async Task<(DateTime TsUtc, decimal Close)?> GetBarAtOrBeforeAsync(
        int assetId, string intervalCode, DateTime tsUtc, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var row = await conn.QuerySingleOrDefaultAsync<(DateTime TsUtc, decimal Close)?>(
            new CommandDefinition("""
            SELECT TOP 1 ts_utc, [close]
              FROM dbo.price_bar
             WHERE asset_id = @assetId AND interval_code = @intervalCode AND ts_utc <= @tsUtc
             ORDER BY ts_utc DESC
            """, new { assetId, intervalCode, tsUtc }, cancellationToken: ct));

        return row;
    }

    public async Task<decimal?> GetCloseAtOrBeforeAsync(
        int assetId, string intervalCode, DateTime tsUtc, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        return await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition("""
            SELECT TOP 1 [close]
              FROM dbo.price_bar
             WHERE asset_id = @assetId AND interval_code = @intervalCode AND ts_utc <= @tsUtc
             ORDER BY ts_utc DESC
            """, new { assetId, intervalCode, tsUtc }, cancellationToken: ct));
    }
}
