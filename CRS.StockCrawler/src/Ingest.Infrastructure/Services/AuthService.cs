using System.Security.Cryptography;
using Dapper;
using Ingest.Core.Analysis;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>Ein angemeldeter Benutzer, so wie ihn die Anwendung braucht.</summary>
public sealed record Angemeldet(int UserId, string Login, string? Anzeigename, string Rolle)
{
    public bool IstAdmin => string.Equals(Rolle, "admin", StringComparison.OrdinalIgnoreCase);
}

public sealed record BenutzerZeile(
    int UserId, string Login, string? Anzeigename, string Rolle, bool Aktiv,
    DateTime ErstelltUtc, DateTime? LetzteAnmeldungUtc, bool Gesperrt);

public interface IAuthService
{
    /// <summary>Gibt es überhaupt schon einen Benutzer?</summary>
    Task<bool> IstEingerichtetAsync(CancellationToken ct = default);

    /// <summary>Legt den ersten Verwalter an — nur solange keiner existiert.</summary>
    Task<(bool Ok, string? Fehler)> EinrichtenAsync(
        string token, string login, string kennwort, CancellationToken ct = default);

    /// <summary>
    /// Legt in der ENTWICKLUNG einen Zugang <c>admin</c>/<c>admin</c> an, falls
    /// noch kein Benutzer existiert. Liefert <c>true</c>, wenn er entstanden ist.
    ///
    /// <para><b>Warum es das gibt.</b> Der reguläre Weg über das
    /// Einrichtungswort ist auf einem erreichbaren Server richtig und für
    /// jemanden, der das Projekt zum ersten Mal auscheckt, eine Hürde: Er
    /// müsste ein Wort aus dem Startprotokoll fischen, bevor er überhaupt
    /// etwas sieht.</para>
    ///
    /// <para><b>Warum es trotzdem kein Loch ist.</b> Der Aufrufer ruft es nur
    /// unter <c>IsDevelopment()</c>. Die Auslieferung setzt
    /// <c>ASPNETCORE_ENVIRONMENT=Production</c> fest (siehe
    /// <c>2-install-target.ps1</c>), dort entsteht dieser Zugang also nie.
    /// Und er entsteht auch in der Entwicklung nur, solange die
    /// Benutzertabelle leer ist — wer einen echten Verwalter angelegt hat,
    /// bekommt ihn nicht nachträglich untergeschoben.</para>
    /// </summary>
    Task<bool> EntwicklerzugangAsync(CancellationToken ct = default);

    /// <summary>Prüft Anmeldedaten und bindet die Sitzung an den Benutzer.</summary>
    Task<(Angemeldet? Benutzer, string? Fehler)> AnmeldenAsync(
        Guid sitzung, string login, string kennwort, CancellationToken ct = default);

    Task AbmeldenAsync(Guid sitzung, CancellationToken ct = default);

    /// <summary>Wer sitzt hinter dieser Sitzung? <c>null</c>, wenn niemand.</summary>
    Task<Angemeldet?> WerIstDasAsync(Guid sitzung, CancellationToken ct = default);

    Task<IReadOnlyList<BenutzerZeile>> ListeAsync(CancellationToken ct = default);

    Task<(bool Ok, string? Fehler)> AnlegenAsync(
        string login, string kennwort, string rolle, string? anzeigename,
        CancellationToken ct = default);

    /// <param name="behalteSitzung">
    /// Eine Sitzung, die eine Kennwortänderung überleben darf. Beim Ändern des
    /// EIGENEN Kennworts ist das die gerade benutzte — sonst würfe die
    /// Anwendung den Benutzer aus dem Fenster, in dem er gerade arbeitet. Beim
    /// Zurücksetzen durch einen Verwalter bleibt sie leer: Dort ist das
    /// Aussperren der Zweck.
    /// </param>
    Task<(bool Ok, string? Fehler)> AendernAsync(
        int userId, string? kennwort, string? rolle, bool? aktiv, string? anzeigename,
        Guid? behalteSitzung = null, CancellationToken ct = default);

    Task<(bool Ok, string? Fehler)> LoeschenAsync(
        int userId, int handelnderUserId, CancellationToken ct = default);

