using System.Security.Cryptography;
using Dapper;
using Ingest.Core.Analysis;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;
using Ingest.Infrastructure.Datenbank;

using Ingest.Infrastructure.Options;

namespace Ingest.Infrastructure.Services;

/// <summary>Ein angemeldeter Benutzer, so wie ihn die Anwendung braucht.</summary>
public sealed record Angemeldet(int UserId, string Login, string? Anzeigename, string Rolle)
{
    public bool IstAdmin => string.Equals(Rolle, "admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Der Gast: ohne Kennwort, ohne Datenbankzeile, nur lesend, ohne Modelle.</summary>
    public bool IstGast => string.Equals(Rolle, "guest", StringComparison.OrdinalIgnoreCase);

    public static readonly Angemeldet Gast = new(0, "gast", "Gast", "guest");
}

public sealed record BenutzerZeile(
    int UserId, string Login, string? Anzeigename, string Rolle, bool Aktiv,
    DateTime ErstelltUtc, DateTime? LetzteAnmeldungUtc, bool Gesperrt);

public interface IAuthService
{
    /// <summary>Gibt es überhaupt schon einen Benutzer?</summary>
    Task<bool> IstEingerichtetAsync(CancellationToken ct = default);

    /// <summary>
    /// Meldet die Sitzung als Gast an — ohne Kennwort, ohne Datenbankzeile. Nur
    /// wenn <c>Betrieb:GastZugang</c> es erlaubt; sonst <c>null</c>.
    /// </summary>
    Angemeldet? GastAnmelden(Guid sitzung);

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
    private SqlDialekt d => _factory.Dialekt;
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

    private readonly BetriebOptions _betrieb;

    /*  Sitzungen im Speicher: fuer Gaeste immer (sie haben keine Zeile in
        app_user, und ein Gast soll keine Spur in der Datenbank hinterlassen),
        auf einem Slave fuer ALLE -- dort ist app_session Teil des Replikats und
        nicht beschreibbar. Der Dienst ist Singleton, der Speicher lebt so lange
        wie der Prozess; ein Neustart meldet alle ab, und das ist auf einem
        Replikat hinnehmbar.                                                   */
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (Angemeldet Wer, DateTime Bis)> _imSpeicher = new();

    /*  Der Kurzspeicher fuer Sitzungen AUS DER DATENBANK -- eine andere Sache
        als `_imSpeicher` daneben, und die Trennung ist Absicht.

        Jede einzelne Anfrage der Oberflaeche geht durch `WerIstDasAsync`, und
        die Oberflaeche fragt `/api/scheduler` im Fuenfsekundentakt. Gemessen an
        einem Lauf: 205 Aufrufe von `/api/scheduler`, und 197 der 232
        Poolfehler kamen aus dieser Methode. Zwei Abfragen je Anfrage -- lesen
        und den gleitenden Ablauf schreiben --, und sobald die Datenbank langsam
        antwortet, stauen sich die Verbindungen schneller, als sie zurueckkommen.
        Dann ist der Pool leer, und es faellt NICHT die Sitzungspruefung aus,
        sondern alles andere mit ihr.

        Die Frist ist bewusst kurz. `_imSpeicher` haelt eine Sitzung ueber ihre
        volle Lebensdauer, weil es dort keine zweite Wahrheit gibt; hier gibt es
        sie, und eine Abmeldung, eine Kennwortaenderung oder ein entzogenes
        Konto muessen ankommen. Fuenfzehn Sekunden decken den Fuenfsekundentakt
        ab und sind kurz genug, dass niemand sie bemerkt. Abmelden und
        Benutzerpflege raeumen zusaetzlich auf -- die Frist ist das Netz, nicht
        der Weg.                                                               */
    private static readonly TimeSpan Kurzfrist = TimeSpan.FromSeconds(15);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (Angemeldet Wer, DateTime Bis)> _kurz = new();

