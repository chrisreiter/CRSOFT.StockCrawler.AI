using Ingest.Core.Abstractions;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Der Autopilot.
///
/// <para><b>`rangfolge` ist absichtlich ein GET ohne jede Nebenwirkung.</b> Es
/// ist dieselbe Rechnung, die der Lauf benutzt — man soll sie ansehen können,
/// bevor Geld fliesst. Wer eine Handelsstrategie nur im Nachhinein an ihren
/// Buchungen prüfen kann, prüft sie nicht.</para>
/// </summary>
public static class AutopilotEndpoints
{
    public sealed record EinstellungEingabe(
        string Depot, bool? Aktiv, int? Werte, decimal? MaxAnteil, decimal? Hysterese,
        string? Takt, string? Waehrung, bool? Nemotron, decimal? Startkapital,
        bool? Zaehlt);

    public static void MapAutopilotEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/autopilot");

        g.MapGet("/", async (IAutopilotService svc, CancellationToken ct) =>
        {
            var einst = await svc.EinstellungenAsync(ct);
            var laeufe = await svc.LaeufeAsync(null, 12, ct);
            var vergleich = await svc.VergleichAsync(ct);

            return Results.Ok(new
            {
                einstellungen = einst.Select(e => new
                {
                    e.Depot, e.Aktiv, e.Werte, e.MaxAnteil, e.Hysterese,
                    e.Takt, e.TaktName, e.TaktTage, e.Waehrung, e.Nemotron,
                    e.Startkapital, e.Zaehlt, e.UpdatedUtc
                }),
                laeufe,
                vergleich,
                takte = new[]
                {
                    new { code = "1T", name = "täglich" },
                    new { code = "1W", name = "wöchentlich" },
                    new { code = "1M", name = "monatlich" },
                    new { code = "3M", name = "alle drei Monate" },
                    new { code = "6M", name = "halbjährlich" },
                    new { code = "1J", name = "jährlich" }
                }
            });
        });

        g.MapPost("/einstellung", async (IAutopilotService svc, EinstellungEingabe e,
                                         CancellationToken ct) =>
        {
            if (e.Startkapital is < 0)
                return Results.BadRequest(new
                {
                    error = "Ein negatives Startbudget ergibt keinen Sinn."
                });

            var r = await svc.EinstellungSetzeAsync(e.Depot, e.Aktiv, e.Werte, e.MaxAnteil,
                                                    e.Hysterese, e.Takt, e.Waehrung,
                                                    e.Nemotron, e.Startkapital,
                                                    e.Zaehlt, ct);

            return r is null
                ? Results.BadRequest(new
                {
                    error = "Depot muss streng, aktiv, halten oder invers sein; der Takt "
                          + "einer von 1T, 1W, 1M, 3M, 6M, 1J."
                })
                : Results.Ok(new
                {
                    r.Depot, r.Aktiv, r.Werte, r.MaxAnteil, r.Hysterese,
                    r.Takt, r.TaktName, r.TaktTage, r.Waehrung, r.Nemotron,
                    r.Startkapital, r.Zaehlt, r.UpdatedUtc
                });
        });

        /*  Zurücksetzen. Ohne `depot` alle drei Strategien -- der Sinn eines
            Vergleichs ist, dass alle vom selben Punkt starten; einzeln
            zurückzusetzen und dann zu vergleichen wäre eine Zahl, die niemand
            deuten kann.                                                       */
        g.MapPost("/reset", async (IAutopilotService svc, string? depot,
                                   CancellationToken ct) =>
        {
            var welche = string.IsNullOrWhiteSpace(depot)
                ? new[] { "streng", "aktiv", "halten", "invers" }
                : [depot];

            var raus = new List<object>();

            foreach (var d in welche)
            {
                var r = await svc.ZuruecksetzenAsync(d, ct);

                if (r is null)
                    return Results.BadRequest(new
                    {
                        error = $"Unbekannte Strategie: {d}. Erlaubt sind streng, aktiv, halten."
                    });

                raus.Add(new { r.Depot, r.Waehrung, r.Startkapital });
            }

            return Results.Ok(new
            {
                zurueckgesetzt = raus,
                hinweis = "Buchungen, Kassenbewegungen, Läufe und Beschlüsse sind weg; "
                        + "das Startbudget steht als Einzahlung im Kassenjournal."
            });
        });

        /*  Die Rangfolge ohne jeden Handel. `depot` dient allein dazu, den
            eigenen Bestand als „gehalten" zu markieren — bewertet wird für
            alle gleich.                                                       */
        g.MapGet("/rangfolge", async (IAutopilotService svc, string depot = "aktiv",
                                      int grenze = 40, CancellationToken ct = default) =>
            Results.Ok(await svc.KennzahlenAsync(depot, grenze, ct)));

        /*  Ein Lauf von Hand. `erzwingen` übergeht den Takt — ohne das liesse
            sich bei Jahrestakt elf Monate lang nicht ausprobieren, ob
            überhaupt etwas passiert.                                          */
        g.MapPost("/lauf", async (IAutopilotService svc, string? depot, bool erzwingen = false,
                                  CancellationToken ct = default) =>
        {
            if (string.IsNullOrWhiteSpace(depot))
                return Results.Ok(new { laeufe = await svc.LaufeAlleAsync(ct) });

            try
            {
                return Results.Ok(await svc.LaufeAsync(depot, erzwingen, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        g.MapGet("/laeufe", async (IAutopilotService svc, string? depot, int grenze = 20,
                                   CancellationToken ct = default) =>
            Results.Ok(new { laeufe = await svc.LaeufeAsync(depot, grenze, ct) }));

        g.MapGet("/beschluesse/{laufId:int}", async (IAutopilotService svc, int laufId,
                                                     int grenze = 200,
                                                     CancellationToken ct = default) =>
            Results.Ok(new { beschluesse = await svc.BeschluesseAsync(laufId, grenze, ct) }));

        g.MapGet("/vergleich", async (IAutopilotService svc, CancellationToken ct) =>
            Results.Ok(await svc.VergleichAsync(ct)));
    }
}
