using Ingest.Infrastructure.Services;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Anmelden, abmelden, Benutzer verwalten.
///
/// <para>Der Sitzungsschlüssel steht im selben Cookie, das die Anwendung schon
/// für den gemerkten Oberflächenzustand benutzt. Das ist Absicht: Ein zweites
/// Cookie hätte eine zweite Lebensdauer, und der Zustand soll die Abmeldung
/// überleben — er hängt an der Sitzung, nicht am Benutzer.</para>
/// </summary>
public static class AuthEndpoints
{
    private const string CookieName = "sc_session";

    public sealed record AnmeldeEingabe(string Login, string Kennwort);
    public sealed record EinrichtenEingabe(string Token, string Login, string Kennwort);
    public sealed record BenutzerEingabe(string Login, string Kennwort, string Rolle, string? Anzeigename);
    public sealed record AenderungEingabe(string? Kennwort, string? Rolle, bool? Aktiv, string? Anzeigename);
    public sealed record KennwortEingabe(string Alt, string Neu);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth");

        /* ------------------------------------------------------- Zustand -- */

        g.MapGet("/status", async (HttpContext ctx, IAuthService auth, CancellationToken ct) =>
        {
            var eingerichtet = await auth.IstEingerichtetAsync(ct);

            var benutzer = ctx.Request.Cookies.TryGetValue(CookieName, out var roh)
                           && Guid.TryParse(roh, out var key)
                ? await auth.WerIstDasAsync(key, ct)
                : null;

            return Results.Ok(new
            {
                eingerichtet,
                angemeldet = benutzer is not null,
                login = benutzer?.Login,
                anzeigename = benutzer?.Anzeigename,
                rolle = benutzer?.Rolle,
                istAdmin = benutzer?.IstAdmin ?? false,

                hinweis = eingerichtet
                    ? null
                    : "Noch kein Benutzer angelegt. Das Einrichtungswort steht im "
                    + "Serverprotokoll — wer es lesen kann, hat Zugriff auf den Server "
                    + "und darf den ersten Verwalter anlegen."
            });
        });

        /* --------------------------------------------------- Einrichtung -- */

        g.MapPost("/einrichten", async (IAuthService auth, EinrichtenEingabe e,
                                        CancellationToken ct) =>
        {
            var (ok, fehler) = await auth.EinrichtenAsync(e.Token, e.Login, e.Kennwort, ct);

            return ok
                ? Results.Ok(new { angelegt = e.Login, rolle = "admin" })
                : Results.BadRequest(new { error = fehler });
        });

        /* ------------------------------------------------------- Anmelden - */

        g.MapPost("/login", async (HttpContext ctx, IAuthService auth,
                                   AnmeldeEingabe e, CancellationToken ct) =>
        {
            var key = SitzungHolen(ctx);

            var (benutzer, fehler) = await auth.AnmeldenAsync(key, e.Login, e.Kennwort, ct);

            if (benutzer is null)
                return Results.Json(new { error = fehler },
                                    statusCode: StatusCodes.Status401Unauthorized);

            return Results.Ok(new
            {
                benutzer.Login, benutzer.Anzeigename, benutzer.Rolle, benutzer.IstAdmin
            });
        });

        g.MapPost("/logout", async (HttpContext ctx, IAuthService auth, CancellationToken ct) =>
        {
            if (ctx.Request.Cookies.TryGetValue(CookieName, out var roh)
                && Guid.TryParse(roh, out var key))
                await auth.AbmeldenAsync(key, ct);

            return Results.Ok(new { abgemeldet = true });
        });

        /* --------------------------------------------- eigenes Kennwort --- */

