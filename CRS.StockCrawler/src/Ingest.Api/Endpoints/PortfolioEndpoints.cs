using Ingest.Core.Abstractions;
using Ingest.Core.Enums;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Bestand und Tauschvorschläge.
///
/// Der Bestand ist die einzige Stelle in dieser Anwendung, an der der Nutzer
/// eigene Daten einträgt. Deshalb liegt er in einer eigenen Tabelle und nicht
/// im Oberflächenzustand: Filtereinstellungen darf man nach einem halben Jahr
/// wegräumen, Positionen nicht.
/// </summary>
public static class PortfolioEndpoints
{
    public sealed record BestandEingabe(
        string Symbol, decimal Kapital, string? Waehrung,
        decimal? Einstand, DateTime? GekauftUtc, string? Notiz);

    public static void MapPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/bestand");

        g.MapGet("/", async (IPortfolioService svc, CancellationToken ct) =>
        {
            var list = await svc.ListAsync(ct);

            return Results.Ok(new
            {
                kapitalGesamt = list.Sum(h => h.Kapital),
                positionen = list.Select(h => new
                {
                    h.AssetId, h.Symbol, h.Name, klasse = h.Klasse.ToString(),
                    h.Kapital, h.Waehrung, h.Einstand, h.GekauftUtc, h.Notiz,
                    h.Kurs, h.KursUtc, h.Buchstand,
                    h.UpdatedUtc
                })
            });
        });

        g.MapPost("/", async (IPortfolioService svc, BestandEingabe e, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(e.Symbol))
                return Results.BadRequest(new { error = "Symbol fehlt." });

            if (e.Kapital <= 0)
                return Results.BadRequest(new { error = "Kapital muss größer als null sein." });

            var h = await svc.UpsertAsync(e.Symbol, e.Kapital, e.Waehrung ?? "USD",
                                          e.Einstand, e.GekauftUtc, e.Notiz, ct);

            return h is null
                ? Results.BadRequest(new
                {
                    error = $"Kein Wert mit dem Symbol {e.Symbol}. Die Schreibweise muss der "
                          + "verfolgten entsprechen — Krypto trägt das Suffix -USD."
                })
                : Results.Ok(new { h.AssetId, h.Symbol, h.Kapital, h.Waehrung });
        });

        g.MapDelete("/{assetId:int}", async (IPortfolioService svc, int assetId,
                                             CancellationToken ct) =>
            await svc.DeleteAsync(assetId, ct)
                ? Results.Ok(new { assetId, entfernt = true })
                : Results.NotFound(new { error = "Keine Position mit dieser Kennung." }));

        /* Die Tauschvorschläge.

           `jeBestand` deckelt, wie viele Ziele je Position genannt werden. Wer
           zwanzig Ziele für eine Position sieht, sucht sich das schönste aus —
           und genau das ist die Art, wie man sich mit einer Rangliste selbst
           betrügt. Drei ist die Voreinstellung. */
        g.MapGet("/tausch", async (IPortfolioService svc,
                                   string interval = BarInterval.Daily,
                                   int tage = 30, int haltedauer = 20,
                                   int jeBestand = 3, bool nurBewaehrt = false,
                                   CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var u = await svc.SwapsAsync(interval, tage, haltedauer, jeBestand, nurBewaehrt, ct);

            return Results.Ok(new
            {
                stand = u.StandUtc,
                u.Intervall, u.Tage, u.Haltedauer,
                u.KapitalGesamt, u.VorschlaegeGesamt, u.DavonBewaehrt,
                u.Rundlaufkosten, u.Kostenhinweis,
                u.Hinweis,
                positionen = u.Positionen.Select(pos => new
                {
                    pos.Symbol, pos.Name, klasse = pos.Klasse.ToString(),
                    pos.Kapital, pos.Waehrung, pos.Lage,
                    vorschlaege = pos.Vorschlaege.Select(v => new
                    {
                        ziel = new { symbol = v.SymbolZiel, name = v.NameZiel, klasse = v.KlasseZiel.ToString() },
                        v.Kreuzung, v.TageSeither,
                        v.RenditeBestand, v.RenditeZiel, v.PaargewinnSeither,
                        v.Kapital, v.WaereGeworden, v.WaereGewordenNachKosten,
                        v.Bewaehrt,
                        historie = new
                        {
                            kreuzungen = v.HistorischeKreuzungen,
                            trefferquote = v.HistorischeTrefferquote,
                            mittelgewinn = v.HistorischerMittelgewinn,
                            mittelgewinnNachKosten = v.MittelgewinnNachKosten
                        }
                    })
                })
            });
        });
    }
}
