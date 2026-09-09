using Ingest.Infrastructure.Services;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Wo Ollama läuft: auf diesem Rechner oder auf einer gemieteten GPU.
///
/// <b>Warum das eine Auswahl braucht.</b> Das Reasoning-Modell kostet auf der
/// CPU ein bis zwei Minuten je Runde, und der Agent ruft mehrfach Werkzeuge
/// auf. Ein kleineres Modell wäre die billige Antwort gewesen — es hat den
/// Rückhalt-Test nicht bestanden: <c>qwen3-vl:4b</c> las ein Fehlerverhältnis
/// von 1,0034 als „nahezu perfekt“ und behauptete das Gegenteil dessen, was das
/// Werkzeug geliefert hatte. Bleibt der Weg über mehr Rechenleistung.
/// </summary>
public static class OllamaEndpointEndpoints
{
    public static void MapOllamaEndpointEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/ollama").WithTags("Ollama-Endpunkte");

        g.MapGet("/endpunkte", (IOllamaEndpointService svc) => Results.Ok(new
        {
            aktiv = svc.Aktiv.Id,

            endpunkte = svc.Alle.Select(e => new
            {
                e.Id, e.Name, e.BaseUrl, e.IstStandard, e.NutztTunnel,
                e.SshHost, e.SshPort, e.SshUser, e.RemotePort,
                istAktiv = e.Id == svc.Aktiv.Id,

                /* Nur DASS eine Anmeldung hinterlegt ist, nie WELCHE. Der Schlüssel geht
                   hinein und kommt nicht wieder heraus — eine Oberfläche, die ihn anzeigt,
                   legt ihn in den Verlauf jedes Browsers, der die Seite je geladen hat. */
                hatAnmeldung = !string.IsNullOrWhiteSpace(e.Schluessel)
            }),

            /* Ein Rückfall muss sichtbar sein. Er verhindert den Ausfall, aber er
               verschweigt ihn nicht -- sonst rechnet die Anlage wochenlang lokal,
               während jemand die gemietete Karte bezahlt. */
            stoerung = svc.Stoerung is { } st
                ? new
                {
                    st.Id, st.Name, st.Grund, st.SeitUtc,
                    text = $"Endpunkt „{st.Name}“ ist nicht erreichbar — es wird "
                         + "ersatzweise auf diesem Rechner gerechnet. Die Auswahl bleibt "
                         + "bestehen, damit eine angehaltene Instanz nach dem Fortsetzen "
                         + "wieder greift."
                }
                : null,

            hinweis = "Bei Tunnelbetrieb wird ein SSH-Tunnel in den eigenen Prozess gelegt. "
                    + "Der lokale Port kommt vom Betriebssystem — eine feste Nummer würde mit "
                    + "einem lokal laufenden Ollama auf 11434 kollidieren, und genau das ist "
                    + "der Normalfall."
        }));

        g.MapPost("/endpunkte", (IOllamaEndpointService svc, OllamaEndpoint e) =>
        {
            svc.Speichere(e);
            return Results.Ok(new { e.Id, gespeichert = true });
        });

        g.MapPost("/waehlen", (IOllamaEndpointService svc, string id) =>
        {
            svc.Waehle(id);
            return Results.Ok(new { aktiv = svc.Aktiv.Id, svc.Aktiv.Name });
        });

        g.MapDelete("/endpunkte/{id}", (IOllamaEndpointService svc, string id) =>
        {
            svc.Entferne(id);
            return Results.Ok(new { entfernt = id });
        });

        /* Erreichbarkeit prüfen — der einzige Weg, es herauszufinden.

           Ein Endpunkt kann konfiguriert und trotzdem tot sein: Instanz
           abgelaufen, Schlüssel nicht hinterlegt, Ollama nicht gestartet. Das
           erst beim Fragen zu merken, kostet die Wartezeit einer Runde. */
        g.MapPost("/pruefen", async (IOllamaEndpointService svc, string id,
                                     CancellationToken ct) =>
        {
            var (ok, meldung, modelle) = await svc.PruefeAsync(id, ct);

            return Results.Ok(new
            {
                id,
                erreichbar = ok,
                meldung,
                modelle,

                hinweis = ok
                    ? null
                    : "Bei „Permission denied“ wurde die Instanz erstellt, BEVOR der "
                    + "öffentliche Schlüssel im Container. Nachreichen geht: POST auf "
                    + "/api/v0/instances/{id}/ssh/ mit dem öffentlichen Schlüssel."
            });
        });

