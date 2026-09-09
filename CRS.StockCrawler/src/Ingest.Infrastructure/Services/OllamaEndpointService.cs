using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Ein Endpunkt antwortet nicht, und die Anwendung rechnet ersatzweise lokal.
/// </summary>
/// <param name="Id">Der Endpunkt, der ausgefallen ist.</param>
/// <param name="Name">Sein Anzeigename.</param>
/// <param name="Grund">Was schiefging — gekürzt, aber unverändert.</param>
/// <param name="SeitUtc">Wann es zuletzt auffiel.</param>
public sealed record EndpunktStoerung(string Id, string Name, string Grund, DateTime SeitUtc);

/// <summary>Ein Ollama-Endpunkt: dieser Rechner oder eine gemietete GPU.</summary>
public sealed class OllamaEndpoint
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public bool IstStandard { get; set; }

    /// <summary>Über einen SSH-Tunnel statt direkt.</summary>
    public bool NutztTunnel { get; set; }

    public string? SshHost { get; set; }
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = "root";
    public string? SshKeyPath { get; set; }
    public int RemotePort { get; set; } = 11434;

    /// <summary>
    /// Anmeldung am Endpunkt, falls er eine verlangt. <c>Benutzer:Passwort</c> ergibt
    /// HTTP-Basic, alles andere einen Bearer-Token.
    /// </summary>
    /// <remarks>
    /// <para><b>Gemietete GPU-Server verlangen oft Basic statt Bearer.</b> Bei vast.ai steht vor
    /// Ollama ein Caddy mit <c>WWW-Authenticate: Basic realm="restricted"</c>, Benutzer
    /// <c>vastai</c> und dem OPEN_BUTTON_TOKEN als Passwort. Ein Bearer-Header wird dort mit 401
    /// abgewiesen — und ein 401 sieht aus wie „Modell nicht da“, nicht wie „falsche Anmeldeart“.</para>
    ///
    /// <para>Die Unterscheidung am Doppelpunkt statt an einem zusätzlichen Schalter: Ein
    /// Bearer-Token enthält keinen, ein Anmeldepaar immer genau einen — und ein Feld weniger ist
    /// ein Feld weniger, das falsch stehen kann.</para>
    ///
    /// <para><b>Über den Tunnel wird das nicht gebraucht.</b> Der SSH-Tunnel endet hinter dem
    /// Caddy, direkt an Ollamas 11434. Das Feld gilt dem Rückfall über den offenen Port.</para>
    /// </remarks>
    public string? Schluessel { get; set; }

    /// <summary>Der fertige Kopfzeilenwert, oder <c>null</c> ohne Schlüssel.</summary>
    public AuthenticationHeaderValue? Anmeldung()
    {
        if (string.IsNullOrWhiteSpace(Schluessel)) return null;

        var wert = Schluessel.Trim();
        var i = wert.IndexOf(':');

        return i <= 0
            ? new AuthenticationHeaderValue("Bearer", wert)
            : new AuthenticationHeaderValue(
                  "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(wert)));
    }
}

public interface IOllamaEndpointService
{
    IReadOnlyList<OllamaEndpoint> Alle { get; }
    OllamaEndpoint Aktiv { get; }

    /// <summary>
    /// Gesetzt, solange der gewählte Endpunkt nicht erreichbar ist und ersatzweise
    /// lokal gerechnet wird. Die Oberfläche muss das anzeigen können — ein stiller
    /// Rückfall wäre nur eine andere Art, das Problem zu verstecken.
    /// </summary>
    EndpunktStoerung? Stoerung { get; }

    /// <summary>Die Adresse des aktiven Endpunkts — bei Tunnelbetrieb die lokale.</summary>
    Task<string> AdresseAsync(CancellationToken ct = default);

    /// <summary>
    /// Adresse <b>und</b> Anmeldung des aktiven Endpunkts.
    ///
    /// <para>Wer nur <see cref="AdresseAsync"/> nimmt, bekommt bei einem Endpunkt hinter einem
    /// Reverse-Proxy 401 — und das liest sich wie ein fehlendes Modell.</para>
    /// </summary>
    Task<(string Adresse, AuthenticationHeaderValue? Anmeldung)> ZugangAsync(
        CancellationToken ct = default);

    void Waehle(string id);
    void Speichere(OllamaEndpoint endpunkt);
    void Entferne(string id);

