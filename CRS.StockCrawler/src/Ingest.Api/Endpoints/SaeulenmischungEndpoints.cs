using Dapper;
using Ingest.Infrastructure.Options;
using Ingest.Infrastructure.Repositories;
using Ingest.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Wie die Säulen in die Prognose eingehen — und was sie gemessen wert sind.
///
/// <para><b>Die Regel dahinter.</b> Der Regler in der Oberfläche bestimmt, WIE VIEL von
/// etwas Brauchbarem einfliesst. Ob etwas brauchbar ist, entscheidet er nicht — das steht
/// in <c>pillar_skill</c> und kommt aus einer Messung gegen eingetroffene Kurse. Eine Säule
/// ohne nachgewiesenen Vorsprung bekommt null und bewegt nichts, auch bei Regler auf
/// hundert.</para>
/// </summary>
public static class SaeulenmischungEndpoints
{
    public static void MapSaeulenmischungEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/saeulen").WithTags("Säulenmischung");

        /* ------------------------------------------------- Kalibrierung -- */

        g.MapPost("/kalibrieren", async (ISemantikKalibrierung svc,
                                         IOptions<IngestOptions> opt,
                                         int tage = 120, CancellationToken ct = default) =>
        {
            var e = await svc.LaufeAsync(opt.Value.EffectiveHorizons, tage, ct);

            return Results.Ok(new
            {
                horizonte = e,

                bilanz = e.Count == 0
                    ? "Keine Meldungen im Zeitraum — nichts zu kalibrieren."
                    : e.Any(x => x.Skill > 0)
                        ? $"{e.Count(x => x.Skill > 0)} von {e.Count} Horizonten mit "
                          + "nachgewiesenem Zusammenhang."
                        : "Kein Horizont überschreitet die Zufallsschwelle. Die "
                          + "Semantik-Säule trägt damit nichts zur Prognose bei — sie wird "
                          + "erkannt und angezeigt, aber ihr Anteil ist null.",

                hinweis =
                    "Gemessen wird die Stimmung eines Tages gegen die DANACH eingetroffene "
                    + "Rendite. Die Schwelle ist 2/√n — darunter ist eine Korrelation "
                    + "Rauschen. Zum Vergleich: GDELT-Nachrichtenton gegen SPY lag "
                    + "prognostisch bei 0,0419 gegen eine Schwelle von 0,077, gleichzeitig "
                    + "dagegen bei 0,1365. Nachrichten hängen mit heute zusammen, nicht "
                    + "mit morgen."
            });
        });

        /* ------------------------------------------------------- Einsicht -- */

        g.MapGet("/skill", async (ISqlConnectionFactory factory, CancellationToken ct) =>
        {
            await using var conn = await factory.OpenAsync(ct);

            var zeilen = await conn.QueryAsync(new CommandDefinition(
                """
                SELECT pillar, horizon_hours, skill, n_obs, detail, measured_utc
                  FROM dbo.pillar_skill
                 ORDER BY pillar, horizon_hours
                """, cancellationToken: ct));

            return Results.Ok(new
            {
                gemessen = zeilen,
                hinweis = "skill = gemessener Vorsprung, 0 bis 1. Null heisst: trägt nichts "
                        + "bei. Der Regler in der Oberfläche kann das nicht überschreiben."
            });
        });

        /* Die Mischung einer konkreten Prognose — woraus sie entstanden ist. */
        g.MapGet("/mischung/{symbol}", async (ISqlConnectionFactory factory, string symbol,
                                              int horizont = 24, CancellationToken ct = default) =>
        {
            await using var conn = await factory.OpenAsync(ct);

            var z = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
                """
                SELECT TOP 1 f.made_at_utc, f.target_ts_utc, f.base_close,
                       f.predicted_close, f.combined_close,
                       f.predicted_return, f.combined_return, f.pillar_mix
                  FROM dbo.forecast f
                  JOIN dbo.asset a ON a.asset_id = f.asset_id
                 WHERE a.symbol = @symbol AND f.horizon_hours = @h
                 ORDER BY f.made_at_utc DESC
                """, new { symbol, h = horizont }, cancellationToken: ct));

            if (z is null)
                return Results.NotFound(new { error = $"Keine Prognose für {symbol} über {horizont} h." });

            return Results.Ok(new
            {
                prognose = z,
                hinweis = "predicted_close ist die erste Säule allein — an ihr hängt die "
                        + "Rückkopplung. combined_close ist die Mischung; pillar_mix sagt, "
                        + "welche Säule mit welchem Anteil und warum."
            });
        });
    }
}
