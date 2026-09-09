using Ingest.Core.Abstractions;
using Ingest.Infrastructure.Services;

using Dapper;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Options;
using Ingest.Infrastructure.Options;
namespace Ingest.Api.Endpoints;

public static class ForecastEndpoints
{
    public static void MapForecastEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/forecast").WithTags("Prognose");

        g.MapPost("/run", async (IForecastService svc, CancellationToken ct) =>
            Results.Ok(await svc.RunAsync(ct)));

        g.MapPost("/run/{assetId:int}", async (IForecastService svc, int assetId, CancellationToken ct) =>
            Results.Ok(await svc.RunForAssetAsync(assetId, ct)));

        /* Auswertung der fällig gewordenen Prognosen. Hier passiert das
           Lernen: jeder bewertete Treffer verschiebt die Modellgewichte. */
        /* ------------------------------------------ Stand und Auffrischen --

           Prognostiziert wird immer vom JETZIGEN Kurs aus über alle Horizonte. Kommt
           ein neuer Bar herein, sind die bisherigen Schätzungen überholt -- nicht
           falsch, nur von gestern.

           Die alten Zeilen bleiben stehen. Genau sie sind später der Beleg: Was am
           24.08. für den 25.08. geschätzt wurde, muss am 25.08. noch dastehen, sonst
           lässt sich nichts nachprüfen. Es wird also ergänzt, nie ersetzt. */

        g.MapGet("/stand", async (ISqlConnectionFactory factory,
                                  IOptions<IngestOptions> opt,
                                  CancellationToken ct) =>
        {
            await using var conn = await factory.OpenAsync(ct);

            var (bar, gestellt, zeilen) = await conn.QuerySingleAsync<(DateTime?, DateTime?, int)>(
                new CommandDefinition(
                    """
                    SELECT (SELECT MAX(b.ts_utc)
                              FROM dbo.price_bar b
                              JOIN dbo.asset a ON a.asset_id = b.asset_id
                             WHERE a.is_tracked = 1),
                           (SELECT MAX(made_at_utc) FROM dbo.forecast),
                           (SELECT COUNT(*) FROM dbo.forecast
                             WHERE made_at_utc = (SELECT MAX(made_at_utc) FROM dbo.forecast))
                    """, cancellationToken: ct));

            var veraltet = bar is not null && (gestellt is null || bar > gestellt);

            return Results.Ok(new
            {
                neuesterBarUtc = bar,
                zuletztGestelltUtc = gestellt,
                zeilenImLetztenLauf = zeilen,
                veraltet,
                horizonte = opt.Value.EffectiveHorizons,

                hinweis = veraltet
                    ? "Seit der letzten Schätzung sind neue Kurse eingetroffen — sie wird "
                      + "neu gerechnet. Die bisherigen Werte bleiben erhalten; nur mit "
                      + "ihnen lässt sich später nachprüfen, was wir vorher gesagt haben."
                    : "Die Schätzungen sind auf dem Stand des jüngsten Kurses."
            });
        });

        g.MapPost("/auffrischen", async (IForecastService svc, ISqlConnectionFactory factory,
                                         bool nurWennVeraltet = true,
                                         CancellationToken ct = default) =>
        {
            if (nurWennVeraltet)
            {
                await using var conn = await factory.OpenAsync(ct);

                var (bar, gestellt) = await conn.QuerySingleAsync<(DateTime?, DateTime?)>(
                    new CommandDefinition(
                        """
                        SELECT (SELECT MAX(b.ts_utc)
                                  FROM dbo.price_bar b
                                  JOIN dbo.asset a ON a.asset_id = b.asset_id
                                 WHERE a.is_tracked = 1),
                               (SELECT MAX(made_at_utc) FROM dbo.forecast)
                        """, cancellationToken: ct));

                if (bar is null || (gestellt is not null && bar <= gestellt))
                    return Results.Ok(new
                    {
                        gerechnet = false,
                        hinweis = "Kein neuer Kurs seit der letzten Schätzung — "
                                + "es gibt nichts nachzurechnen."
                    });
            }

            var r = await svc.RunAsync(ct);

            return Results.Ok(new
            {
                gerechnet = true,
                ergebnis = r,
                hinweis = "Neu geschätzt vom jüngsten Kurs aus, über alle Horizonte. "
                        + "Die bisherigen Schätzungen stehen unverändert daneben."
            });
        });

        g.MapPost("/score", async (IScoringService svc, CancellationToken ct) =>
            Results.Ok(await svc.ScoreDueAsync(ct)));


        /* Walk-Forward-Backtest: Prognosen zu vergangenen Zeitpunkten, sofort
           gegen den eingetretenen Kurs bewertet. Trainiert die Gewichte, ohne
           dass man auf fällig werdende Prognosen warten muss. */
        g.MapPost("/backtest", async (IBacktestService svc, int? assetId, int steps = 40,
                                      int stride = 3, bool persist = true,
                                      CancellationToken ct = default) =>
            Results.Ok(await svc.RunAsync(assetId, Math.Clamp(steps, 1, 500),
                                          Math.Clamp(stride, 1, 50), persist, ct)));

