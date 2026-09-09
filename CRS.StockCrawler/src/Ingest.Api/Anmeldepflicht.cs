using Ingest.Infrastructure.Services;

namespace Ingest.Api;

/// <summary>
/// Die Zugangskontrolle. Sie sitzt vor allen Endpunkten und lässt im Zweifel
/// nichts durch.
///
/// <para><b>Warum die Regel an der HTTP-Methode hängt und nicht an einer
/// Liste.</b> „Nutzer dürfen lesen, aber keine Läufe anstoßen" liesse sich
/// auch mit einer Liste erlaubter Endpunkte umsetzen. Die müsste jemand bei
/// jedem neuen Endpunkt pflegen — und wer einen vergisst, hat ein Loch, das
/// niemandem auffällt, weil alles funktioniert. Die Methodenregel gilt
/// dagegen auch für Endpunkte, die es heute noch nicht gibt: Lesen ist GET,
/// alles andere ist Verändern.</para>
///
/// <para><b>Ohne gültige Sitzung wird NICHTS ausgeliefert.</b> Keine
/// Oberfläche, kein Skript, kein Stylesheet — nur <c>anmeldung.html</c>, eine
/// eigenständige Seite ohne jedes Bauteil der Anwendung.</para>
///
/// <para><b>Der erste Entwurf war hier falsch, und der Kommentar an dieser
/// Stelle begründete den Fehler auch noch.</b> Er lautete: „Die Startseite und
/// die statischen Dateien bleiben offen — sonst könnte niemand das Anmeldefeld
/// sehen. Sie enthalten keine Daten." Der erste Halbsatz stimmt nicht: Ein
/// Anmeldefeld braucht keine Anwendung, es braucht eine Anmeldeseite. Der
/// zweite ist zu eng gedacht: <c>index.html</c> (91 KB) und <c>app.js</c>
/// (237 KB) enthalten zwar keine Kurse, aber die vollständige Methodik im
/// Klartext. Über die ausgelieferte Fassung gemessen: „Sperrbereich" siebenmal,
/// die Fehlerverhältnisse, der gesamte Aufbau. Dazu kam, dass
/// <c>UseStaticFiles()</c> in <c>Program.cs</c> VOR dieser Sperre stand — die
/// Dateien liefen also ohnehin an ihr vorbei.</para>
///
/// <para>Sichtbar wurde es an der Oberfläche: Die Anwendung lud vollständig und
/// wurde nur mit einem Schleier überdeckt. Wer den im Browser wegräumte, sah
/// sie. Ein Schleier ist keine Zugangskontrolle.</para>
///
/// <para>Offen bleiben deshalb nur noch: <c>anmeldung.html</c>, die drei
/// Endpunkte zum Anmelden und <c>/api/health</c>.</para>
///
/// <para><b>Fehlt die Einrichtung</b>, also gibt es noch keinen Benutzer, dann
/// ist die Anwendung nicht etwa offen, sondern verschlossen: Es geht nur der
/// Weg über das Einrichtungswort aus dem Serverprotokoll.</para>
/// </summary>
public static class Anmeldepflicht
{
    private const string CookieName = "sc_session";

    /// <summary>Ohne Anmeldung erreichbar — mehr nicht.</summary>
    private static readonly string[] Offen =
    [
        "/api/auth/status",
        "/api/auth/login",
        "/api/auth/einrichten"
    ];

    /// <summary>
    /// Genau ein Pfad ist zusätzlich offen, und zwar buchstabengetreu:
    /// <c>/api/health</c>. Er antwortet mit „läuft" und einem Zeitstempel und
    /// verrät nichts.
    ///
    /// <para>Der Grund ist praktisch: Ein Lastverteiler, ein Dienstwächter
    /// oder ein Neustartskript fragt genau hier nach, ob die Anwendung
    /// erreichbar ist. Antwortet sie mit 401, hält jedes dieser Werkzeuge sie
    /// für tot. Genau das ist beim ersten Neustart nach dem Einbau passiert —
    /// die Anwendung lief, und das Startskript gab sie auf.</para>
    ///
    /// <para><c>/api/health/stats</c> ist ausdrücklich NICHT offen: Dort
    /// stehen Bestandszahlen.</para>
    /// </summary>
    private const string Lebenszeichen = "/api/health";

    /// <summary>Die einzige Seite, die ohne Sitzung ausgeliefert wird.</summary>
    private const string Anmeldeseite = "/anmeldung.html";

    /// <summary>
    /// Die Wörter der Anmeldeseite in der gewählten Sprache.
    ///
    /// <para>Eine Anmeldeseite, die nur auf Deutsch erscheint, wäre der eine
    /// Ort, an dem die Sprachwahl nichts nützt: Man bekommt sie erst zu
    /// Gesicht, nachdem man sich angemeldet hat.</para>
    ///
    /// <para>Ausgeliefert wird hier <b>nicht</b> die ganze Sprachdatei — die
    /// enthält sämtliche Erklärtexte der Anwendung samt ihrer Messwerte —,
    /// sondern nur, was die Datei selbst mit <c>offen="1"</c> kennzeichnet.
    /// Das sind die fünf Zeilen des Anmeldefeldes. Welche Einträge das sind,
    /// entscheidet damit die Sprachdatei und nicht eine Liste im Quelltext,
    /// die beim nächsten neuen Text jemand zu pflegen vergisst.</para>
    /// </summary>
    private const string Sprachwoerter = "/loc-offen";

