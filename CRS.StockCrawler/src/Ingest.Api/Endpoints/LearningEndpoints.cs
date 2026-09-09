using Ingest.Core.Abstractions;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Api.Endpoints;

public static class LearningEndpoints
{
    public static void MapLearningEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/learning").WithTags("Lernen");

        /* Vollständiger Walk-Forward: Tag für Tag beziehungsweise Stunde für
           Stunde durch die gesamte Historie. Bewertet wird erst, wenn der
           Zielzeitpunkt im Durchlauf erreicht ist — sonst kennten die Gewichte
           Ergebnisse aus der Zukunft. */
        g.MapPost("/walkforward", async (IWalkForwardService svc,
                                         string? label, int buckets = 40,
                                         int pairRefreshEvery = 60,
                                         string? persistHorizons = null,
                                         CancellationToken ct = default) =>
        {
            /* persistHorizons bestimmt, für welche Horizonte der Verlauf Zeile
               für Zeile gespeichert wird. Ohne Angabe nur Kennzahlen — ein
               voller Tagesdurchlauf über 25 Jahre erzeugt je Horizont rund
               1,1 Millionen Zeilen. */
            var horizons = string.IsNullOrWhiteSpace(persistHorizons)
                ? null
                : persistHorizons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .Select(x => int.TryParse(x, out var v) ? v : -1)
                                 .Where(v => v > 0)
                                 .ToArray();

            return Results.Ok(await svc.RunAsync(label, null, buckets, pairRefreshEvery, horizons, ct));
        });

        // Gespeicherten Verlauf verwerfen.
        g.MapDelete("/track", async (IForecastTrackRepository repo, string? label,
                                     CancellationToken ct = default) =>
            Results.Ok(new { deleted = await repo.ClearAsync(label, ct) }));

        g.MapGet("/epochs", async (ILearningRepository repo, string? label, int limit = 50,
                                   CancellationToken ct = default) =>
            Results.Ok(await repo.GetEpochsAsync(label, Math.Clamp(limit, 1, 500), ct)));

        // Lernkurve einer Epoche: wird die Prognose über den Verlauf besser?
        g.MapGet("/curve/{epochId:int}", async (ILearningRepository repo, int epochId,
                                                CancellationToken ct = default) =>
        {
            var (curve, models) = await repo.GetCurveAsync(epochId, ct);
            if (curve.Count == 0 && models.Count == 0)
                return Results.NotFound(new { error = $"Keine Lernkurve zu Epoche {epochId}" });

            return Results.Ok(new { epochId, curve, models });
        });
    }
}