    public AuthService(ISqlConnectionFactory factory, ILogger<AuthService> log,
                       Microsoft.Extensions.Options.IOptions<BetriebOptions>? betrieb = null)
    {
        _betrieb = betrieb?.Value ?? new BetriebOptions();
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
            "SELECT CAST(COUNT(*) AS INT) FROM dbo.app_user WHERE password_hash IS NOT NULL",
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

    public Angemeldet? GastAnmelden(Guid sitzung)
    {
        if (!_betrieb.GastZugang) return null;
        Aufraeumen();
        _imSpeicher[sitzung] = (Angemeldet.Gast, DateTime.UtcNow + Sitzungsdauer);
        _log.LogInformation("Gast angemeldet");
        return Angemeldet.Gast;
    }

    private void Aufraeumen()
    {
        var jetzt = DateTime.UtcNow;
        foreach (var (k, v) in _imSpeicher)
            if (v.Bis < jetzt) _imSpeicher.TryRemove(k, out _);
    }

    public async Task<(Angemeldet? Benutzer, string? Fehler)> AnmeldenAsync(
        Guid sitzung, string login, string kennwort, CancellationToken ct = default)
    {
        // Ein Gast, der sich mit Kennwort anmeldet, ist keiner: der Name ist reserviert.
        if (string.Equals((login ?? "").Trim(), "gast", StringComparison.OrdinalIgnoreCase)
            || string.Equals((login ?? "").Trim(), "guest", StringComparison.OrdinalIgnoreCase))
            return (null, _betrieb.GastZugang
                ? "Der Gastzugang braucht kein Kennwort — bitte „Als Gast ansehen“ benutzen."
                : "Anmeldename oder Kennwort stimmt nicht.");

        await using var conn = await _factory.OpenAsync(ct);

        /*  LOWER auf beiden Seiten: SQL Server vergleicht in der Standardkollation
            ohne Ruecksicht auf Gross- und Kleinschreibung, Postgres nicht -- dort
            waere "Admin" ein anderer Benutzer als "admin". Der eindeutige Index
            liegt in Postgres deshalb ebenfalls auf LOWER(login).             */
        var u = await conn.QuerySingleOrDefaultAsync<Roh>(new CommandDefinition(
            """
            SELECT user_id AS UserId, login AS Login, display_name AS Anzeigename,
                   password_hash AS Hash, role AS Rolle, is_active AS Aktiv,
                   failed_logins AS Fehlversuche, locked_until_utc AS GesperrtBis
              FROM dbo.app_user WHERE LOWER(login) = LOWER(@login)
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

            /*  Auf dem Slave laesst sich der Fehlversuch nicht zaehlen -- die
                Tabelle ist ein Replikat. Die Sperre des Masters gilt dort
                trotzdem, sie wird ja mitrepliziert.                          */
            if (_betrieb.IstSlave)
                return (null, "Anmeldename oder Kennwort stimmt nicht.");


            /*  Ob und bis wann gesperrt wird, entscheidet sich HIER, nicht in
                der Datenbank.

                Vorher stand dort ein `CASE WHEN @neu >= @max THEN jetzt+15min
                ELSE locked_until_utc END`. Das verlangt zweierlei, was das
                EventMesh-DataCell-Backend im Schreibpfad nicht kann: eine
                Bedingung auszuwerten und den aktuellen Zeilenwert zu lesen.
                Der Node nahm den THEN-Zweig unabhaengig von der Bedingung --
                gemessen am 27.09.2026: Fehlversuch um 09:22:53 UTC, danach
                locked_until_utc = 09:37:53, also volle Sperre beim ERSTEN
                Versuch statt beim fuenften.

                Das ist boesartiger als es klingt: Ein einziger Vertipper -- oder
                ein Kennwortspeicher, der ein veraltetes Kennwort einsetzt --
                sperrte das einzige Verwalterkonto fuer eine Viertelstunde aus,
                und die Meldung sagte dabei "Anmeldename oder Kennwort stimmt
                nicht", nicht "gesperrt". Wer daraufhin das richtige Kennwort
                eintippt, bekommt dieselbe Meldung und sucht den Fehler beim
                Kennwort.

                Ein fertig berechneter Wert wird zuverlaessig gespeichert. Und
                da die Entscheidung ohnehin schon hier getroffen wird -- die
                Rueckmeldung unten prueft dasselbe `neu >= MaxFehlversuche` --
                stand die Bedingung ohnehin doppelt im Code.                    */
            var gesperrtBis = neu >= MaxFehlversuche
                ? DateTime.UtcNow + Sperrdauer
                : u.GesperrtBis;

            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.app_user
                   SET failed_logins = @neu, locked_until_utc = @gesperrtBis
                 WHERE user_id = @id
                """,
                new { neu, gesperrtBis, id = u.UserId }, cancellationToken: ct));

            _log.LogWarning("Fehlanmeldung für {Login} ({Zahl}. Versuch)", u.Login, neu);

            return (null, neu >= MaxFehlversuche
                ? $"Zu viele Fehlversuche. Gesperrt für {Sperrdauer.TotalMinutes:0} Minuten."
                : "Anmeldename oder Kennwort stimmt nicht.");
        }

        // Erfolg: Zähler zurück, Sitzung binden, Hash bei Bedarf erneuern.
        if (_betrieb.IstSlave)
        {
            Aufraeumen();
            var wer = new Angemeldet(u.UserId, u.Login, u.Anzeigename, u.Rolle);
            _imSpeicher[sitzung] = (wer, DateTime.UtcNow + Sitzungsdauer);
            _log.LogInformation("Angemeldet am Replikat: {Login} ({Rolle}), Sitzung im Speicher", u.Login, u.Rolle);
            return (wer, null);
        }

        /*  Ablauf und Zeitstempel werden HIER gerechnet, nicht in der Datenbank.

            Nicht aus Geschmack. Auf dem EventMesh-DataCell-Backend wird ein
            berechneter Ausdruck in einem Schreibvorgang nicht gespeichert: Ein
            `expires_utc = jetzt + 14 Tage` legt `jetzt` ab, die Intervall-
            addition faellt weg -- ohne Fehler, ohne Warnung. Derselbe Ausdruck
            in einem SELECT rechnet richtig; nur beim Schreiben verschwindet er.
            Gemessen am 26.09.2026 ueber Npgsql, sowohl mit Literal-Intervall als
            auch mit gebundenem Faktor, im INSERT wie im MERGE.

            Die Folge war nicht etwa eine falsche Anzeige, sondern: Anmelden
            unmoeglich. Der Sitzungslesevorgang weiter unten verlangt
            `expires_utc > jetzt`; steht dort der Entstehungszeitpunkt, ist jede
            Sitzung im Moment ihrer Entstehung abgelaufen. Der Login antwortete
            mit 200 und der richtigen Rolle, die naechste Anfrage kannte
            niemanden mehr, und die Oberflaeche fiel wortlos aufs Formular
            zurueck -- sah also aus wie ein falsches Kennwort.

            Ein fertiger Zeitstempel als Parameter wird korrekt gespeichert.
            Das gilt auf beiden Backends, kostet nichts und macht den
            Schreibvorgang unabhaengig davon, welche Uhr die Datenbank fuehrt.  */
        var jetzt = DateTime.UtcNow;

        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE dbo.app_user
               SET failed_logins = 0, locked_until_utc = NULL,
                   last_login_utc = @jetzt
             WHERE user_id = @id;

            MERGE INTO dbo.app_session {d.MergeSperre} AS t
            USING (SELECT @key AS session_key) AS s ON t.session_key = s.session_key
            WHEN MATCHED THEN UPDATE SET
                 user_id = @id, last_seen_utc = @jetzt, expires_utc = @ablauf
            WHEN NOT MATCHED THEN
                 INSERT (session_key, user_id, expires_utc)
                 VALUES (@key, @id, @ablauf);
            """,
            new { id = u.UserId, key = sitzung, jetzt, ablauf = jetzt + Sitzungsdauer },
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
        _kurz.TryRemove(sitzung, out _);
        if (_imSpeicher.TryRemove(sitzung, out _) || _betrieb.IstSlave) return;

        await using var conn = await _factory.OpenAsync(ct);

        /* Die Sitzung wird gelöst, nicht gelöscht: Der Oberflächenzustand
           hängt daran und soll eine Abmeldung überleben. */
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.app_session SET user_id = NULL, expires_utc = NULL WHERE session_key = @key",
            new { key = sitzung }, cancellationToken: ct));
    }

    public async Task<Angemeldet?> WerIstDasAsync(Guid sitzung, CancellationToken ct = default)
    {
        if (_imSpeicher.TryGetValue(sitzung, out var im))
        {
            if (im.Bis > DateTime.UtcNow)
            {
                // Gleitender Ablauf wie bei der Datenbanksitzung.
                _imSpeicher[sitzung] = (im.Wer, DateTime.UtcNow + Sitzungsdauer);
                return im.Wer;
            }
            _imSpeicher.TryRemove(sitzung, out _);
        }
        if (_betrieb.IstSlave) return null;

        if (_kurz.TryGetValue(sitzung, out var kurz))
        {
            /* Feste Frist, kein gleitender Ablauf: Dieser Speicher SOLL
               verfallen, gerade weil die Datenbank die Wahrheit haelt. */
            if (kurz.Bis > DateTime.UtcNow) return kurz.Wer;
            _kurz.TryRemove(sitzung, out _);
        }

        await using var conn = await _factory.OpenAsync(ct);

        var u = await conn.QuerySingleOrDefaultAsync<Angemeldet>(new CommandDefinition(
            $"""
            SELECT u.user_id AS UserId, u.login AS Login,
                   u.display_name AS Anzeigename, u.role AS Rolle
              FROM dbo.app_session s
              JOIN dbo.app_user u ON u.user_id = s.user_id
             WHERE s.session_key = @key
               AND u.is_active = {d.Wahr}
               AND (s.expires_utc IS NULL OR s.expires_utc > {d.Jetzt})
            """, new { key = sitzung }, cancellationToken: ct));

        if (u is not null)
        {
            /*  Gleitender Ablauf: Wer die Anwendung benutzt, bleibt angemeldet.

                Auch hier der fertige Zeitstempel statt der Rechnung in der
                Datenbank -- aus demselben Grund wie beim Anmelden. Ein
                berechneter Ausdruck verschwindet auf dem DataCell-Backend
                still, und dann verlaengert sich nichts, sondern die Sitzung
                laeuft bei jedem Zugriff aufs Neue sofort ab.                   */
            var jetzt = DateTime.UtcNow;

            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.app_session
                   SET last_seen_utc = @jetzt, expires_utc = @ablauf
                 WHERE session_key = @key
                """, new { key = sitzung, jetzt, ablauf = jetzt + Sitzungsdauer },
                cancellationToken: ct));

