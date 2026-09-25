using Ingest.Infrastructure.Options;
using Ingest.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Der Hausmeister. Das Aufräumen ist ein POST, weil es löscht; der Stand ein
/// GET ohne Nebenwirkung.
///
/// <para><b>Der Probelauf ist die Voreinstellung</b> — wer <c>probe</c> nicht
/// angibt, bekommt gezählt, nicht gelöscht. Ein Löschlauf soll man ausdrücklich
/// verlangen müssen, nicht versehentlich auslösen.</para>
/// </summary>
public static class HousekeepingEndpoints
{
    public static void MapHousekeepingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/housekeeping").WithTags("Hausmeister");

        g.MapGet("/", async (IHousekeepingService svc, IOptions<HousekeepingOptions> opt,
                             int laeufe = 10, CancellationToken ct = default) =>
        {
            var stand = await svc.StandAsync(laeufe, ct);
            var o = opt.Value;
            return Results.Ok(new
            {
                stand.LetzterLauf, stand.Laeufe, stand.Groessen, stand.SummeMegabyte, stand.Hinweis,
                einstellungen = new
                {
                    o.Aktiv, o.ImTageslaufLoeschen, o.BudgetMinuten, o.Blockgroesse,
                    fristen = new
                    {
                        kurvenlaeufe = o.KurvenLaeufeTage,
                        prognose_komponenten = o.PrognoseKomponentenTage,
                        unbewertbar = o.UnbewertbareTage,
                        bewertete_prognosen = o.BewertetePrognosenTage,
                        rueckrechnung = o.RueckrechnungTage,
                        kreuzungen = o.KreuzungenTage,
                        nachrichten = o.NachrichtenTage,
                        laufprotokoll = o.LaufprotokollTage,
                        reasoning_protokoll = o.ReasoningProtokollTage,
                        autopilot_protokoll = o.AutopilotProtokollTage,
                        eigenes_protokoll = o.EigenesProtokollTage
                    }
                }
            });
        });

        /*  probe = true zaehlt nur. Die Voreinstellung steht bewusst auf true:
            Wer sich vertippt, verliert nichts.                               */
        g.MapPost("/lauf", async (IHousekeepingService svc, bool probe = true,
                                  CancellationToken ct = default) =>
        {
            var lauf = await svc.LaufeAsync(probe, probe ? "probe" : "hand", ct);
            StartEndpoints.Verwerfen();
            return Results.Ok(lauf);
        });
    }
}