    Task<(bool Erreichbar, string Meldung, IReadOnlyList<string> Modelle)> PruefeAsync(
        string id, CancellationToken ct = default);
}

/// <summary>
/// Verwaltet, wo Ollama läuft — auf diesem Rechner oder auf einer gemieteten GPU.
///
/// <b>Warum das nötig wurde.</b> Das Reasoning-Modell braucht auf der CPU ein
/// bis zwei Minuten je Runde, und der Agent ruft mehrfach Werkzeuge auf. Ein
/// kleineres Modell wäre die billige Antwort gewesen — <c>qwen3-vl:4b</c> ist
/// halb so langsam, las aber ein Fehlerverhältnis von 1,0034 als „nahezu
/// perfekt" und behauptete, das Modell schlage den Stillstand. Das Gegenteil
/// der Werkzeugausgabe. Ein Modell, das Verneinungen und Schwellen nicht hält,
/// ist in dieser Anwendung gefährlicher als ein langsames.
///
/// <b>Warum ein Tunnel und keine offene Adresse.</b> Der nach außen abgebildete
/// Port einer gemieteten Maschine spricht reines HTTP. Dort gingen die Fragen
/// und die Analysewerte im Klartext durchs Netz. Die Alternative — der Nutzer
/// hält ein SSH-Fenster offen — ist keine Lösung, sondern eine Fehlerquelle:
/// Er vergisst es, schließt es versehentlich, und ein Lauf bricht mittendrin
/// ab.
///
/// Der lokale Port wird vom Betriebssystem vergeben. Eine feste Nummer würde
/// mit einem lokal laufenden Ollama auf 11434 kollidieren — und genau das ist
/// der Normalfall: Wer eine GPU mietet, hat meist trotzdem eine lokale Instanz.
/// </summary>
public sealed class OllamaEndpointService : IOllamaEndpointService, IAsyncDisposable
{
    private sealed record Tunnel(SshClient Client, ForwardedPortLocal Port)
    {
        public string BaseUrl => $"http://127.0.0.1:{Port.BoundPort}";
    }

    private readonly ILogger<OllamaEndpointService> _log;
    private readonly IHttpClientFactory _http;
    private readonly string _pfad;

    private readonly Dictionary<string, Tunnel> _offen = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sperre = new(1, 1);

    private List<OllamaEndpoint> _endpunkte = [];
    private string _aktiv = "lokal";

    /// <summary>Die letzte Störung am aktiven Endpunkt, oder <c>null</c>.</summary>
    private EndpunktStoerung? _stoerung;

    /// <summary>Wie lange nach einem Ausfall gar nicht erst angeklopft wird.</summary>
    private static readonly TimeSpan Schonfrist = TimeSpan.FromMinutes(1);

    public OllamaEndpointService(ILogger<OllamaEndpointService> log, IHttpClientFactory http)
    {
        _log = log;
        _http = http;

        _pfad = Ablage.OllamaEndpunkte;

        Lade();
    }

    public IReadOnlyList<OllamaEndpoint> Alle => _endpunkte;

    public EndpunktStoerung? Stoerung => _stoerung;

    public OllamaEndpoint Aktiv =>
        _endpunkte.FirstOrDefault(e => e.Id == _aktiv)
        ?? _endpunkte.FirstOrDefault(e => e.IstStandard)
        ?? _endpunkte.FirstOrDefault()
        ?? new OllamaEndpoint { Id = "lokal", Name = "Dieser Rechner" };

    /// <summary>
    /// Wählt den aktiven Endpunkt — und merkt sich das über den Neustart hinweg.
    ///
    /// <para><b>Der erste Entwurf setzte nur ein Feld im Arbeitsspeicher.</b> Nach jedem
    /// Neustart fiel die Auswahl auf den als Standard markierten zurück, also auf den
    /// lokalen. Wer eine GPU gemietet, sie eingetragen und ausgewählt hatte, rechnete nach
    /// dem nächsten Neustart wieder auf der CPU — ohne Meldung, nur sechsmal langsamer.
    /// Gemessen: dieselbe Anwendung, dieselbe Frage, 83 s gegen 524 s.</para>
    ///
    /// <para>Eine Auswahl, die man treffen kann und die stillschweigend zurückfällt, ist
    /// schlimmer als gar keine — man glaubt, sie gelte.</para>
    /// </summary>
    public void Waehle(string id)
    {
        if (!_endpunkte.Any(e => e.Id == id)) return;

        _aktiv = id;
        _stoerung = null;   // Eine Störung des vorigen Endpunkts sagt nichts über diesen.

        foreach (var e in _endpunkte) e.IstStandard = e.Id == id;

        Schreibe();
    }

