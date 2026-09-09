using Ingest.Core.Abstractions;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Langfristiger Vermögensaufbau.
///
/// Die einzige Ansicht dieser Anwendung, deren Grundgröße sich als tragfähig
/// erwiesen hat: Die blosse Drift, an der jedes Prognosemodell gescheitert
/// ist, IST auf lange Sicht die Rendite.
/// </summary>
public static class LangfristEndpoints
{
    public static void MapLangfristEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/langfrist");

        g.MapGet("/uebersicht", async (ILangfristService svc,
                                       int jahre = 5, int minTage = 500, int limit = 40,
                                       CancellationToken ct = default) =>
        {
            var u = await svc.BuildAsync(jahre, minTage, limit, ct);

            return Results.Ok(new
            {
                stand = u.StandUtc,
                u.VonUtc, u.BisUtc, u.SperrbereichAbUtc,
                u.WerteGeprueft,
                u.Kernaussage,
                u.Verzerrungshinweis,
                einzelwerte = u.Einzelwerte.Select(w => new
                {
                    w.Symbol, w.Name, klasse = w.Klasse.ToString(),
                    w.Handelstage, w.Jahre,
                    w.RenditeProJahr, w.SchwankungProJahr, w.GroessterRueckgang,
                    w.AnteilPositiverJahre, w.SchlechtestesJahr, w.BestesJahr,
                    w.RenditeJeSchwankung, w.RenditeJeRueckgang
                }),
                koerbe = u.Koerbe.Select(k => new
                {
                    k.Name, k.Begruendung, k.Mitglieder,
                    k.Handelstage, k.Jahre,
                    k.RenditeProJahr, k.SchwankungProJahr, k.GroessterRueckgang,
                    k.AnteilPositiverJahre, k.MittlereKorrelation, k.HoechsteKorrelation,
                    k.HoechsteKorrelationBeiAuswahl, k.Korrelationsdrift,
                    k.RenditeJeSchwankung, k.RenditeJeRueckgang,
                    k.ImSperrbereich
                }),
                anweisungen = u.Anweisungen
            });
        });
    }
}
