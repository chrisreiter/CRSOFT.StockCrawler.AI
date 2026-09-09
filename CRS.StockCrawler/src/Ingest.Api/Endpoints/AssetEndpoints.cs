using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Options;
using Ingest.Infrastructure.Providers;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Options;

namespace Ingest.Api.Endpoints;

public sealed record TrackRequest(int[] AssetIds, bool Tracked);

public static class AssetEndpoints
{
    public static void MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/assets").WithTags("Assets");

        // Die Gesamtliste, aus der im Backend ausgewählt wird, was getrackt wird.
        g.MapGet("/", async (IAssetRepository repo,
                             string? cls, bool? tracked, string? search, int limit = 500,
                             string? sector = null, string? country = null,
                             CancellationToken ct = default) =>
        {
            AssetClass? parsed = null;
            if (!string.IsNullOrWhiteSpace(cls))
            {
                if (!Enum.TryParse<AssetClass>(cls, ignoreCase: true, out var v))
                    return Results.BadRequest(new { error = $"Unbekannte Anlageklasse: {cls}" });
                parsed = v;
            }

            var list = await repo.ListAsync(parsed, tracked, search, Math.Clamp(limit, 1, 5000),
                                            sector, country, ct);
            return Results.Ok(list);
        });

        g.MapGet("/tracked", async (IAssetRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.GetTrackedAsync(ct)));

        g.MapPost("/track", async (IAssetRepository repo, TrackRequest req, CancellationToken ct) =>
        {
            if (req.AssetIds.Length == 0)
                return Results.BadRequest(new { error = "assetIds ist leer" });

            var n = await repo.SetTrackedAsync(req.AssetIds, req.Tracked, ct);
            return Results.Ok(new { updated = n, tracked = req.Tracked });
        });

        // Universum neu ermitteln und die größten N je Klasse aktivieren.
        g.MapPost("/universe/refresh", async (IUniverseService svc, CancellationToken ct) =>
            Results.Ok(await svc.RefreshAllAsync(ct)));

        g.MapPost("/universe/refresh/{cls}", async (IUniverseService svc, IOptions<IngestOptions> opt,
                                                    string cls, int? top, bool autoTrack = true,
                                                    CancellationToken ct = default) =>
        {
            if (!Enum.TryParse<AssetClass>(cls, ignoreCase: true, out var parsed))
                return Results.BadRequest(new { error = $"Unbekannte Anlageklasse: {cls}" });

            var limit = top ?? parsed switch
            {
                AssetClass.Stock => opt.Value.TopStocks,
                AssetClass.Etf => opt.Value.TopEtfs,
                AssetClass.Crypto => opt.Value.TopCrypto,
                _ => 100
            };

            return Results.Ok(await svc.RefreshAsync(parsed, limit, autoTrack, ct));
        });

