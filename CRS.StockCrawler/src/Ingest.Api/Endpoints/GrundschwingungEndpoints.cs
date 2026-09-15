using Ingest.Core.Abstractions;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Der Katalog der Grundschwingungen. Der Lauf ist ein POST, weil er den
/// Katalog ersetzt; die Ansichten sind GETs ohne Nebenwirkung. Der Tageslauf
/// ruft dasselbe <c>LaufeAsync</c> — der Knopf ist nur der Weg, es sofort zu tun.
/// </summary>
public static class GrundschwingungEndpoints
{
    public static void MapGrundschwingungEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/grundschwingungen").WithTags("Grundschwingungen");

        g.MapGet("/", async (IGrundschwingungService svc, string interval = "1d", CancellationToken ct = default) =>
            Results.Ok(await svc.UebersichtAsync(interval, ct)));

        g.MapGet("/klasse/{classId:int}", async (IGrundschwingungService svc, int classId, CancellationToken ct) =>
            await svc.KlasseAsync(classId, ct) is { } k ? Results.Ok(k)
                : Results.NotFound(new { error = $"Keine Klasse {classId}." }));

        g.MapGet("/wert/{assetId:int}", async (IGrundschwingungService svc, int assetId, CancellationToken ct) =>
            await svc.WertAsync(assetId, ct) is { } w ? Results.Ok(w)
                : Results.NotFound(new { error = $"Kein Wert {assetId}." }));

        g.MapPost("/lauf", async (IGrundschwingungService svc, string interval = "1d", CancellationToken ct = default) =>
            Results.Ok(await svc.LaufeAsync(interval, ct)));
    }
}
