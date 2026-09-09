using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Merkmalsexport und Modellanbindung — Stufen 2 und 3.
/// </summary>
public static class ModelEndpoints
{
    public static void MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/model").WithTags("Modell");

        /* Schreibt die Querschnitts-Merkmalsmatrix als CSV. Das ist die
           Schnittstelle zum Training: eine Zeile je Zeitpunkt und Wert, mit
           Markt-, Klassen- und Nachbarschaftsmerkmalen. */
        g.MapPost("/export", async (IFeatureExportService svc,
                                    string interval = BarInterval.Daily,
                                    string? horizons = null, string? path = null,
                                    CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            // Horizonte in Bars dieser Auflösung.
            var bars = string.IsNullOrWhiteSpace(horizons)
                ? (interval == BarInterval.Hourly ? new[] { 1, 4, 24, 72, 168 } : [1, 3, 7, 30])
                : horizons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Select(s => int.TryParse(s, out var v) ? v : -1)
                          .Where(v => v > 0)
                          .Distinct()
                          .OrderBy(v => v)
                          .ToArray();

            if (bars.Length == 0)
                return Results.BadRequest(new { error = "Keine gültigen Horizonte angegeben" });

            return Results.Ok(await svc.ExportAsync(interval, bars, path, ct));
        });

        // Welche Merkmale erzeugt der Export, in welcher Reihenfolge?
        g.MapGet("/features", () => Results.Ok(new
        {
            version = FeatureSet.Version,
            count = FeatureSet.Count,
            minHistoryBars = FeatureSet.MinHistoryBars,
            neighbourCount = FeatureSet.NeighbourCount,
            names = FeatureSet.Names
        }));
    }
}
