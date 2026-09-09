using Dapper;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Kapitalfluss — Stufe 1. Misst, wie viel Geld tatsächlich bewegt wird und
/// wohin es sich verschiebt, statt nur Kurse zu betrachten.
/// </summary>
public static class FlowEndpoints
{
    /* Krypto meldet das Volumen bereits in Dollar, Aktien und ETFs in Stück.
       Die Umrechnung passiert deshalb schon in SQL, sonst müsste jede Abfrage
       Millionen Zeilen an den Prozess schicken. Siehe FlowMetrics. */
    private const string FlowExpr =
        "CASE WHEN a.asset_class = 2 THEN p.volume ELSE p.[close] * p.volume END";

    public static void MapFlowEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/flow").WithTags("Kapitalfluss");

        /* Rotation zwischen den Anlageklassen: welcher Anteil des gesamten
           Geldumsatzes entfällt im Zeitverlauf auf Aktien, Fonds und Krypto.
           Das ist die einfachste belastbare Sicht auf Kapitalfluss. */
        g.MapGet("/rotation", async (ISqlConnectionFactory factory,
                                     string interval = BarInterval.Daily, int months = 12,
                                     CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var from = DateTime.UtcNow.AddMonths(-Math.Clamp(months, 1, 240));
            await using var conn = await factory.OpenAsync(ct);

            var rows = (await conn.QueryAsync<(DateTime TsUtc, byte AssetClass, decimal Flow)>(
                new CommandDefinition($"""
                    SELECT p.ts_utc, a.asset_class, SUM({FlowExpr}) AS flow
                      FROM dbo.price_bar p
                      JOIN dbo.asset a ON a.asset_id = p.asset_id
                     WHERE p.interval_code = @interval AND p.ts_utc >= @from
                       AND a.is_tracked = 1 AND p.volume > 0
                     GROUP BY p.ts_utc, a.asset_class
                     ORDER BY p.ts_utc
                    """, new { interval, from }, commandTimeout: 300, cancellationToken: ct))).ToList();

            var byTs = rows
                .GroupBy(r => r.TsUtc)
                .OrderBy(g2 => g2.Key)
                .Select(g2 =>
                {
                    var total = g2.Sum(r => r.Flow);
                    return new
                    {
                        tsUtc = g2.Key,
                        totalFlow = total,
                        shares = g2.ToDictionary(
                            r => ((AssetClass)r.AssetClass).ToString(),
                            r => Math.Round(FlowMetrics.Share(r.Flow, total), 5))
                    };
                })
                .ToList();

            return Results.Ok(new { interval, months, points = byTs });
        });

        /* Wohin fließt das Geld gerade? Vergleicht den Umsatzanteil der
           jüngsten Tage mit dem der davorliegenden Vergleichsperiode. Positive
           Rotation heißt: dieser Wert zieht überdurchschnittlich Kapital an. */
        g.MapGet("/leaders", async (ISqlConnectionFactory factory,
                                    string interval = BarInterval.Daily,
                                    int recentDays = 7, int baselineDays = 60, int limit = 25,
                                    CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            recentDays = Math.Clamp(recentDays, 1, 120);
            baselineDays = Math.Clamp(baselineDays, recentDays + 1, 720);

            var now = DateTime.UtcNow;
            var recentFrom = now.AddDays(-recentDays);
            var baseFrom = now.AddDays(-baselineDays);

            await using var conn = await factory.OpenAsync(ct);

            var rows = (await conn.QueryAsync<FlowRow>(new CommandDefinition($"""
                WITH f AS (
                  SELECT p.asset_id, p.ts_utc, {FlowExpr} AS flow
                    FROM dbo.price_bar p
                    JOIN dbo.asset a ON a.asset_id = p.asset_id
                   WHERE p.interval_code = @interval AND p.ts_utc >= @baseFrom
                     AND a.is_tracked = 1 AND p.volume > 0
                ),
                tot AS (
                  SELECT SUM(CASE WHEN ts_utc >= @recentFrom THEN flow ELSE 0 END) AS recent_total,
                         SUM(CASE WHEN ts_utc <  @recentFrom THEN flow ELSE 0 END) AS base_total
                    FROM f
                )
                SELECT a.asset_id AS AssetId, a.symbol AS Symbol, a.[name] AS Name,
                       a.asset_class AS AssetClass,
                       SUM(CASE WHEN f.ts_utc >= @recentFrom THEN f.flow ELSE 0 END) AS RecentFlow,
                       SUM(CASE WHEN f.ts_utc <  @recentFrom THEN f.flow ELSE 0 END) AS BaseFlow,
                       MAX(t.recent_total) AS RecentTotal,
                       MAX(t.base_total)   AS BaseTotal
                  FROM f
                  JOIN dbo.asset a ON a.asset_id = f.asset_id
                  CROSS JOIN tot t
                 GROUP BY a.asset_id, a.symbol, a.[name], a.asset_class
                HAVING SUM(CASE WHEN f.ts_utc >= @recentFrom THEN f.flow ELSE 0 END) > 0
                """, new { interval, baseFrom, recentFrom },
                commandTimeout: 300, cancellationToken: ct))).ToList();

            var scored = rows
                .Select(r =>
                {
                    var recentShare = FlowMetrics.Share(r.RecentFlow, r.RecentTotal);
                    var baseShare = FlowMetrics.Share(r.BaseFlow, r.BaseTotal);

                    return new
                    {
                        r.AssetId,
                        r.Symbol,
                        r.Name,
                        assetClass = ((AssetClass)r.AssetClass).ToString(),
                        recentFlow = r.RecentFlow,
                        recentSharePct = Math.Round(recentShare * 100, 4),
                        baseSharePct = Math.Round(baseShare * 100, 4),
                        rotationPct = Math.Round(FlowMetrics.Rotation(recentShare, baseShare) * 100, 2)
                    };
                })
                .Where(x => x.baseSharePct > 0)
                .ToList();

            return Results.Ok(new
            {
                interval,
                recentDays,
                baselineDays,
                zufluss = scored.OrderByDescending(x => x.rotationPct).Take(Math.Clamp(limit, 1, 200)),
                abfluss = scored.OrderBy(x => x.rotationPct).Take(Math.Clamp(limit, 1, 200))
            });
        });
    }

    private sealed record FlowRow(
        int AssetId, string Symbol, string? Name, byte AssetClass,
        decimal RecentFlow, decimal BaseFlow, decimal RecentTotal, decimal BaseTotal);
}
