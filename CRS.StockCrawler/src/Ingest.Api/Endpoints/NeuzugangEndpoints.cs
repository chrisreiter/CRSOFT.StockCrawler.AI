using Ingest.Core.Abstractions;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Vorankündigungen neuer Werte.
///
/// <para>Das Abholen ist ein POST, weil es nach aussen geht und die Sammlung
/// verändert; die Ansicht ein GET ohne Nebenwirkung. Der Tageslauf ruft
/// dasselbe <c>SammleAsync</c> — der Knopf ist nur der Weg, es sofort zu
/// tun.</para>
/// </summary>
public static class NeuzugangEndpoints
{
    public static void MapNeuzugangEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/neuzugang");

        g.MapGet("/", async (INeuzugangService svc, CancellationToken ct) =>
            Results.Ok(await svc.UebersichtAsync(ct)));

        g.MapPost("/sammeln", async (INeuzugangService svc, CancellationToken ct) =>
            Results.Ok(new { laeufe = await svc.SammleAsync(ct) }));
    }
}