    public async Task<string> AdresseAsync(CancellationToken ct = default) =>
        (await ZugangAsync(ct)).Adresse;

    public async Task<(string Adresse, AuthenticationHeaderValue? Anmeldung)> ZugangAsync(
        CancellationToken ct = default)
    {
        var e = Aktiv;

        /* Über den Tunnel entfällt die Anmeldung: Er endet hinter dem Reverse-Proxy, direkt
           an Ollama. Den Schlüssel trotzdem mitzuschicken wäre nicht falsch, aber er ginge
           dann durch eine Leitung, die ihn nicht braucht. */
        var anmeldung = e.NutztTunnel ? null : e.Anmeldung();

        if (!e.NutztTunnel || string.IsNullOrWhiteSpace(e.SshHost)) return (e.BaseUrl, anmeldung);

        /* Nach einem Ausfall nicht bei jedem Aufruf neu anklopfen.

           Ohne diese Schonfrist kostet ein toter Endpunkt seine volle Verbindungsfrist
           pro Einbettung. Ein Stundenlauf über hundert Abschnitte stünde damit still,
           obwohl der Rückfall längst greift -- der Ausfall wäre behoben und die Anlage
           trotzdem unbenutzbar.

           Eine Minute ist der Kompromiss: kurz genug, dass eine fortgesetzte Instanz
           rasch wieder benutzt wird, lang genug, dass ein Stapel durchläuft. */
        if (_stoerung is { } alt && alt.Id == e.Id
            && DateTime.UtcNow - alt.SeitUtc < Schonfrist
            && Oertlich() is { } ersatz)
        {
            return (ersatz.BaseUrl, ersatz.Anmeldung());
        }

        await _sperre.WaitAsync(ct);

        try
        {
            if (_offen.TryGetValue(e.Id, out var vorhanden))
            {
                /* Verbindungen sterben leise — Zeitüberschreitung der
                   Gegenstelle, Netzwechsel, Standby. Deshalb prüfen statt
                   annehmen. */
                if (vorhanden.Client.IsConnected && vorhanden.Port.IsStarted)
                    return (vorhanden.BaseUrl, anmeldung);

                _log.LogInformation("Tunnel zu {Name} war abgerissen — neu aufgebaut", e.Name);
                Schliesse(e.Id);
            }

            try
            {
                var t = Oeffne(e);
                _offen[e.Id] = t;
                _stoerung = null;

                _log.LogInformation("Tunnel zu {Name} offen: {Adresse} → Gegenstelle {Remote}",
                                    e.Name, t.BaseUrl, e.RemotePort);

                return (t.BaseUrl, anmeldung);
            }
            catch (Exception ex) when (Oertlich() is { } lokal)
            {
                /* Der gewählte Endpunkt ist weg — es wird lokal weitergerechnet.

                   Warum überhaupt ein Rückfall: Eine gemietete Instanz verschwindet
                   irgendwann, das ist der Normalfall und kein Ausnahmezustand. Ohne
                   Rückfall reisst sie beim Verschwinden das Einbetten mit, und das läuft
                   stündlich. Genau so passiert: Die Auswahl auf eine GPU überlebte deren
                   Zerstörung, und danach fand die Wissenssuche zu keiner Frage mehr etwas.
                   Die Meldung lautete „Ollama antwortet nicht oder bge-m3 fehlt" -- beides
                   war falsch, das Modell lag bereit und Ollama lief.

                   Warum die Auswahl NICHT zurückgesetzt wird: Eine angehaltene Instanz
                   kommt beim Fortsetzen wieder. Wer die Wahl automatisch verwürfe, müsste
                   sie jedes Mal neu treffen -- und würde nicht merken, dass er es tut. */
                _stoerung = new EndpunktStoerung(
                    e.Id, e.Name,
                    ex.Message[..Math.Min(200, ex.Message.Length)],
                    DateTime.UtcNow);

                _log.LogWarning(ex,
                    "Endpunkt {Name} nicht erreichbar — es wird ersatzweise lokal gerechnet "
                    + "({Lokal}). Die Auswahl bleibt bestehen.", e.Name, lokal.BaseUrl);

                return (lokal.BaseUrl, lokal.Anmeldung());
            }
        }
        finally
        {
            _sperre.Release();
        }
    }

