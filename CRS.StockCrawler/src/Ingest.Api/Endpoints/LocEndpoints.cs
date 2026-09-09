using Ingest.Core.Abstractions;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Die Sprachdateien für die Oberfläche.
///
/// <para><b>Zwei Wege, und der Unterschied ist Absicht.</b> Unter
/// <c>/api/loc/</c> liegt alles hinter der Anmeldung — die Sprachdateien
/// enthalten sämtliche Erklärtexte der Anwendung samt ihrer Messwerte, und
/// CLAUDE.md hält ausdrücklich fest, dass „kein Kurs“ noch keine Harmlosigkeit
/// ist. <c>/loc-offen/</c> liefert dagegen nur die Einträge, die in der
/// Sprachdatei selbst mit <c>offen="1"</c> gekennzeichnet sind; das sind die
/// paar Wörter der Anmeldeseite. Eine Anmeldeseite, die nur auf Deutsch
/// erscheint, wäre der eine Ort, an dem die Sprachwahl nichts nützt: Man
/// bekommt sie erst zu Gesicht, nachdem man sich angemeldet hat.</para>
/// </summary>
public static class LocEndpoints
{
    public static void MapLocEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/loc");

        g.MapGet("/", (ILocService svc) => Results.Ok(svc.Sprachen()));

        g.MapGet("/{code}", (string code, ILocService svc) =>
            svc.Lade(code) is { } d
                ? Results.Ok(new { d.Code, d.Name, d.Texte })
                : Results.NotFound(new { fehler = $"Keine Sprachdatei loc.res.{code}.xml." }));

        /*  Zum Nachsehen, warum eine Antwort in einer bestimmten Sprache kam.
            Ohne diesen Weg waere die Erkennung eine Blackbox, die man nur an
            ihrer Wirkung bemerkt -- und das ist genau die Sorte Bauteil, der
            man spaeter Dinge zuschreibt, die sie nicht tut.                  */
        g.MapGet("/erkenne", (string text, ILocService svc) =>
            Results.Ok(new { text, erkannt = svc.Erkenne(text) }));

        // Neu eingelesen, ohne Neustart -- beim Uebersetzen sonst unzumutbar.
        g.MapPost("/auffrischen", (ILocService svc) =>
            Results.Ok(new { sprachen = svc.Auffrischen(), liste = svc.Sprachen() }));

        /*  Der offene Weg. Er liegt bewusst NICHT unter /api, weil die
            Anmeldepflicht dort buchstabengetreu greift -- und weil ein
            zweiter Pfad unter /api eine Ausnahme waere, die man beim Lesen
            der Sperre uebersieht.                                            */
        app.MapGet("/loc-offen", (ILocService svc) => Results.Ok(
            svc.Sprachen().Select(s => new { s.Code, s.Name })));

        app.MapGet("/loc-offen/{code}", (string code, ILocService svc) =>
            svc.Lade(code) is { } d
                ? Results.Ok(new { d.Code, d.Name, Texte = d.Offen })
                : Results.NotFound(new { fehler = "unbekannte Sprache" }));
    }
}