        /* Wartung: Werte aussortieren, deren Kursreihe unmögliche Sprünge
           enthält. Ursache ist fast immer, dass das Symbol auf ein anderes
           Papier zeigt — beobachtet wurden Tagessprünge um den Faktor 1254.
           Solche Reihen ruinieren Korrelationen und Prognosefehler, deshalb
           werden ihre Bars gelöscht und der Wert aus der Verfolgung genommen. */
        g.MapPost("/quarantine", async (ISqlConnectionFactory factory,
                                        double maxFactor = 10.0, int minJumps = 2,
                                        bool apply = true, CancellationToken ct = default) =>
        {
            await using var conn = await factory.OpenAsync(ct);

            var suspects = (await conn.QueryAsync<(int AssetId, string Symbol, string? Name,
                                                   int Jumps, double WorstFactor)>(
                new CommandDefinition("""
                    WITH d AS (
                      SELECT p.asset_id, p.[close],
                             LAG(p.[close]) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc) AS prev
                        FROM dbo.price_bar p
                       WHERE p.interval_code = '1d'
                    )
                    SELECT a.asset_id, a.symbol, a.[name],
                           COUNT(*) AS jumps,
                           MAX(CASE WHEN d.[close] > d.prev
                                    THEN d.[close] / d.prev ELSE d.prev / d.[close] END) AS worst
                      FROM d
                      JOIN dbo.asset a ON a.asset_id = d.asset_id
                     WHERE d.prev > 0 AND d.[close] > 0
                       AND (d.[close] / d.prev > @maxFactor OR d.prev / d.[close] > @maxFactor)
                     GROUP BY a.asset_id, a.symbol, a.[name]
                    HAVING COUNT(*) >= @minJumps
                     ORDER BY jumps DESC
                    """, new { maxFactor = (decimal)maxFactor, minJumps },
                    commandTimeout: 300, cancellationToken: ct))).ToList();

            /* Zusätzlich Reihen ohne Informationsgehalt aussortieren.

               Gemeint sind vor allem Stablecoins: sie liegen bei 1 $ und
               liefern für jede Korrelation eine Nullreihe, kreuzen dabei aber
               jede andere Kurve ununterbrochen und überschwemmen die
               Kreuzungssuche.

               Eine reine Volatilitätsschwelle trennt sie NICHT von legitimen
               Werten: gemessen liegt USDD bei 0,00143 relativer Streuung, der
               Anleihe-ETF PULS bei 0,00154 — praktisch gleich. Kurzlaufende
               Anleihe-ETFs bewegen sich eben auch kaum, sind aber echte Werte.

               Was die beiden trennt, ist der Kursanker: Stablecoins liegen
               konstruktionsbedingt bei 1 $, Anleihe-ETFs bei 30–60 $. Deshalb
               beide Kriterien zusammen. Zusätzlich fallen völlig konstante oder
               auf null gerundete Reihen heraus — die sind schlicht defekt. */
            var flat = (await conn.QueryAsync<(int AssetId, string Symbol, string? Name,
                                               int Jumps, double WorstFactor)>(
                new CommandDefinition("""
                    WITH s AS (
                      SELECT p.asset_id,
                             AVG(p.[close])                              AS avg_close,
                             STDEV(p.[close]) / NULLIF(AVG(p.[close]),0) AS rel_std
                        FROM dbo.price_bar p
                       WHERE p.interval_code = '1d'
                       GROUP BY p.asset_id
                    )
                    SELECT a.asset_id, a.symbol, a.[name], 0 AS jumps, 1.0 AS worst
                      FROM dbo.asset a
                      JOIN s ON s.asset_id = a.asset_id
                     WHERE a.is_tracked = 1
                       AND (
                             -- an 1 $ verankert und praktisch unbewegt: Stablecoin
                             (s.avg_close BETWEEN 0.9 AND 1.1 AND s.rel_std < 0.02)
                             -- oder gar keine Bewegung / auf null gerundet: defekt
                          OR s.rel_std IS NULL
                          OR s.rel_std < 0.00001
                          OR s.avg_close <= 0.00000001
                           )
                    """, commandTimeout: 300, cancellationToken: ct))).ToList();

            /* Dritter Test: stimmt der Kurs überhaupt mit dem überein, was der
               Universum-Provider für dieses Papier meldet?

               Beobachtet an USDG-USD: CoinGecko führt es als Stablecoin bei
               1 $, die Yahoo-Reihe schwankt zwischen 0,91 $ und 15,22 $. Der
               Sprungfilter greift dort nicht, weil die Reihe allmählich
               davonläuft statt zu springen — der Vergleich mit dem
               Referenzpreis deckt es dagegen sofort auf. */
            var mismatch = (await conn.QueryAsync<(int AssetId, string Symbol, string? Name,
                                                   int Jumps, double WorstFactor)>(
                new CommandDefinition("""
                    WITH last_bar AS (
                      SELECT p.asset_id, p.[close],
                             ROW_NUMBER() OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc DESC) AS rn
                        FROM dbo.price_bar p
                       WHERE p.interval_code = '1d'
                    )
                    SELECT a.asset_id, a.symbol, a.[name], 0 AS jumps,
                           CAST(CASE WHEN b.[close] > a.reference_price
                                     THEN b.[close] / a.reference_price
                                     ELSE a.reference_price / b.[close] END AS FLOAT) AS worst
                      FROM dbo.asset a
                      JOIN last_bar b ON b.asset_id = a.asset_id AND b.rn = 1
                     WHERE a.is_tracked = 1
                       AND a.reference_price > 0 AND b.[close] > 0
                       AND (b.[close] / a.reference_price > @tol
                         OR a.reference_price / b.[close] > @tol)
                    """, new { tol = 3.0m }, commandTimeout: 300, cancellationToken: ct))).ToList();

            // Alle Befunde zusammenführen, ohne Dubletten.
            var known = suspects.Select(s => s.AssetId).ToHashSet();
            suspects.AddRange(flat.Where(f => known.Add(f.AssetId)));
            suspects.AddRange(mismatch.Where(m => known.Add(m.AssetId)));

            if (!apply || suspects.Count == 0)
            {
                return Results.Ok(new
                {
                    applied = false,
                    found = suspects.Count,
                    assets = suspects.Select(s => new { s.AssetId, s.Symbol, s.Name, s.Jumps, s.WorstFactor })
                });
            }

            var ids = suspects.Select(s => s.AssetId).ToArray();

            // Abhängige Daten zuerst — Fremdschlüssel auf price_bar und forecast.
            await conn.ExecuteAsync(new CommandDefinition("""
                DELETE FROM dbo.forecast_component
                 WHERE forecast_id IN (SELECT forecast_id FROM dbo.forecast WHERE asset_id IN @ids);
                DELETE FROM dbo.forecast_score
                 WHERE forecast_id IN (SELECT forecast_id FROM dbo.forecast WHERE asset_id IN @ids);
                DELETE FROM dbo.forecast      WHERE asset_id IN @ids;
                DELETE FROM dbo.model_weight  WHERE asset_id IN @ids;
                DELETE FROM dbo.pair_stat     WHERE asset_id_a IN @ids OR asset_id_b IN @ids;
                DELETE FROM dbo.crossing      WHERE asset_id_a IN @ids OR asset_id_b IN @ids;
                DELETE FROM dbo.price_bar     WHERE asset_id IN @ids;
                UPDATE dbo.asset SET is_tracked = 0, updated_utc = SYSUTCDATETIME()
                 WHERE asset_id IN @ids;
                """, new { ids }, commandTimeout: 600, cancellationToken: ct));

            return Results.Ok(new
            {
                applied = true,
                quarantined = suspects.Count,
                assets = suspects.Select(s => new { s.AssetId, s.Symbol, s.Name, s.Jumps, s.WorstFactor })
            });
        });