            _kurz[sitzung] = (u, DateTime.UtcNow + Kurzfrist);
        }

        return u;
    }

    // -------------------------------------------------------- Benutzerpflege

    public async Task<IReadOnlyList<BenutzerZeile>> ListeAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        /*  „Gesperrt" wird hier entschieden, nicht in der Datenbank.

            Vorher stand dort `CASE WHEN locked_until_utc > jetzt THEN 1 ELSE 0
            END`. Auf dem EventMesh-DataCell-Backend kam daraus TRUE, obwohl
            locked_until_utc NULL war -- die Benutzerverwaltung zeigte das
            einzige Verwalterkonto am 27.09.2026 dauerhaft als gesperrt an,
            waehrend die Anmeldung einwandfrei funktionierte.

            Eine Anzeige, die faelschlich „gesperrt" sagt, ist schlimmer als
            eine fehlende: Wer sie liest, sucht den Fehler dort, wo keiner ist.
            Der Vergleich ist ein Zweizeiler in C# und dort nachweislich
            richtig.                                                            */
        var rows = await conn.QueryAsync<(int UserId, string Login, string? Anzeigename,
                                          string Rolle, bool Aktiv, DateTime ErstelltUtc,
                                          DateTime? LetzteAnmeldungUtc, DateTime? GesperrtBis)>(
            new CommandDefinition("""
                SELECT user_id AS UserId, login AS Login, display_name AS Anzeigename,
                       role AS Rolle, is_active AS Aktiv, created_utc AS ErstelltUtc,
                       last_login_utc AS LetzteAnmeldungUtc,
                       locked_until_utc AS GesperrtBis
                  FROM dbo.app_user ORDER BY role, login
                """, cancellationToken: ct));

        var jetzt = DateTime.UtcNow;

        return rows.Select(r => new BenutzerZeile(
            r.UserId, r.Login, r.Anzeigename, r.Rolle, r.Aktiv, r.ErstelltUtc,
            r.LetzteAnmeldungUtc, r.GesperrtBis is { } bis && bis > jetzt)).ToList();
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
                $"""
                SELECT CAST(COUNT(*) AS INT) FROM dbo.app_user
                 WHERE role = 'admin' AND is_active = {d.Wahr} AND user_id <> @id
                """, new { id = userId }, cancellationToken: ct));

            if (verbleibend == 0)
                return (false, "Das ist der letzte aktive Verwalter — Rolle und Zustand "
                             + "lassen sich nicht ändern, sonst kommt niemand mehr hinein.");
        }

        /*  „Was nicht angegeben wurde, bleibt" wird hier entschieden, nicht in
            der Datenbank.

            Vorher stand dort `COALESCE(@hash, password_hash)` und zweimal
            `CASE WHEN @hash IS NULL THEN spalte ELSE ... END` -- beides liest
            im Schreibvorgang den aktuellen Zeilenwert, und genau das kann das
            EventMesh-DataCell-Backend nicht (bestaetigt von der Node-Seite:
            der Schreibpfad hat keinen Zugriff auf den bestehenden Wert).

            Hier waere das nicht bloss unwirksam, sondern zerstoerend gewesen:
            Wer nur den Anzeigenamen aendert, haette Rolle, Zustand UND den
            Kennworthash mit NULL ueberschrieben -- das Konto waere danach nicht
            mehr anmeldbar gewesen. Deshalb wird die Zeile zuerst gelesen, in C#
            zusammengesetzt und dann vollstaendig geschrieben.                  */
        var alt = await conn.QuerySingleOrDefaultAsync<Bestand>(new CommandDefinition(
            """
            SELECT password_hash AS Hash, role AS Rolle, is_active AS Aktiv,
                   display_name AS Name, failed_logins AS Fehlversuche,
                   locked_until_utc AS GesperrtBis
              FROM dbo.app_user WHERE user_id = @id
            """, new { id = userId }, cancellationToken: ct));

        if (alt is null)
            return (false, "Diesen Benutzer gibt es nicht.");

        // Ein neues Kennwort loescht Fehlversuche und Sperre; ohne bleibt beides.
        var neuesKennwort = kennwort is not null;

        var n = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.app_user
               SET password_hash = @hash, role = @rolle, is_active = @aktiv,
                   display_name = @name,
                   failed_logins = @fehlversuche, locked_until_utc = @gesperrtBis
             WHERE user_id = @id
            """,
            new
            {
                id = userId,
                hash = neuesKennwort ? Kennwort.Hashen(kennwort!) : alt.Hash,
                rolle = rolle ?? alt.Rolle,
                aktiv = aktiv ?? alt.Aktiv,
                name = anzeigename ?? alt.Name,
                fehlversuche = neuesKennwort ? 0 : alt.Fehlversuche,
                gesperrtBis = neuesKennwort ? null : alt.GesperrtBis
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
                   AND (session_key <> @behalten OR @behalten IS NULL)
                """, new { id = userId, behalten = behalteSitzung },
                cancellationToken: ct));

            if (beendet > 0)
                _log.LogInformation(
                    "Kennwort für Benutzer {Id} geändert — {Zahl} Sitzung(en) beendet",
                    userId, beendet);
        }


        /*  Den Kurzspeicher raeumen: Rolle, Kennwort oder das aktive Kennzeichen
            koennen sich gerade geaendert haben, und eine Sitzung, die dort noch
            fuenfzehn Sekunden mit der alten Rolle laege, waere die unangenehmste
            Sorte -- sie faellt niemandem auf. Der Speicher ist klein und in
            Sekunden wieder gefuellt.                                          */
        _kurz.Clear();

        return n > 0 ? (true, null) : (false, "Kein Benutzer mit dieser Kennung.");
    }

    public async Task<(bool Ok, string? Fehler)> LoeschenAsync(
        int userId, int handelnderUserId, CancellationToken ct = default)
    {
        if (userId == handelnderUserId)
            return (false, "Man kann sich nicht selbst löschen.");

        _kurz.Clear();

        await using var conn = await _factory.OpenAsync(ct);

        var verbleibend = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"""
            SELECT CAST(COUNT(*) AS INT) FROM dbo.app_user
             WHERE role = 'admin' AND is_active = {d.Wahr} AND user_id <> @id
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

    /// <summary>
    /// Der bestehende Stand einer Benutzerzeile, gelesen bevor sie geschrieben
    /// wird — siehe <see cref="AendernAsync"/>: „was nicht angegeben wurde,
    /// bleibt" muss in C# entschieden werden, weil der Schreibpfad des
    /// DataCell-Backends den aktuellen Zeilenwert nicht lesen kann.
    /// </summary>
    private sealed record Bestand(
        string? Hash, string Rolle, bool Aktiv, string? Name,
        int Fehlversuche, DateTime? GesperrtBis);
}
