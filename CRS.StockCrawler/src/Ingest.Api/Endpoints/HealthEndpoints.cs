using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Options;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Options;
using Dapper;

namespace Ingest.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }))
           .WithTags("Health");

        app.MapGet("/api/health/runs", async (IIngestRunRepository repo, int last = 50,
                                              CancellationToken ct = default) =>
            Results.Ok(await repo.GetRecentAsync(Math.Clamp(last, 1, 500), ct)))
           .WithTags("Health");

        // Welche Provider sind einsatzbereit? Beantwortet die Frage
        // "warum kommen keine Daten" ohne Log-Wühlen.
        app.MapGet("/api/health/providers", (IEnumerable<IMarketDataProvider> providers,
                                             IEnumerable<IUniverseProvider> universe) =>
            Results.Ok(new
            {
                marketData = providers.Select(p => new
                {
                    provider = p.Id.ToString(),
                    configured = p.IsConfigured,
                    classes = p.SupportedClasses.Select(c => c.ToString()),
                    intervals = BarInterval.All.Where(p.SupportsInterval)
                }),
                universe = universe.Select(p => new
                {
                    provider = p.Id.ToString(),
                    configured = p.IsConfigured
                })
            }))
           .WithTags("Health");

        /* Überblick über den Datenbestand: wie viele Assets, wie viele Bars,
           wie aktuell. Das ist die Seite, die man morgens zuerst aufmacht. */
        app.MapGet("/api/health/stats", async (ISqlConnectionFactory factory,
                                               IOptions<IngestOptions> opt,
                                               CancellationToken ct) =>
        {
            await using var conn = await factory.OpenAsync(ct);

            var byClass = await conn.QueryAsync<(byte AssetClass, int Total, int Tracked)>(
                new CommandDefinition("""
                    SELECT asset_class,
                           COUNT(*) AS total,
                           SUM(CASE WHEN is_tracked = 1 THEN 1 ELSE 0 END) AS tracked
                      FROM dbo.asset
                     GROUP BY asset_class
                    """, cancellationToken: ct));

            var barStats = await conn.QueryAsync<(string Interval, long Bars, int Assets,
                                                   DateTime? Oldest, DateTime? Newest)>(
                new CommandDefinition("""
                    SELECT interval_code,
                           COUNT_BIG(*)            AS bars,
                           COUNT(DISTINCT asset_id) AS assets,
                           MIN(ts_utc)             AS oldest,
                           MAX(ts_utc)             AS newest
                      FROM dbo.price_bar
                     GROUP BY interval_code
                    """, commandTimeout: 120, cancellationToken: ct));

            var forecasts = await conn.QuerySingleAsync<(int Total, int Scored, int Pending)>(
                new CommandDefinition("""
                    SELECT COUNT(*) AS total,
                           (SELECT COUNT(*) FROM dbo.forecast_score) AS scored,
                           (SELECT COUNT(*) FROM dbo.forecast f
                             LEFT JOIN dbo.forecast_score s ON s.forecast_id = f.forecast_id
                            WHERE s.forecast_id IS NULL) AS pending
                      FROM dbo.forecast
                    """, cancellationToken: ct));

            var pairs = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT COUNT(*) FROM dbo.pair_stat", cancellationToken: ct));

            var crossings = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT COUNT(*) FROM dbo.crossing", cancellationToken: ct));

            return Results.Ok(new
            {
                assets = byClass.Select(r => new
                {
                    assetClass = ((AssetClass)r.AssetClass).ToString(),
                    r.Total,
                    r.Tracked
                }),
                bars = barStats.Select(r => new { r.Interval, r.Bars, r.Assets, r.Oldest, r.Newest }),
                forecasts = new { forecasts.Total, forecasts.Scored, forecasts.Pending },
                analysis = new { pairs, crossings },
                config = new
                {
                    Horizons = opt.Value.EffectiveHorizons,
                    opt.Value.HistoryMonths,
                    opt.Value.HourlyHistoryMonths,
                    opt.Value.HourlyCronUtc,
                    opt.Value.DailyCronUtc
                }
            });
        }).WithTags("Health");
    }
}