        // Aktuelle Prognosen eines Assets über alle Horizonte.
        g.MapGet("/{assetId:int}", async (IForecastRepository repo, IAssetRepository assets,
                                          int assetId, CancellationToken ct) =>
        {
            var asset = await assets.GetAsync(assetId, ct);
            if (asset is null) return Results.NotFound(new { error = $"Asset {assetId} unbekannt" });

            var list = await repo.GetLatestAsync(assetId, ct);
            var accuracy = (await repo.GetAccuracyAsync(assetId, ct))
                .ToDictionary(a => a.HorizonHours, a => new { a.N, a.Mape, a.HitRate });

            return Results.Ok(new
            {
                asset.AssetId,
                asset.Symbol,
                asset.Name,
                forecasts = list.Select(f => new
                {
                    f.HorizonHours,
                    horizonLabel = Label(f.HorizonHours),
                    f.MadeAtUtc,
                    f.TargetTsUtc,
                    f.BaseClose,
                    f.PredictedClose,
                    changePct = f.BaseClose > 0
                        ? Math.Round(((double)f.PredictedClose - (double)f.BaseClose)
                                     / (double)f.BaseClose * 100.0, 3)
                        : 0,
                    f.PredictedReturn,
                    f.Confidence,
                    f.ModelVersion,

                    // Bisherige Treffsicherheit auf genau diesem Horizont —
                    // ohne die ist eine Prognosezahl nicht einzuordnen.
                    track = accuracy.GetValueOrDefault(f.HorizonHours)
                })
            });
        });

        // Wie gut lag das System bisher? Getrennt nach Horizont.
        /* Die kombinierte Prognose — die einzige, in der die Säulengewichte
           tatsächlich wirken.

           `/{assetId}` liefert weiterhin allein das Ensemble; das ist die
           Grundlage, gegen die man vergleicht. Wer wissen will, was seine
           Regler bewirken, fragt hier. */
        g.MapGet("/kombiniert/{assetId:int}", async (ICombinedForecastService svc,
                                                     int assetId,
                                                     string? gewichte = null,
                                                     CancellationToken ct = default) =>
        {
            var w = new Dictionary<string, int>
            {
                ["learning"] = 100, ["math"] = 0, ["flow"] = 0,
                ["deep"] = 0, ["knowledge"] = 0, ["semantic"] = 0
            };

            foreach (var teil in (gewichte ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = teil.Split(':', 2);
                if (kv.Length == 2 && int.TryParse(kv[1], out var v))
                    w[kv[0].Trim()] = Math.Clamp(v, 0, 100);
            }

            var f = await svc.BuildAsync(assetId, w, ct);

            if (f is null) return Results.NotFound(new { error = "Wert unbekannt oder ohne Kurse" });

            return Results.Ok(new
            {
                f.AssetId, f.Symbol, f.Name,
                letzterKurs = f.LastClose,
                stand = f.AsOfUtc,
                gewichte = f.Weights,

                punkte = f.Points.Select(p => new
                {
                    p.HorizonHours,
                    horizont = p.HorizonLabel,
                    ziel = p.TargetTsUtc,
                    basis = p.BaseClose,
                    kurs = p.CombinedClose,
                    p.ChangePct,

                    /* Das wirksame Gewicht ist die Summe aus Gewicht × Verdienst,
                       auf eins bezogen. Es sagt, wie viel Rückhalt hinter der
                       Zahl steht — und nicht, wie weit ein Schieber steht. */
                    wirksamesGewicht = p.EffectiveWeight,
                    urteil = p.Verdict,

                    beitraege = p.Contributions.Select(b => new
                    {
                        saeule = b.Pillar,
                        renditePct = Math.Round((Math.Exp(b.PredictedReturn) - 1) * 100, 4),
                        gewicht = b.Weight,
                        verdienst = b.Merit,
                        anteilPct = p.EffectiveWeight > 1e-9
                            ? Math.Round(b.Weight * b.Merit / (p.EffectiveWeight * 100) * 100, 1)
                            : 0,
                        grundlage = b.Basis
                    })
                }),

                hinweis = f.Note
            });
        });

        g.MapGet("/accuracy", async (IForecastRepository repo, int? assetId, CancellationToken ct) =>
        {
            var rows = await repo.GetAccuracyAsync(assetId, ct);

            return Results.Ok(rows.Select(r => new
            {
                r.HorizonHours,
                horizonLabel = Label(r.HorizonHours),
                scored = r.N,
                meanAbsPctError = Math.Round(r.Mape * 100, 4),
                hitRatePct = Math.Round(r.HitRate * 100, 2)
            }));
        });

        // Gelernte Modellgewichte — zeigt, welches Teilmodell sich durchsetzt.
        g.MapGet("/weights/{assetId:int}/{horizonHours:int}",
            async (IForecastRepository repo, int assetId, int horizonHours, CancellationToken ct) =>
            {
                var w = await repo.GetWeightsAsync(assetId, horizonHours, ct);

                return Results.Ok(w.OrderByDescending(x => x.Weight).Select(x => new
                {
                    x.ModelName,
                    weight = Math.Round(x.Weight, 4),
                    observations = x.NObs,
                    meanAbsError = Math.Round(x.MeanAbsPctErr, 6),
                    hitRatePct = Math.Round(x.HitRate * 100, 2)
                }));
            });
    }

    private static string Label(int hours) => hours switch
    {
        < 24 => $"{hours} h",
        24 => "1 Tag",
        < 168 => $"{hours / 24} Tage",
        168 => "1 Woche",
        < 720 => $"{hours / 168} Wochen",
        720 => "30 Tage",
        _ => $"{hours / 24} Tage"
    };
}
