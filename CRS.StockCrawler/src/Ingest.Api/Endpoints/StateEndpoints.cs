using System.Text.Json;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Gemerkter Oberflächenzustand: welcher Reiter offen war, was im Filter stand,
/// welche Werte ausgewählt waren.
///
/// Erkannt wird der Besucher über ein Cookie mit einem Sitzungsschlüssel. Das
/// ist bewusst das einzige, was im Cookie steht — der Zustand selbst liegt in
/// der Datenbank. So bleibt er über Neustarts erhalten, lässt sich später an
/// einen Benutzer binden und ist nicht auf die vier Kilobyte begrenzt, die ein
/// Cookie fasst.
/// </summary>
public static class StateEndpoints
{
    private const string CookieName = "sc_session";

    /* Bereiche, die gespeichert werden dürfen. Eine feste Liste, damit über
       diese Schnittstelle nicht beliebig viele Zeilen angelegt werden können —
       sie ist offen erreichbar und schreibt in die Datenbank. */
    private static readonly HashSet<string> KnownAreas =
        new(StringComparer.OrdinalIgnoreCase) { "charts", "nav", "pillars", "analysis", "forecast" };

    /* Obergrenze je Bereich. Der Kursfilter mit vierzig ausgewählten Werten
       liegt bei etwa vier Kilobyte; alles darüber ist keine Einstellung mehr. */
    private const int MaxPayloadBytes = 64 * 1024;

    public static void MapStateEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/state").WithTags("Sitzung");

        /* Alles Gemerkte auf einmal. Die Oberfläche holt es genau einmal beim
           Laden — einzelne Abfragen je Bereich würden den Seitenaufbau
           verzögern, ohne etwas zu gewinnen. */
        g.MapGet("/", async (HttpContext http, IAppStateRepository states,
                             CancellationToken ct) =>
        {
            var key = ResolveSession(http);
            await states.TouchSessionAsync(key, ct);

            var userId = await states.GetSessionUserAsync(key, ct);
            var entries = await states.GetAsync(key, userId, ct);

            var areas = new Dictionary<string, JsonElement>(entries.Count);

            foreach (var e in entries)
            {
                /* Der Inhalt wird als JSON durchgereicht, nicht als Zeichenkette
                   — sonst müsste die Oberfläche ein zweites Mal auspacken. Ist
                   eine Zeile beschädigt, wird sie übergangen statt die ganze
                   Antwort scheitern zu lassen. */
                try { areas[e.Area] = JsonDocument.Parse(e.Payload).RootElement.Clone(); }
                catch (JsonException) { }
            }

            return Results.Ok(new
            {
                session = key,
                angemeldet = userId is not null,
                areas
            });
        });

        /* Ein Bereich je Aufruf. Getrennt, damit der Kursfilter nicht
           überschreibt, was ein anderer Reiter gerade gespeichert hat. */
        g.MapPut("/{area}", async (HttpContext http, IAppStateRepository states,
                                   string area, JsonElement payload,
                                   CancellationToken ct) =>
        {
            if (!KnownAreas.Contains(area))
                return Results.BadRequest(new { error = $"Unbekannter Bereich: {area}" });

            var json = payload.GetRawText();

            if (json.Length > MaxPayloadBytes)
                return Results.BadRequest(new { error = "Zustand zu groß" });

            var key = ResolveSession(http);
            await states.TouchSessionAsync(key, ct);

            /* Geschrieben wird immer auf die Sitzung, auch wenn ein Benutzer
               angemeldet ist — beim Anmelden werden die Zeilen übernommen. So
               bleibt genau eine Stelle, an der der laufende Zustand entsteht. */
            var userId = await states.GetSessionUserAsync(key, ct);

            await states.SaveAsync(
                userId is null ? StateOwner.Session : StateOwner.User,
                userId?.ToString() ?? key.ToString(),
                area, json, ct);

            return Results.Ok(new { ok = true, area });
        });

        g.MapDelete("/", async (HttpContext http, IAppStateRepository states,
                                CancellationToken ct) =>
        {
            var key = ResolveSession(http);
            var userId = await states.GetSessionUserAsync(key, ct);

            var n = await states.ClearAsync(
                userId is null ? StateOwner.Session : StateOwner.User,
                userId?.ToString() ?? key.ToString(), ct);

            return Results.Ok(new { geloescht = n });
        });
    }

    /// <summary>
    /// Holt den Sitzungsschlüssel aus dem Cookie oder vergibt einen neuen.
    /// </summary>
    private static Guid ResolveSession(HttpContext http)
    {
        if (http.Request.Cookies.TryGetValue(CookieName, out var raw)
            && Guid.TryParse(raw, out var existing))
            return existing;

        var key = Guid.NewGuid();

        http.Response.Cookies.Append(CookieName, key.ToString(), new CookieOptions
        {
            /* Kein Zugriff aus JavaScript: der Schlüssel wird ausschließlich
               vom Browser mitgeschickt, die Oberfläche muss ihn nie kennen. */
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Expires = DateTimeOffset.UtcNow.AddYears(1)
        });

        return key;
    }
}
