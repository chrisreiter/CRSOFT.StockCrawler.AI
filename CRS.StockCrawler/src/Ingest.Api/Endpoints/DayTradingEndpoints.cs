using Ingest.Core.Abstractions;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Day Trading — die Seite, die zuerst die Frage vor der Frage beantwortet:
/// Trägt der Markt heute überhaupt einen Handel innerhalb des Tages, nachdem
/// die Gebühren abgezogen sind?
/// </summary>
public static class DayTradingEndpoints
{
    public static void MapDayTradingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/daytrading");

        g.MapGet("/heute", async (IDayTradingService svc, int stunden = 720,
                                  CancellationToken ct = default) =>
        {
            var t = await svc.BuildAsync(stunden, ct);

            return Results.Ok(new
            {
                stand = t.StandUtc,
                t.Rundlaufkosten,
                t.Kostenhinweis,
                t.Kernaussage,
                fenster = t.Fenster.Select(f => new
                {
                    klasse = f.KlasseName,
                    f.JuengsteBar, f.StundenAlt, f.VermutlichOffen, f.Bemerkung
                }),
                beweglichkeit = t.Beweglichkeit.Select(b => new
                {
                    klasse = b.KlasseName,
                    b.Bars, b.Werte,
                    b.MittlereBewegung, b.MedianBewegung,
                    b.AnteilUeberKosten, b.AnteilUeberDoppeltenKosten
                }),
                bewegungen = t.Bewegungen.Select(b => new
                {
                    b.Symbol, b.Name, klasse = b.Klasse.ToString(),
                    b.Kurs, b.TsUtc,
                    b.VeraenderungStunde, b.VeraenderungSechsStunden, b.Spannweite
                }),
                guete = t.Guete.Select(x => new
                {
                    x.Quelle, x.HorizontStunden,
                    x.Fehlerverhaeltnis, x.Trefferquote,
                    x.DriftFehlerverhaeltnis, x.DriftTrefferquote,
                    x.Traegt, x.Urteil
                }),
                anweisungen = t.Anweisungen,
                literatur = t.Literatur.Select(l => new
                {
                    l.Titel, l.Herkunft, l.Auszug, l.Fundstelle, l.Score
                })
            });
        });
    }
}