    /// <summary>
    /// Der lokale Endpunkt — sofern er nicht selbst der gerade gewählte ist.
    ///
    /// <para>Die Bedingung ist der Punkt: Scheitert der lokale Endpunkt, gibt es nichts
    /// mehr, worauf zurückzufallen wäre. Dann muss der Fehler durchschlagen, statt in
    /// einer Schleife auf sich selbst zu zeigen.</para>
    /// </summary>
    private OllamaEndpoint? Oertlich() =>
        _aktiv == "lokal" ? null : _endpunkte.FirstOrDefault(x => x.Id == "lokal");

    public async Task<(bool Erreichbar, string Meldung, IReadOnlyList<string> Modelle)> PruefeAsync(
        string id, CancellationToken ct = default)
    {
        var vorher = _aktiv;

        try
        {
            /* NICHT ueber Waehle: Das wuerde die Auswahl festschreiben. Eine Pruefung
               soll den aktiven Endpunkt nicht verstellen -- man prueft ja gerade, ob der
               andere ueberhaupt taugt. */
            if (_endpunkte.Any(e => e.Id == id)) _aktiv = id;

            var (adresse, anmeldung) = await ZugangAsync(ct);
            var client = _http.CreateClient();

            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Authorization = anmeldung;

            using var res = await client.GetAsync($"{adresse}/api/tags", ct);

            if (!res.IsSuccessStatusCode)
            {
                /* 401 hat hier eine eigene Ursache und einen eigenen Ausweg. Ohne den Hinweis
                   sucht man das Modell, das gar nicht das Problem ist. */
                var grund = res.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? " — der Endpunkt verlangt eine Anmeldung. Bei vast.ai ist das "
                      + "\"vastai:<OPEN_BUTTON_TOKEN>\" im Feld Schlüssel, oder man nimmt "
                      + "den Tunnel, der am Reverse-Proxy vorbeigeht."
                    : "";

                return (false, $"Ollama antwortet mit {(int)res.StatusCode}{grund}", []);
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));

            var modelle = doc.RootElement.GetProperty("models").EnumerateArray()
                             .Select(m => m.GetProperty("name").GetString() ?? "")
                             .Where(n => n.Length > 0).OrderBy(n => n).ToList();

            return (true, $"erreichbar unter {adresse}", modelle);
        }
        catch (Exception ex)
        {
            return (false, ex.Message[..Math.Min(300, ex.Message.Length)], []);
        }
        finally
        {
            _aktiv = vorher;
        }
    }

    // ------------------------------------------------------------- Ablage --

    public void Speichere(OllamaEndpoint e)
    {
        if (string.IsNullOrWhiteSpace(e.Id))
            e.Id = Guid.NewGuid().ToString("N")[..8];

        _endpunkte.RemoveAll(x => x.Id == e.Id);
        _endpunkte.Add(e);

        // Ein geänderter Endpunkt heißt: Der alte Tunnel führt woanders hin.
        Schliesse(e.Id);

        Schreibe();
    }

    public void Entferne(string id)
    {
        if (id == "lokal") return;   // Der lokale Endpunkt bleibt immer.

        _endpunkte.RemoveAll(x => x.Id == id);
        Schliesse(id);

        if (_aktiv == id) _aktiv = "lokal";

        Schreibe();
    }

    private void Lade()
    {
        try
        {
            if (File.Exists(_pfad))
            {
                _endpunkte = JsonSerializer.Deserialize<List<OllamaEndpoint>>(
                    File.ReadAllText(_pfad)) ?? [];
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Endpunktdatei {Pfad} unlesbar — es bleibt beim lokalen", _pfad);
        }

        /* Der lokale Endpunkt wird nie aus der Datei gelesen, sondern immer
           gesetzt. Wer die Datei zerschießt, verliert damit höchstens die
           gemieteten Maschinen, nicht die Fähigkeit, überhaupt zu arbeiten. */
        if (_endpunkte.All(e => e.Id != "lokal"))
            _endpunkte.Insert(0, new OllamaEndpoint
            {
                Id = "lokal",
                Name = "Dieser Rechner",
                BaseUrl = Environment.GetEnvironmentVariable("CRS_OLLAMA_URL")
                          ?? "http://localhost:11434",
                IstStandard = true
            });

        _aktiv = _endpunkte.FirstOrDefault(e => e.IstStandard)?.Id ?? "lokal";
    }

    private void Schreibe()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pfad)!);

            File.WriteAllText(_pfad, JsonSerializer.Serialize(
                _endpunkte, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Endpunkte konnten nicht gespeichert werden");
        }
    }

    // -------------------------------------------------------------- SSH ----

    private Tunnel Oeffne(OllamaEndpoint e)
    {
        var schluessel = SchluesselDatei(e.SshKeyPath)
            ?? throw new InvalidOperationException(
                "Kein privater SSH-Schlüssel gefunden. Gesucht wurde in "
                + $"{Path.Combine(AppContext.BaseDirectory, "secrets")} und in ~/.ssh "
                + "(id_ed25519, id_rsa, id_ecdsa).");

        /* Seit SSH.NET 2026.0.0 ist der Vertrag annotiert: Host und Benutzer
           duerfen nicht null sein. Hier abzubrechen ist besser, als die
           Bibliothek mit einer Nullreferenz sterben zu lassen -- die Meldung
           sagt dann, welches Feld des Endpunkts fehlt. */
        if (string.IsNullOrWhiteSpace(e.SshHost) || string.IsNullOrWhiteSpace(e.SshUser))
            throw new InvalidOperationException(
                $"Endpunkt „{e.Name}“ ist unvollstaendig: SshHost und SshUser "
                + "muessen gesetzt sein.");

        var zugang = new SshConnectionInfo(
            e.SshHost, e.SshPort, e.SshUser,
            new PrivateKeyAuthenticationMethod(e.SshUser, new PrivateKeyFile(schluessel)))
        {
            /* Acht Sekunden, nicht die voreingestellten dreissig.

               Eine gemietete Instanz ist entweder da oder weg; dazwischen gibt es
               wenig. Wer dreissig Sekunden wartet, wartet sie fast immer vergeblich --
               gemessen an einer zerstörten Instanz: 25 s bis zur Fehlermeldung, und
               diese Zeit fiel bei JEDEM Aufruf an. */
            Timeout = TimeSpan.FromSeconds(8)
        };

        var client = new SshClient(zugang);

        /* Ohne das stirbt der Tunnel an der Zeitüberschreitung der Gegenstelle:
           Eine Runde des Agenten dauert Minuten, in denen nichts fließt. */
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);

        try
        {
            client.Connect();

            var port = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)e.RemotePort);
            client.AddForwardedPort(port);

            port.Exception += (_, ev) =>
                _log.LogWarning(ev.Exception, "Fehler im Tunnel zu {Name}", e.Name);

            port.Start();

            return new Tunnel(client, port);
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();

            throw new InvalidOperationException(
                $"SSH-Anmeldung an {e.SshHost} abgelehnt. Ist der öffentliche Schlüssel dort "
                + "hinterlegt? Bei vast.ai gilt ein Kontoschlüssel nur für NEU erstellte "
                + "Instanzen — eine bereits laufende bekommt ihn nicht nachträglich.", ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static string? SchluesselDatei(string? angegeben)
    {
        if (!string.IsNullOrWhiteSpace(angegeben))
            return File.Exists(angegeben) ? angegeben : null;

        string[] namen = ["id_ed25519", "id_rsa", "id_ecdsa"];

        string[] orte =
        [
            Path.Combine(AppContext.BaseDirectory, "secrets"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh")
        ];

        return (from ort in orte
                from name in namen
                let pfad = Path.Combine(ort, name)
                where File.Exists(pfad)
                select pfad).FirstOrDefault();
    }

    private void Schliesse(string id)
    {
        if (!_offen.Remove(id, out var t)) return;

        try { t.Port.Stop(); } catch { /* schon zu */ }
        try { t.Client.Disconnect(); } catch { /* schon weg */ }

        t.Port.Dispose();
        t.Client.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        foreach (var id in _offen.Keys.ToList()) Schliesse(id);

        _sperre.Dispose();
        return ValueTask.CompletedTask;
    }
}