        MapVast(app);
    }

    /* ================================================================= vast.ai ==

       Eine gemietete Instanz bekommt bei JEDEM Fortsetzen eine neue Adresse und einen
       neuen SSH-Port. Von Hand abgetippt ist das die Art Fleißarbeit, bei der man sich
       vertippt und danach den Fehler beim Modell sucht. Deshalb wird die Liste geholt
       und die Instanz übernommen, statt sie zu beschreiben.

       Mieten und Zerstören bleiben draußen: Das eine schließt einen Vertrag, das andere
       löscht Daten unwiederbringlich. Beides gehört in die Oberfläche von vast.ai, wo
       Preis und Laufzeit sichtbar sind — nicht in eine Kursanalyse.                    */

    private static void MapVast(IEndpointRouteBuilder app)
    {
        var v = app.MapGroup("/api/vast").WithTags("vast.ai");

        v.MapGet("/instanzen", async (IVastAiClient vast, IOllamaEndpointService svc,
                                      CancellationToken ct) =>
        {
            if (!vast.SchluesselVorhanden)
                return Results.Ok(new
                {
                    schluessel = false,
                    instanzen = Array.Empty<object>(),
                    hinweis = "Kein vast.ai-Schlüssel hinterlegt. Er gehört in die "
                            + "Umgebungsvariable CRS_VASTAI_KEY, nicht in appsettings.json — "
                            + "die Datei liegt in der Versionsverwaltung und wird "
                            + "mit ausgeliefert. Nach dem Setzen muss die Anwendung neu starten."
                });

            var liste = await vast.InstanzenAsync(ct);
            var bekannt = svc.Alle.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Results.Ok(new
            {
                schluessel = true,

                instanzen = liste.Select(i => new
                {
                    i.Id, i.Status, i.GpuName, i.GpuCount, i.Laeuft,
                    i.PricePerHour, i.KostenJetztProStunde, i.GpuRamMb,
                    i.SshHost, i.SshPort, i.TunnelMoeglich, i.BaseUrl,
                    i.ReichtFuerNemotron, i.DirektSshPort,
                    sshWeg = i.SshZugang.Weg,

                    uebernommen = bekannt.Contains($"vast{i.Id}"),

                    /* Der Grafikspeicher entscheidet, ob die Miete überhaupt etwas bringt:
                       Passt nemotron3:33b nicht hinein, lagert Ollama in den Hauptspeicher
                       aus — man zahlt dann Stundenpreis für CPU-Geschwindigkeit. */
                    warnung = i.GpuRamMb > 0 && !i.ReichtFuerNemotron
                        ? $"{i.GpuRamMb / 1024} GB Grafikspeicher reichen für nemotron3:33b "
                          + "(27,6 GB Gewichte plus Kontext) nicht sicher aus."
                        : null
                }),

                hinweis = liste.Count == 0
                    ? "Keine Instanzen im Konto. Eine wird auf vast.ai gemietet — dort sind "
                      + "Preis und Laufzeit sichtbar."
                    : "Angehalten kostet nur den Plattenanteil, gemessen rund 6 % des "
                      + "Laufpreises. Wer zwischen zwei Fragen laufen lässt, zahlt für nichts."
            });
        });

        /* Anhalten und Fortsetzen — beides umkehrbar und ohne Datenverlust.

           Der Weg geht über das Konto, nicht über SSH: Eine SSH-Sitzung landet IM Container.
           Von dort ließe sich anhalten, aber nie wieder starten — man säße vor einer Maschine,
           deren einziger Zugang mit ihr abgeschaltet wurde. */
        v.MapPost("/zustand", async (IVastAiClient vast, long id, bool laufen,
                                     CancellationToken ct) =>
        {
            var fehler = await vast.SetzeZustandAsync(id, laufen, ct);

            return fehler is null
                ? Results.Ok(new
                {
                    id,
                    laufen,
                    hinweis = laufen
                        ? "Der Start dauert Sekunden bis Minuten. Danach „übernehmen“ erneut "
                          + "aufrufen: Adresse und SSH-Port werden beim Fortsetzen NEU vergeben, "
                          + "die alten Angaben zeigen ins Leere."
                        : "Angehalten. Die Weiterleitungen sind damit weg — beim nächsten Start "
                          + "gibt es andere."
                })
                : Results.BadRequest(new { error = fehler });
        });

        /* Übernahme als Endpunkt — bevorzugt über den SSH-Tunnel.

           Der nach außen abgebildete Port spricht reines HTTP über das offene Netz; dort
           gingen die Fragen und die Analysewerte im Klartext. Der Tunnel braucht diesen Port
           außerdem gar nicht — er reicht durch SSH hindurch und funktioniert deshalb auch bei
           einer Instanz, bei der 11434 nie freigegeben wurde. Das ist der häufigere Fall.

           Geführt wird über die Instanz-Nummer, nicht über die Adresse: Bei Tunnelbetrieb ist
           die Adresse nur Beschriftung, und zwei Einträge derselben Instanz mit verschiedenen
           Beschriftungen wären keine Dublette mehr — obwohl es dieselbe Maschine ist.        */
        v.MapPost("/uebernehmen", async (IVastAiClient vast, IOllamaEndpointService svc,
                                         long id, CancellationToken ct) =>
        {
            var i = (await vast.InstanzenAsync(ct)).FirstOrDefault(x => x.Id == id);

            if (i is null)
                return Results.BadRequest(new { error = $"Keine Instanz {id} im Konto." });

            if (!i.TunnelMoeglich && i.BaseUrl is null)
                return Results.BadRequest(new
                {
                    error = "Die Instanz meldet weder SSH-Zugang noch einen freigegebenen "
                          + "Port 11434.",

                    hinweis = i.Laeuft
                        ? "Beim Anlegen der Instanz muss eines von beidem vorhanden sein."
                        : "Sie ist angehalten — im angehaltenen Zustand gibt es keine "
                          + "Weiterleitungen. Zuerst starten."
                });

            var name = $"vast.ai {i.GpuCount}× {i.GpuName}";

            /* Erst der direkte Behälterport, dann der Sprungrechner.

               Am 24.08.2026 gemessen: ssh2.vast.ai:39384 schloss jede Verbindung sofort,
               derselbe Container über public_ipaddr:16054 antwortete anstandslos. Die
               Reihenfolge ist also nicht Geschmackssache. */
            var (sshHost, sshPort, weg) = i.SshZugang;

            var e = i.TunnelMoeglich
                ? new OllamaEndpoint
                {
                    Id = $"vast{i.Id}",
                    Name = name,

                    // Nur Beschriftung: Die tatsächliche Adresse entsteht beim Verbinden.
                    BaseUrl = $"ssh://{sshHost}:{sshPort} → 127.0.0.1:11434",
                    NutztTunnel = true,
                    SshHost = sshHost,
                    SshPort = sshPort,
                    SshUser = "root",
                    RemotePort = 11434
                }
                : new OllamaEndpoint
                {
                    Id = $"vast{i.Id}",
                    Name = name,
                    BaseUrl = i.BaseUrl!
                };

            svc.Speichere(e);

            return Results.Ok(new
            {
                e.Id,
                e.Name,
                tunnel = e.NutztTunnel,

                meldung = e.NutztTunnel
                    ? $"Instanz {i.Id} übernommen — verschlüsselt über SSH-Tunnel, {weg} "
                      + $"({sshHost}:{sshPort})."
                    : $"Instanz {i.Id} übernommen — über den offenen Port {i.BaseUrl}. "
                      + "Achtung: unverschlüsselt.",

                hinweis = e.NutztTunnel
                    ? "Der Tunnel wird bei der ersten Frage aufgebaut. Meldet die Prüfung "
                      + "„Permission denied“, fehlt der öffentliche Schlüssel im Container. "
                      + "Das lässt sich nachreichen: POST /api/v0/instances/{id}/ssh/ mit "
                      + "dem Schlüssel — an einer laufenden Instanz erprobt."
                    : "Ohne Tunnel steht vor Ollama ein Caddy mit Basic-Anmeldung. Ins Feld "
                      + "Schlüssel gehört „vastai:<OPEN_BUTTON_TOKEN>“; ein Bearer-Token wird "
                      + "dort mit 401 abgewiesen."
            });
        });
    }
}
