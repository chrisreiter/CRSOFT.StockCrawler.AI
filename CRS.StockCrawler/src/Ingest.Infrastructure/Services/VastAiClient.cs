using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <param name="Id">Instanz-Nummer bei vast.ai.</param>
/// <param name="Status">Laufzustand („running“, „loading“, „exited“ …).</param>
/// <param name="GpuName">Verbaute GPU, etwa „RTX 4090“.</param>
/// <param name="GpuCount">Anzahl der GPUs.</param>
/// <param name="Host">Öffentlich erreichbare Adresse.</param>
/// <param name="OllamaPort">Der nach außen gemappte Port von 11434, sofern vorhanden.</param>
/// <param name="PricePerHour">Kosten je Stunde in USD im <b>laufenden</b> Zustand.</param>
/// <param name="SshHost">Sprungrechner für den Tunnel, etwa <c>ssh7.vast.ai</c>.</param>
/// <param name="SshPort">Dessen Port. <b>Ändert sich beim Fortsetzen</b> — deshalb gehört er
/// hierher und nicht nur in die gespeicherte Endpunktdatei.</param>
/// <param name="KostenJetztProStunde">Was gerade tatsächlich anfällt. Angehalten bleibt nur der
/// Plattenanteil übrig; das ist die Zahl, die beantwortet, ob sich das Abschalten lohnt.</param>
/// <param name="GpuRamMb">Grafikspeicher der Karte in MB (<c>gpu_totalram</c>). 0 = unbekannt.</param>
/// <param name="DirektSshPort">
/// Der nach außen gemappte Port von <c>22/tcp</c> — SSH direkt in den Container, ohne den
/// Sprungrechner. <b>Am 24.08.2026 an einer echten Instanz gemessen: Der Sprungrechner
/// <c>ssh2.vast.ai:39384</c> schloss jede Verbindung sofort</b> („Connection closed before a
/// valid SSH identification string was received“), während derselbe Container über
/// <c>public_ipaddr:16054</c> anstandslos antwortete. Deshalb ist der direkte Weg der
/// bevorzugte und der Sprungrechner der Rückfall — nicht umgekehrt.
/// </param>
public sealed record VastInstanz(
    long Id, string Status, string GpuName, int GpuCount,
    string? Host, int? OllamaPort, double PricePerHour,
    string? SshHost = null, int? SshPort = null, double KostenJetztProStunde = 0,
    int GpuRamMb = 0, int? DirektSshPort = null)
{
    public bool Laeuft => Status.Equals("running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ollama-Adresse, sofern Host und Port bekannt sind.</summary>
    /// <remarks>
    /// Im angehaltenen Zustand <c>null</c>: vast.ai meldet dann <c>ports: null</c>. Das ist keine
    /// Lücke in der Antwort, sondern die Wahrheit — es gibt gerade keine Weiterleitung.
    /// </remarks>
    public string? BaseUrl => Host is not null && OllamaPort is not null
        ? $"http://{Host}:{OllamaPort}"
        : null;

    /// <summary>Reicht für den Tunnel, auch wenn Ollamas Port nicht nach außen gemappt ist.</summary>
    public bool TunnelMoeglich => DirektMoeglich
                                  || (!string.IsNullOrWhiteSpace(SshHost) && SshPort is > 0);

    /// <summary>SSH direkt in den Container — der Weg, der gemessen funktioniert hat.</summary>
    public bool DirektMoeglich => !string.IsNullOrWhiteSpace(Host) && DirektSshPort is > 0;

    /// <summary>Der SSH-Zugang, der genommen werden soll: erst direkt, dann Sprungrechner.</summary>
    public (string Host, int Port, string Weg) SshZugang => DirektMoeglich
        ? (Host!, DirektSshPort!.Value, "direkt")
        : (SshHost ?? "", SshPort ?? 22, "Sprungrechner");

    /// <summary>
    /// Reicht der Grafikspeicher für <c>nemotron3:33b</c>?
    ///
    /// <para>Das Modell belegt 27,6 GB auf der Platte; für die Gewichte plus Kontext sollten
    /// rund 32 GB Grafikspeicher da sein. Darunter lagert Ollama in den Hauptspeicher aus und
    /// die gemietete Karte bringt wenig — man zahlt Stundenpreis für CPU-Geschwindigkeit.</para>
    /// </summary>
    public bool ReichtFuerNemotron => GpuRamMb >= 32_000;
}

/// <summary>
/// Liest die eigenen vast.ai-Instanzen über deren REST-API.
///
/// <para><b>Was das löst.</b> Eine gemietete Instanz bekommt bei jedem Start eine neue Adresse
/// und einen neuen Port. Sie von Hand in die Endpunktliste zu tippen ist genau die Art
/// Fleißarbeit, bei der man sich vertippt und danach eine halbe Stunde sucht, warum der
/// Reasoning-Lauf in eine Zeitüberschreitung läuft.</para>
///
/// <para><b>Lesen, plus Anhalten und Fortsetzen einer BESTEHENDEN Instanz.</b> Abgerechnet wird
/// je Stunde Laufzeit; eine Instanz, die zwischen zwei Fragen durchläuft, kostet Geld für
/// nichts. Deshalb <see cref="SetzeZustandAsync"/>.</para>
///
/// <para><b>Mieten und Zerstören bleiben draußen.</b> Das eine schließt einen Vertrag, das
/// andere löscht Daten unwiederbringlich — beides gehört in die Oberfläche von vast.ai, wo
/// Preis und Laufzeit sichtbar sind, und nicht in eine Kursanalyse. Anhalten und Fortsetzen
/// sind dagegen umkehrbar und lassen die Platte unangetastet.</para>
///
/// <para><b>Der Pfad ist <c>/api/v1/</c>, nicht <c>v0</c>.</b> Gegen die echte API gemessen: v0
/// antwortet mit <c>HTTP 410 Gone</c> und <c>{"error":"deprecated_endpoint"}</c>. Der Aufruf
/// schlägt also nicht halb fehl, sondern gar nicht — und die Liste bliebe stillschweigend
/// leer.</para>
///
/// <para><b>Zwei Eigenheiten, an einer echten Instanz nachgesehen:</b></para>
/// <list type="number">
///   <item><b>Im angehaltenen Zustand ist <c>ports</c> <c>null</c>.</b> Die Weiterleitungen
///   existieren nur, solange der Container läuft, und werden beim Fortsetzen neu vergeben. Ein
///   gespeicherter Endpunkt zeigt danach ins Leere; die Verbindungsdaten müssen <b>nach</b> dem
///   Start neu geholt werden, nicht vor dem Anhalten gemerkt.</item>
///   <item><b>Angehalten kostet weiter — aber wenig.</b> Gemessen an einer echten Instanz:
///   0,0210 USD/h angehalten gegen 0,3543 USD/h laufend, das Anhalten spart <b>94 %</b>. Die
///   Zahlen hängen an Plattengröße und Angebot, die Größenordnung ist die Aussage.</item>
/// </list>
///
/// <para>Die Feldnamen sind gegen eine echte Antwort geprüft (<c>id</c>, <c>actual_status</c>,
/// <c>cur_state</c>, <c>gpu_name</c>, <c>num_gpus</c>, <c>public_ipaddr</c>, <c>ssh_host</c>,
/// <c>ssh_port</c>, <c>dph_total</c>). Weicht eine Antwort ab, bleibt die Liste leer statt zu
/// werfen — die Endpunktverwaltung muss ohne vast.ai vollständig funktionieren.</para>
/// </summary>
public interface IVastAiClient
{
    /// <summary>Ist überhaupt ein Schlüssel hinterlegt?</summary>
    bool SchluesselVorhanden { get; }

    Task<IReadOnlyList<VastInstanz>> InstanzenAsync(CancellationToken ct = default);

    /// <returns>Leer bei Erfolg, sonst die Begründung.</returns>
    Task<string?> SetzeZustandAsync(long instanzId, bool laufen, CancellationToken ct = default);

    Task<VastInstanz?> WarteBisLaeuftAsync(long instanzId, TimeSpan grenze,
                                           CancellationToken ct = default);
}

public sealed class VastAiClient : IVastAiClient
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<VastAiClient> _log;
    private readonly string? _schluessel;

    /// <summary>
    /// Die Ports, hinter denen Ollama stecken kann — <b>in dieser Reihenfolge</b>.
    ///
    /// <para>Im Abbild <c>vastai/ollama</c> steht vor Ollama ein Caddy: <c>PORTAL_CONFIG</c>
    /// meldet <c>localhost:21434:11434</c>, der Proxy horcht also auf 21434 und reicht an
    /// Ollamas 11434 weiter. <b>Gemessen:</b> Der Aussenport zu 21434 antwortete mit
    /// <c>401 Basic realm="restricted"</c>, der zu 11434 nahm überhaupt keine Verbindung an.
    /// Wer nur 11434 sucht, findet einen toten Port und hält die Instanz für kaputt.</para>
    /// </summary>
    private static readonly string[] OllamaContainerPorts = ["21434/tcp", "11434/tcp"];

    private const string Basis = "https://console.vast.ai/api/v1/instances/";

    /// <param name="schluessel">
    /// Der Kontoschlüssel. <b>Kommt aus der Umgebung, nicht aus <c>appsettings.json</c></b> —
    /// die Datei liegt in der Versionsverwaltung und wird mit ausgeliefert. Gelesen wird
    /// <c>CRS_VASTAI_KEY</c>, ersatzweise <c>VastAi:ApiKey</c> aus der Konfiguration; die
    /// Reihenfolge steht in <c>DependencyInjection</c>.
    /// </param>
    public VastAiClient(ILogger<VastAiClient> log, IHttpClientFactory http, string? schluessel)
    {
        _log = log;
        _http = http;
        _schluessel = string.IsNullOrWhiteSpace(schluessel) ? null : schluessel.Trim();
    }

    public bool SchluesselVorhanden => _schluessel is not null;

    public async Task<IReadOnlyList<VastInstanz>> InstanzenAsync(CancellationToken ct = default)
    {
        if (_schluessel is null) return [];

        try
        {
            using var client = Client(TimeSpan.FromSeconds(20));

            using var antwort = await client.GetAsync(Basis, ct);
            if (!antwort.IsSuccessStatusCode)
            {
                _log.LogWarning("vast.ai antwortete mit HTTP {Code}", (int)antwort.StatusCode);
                return [];
            }

            using var doc = JsonDocument.Parse(await antwort.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("instances", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var liste = new List<VastInstanz>();
            foreach (var e in arr.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();

                liste.Add(new VastInstanz(
                    Id: Zahl(e, "id"),
                    Status: Text(e, "actual_status") ?? Text(e, "cur_state") ?? "unbekannt",
                    GpuName: Text(e, "gpu_name") ?? "GPU",
                    GpuCount: (int)Zahl(e, "num_gpus"),
                    Host: Text(e, "public_ipaddr")?.Trim(),
                    OllamaPort: PortFuerOllama(e),
                    PricePerHour: Kommazahl(e, "dph_total"),
                    SshHost: Text(e, "ssh_host")?.Trim(),
                    SshPort: (int?)Zahl(e, "ssh_port") is var sp && sp > 0 ? sp : null,
                    GpuRamMb: (int)Zahl(e, "gpu_totalram"),
                    DirektSshPort: GemappterPort(e, "22/tcp"),

                    /* Steckt in einem Unterobjekt „instance“ — dort fällt gpuCostPerHour im
                       angehaltenen Zustand auf 0 und nur diskHour bleibt stehen. */
                    KostenJetztProStunde: e.TryGetProperty("instance", out var k)
                                          && k.ValueKind == JsonValueKind.Object
                        ? Kommazahl(k, "totalHour")
                        : 0));
            }

            return liste;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "vast.ai-Instanzen konnten nicht gelesen werden");
            return [];
        }
    }

    /// <summary>
    /// Hält eine Instanz an oder setzt sie fort — <c>PUT /api/v1/instances/{id}</c> mit
    /// <c>{"state": "running"}</c> bzw. <c>{"state": "stopped"}</c>.
    /// </summary>
    /// <remarks>
    /// <b>Nur über die API, nicht über SSH.</b> Eine SSH-Sitzung landet <i>im</i> Container. Von
    /// dort ließe sich zwar anhalten, aber nie wieder starten — man säße vor einer Maschine,
    /// deren einziger Zugang mit ihr abgeschaltet wurde. Der Weg über das Konto ist der einzige,
    /// der in beide Richtungen funktioniert.
    /// </remarks>
    public async Task<string?> SetzeZustandAsync(long instanzId, bool laufen,
                                                 CancellationToken ct = default)
    {
        if (_schluessel is null)
            return "Kein vast.ai-Schlüssel hinterlegt (Umgebungsvariable CRS_VASTAI_KEY).";

        try
        {
            using var client = Client(TimeSpan.FromSeconds(30));

            using var inhalt = new StringContent(
                laufen ? """{"state":"running"}""" : """{"state":"stopped"}""",
                System.Text.Encoding.UTF8, "application/json");

            using var antwort = await client.PutAsync($"{Basis}{instanzId}", inhalt, ct);

            if (!antwort.IsSuccessStatusCode)
            {
                var rumpf = await antwort.Content.ReadAsStringAsync(ct);
                return $"vast.ai antwortete mit HTTP {(int)antwort.StatusCode}: "
                     + (rumpf.Length > 300 ? rumpf[..300] : rumpf);
            }

            _log.LogInformation("vast.ai-Instanz {Id} {Was}", instanzId,
                                laufen ? "wird fortgesetzt" : "wird angehalten");
            return null;
        }
        catch (Exception ex)
        {
            return $"vast.ai nicht erreichbar: {ex.Message}";
        }
    }

    /// <summary>
    /// Wartet, bis die Instanz läuft, und liefert ihren <b>aktuellen</b> Stand zurück.
    /// </summary>
    /// <remarks>
    /// <b>Der Rückgabewert ist der eigentliche Zweck.</b> Nach dem Fortsetzen vergibt vast.ai
    /// neue Weiterleitungen — die vor dem Anhalten gemerkten Verbindungsdaten sind wertlos. Wer
    /// nach einem Start mit der alten Adresse verbindet, läuft in eine Zeitüberschreitung und
    /// sucht den Fehler an der falschen Stelle.
    /// <para>
    /// „running“ heißt außerdem nur, dass der Container läuft — Ollama darin braucht danach noch
    /// einen Moment, und das erste Laden von 27,6 GB Modell von Platte kommt obendrauf. Der
    /// Aufrufer muss weiterhin mit einem trägen ersten Aufruf rechnen.
    /// </para>
    /// </remarks>
    public async Task<VastInstanz?> WarteBisLaeuftAsync(long instanzId, TimeSpan grenze,
                                                        CancellationToken ct = default)
    {
        var bis = DateTimeOffset.UtcNow + grenze;

        while (DateTimeOffset.UtcNow < bis)
        {
            ct.ThrowIfCancellationRequested();

            var instanz = (await InstanzenAsync(ct)).FirstOrDefault(i => i.Id == instanzId);
            if (instanz is not null && instanz.Laeuft) return instanz;

            /* 5 s: Ein Containerstart dauert Sekunden bis Minuten. Häufiger zu fragen belastet
               nur die API und beschleunigt nichts. */
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        return null;
    }

    private HttpClient Client(TimeSpan grenze)
    {
        var c = _http.CreateClient();
        c.Timeout = grenze;
        c.DefaultRequestHeaders.Authorization = new("Bearer", _schluessel);
        return c;
    }

    /// <summary>
    /// Sucht den nach außen gemappten Port für Ollamas 11434.
    ///
    /// <para>vast.ai liefert die Zuordnung als verschachteltes Objekt:
    /// <c>ports["11434/tcp"][0].HostPort</c>. Fehlt sie, wurde der Port beim Anlegen der Instanz
    /// nicht freigegeben — dann ist die Instanz zwar da, aber von außen nicht erreichbar, und
    /// genau das soll sichtbar sein.</para>
    /// </summary>
    private static int? PortFuerOllama(JsonElement instanz)
    {
        foreach (var p in OllamaContainerPorts)
        {
            var gefunden = GemappterPort(instanz, p);
            if (gefunden is not null) return gefunden;
        }

        return null;
    }

    /// <summary>
    /// Der nach außen gemappte Port zu einem Containerport.
    ///
    /// <para>vast.ai liefert die Zuordnung als verschachteltes Objekt:
    /// <c>ports["22/tcp"][0].HostPort</c>. Fehlt sie, ist der Port beim Anlegen nicht
    /// freigegeben worden — dann ist er von außen nicht erreichbar, und genau das soll
    /// sichtbar sein.</para>
    /// </summary>
    private static int? GemappterPort(JsonElement instanz, string containerPort)
    {
        if (!instanz.TryGetProperty("ports", out var ports)
            || ports.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!ports.TryGetProperty(containerPort, out var eintrag)
            || eintrag.ValueKind != JsonValueKind.Array || eintrag.GetArrayLength() == 0)
        {
            return null;
        }

        var erster = eintrag[0];
        var hostPort = Text(erster, "HostPort") ?? Text(erster, "host_port");
        return int.TryParse(hostPort, out var p) ? p : null;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static long Zahl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static double Kommazahl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0d;
}