    /// <summary>Das Einrichtungswort dieses Starts — nur wenn noch niemand da ist.</summary>
    string? Einrichtungswort { get; }
}

/// <summary>
/// Anmeldung, Sitzungen und Benutzerverwaltung.
///
/// <para><b>Die Einrichtung des ersten Verwalters.</b> Eine frisch
/// ausgelieferte Anwendung hat keinen Benutzer und wäre damit unbedienbar.
/// Der naheliegende Ausweg — „der erste, der sich meldet, wird Verwalter" —
/// ist auf einem öffentlich erreichbaren Server genau das Loch, das die
/// Anmeldung schließen soll: Wer als Erster vorbeikommt, übernimmt.</para>
///
/// <para>Stattdessen erzeugt der Start ein Einrichtungswort und schreibt es
/// ins Protokoll. Wer es lesen kann, hat Zugriff auf den Server — und nur der
/// darf den ersten Verwalter anlegen. Das Wort lebt genau so lange wie der
/// Prozess und verschwindet, sobald der erste Benutzer steht.</para>
/// </summary>
public sealed class AuthService : IAuthService
{
    private readonly ISqlConnectionFactory _factory;
    private readonly ILogger<AuthService> _log;

    /// <summary>Sitzungsdauer. Vierzehn Tage, bei jedem Zugriff verlängert.</summary>
    private static readonly TimeSpan Sitzungsdauer = TimeSpan.FromDays(14);

    /// <summary>Nach so vielen Fehlversuchen wird gesperrt.</summary>
    private const int MaxFehlversuche = 5;

    private static readonly TimeSpan Sperrdauer = TimeSpan.FromMinutes(15);