        g.MapPost("/passwort", async (HttpContext ctx, IAuthService auth,
                                      KennwortEingabe e, CancellationToken ct) =>
        {
            var ich = ctx.Benutzer();
            if (ich is null) return Results.Unauthorized();

            /* Das alte Kennwort wird verlangt, obwohl die Sitzung schon
               nachweist, wer da sitzt. Der Grund ist ein anderer: Wer an einem
               unbeaufsichtigten Rechner sitzt, soll das Kennwort nicht in zwei
               Klicks austauschen können. */
            var key = SitzungHolen(ctx);
            var (geprueft, _) = await auth.AnmeldenAsync(key, ich.Login, e.Alt, ct);

            if (geprueft is null)
                return Results.BadRequest(new { error = "Das alte Kennwort stimmt nicht." });

            /* Die eigene Sitzung überlebt -- alle anderen nicht. Wer sein
               Kennwort ändert, will genau das: an anderen Geräten abgemeldet
               werden und im eigenen Fenster weiterarbeiten. */
            var (ok, fehler) = await auth.AendernAsync(
                ich.UserId, e.Neu, null, null, null, key, ct);

            return ok ? Results.Ok(new { geaendert = true })
                      : Results.BadRequest(new { error = fehler });
        });

        /* ------------------------------------------ Benutzer verwalten ---- */

        g.MapGet("/benutzer", async (IAuthService auth, CancellationToken ct) =>
            Results.Ok(new
            {
                benutzer = await auth.ListeAsync(ct),
                hinweis = "Verwalter dürfen alles. Nutzer sehen denselben Inhalt, "
                        + "können aber keine Läufe anstoßen und nichts ändern — "
                        + "durchgesetzt an der HTTP-Methode, nicht an einer Liste."
            }));

        g.MapPost("/benutzer", async (IAuthService auth, BenutzerEingabe e,
                                      CancellationToken ct) =>
        {
            var (ok, fehler) = await auth.AnlegenAsync(
                e.Login, e.Kennwort, e.Rolle, e.Anzeigename, ct);

            return ok ? Results.Ok(new { angelegt = e.Login, e.Rolle })
                      : Results.BadRequest(new { error = fehler });
        });

        g.MapPost("/benutzer/{userId:int}", async (IAuthService auth, int userId,
                                                   AenderungEingabe e, CancellationToken ct) =>
        {
            /* Kein `behalteSitzung`: Setzt ein Verwalter ein Kennwort zurück,
               sollen ALLE Sitzungen dieses Benutzers enden. Genau das ist der
               Zweck, wenn ein Konto übernommen wurde. */
            var (ok, fehler) = await auth.AendernAsync(
                userId, e.Kennwort, e.Rolle, e.Aktiv, e.Anzeigename, null, ct);

            return ok ? Results.Ok(new { geaendert = userId })
                      : Results.BadRequest(new { error = fehler });
        });

        g.MapDelete("/benutzer/{userId:int}", async (HttpContext ctx, IAuthService auth,
                                                     int userId, CancellationToken ct) =>
        {
            var ich = ctx.Benutzer();
            if (ich is null) return Results.Unauthorized();

            var (ok, fehler) = await auth.LoeschenAsync(userId, ich.UserId, ct);

            return ok ? Results.Ok(new { geloescht = userId })
                      : Results.BadRequest(new { error = fehler });
        });
    }

    /// <summary>
    /// Holt den Sitzungsschlüssel aus dem Cookie oder vergibt einen neuen.
    ///
    /// <para><c>Secure</c> hängt daran, ob die Anfrage über HTTPS kam. Es fest
    /// zu setzen machte die Anwendung auf einem Rechner ohne Zertifikat
    /// unbedienbar — der Browser sendet ein Secure-Cookie über HTTP nicht
    /// zurück, und man käme nie über die Anmeldung hinaus.</para>
    /// </summary>
    private static Guid SitzungHolen(HttpContext ctx)
    {
        if (ctx.Request.Cookies.TryGetValue(CookieName, out var roh)
            && Guid.TryParse(roh, out var vorhanden))
            return vorhanden;

        var key = Guid.NewGuid();

        ctx.Response.Cookies.Append(CookieName, key.ToString(), new CookieOptions
        {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Expires = DateTimeOffset.UtcNow.AddDays(14)
        });

        return key;
    }
}