        /* Branchen zuordnen. Yahoos Kursdatensatz kennt kein Branchenfeld —
           die Zuordnung entsteht über elf eigene Screener, je einer pro
           Branche. Auflösung ist damit die Branchenebene; feineres wie
           „Rüstung“ gibt es dort nicht. */
        g.MapPost("/sectors/refresh", async (YahooSectorProvider provider, IAssetRepository repo,
                                             ISqlConnectionFactory factory, int perSector = 500,
                                             CancellationToken ct = default) =>
        {
            var map = await provider.GetSectorsAsync(perSector, ct);
            if (map.Count == 0)
                return Results.Problem("Keine Branchendaten erhalten.");

            await using var conn = await factory.OpenAsync(ct);
            var updated = 0;

            foreach (var (symbol, info) in map)
            {
                updated += await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE dbo.asset
                       SET sector = @sector, country = COALESCE(@country, country),
                           updated_utc = SYSUTCDATETIME()
                     WHERE symbol = @symbol AND asset_class IN (0, 1)
                    """, new { symbol, sector = info.Sector, country = info.Country },
                    cancellationToken: ct));
            }

            var byCat = await conn.QueryAsync<(string? Sector, int N)>(new CommandDefinition(
                "SELECT sector, COUNT(*) FROM dbo.asset WHERE is_tracked = 1 GROUP BY sector",
                cancellationToken: ct));

            return Results.Ok(new
            {
                erkannt = map.Count,
                zugeordnet = updated,
                verteilung = byCat.OrderByDescending(x => x.N)
                                  .Select(x => new { sector = x.Sector ?? "(ohne)", count = x.N })
            });
        });

        // Welche Branchen und Länder gibt es im Bestand?
        g.MapGet("/facets", async (ISqlConnectionFactory factory, CancellationToken ct) =>
        {
            await using var conn = await factory.OpenAsync(ct);

            var sectors = await conn.QueryAsync<(string Sector, int N)>(new CommandDefinition("""
                SELECT sector, COUNT(*) AS n FROM dbo.asset
                 WHERE sector IS NOT NULL GROUP BY sector ORDER BY n DESC
                """, cancellationToken: ct));

            var countries = await conn.QueryAsync<(string Country, int N)>(new CommandDefinition("""
                SELECT country, COUNT(*) AS n FROM dbo.asset
                 WHERE country IS NOT NULL GROUP BY country ORDER BY n DESC
                """, cancellationToken: ct));

            return Results.Ok(new
            {
                sectors = sectors.Select(x => new { name = x.Sector, count = x.N }),
                countries = countries.Select(x => new { name = x.Country, count = x.N })
            });
        });

        // Top-N einer Klasse (erneut) aktivieren, ohne das Universum neu zu laden.
        g.MapPost("/track-top/{cls}", async (IAssetRepository repo, string cls, int top = 100,
                                             CancellationToken ct = default) =>
        {
            if (!Enum.TryParse<AssetClass>(cls, ignoreCase: true, out var parsed))
                return Results.BadRequest(new { error = $"Unbekannte Anlageklasse: {cls}" });

            var n = await repo.TrackTopByMarketCapAsync(parsed, top, ct);
            return Results.Ok(new { activated = n, assetClass = parsed.ToString(), top });
        });
    }
}