    /* Ein fester Blind-Hash, EINMAL berechnet.

       Der erste Entwurf rief bei unbekanntem Anmeldenamen `Hashen` auf und
       prüfte das Ergebnis -- also Erzeugen UND Prüfen, doppelte Arbeit.
       Gemessen: 133 ms für einen unbekannten Namen gegen 69 ms für einen
       bekannten. Damit verriet ausgerechnet die Maßnahme gegen das Ausspähen
       von Anmeldenamen, welche existieren.

       Jetzt wird gegen einen vorberechneten Hash geprüft: derselbe Aufwand
       wie bei einem echten Benutzer, eine einzige PBKDF2-Ableitung. */
    private static readonly string BlindHash = Kennwort.Hashen(
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    public string? Einrichtungswort { get; private set; }

    public AuthService(ISqlConnectionFactory factory, ILogger<AuthService> log)
    {
        _factory = factory;
        _log = log;

        /* Das Einrichtungswort wird beim Erzeugen des Dienstes gezogen, nicht
           beim ersten Aufruf: So steht es im Protokoll direkt beim Start und
           nicht irgendwo mittendrin. */
        Einrichtungswort = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    }

    // ----------------------------------------------------------- Einrichtung

    public async Task<bool> IstEingerichtetAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.app_user WHERE password_hash IS NOT NULL",
            cancellationToken: ct)) > 0;
    }

    public async Task<(bool Ok, string? Fehler)> EinrichtenAsync(
        string token, string login, string kennwort, CancellationToken ct = default)
    {
        if (await IstEingerichtetAsync(ct))
            return (false, "Es gibt bereits Benutzer. Die Einrichtung ist abgeschlossen.");

        var erwartet = Einrichtungswort;

        if (string.IsNullOrEmpty(erwartet))
            return (false, "Kein Einrichtungswort vorhanden.");

        /* Zeitgleicher Vergleich, auch hier: Ein Wort, das man Zeichen für
           Zeichen erraten kann, ist keines. */
        var gleich = CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(token ?? ""),
            System.Text.Encoding.UTF8.GetBytes(erwartet));

        if (!gleich)
        {
            _log.LogWarning("Einrichtung mit falschem Wort versucht");
            return (false, "Falsches Einrichtungswort.");
        }

        var (ok, fehler) = await AnlegenAsync(login, kennwort, "admin", null, ct);

        if (ok)
        {
            Einrichtungswort = null;
            _log.LogWarning("Erster Verwalter angelegt: {Login}", login);
        }

        return (ok, fehler);
    }

    public async Task<bool> EntwicklerzugangAsync(CancellationToken ct = default)
    {
        if (await IstEingerichtetAsync(ct)) return false;

        var (ok, fehler) = await AnlegenAsync(
            "admin", "admin", "admin", "Entwicklerzugang", regelnPruefen: false, ct);

        if (!ok)
        {
            _log.LogError("Entwicklerzugang nicht angelegt: {Fehler}", fehler);
            return false;
        }

        /*  Das Einrichtungswort verfaellt mit -- sonst stuenden zwei Wege in
            die frische Anlage offen, und der zweite waere der unbemerkte.    */
        Einrichtungswort = null;
        return true;
    }

    // -------------------------------------------------------------- Anmelden

    public async Task<(Angemeldet? Benutzer, string? Fehler)> AnmeldenAsync(
        Guid sitzung, string login, string kennwort, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var u = await conn.QuerySingleOrDefaultAsync<Roh>(new CommandDefinition(
            """
            SELECT user_id AS UserId, login AS Login, display_name AS Anzeigename,
                   password_hash AS Hash, role AS Rolle, is_active AS Aktiv,
                   failed_logins AS Fehlversuche, locked_until_utc AS GesperrtBis
              FROM dbo.app_user WHERE login = @login
            """, new { login = (login ?? "").Trim() }, cancellationToken: ct));

        /* Auch bei unbekanntem Benutzer wird gerechnet.

           Sonst antwortet die Anwendung auf einen unbekannten Namen in einer
           Millisekunde und auf einen bekannten in fünfzig -- daraus lässt sich
           ablesen, welche Anmeldenamen es gibt. */
        if (u is null)
        {
            Kennwort.Stimmt(kennwort ?? "", BlindHash);
            return (null, "Anmeldename oder Kennwort stimmt nicht.");
        }

        if (u.GesperrtBis is { } bis && bis > DateTime.UtcNow)
            return (null, $"Zu viele Fehlversuche. Gesperrt bis {bis:HH:mm} UTC.");

        if (!u.Aktiv)
            return (null, "Dieses Konto ist abgeschaltet.");

        if (!Kennwort.Stimmt(kennwort ?? "", u.Hash))
        {
            var neu = u.Fehlversuche + 1;

            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.app_user
                   SET failed_logins = @neu,
                       locked_until_utc = CASE WHEN @neu >= @max
                                               THEN DATEADD(MINUTE, @minuten, SYSUTCDATETIME())
                                               ELSE locked_until_utc END
                 WHERE user_id = @id
                """,
                new { neu, max = MaxFehlversuche, minuten = (int)Sperrdauer.TotalMinutes, id = u.UserId },
                cancellationToken: ct));

            _log.LogWarning("Fehlanmeldung für {Login} ({Zahl}. Versuch)", u.Login, neu);

            return (null, neu >= MaxFehlversuche
                ? $"Zu viele Fehlversuche. Gesperrt für {Sperrdauer.TotalMinutes:0} Minuten."
                : "Anmeldename oder Kennwort stimmt nicht.");
        }

        // Erfolg: Zähler zurück, Sitzung binden, Hash bei Bedarf erneuern.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.app_user
               SET failed_logins = 0, locked_until_utc = NULL,
                   last_login_utc = SYSUTCDATETIME()
             WHERE user_id = @id;

            MERGE dbo.app_session WITH (HOLDLOCK) AS t
            USING (SELECT @key AS session_key) AS s ON t.session_key = s.session_key
            WHEN MATCHED THEN UPDATE SET
                 user_id = @id, last_seen_utc = SYSUTCDATETIME(),
                 expires_utc = DATEADD(DAY, @tage, SYSUTCDATETIME())
            WHEN NOT MATCHED THEN
                 INSERT (session_key, user_id, expires_utc)
                 VALUES (@key, @id, DATEADD(DAY, @tage, SYSUTCDATETIME()));
            """,
            new { id = u.UserId, key = sitzung, tage = (int)Sitzungsdauer.TotalDays },
            cancellationToken: ct));

        if (Kennwort.VeraltetSich(u.Hash))
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.app_user SET password_hash = @h WHERE user_id = @id",
                new { h = Kennwort.Hashen(kennwort!), id = u.UserId }, cancellationToken: ct));

        _log.LogInformation("Angemeldet: {Login} ({Rolle})", u.Login, u.Rolle);

        return (new Angemeldet(u.UserId, u.Login, u.Anzeigename, u.Rolle), null);
    }

    public async Task AbmeldenAsync(Guid sitzung, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        /* Die Sitzung wird gelöst, nicht gelöscht: Der Oberflächenzustand
           hängt daran und soll eine Abmeldung überleben. */
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.app_session SET user_id = NULL, expires_utc = NULL WHERE session_key = @key",
            new { key = sitzung }, cancellationToken: ct));
    }

    public async Task<Angemeldet?> WerIstDasAsync(Guid sitzung, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var u = await conn.QuerySingleOrDefaultAsync<Angemeldet>(new CommandDefinition(
            """
            SELECT u.user_id AS UserId, u.login AS Login,
                   u.display_name AS Anzeigename, u.role AS Rolle
              FROM dbo.app_session s
              JOIN dbo.app_user u ON u.user_id = s.user_id
             WHERE s.session_key = @key
               AND u.is_active = 1
               AND (s.expires_utc IS NULL OR s.expires_utc > SYSUTCDATETIME())
            """, new { key = sitzung }, cancellationToken: ct));

        if (u is not null)
            // Gleitender Ablauf: Wer die Anwendung benutzt, bleibt angemeldet.
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.app_session
                   SET last_seen_utc = SYSUTCDATETIME(),
                       expires_utc = DATEADD(DAY, @tage, SYSUTCDATETIME())
                 WHERE session_key = @key
                """, new { key = sitzung, tage = (int)Sitzungsdauer.TotalDays },
                cancellationToken: ct));

        return u;
    }

    // -------------------------------------------------------- Benutzerpflege

    public async Task<IReadOnlyList<BenutzerZeile>> ListeAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<BenutzerZeile>(new CommandDefinition(
            """
            SELECT user_id AS UserId, login AS Login, display_name AS Anzeigename,
                   role AS Rolle, is_active AS Aktiv, created_utc AS ErstelltUtc,
                   last_login_utc AS LetzteAnmeldungUtc,
                   CAST(CASE WHEN locked_until_utc > SYSUTCDATETIME() THEN 1 ELSE 0 END AS BIT) AS Gesperrt
              FROM dbo.app_user ORDER BY role, login
            """, cancellationToken: ct));

        return rows.ToList();
    }

    public Task<(bool Ok, string? Fehler)> AnlegenAsync(
        string login, string kennwort, string rolle, string? anzeigename,
        CancellationToken ct = default)
        => AnlegenAsync(login, kennwort, rolle, anzeigename, true, ct);

    private async Task<(bool Ok, string? Fehler)> AnlegenAsync(
        string login, string kennwort, string rolle, string? anzeigename,
        bool regelnPruefen, CancellationToken ct)
    {
        login = (login ?? "").Trim();

        if (login.Length < 3) return (false, "Anmeldename braucht mindestens drei Zeichen.");
        if (login.Length > 128) return (false, "Anmeldename ist zu lang.");

        if (rolle is not ("admin" or "user"))
            return (false, "Rolle muss admin oder user sein.");

        /*  Die Kennwortregeln gelten fuer jeden Weg, der von aussen erreichbar
            ist. Nur der Entwicklerzugang setzt sie aus -- „admin" verstiesse
            gegen die Mindestlaenge von zwoelf Zeichen UND gegen die Liste
            verbreiteter Zeichenfolgen, und genau das ist dort beabsichtigt.  */
        if (regelnPruefen && Kennwort.Beanstandung(kennwort) is { } b)
            return (false, b);

        await using var conn = await _factory.OpenAsync(ct);

        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO dbo.app_user (login, display_name, password_hash, role)
                VALUES (@login, @name, @hash, @rolle)
                """,
                new { login, name = anzeigename, hash = Kennwort.Hashen(kennwort), rolle },
                cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return (false, $"Den Anmeldenamen {login} gibt es bereits.");
        }

        _log.LogInformation("Benutzer angelegt: {Login} ({Rolle})", login, rolle);
        return (true, null);
    }

    public async Task<(bool Ok, string? Fehler)> AendernAsync(
        int userId, string? kennwort, string? rolle, bool? aktiv, string? anzeigename,
        Guid? behalteSitzung = null, CancellationToken ct = default)
    {
        if (rolle is not null && rolle is not ("admin" or "user"))
            return (false, "Rolle muss admin oder user sein.");

        if (kennwort is not null && Kennwort.Beanstandung(kennwort) is { } b)
            return (false, b);

        await using var conn = await _factory.OpenAsync(ct);

        /* Der letzte Verwalter darf sich nicht selbst entmachten oder
           abschalten -- danach käme niemand mehr an die Benutzerverwaltung,
           und es bliebe nur der Weg über die Datenbank. */
        if (rolle == "user" || aktiv == false)
        {
            var verbleibend = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                SELECT COUNT(*) FROM dbo.app_user
                 WHERE role = 'admin' AND is_active = 1 AND user_id <> @id
                """, new { id = userId }, cancellationToken: ct));

            if (verbleibend == 0)
                return (false, "Das ist der letzte aktive Verwalter — Rolle und Zustand "
                             + "lassen sich nicht ändern, sonst kommt niemand mehr hinein.");
        }

        var n = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.app_user
               SET password_hash = COALESCE(@hash, password_hash),
                   role          = COALESCE(@rolle, role),
                   is_active     = COALESCE(@aktiv, is_active),
                   display_name  = COALESCE(@name, display_name),
                   failed_logins = CASE WHEN @hash IS NULL THEN failed_logins ELSE 0 END,
                   locked_until_utc = CASE WHEN @hash IS NULL THEN locked_until_utc ELSE NULL END
             WHERE user_id = @id
            """,
            new
            {
                id = userId,
                hash = kennwort is null ? null : Kennwort.Hashen(kennwort),
                rolle,
                aktiv,
                name = anzeigename
            }, cancellationToken: ct));

        /* Ein neues Kennwort beendet die alten Sitzungen.

           Ohne das ist das Zurücksetzen wirkungslos, wo es am meisten zählt:
           Wer ein Kennwort ändert, weil ein Konto übernommen wurde, sperrt den
           Angreifer nicht aus -- dessen Cookie bleibt vierzehn Tage gültig.
           Gemessen und bestätigt: Nach dem Zurücksetzen antwortete die alte
           Sitzung weiterhin mit 200.

           Beim Ändern des EIGENEN Kennworts überlebt die gerade benutzte
           Sitzung; alles andere würfe den Benutzer aus dem Fenster, in dem er
           gerade steht. */
        if (n > 0 && kennwort is not null)
        {
            var beendet = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.app_session
                   SET user_id = NULL, expires_utc = NULL
                 WHERE user_id = @id
                   AND (@behalten IS NULL OR session_key <> @behalten)
                """, new { id = userId, behalten = behalteSitzung },
                cancellationToken: ct));

            if (beendet > 0)
                _log.LogInformation(
                    "Kennwort für Benutzer {Id} geändert — {Zahl} Sitzung(en) beendet",
                    userId, beendet);
        }

        return n > 0 ? (true, null) : (false, "Kein Benutzer mit dieser Kennung.");
    }

    public async Task<(bool Ok, string? Fehler)> LoeschenAsync(
        int userId, int handelnderUserId, CancellationToken ct = default)
    {
        if (userId == handelnderUserId)
            return (false, "Man kann sich nicht selbst löschen.");

        await using var conn = await _factory.OpenAsync(ct);

        var verbleibend = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*) FROM dbo.app_user
             WHERE role = 'admin' AND is_active = 1 AND user_id <> @id
            """, new { id = userId }, cancellationToken: ct));

        if (verbleibend == 0)
            return (false, "Das ist der letzte aktive Verwalter.");

        // Sitzungen erst lösen, sonst hält der Fremdschlüssel.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.app_session SET user_id = NULL WHERE user_id = @id",
            new { id = userId }, cancellationToken: ct));

        var n = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.app_user WHERE user_id = @id",
            new { id = userId }, cancellationToken: ct));

        return n > 0 ? (true, null) : (false, "Kein Benutzer mit dieser Kennung.");
    }

    private sealed record Roh(
        int UserId, string Login, string? Anzeigename, string? Hash, string Rolle,
        bool Aktiv, int Fehlversuche, DateTime? GesperrtBis);
}