    /// <summary>
    /// Schreibende Pfade, die auch ein Nutzer benutzen darf. Beide betreffen
    /// ausschließlich ihn selbst: der gemerkte Oberflächenzustand und das
    /// eigene Kennwort.
    /// </summary>
    private static readonly string[] SchreibenErlaubt =
    [
        "/api/state",
        "/api/auth/logout",
        "/api/auth/passwort"
    ];

    public static IApplicationBuilder UseAnmeldepflicht(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            var pfad = ctx.Request.Path.Value ?? "";
            var istApi = pfad.StartsWith("/api", StringComparison.OrdinalIgnoreCase);

            /* Die Anmeldeseite selbst muss ohne Sitzung erreichbar sein -- sie ist der
               einzige Weg, eine zu bekommen. Sie ist eigenständig und lädt nichts nach. */
            if (pfad.Equals(Anmeldeseite, StringComparison.OrdinalIgnoreCase)
                || pfad.StartsWith(Sprachwoerter, StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (istApi
                && (Offen.Any(p => pfad.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    || pfad.Equals(Lebenszeichen, StringComparison.OrdinalIgnoreCase)
                    || pfad.Equals(Lebenszeichen + "/", StringComparison.OrdinalIgnoreCase)))
            {
                await next();
                return;
            }

            var auth = ctx.RequestServices.GetRequiredService<IAuthService>();

            var benutzer = ctx.Request.Cookies.TryGetValue(CookieName, out var roh)
                           && Guid.TryParse(roh, out var key)
                ? await auth.WerIstDasAsync(key, ctx.RequestAborted)
                : null;

            if (benutzer is null)
            {
                /* Eine Abfrage bekommt 401 -- sie kann damit umgehen. Ein Browser, der
                   eine Seite will, bekommt die Anmeldeseite. Nicht die Anwendung mit
                   einem Schleier darüber: Was nie ausgeliefert wird, kann auch niemand
                   im Browser freilegen. */
                if (istApi)
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;

                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        error = "Nicht angemeldet.",
                        eingerichtet = await auth.IstEingerichtetAsync(ctx.RequestAborted)
                    }, ctx.RequestAborted);

                    return;
                }

                await AnmeldeseiteAusliefernAsync(ctx);
                return;
            }

            // Ab hier steht der Benutzer allen Endpunkten zur Verfügung.
            ctx.Items["benutzer"] = benutzer;

            if (!benutzer.IstAdmin)
            {
                var lesend = HttpMethods.IsGet(ctx.Request.Method)
                             || HttpMethods.IsHead(ctx.Request.Method)
                             || HttpMethods.IsOptions(ctx.Request.Method);

                var erlaubt = lesend
                              || SchreibenErlaubt.Any(p =>
                                     pfad.StartsWith(p, StringComparison.OrdinalIgnoreCase));

                if (!erlaubt)
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;

                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        error = "Diese Rolle darf nur lesen.",
                        hinweis = "Läufe anstoßen, Daten holen und Einstellungen ändern ist "
                                + "Verwaltern vorbehalten. Angezeigt wird Ihnen derselbe Inhalt."
                    }, ctx.RequestAborted);

                    return;
                }
            }

            // Die Benutzerverwaltung ist auch lesend nur für Verwalter.
            if (pfad.StartsWith("/api/auth/benutzer", StringComparison.OrdinalIgnoreCase)
                && !benutzer.IstAdmin)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(
                    new { error = "Nur für Verwalter." }, ctx.RequestAborted);
                return;
            }

            await next();
        });

    /// <summary>
    /// Liefert die Anmeldeseite aus — mit <b>200</b>, nicht mit einer Umleitung.
    ///
    /// <para>Eine Umleitung auf <c>/anmeldung.html</c> würde die angeforderte Adresse in
    /// der Leiste ersetzen; nach dem Anmelden landet man dann dort statt auf der Seite,
    /// die man wollte. Der Inhalt wird deshalb unter der ursprünglichen Adresse
    /// ausgegeben.</para>
    ///
    /// <para><c>no-store</c> ist hier keine Förmlichkeit: Ohne das legt ein
    /// zwischengeschalteter Puffer die Anmeldeseite unter <c>/</c> ab und zeigt sie
    /// anschließend auch dem, der längst angemeldet ist.</para>
    /// </summary>
    private static async Task AnmeldeseiteAusliefernAsync(HttpContext ctx)
    {
        var wurzel = ctx.RequestServices
                        .GetRequiredService<IWebHostEnvironment>().WebRootPath;

        var datei = Path.Combine(wurzel, "anmeldung.html");

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        ctx.Response.Headers.Pragma = "no-cache";

        if (!File.Exists(datei))
        {
            /* Fehlt die Datei, wird trotzdem NICHTS von der Anwendung ausgeliefert.
               Eine kaputte Auslieferung darf nicht zum offenen Zugang führen. */
            await ctx.Response.WriteAsync(
                "<!DOCTYPE html><meta charset=\"utf-8\"><title>Anmeldung</title>"
                + "<p>Anmeldeseite fehlt in der Auslieferung. "
                + "Bitte anmeldung.html neben index.html legen.</p>");
            return;
        }

        await ctx.Response.SendFileAsync(datei, ctx.RequestAborted);
    }

    /// <summary>
    /// Der angemeldete Benutzer eines Aufrufs. Kann nur dort <c>null</c> sein,
    /// wo die Anmeldepflicht den Pfad durchlässt.
    /// </summary>
    public static Angemeldet? Benutzer(this HttpContext ctx)
        => ctx.Items.TryGetValue("benutzer", out var b) ? b as Angemeldet : null;
}
