using System.Data;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Core.Models;

namespace Ingest.Infrastructure.Repositories;

public sealed class AssetRepository : IAssetRepository
{
    private readonly ISqlConnectionFactory _factory;

    public AssetRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<int> UpsertAsync(Asset asset, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "dbo.upsert_asset",
            new
            {
                asset_class = (byte)asset.AssetClass,
                symbol = asset.Symbol,
                provider = (byte)asset.Provider,
                provider_symbol = asset.ProviderSymbol,
                name = asset.Name,
                currency = asset.Currency,
                exchange = asset.Exchange,
                market_cap = asset.MarketCap,
                market_cap_rank = asset.MarketCapRank,
                reference_price = asset.ReferencePrice,
                sector = asset.Sector,
                country = asset.Country
            },
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
    }

    private const string SelectColumns = """
        asset_id AS AssetId, asset_class AS AssetClass, symbol AS Symbol,
        [name] AS Name, currency AS Currency, exchange AS Exchange,
        provider AS Provider, provider_symbol AS ProviderSymbol,
        market_cap AS MarketCap, market_cap_rank AS MarketCapRank,
        reference_price AS ReferencePrice, reference_price_utc AS ReferencePriceUtc,
        sector AS Sector, country AS Country,
        is_tracked AS IsTracked, first_seen_utc AS FirstSeenUtc, updated_utc AS UpdatedUtc
        """;

    public async Task<Asset?> GetAsync(int assetId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Asset>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM dbo.asset WHERE asset_id = @assetId",
            new { assetId }, cancellationToken: ct));
    }

    public async Task<Asset?> FindAsync(AssetClass cls, string symbol, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Asset>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM dbo.asset WHERE asset_class = @cls AND symbol = @symbol",
            new { cls = (byte)cls, symbol }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Asset>> ListAsync(
        AssetClass? cls = null, bool? trackedOnly = null, string? search = null,
        int limit = 500, string? sector = null, string? country = null,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var sql = $"""
            SELECT TOP (@limit) {SelectColumns}
              FROM dbo.asset
             WHERE (@cls IS NULL OR asset_class = @cls)
               AND (@tracked IS NULL OR is_tracked = @tracked)
               AND (@search IS NULL OR symbol LIKE @like OR [name] LIKE @like)
               AND (@sector IS NULL OR sector = @sector)
               AND (@country IS NULL OR country = @country)
             ORDER BY CASE WHEN market_cap_rank IS NULL THEN 1 ELSE 0 END,
                      market_cap_rank, symbol
            """;

        var rows = await conn.QueryAsync<Asset>(new CommandDefinition(sql, new
        {
            limit,
            cls = cls.HasValue ? (byte?)cls.Value : null,
            tracked = trackedOnly,
            search,
            like = search is null ? null : $"%{search}%",
            sector,
            country
        }, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<Asset>> GetTrackedAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<Asset>(new CommandDefinition(
            $"""
             SELECT {SelectColumns} FROM dbo.asset
              WHERE is_tracked = 1
              ORDER BY asset_class,
                       CASE WHEN market_cap_rank IS NULL THEN 1 ELSE 0 END,
                       market_cap_rank, symbol
             """, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<int> SetTrackedAsync(IEnumerable<int> assetIds, bool tracked, CancellationToken ct = default)
    {
        var ids = assetIds.Distinct().ToArray();
        if (ids.Length == 0) return 0;

        await using var conn = await _factory.OpenAsync(ct);

        // Blockweise, aus demselben Grund wie überall: 2100 Parameter je Befehl.
        var affected = 0;

        foreach (var chunk in SqlBatching.Chunks(ids))
        {
            affected += await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.asset SET is_tracked = @tracked, updated_utc = SYSUTCDATETIME() "
                + "WHERE asset_id IN @ids",
                new { tracked, ids = chunk }, cancellationToken: ct));
        }

        return affected;
    }

    public async Task<int> TrackTopByMarketCapAsync(AssetClass cls, int limit, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // Nur die Top-N dieser Klasse aktivieren; bereits getrackte Werte
        // anderer Klassen bleiben unberührt.
        return await conn.ExecuteAsync(new CommandDefinition("""
            WITH ranked AS (
              SELECT asset_id,
                     ROW_NUMBER() OVER (ORDER BY
                       CASE WHEN market_cap_rank IS NULL THEN 1 ELSE 0 END,
                       market_cap_rank,
                       market_cap DESC) AS rn
                FROM dbo.asset
               WHERE asset_class = @cls
            )
            UPDATE a SET is_tracked = 1, updated_utc = SYSUTCDATETIME()
              FROM dbo.asset a
              JOIN ranked r ON r.asset_id = a.asset_id
             WHERE r.rn <= @limit AND a.is_tracked = 0
            """, new { cls = (byte)cls, limit }, cancellationToken: ct));
    }
}
