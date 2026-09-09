using System.Text;
using System.Text.Json;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>Eine Nachricht im Gespräch.</summary>
public sealed record ChatTurn(string Role, string Content);

/// <summary>Was ein Werkzeug geliefert hat — für die Nachvollziehbarkeit.</summary>
public sealed record ToolTrace(string Name, string Arguments, string Result);

/// <summary>Die Antwort samt allem, worauf sie beruht.</summary>
public sealed record ReasoningAnswer(
    string Text,
    IReadOnlyList<ToolTrace> Tools,
    int Rounds,
    string Model,
    double Seconds);

public interface IReasoningService
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    IReadOnlyList<string> ToolNames { get; }

    /// <summary>Welche Modelle lokal bereitstehen.</summary>
    Task<IReadOnlyList<string>> ModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Eine Frage an den Agenten.
    /// </summary>
    /// <param name="sprachcode">
    /// In welcher Sprache geantwortet werden soll — das Kürzel einer
    /// vorhandenen Sprachdatei (<c>de</c>, <c>en</c>, …).
    ///
    /// <para>Es ist ein VORSCHLAG, kein Befehl: Ist die Frage erkennbar in
    /// einer anderen Sprache gestellt, gilt die Frage. Wer auf englischer
    /// Oberfläche eine deutsche Frage tippt, erwartet keine englische
    /// Antwort.</para>
    /// </param>
    Task<ReasoningAnswer> AskAsync(
        IReadOnlyList<ChatTurn> history, string question,
        string? sprachcode = null, CancellationToken ct = default);
}

/// <summary>
/// Der Gesprächsagent: beantwortet Fragen zu Prognosen, Analysen und dem
/// gesammelten Wissen.
///
/// <b>Was er darf und was nicht.</b> Er darf keine Zahl erfinden. Jede Zahl in
/// seiner Antwort muss aus einem Werkzeugaufruf stammen, und jeder Aufruf wird
/// mit ausgeliefert — Name, Argumente, Ergebnis. Wer die Antwort liest, kann
/// nachsehen, worauf sie beruht.
///
/// <b>Warum Werkzeuge und nicht ein Kontextblock.</b> Man könnte dem Modell zu
/// jeder Frage einfach alles mitgeben, was die Anwendung weiß. Das scheitert an
/// der Menge — allein die Kurvendiskussion hat 98.000 Einträge — und es
/// verwischt die Herkunft: In einem großen Block ist nicht mehr zu erkennen,
/// welche Zahl der Antwort zugrunde liegt und welche nur danebenstand.
///
/// <b>Die eingebaute Skepsis.</b> Die Anweisung an das Modell enthält die
/// gemessenen Grenzen dieses Systems, und zwar als Pflicht: Wenn ein Modell den
/// Stillstand nicht schlägt, muss die Antwort das sagen. Ein Agent, der eine
/// Prognose weitergibt, ohne ihren Rückhalt-Wert zu nennen, wäre in diesem
/// Projekt die gefährlichste Komponente — er klänge kompetent und wäre es nicht.
/// </summary>
public sealed class ReasoningService(
    HttpClient http,
    IOllamaEndpointService endpunkte,
    IAssetRepository assets,
    IPriceBarRepository bars,
    IDeepForecastService deep,
    IFeatureExportService features,
    IKnowledgeService knowledge,
    ICurveDiscussionService curve,
    ILocService loc,
    ILogger<ReasoningService> log) : IReasoningService
{
    /* Das Modell ist zur Laufzeit umschaltbar.

       Fest verdrahtet war es die falsche Wahl: Auf der CPU kostet ein
       33-Milliarden-Modell ein bis zwei Minuten je Runde, ein Vier-Milliarden-
       Modell die Hälfte. Welches man will, hängt davon ab, ob man auf eine
       Antwort wartet oder sie im Hintergrund entsteht — und das ändert sich von
       Frage zu Frage.

       Gemessen an denselben acht Fragen:

           nemotron3:33b   langsam (70-250 s), Urteile korrekt wiedergegeben
           qwen3:8b        mit Denken korrekt, aber unzuverlaessig: mal ruft es
                           das Werkzeug, mal beschreibt es nur, was das Werkzeug
                           taete. Ohne Denken 17 s und inhaltlich falsch.
           qwen3-vl:4b     schnell und gefaehrlich -- las ein Fehlerverhaeltnis
                           von 1,0034 als "nahezu perfekt"
           granite3.2      beherrscht keine Werkzeugaufrufe

       Die Voreinstellung ist deshalb das langsame, verlaessliche Modell. Wer
       Tempo braucht, mietet Rechenleistung (siehe IOllamaEndpointService) --
       nicht ein kleineres Modell. */
    private static string _model =
        Environment.GetEnvironmentVariable("CRS_REASONING_MODEL") ?? "nemotron3:33b";

    public static string Model
    {
        get => _model;
        set { if (!string.IsNullOrWhiteSpace(value)) _model = value.Trim(); }
    }

    /// <summary>Wie oft das Modell höchstens Werkzeuge aufrufen darf.</summary>
    private const int MaxRounds = 6;

    /// <summary>
    /// Ob das Modell vor der Antwort denken darf, sofern es das kennt.
    ///
    /// Voreinstellung an: Es kostet den Faktor acht an Zeit und ist es wert —
    /// ohne Denken verlor qwen3:8b sämtliche Zahlen und las „Sperrbereich" als
    /// Handelsverbot. Ollama übergeht das Feld bei Modellen, die es nicht
    /// kennen.
    /// </summary>
    public static bool Think { get; set; } = true;

    public IReadOnlyList<string> ToolNames =>
    [
        "kurs", "prognose", "modellzustand", "kurvenereignisse",
        "verknuepfungen", "wissen", "nachrichten", "werteliste"
    ];

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            /* Die Adresse kommt vom Endpunktdienst, nicht aus der festen
               Konfiguration des Klienten. Bei Tunnelbetrieb ist es eine lokale
               Adresse, hinter der eine gemietete GPU steht — der Aufrufer muss
               das nicht wissen. */
            var (basis, anmeldung) = await endpunkte.ZugangAsync(ct);

            using var res = await http.HolenAsync($"{basis}/api/tags", anmeldung, ct);
            if (!res.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));

            return doc.RootElement.GetProperty("models").EnumerateArray()
                      .Any(m => (m.GetProperty("name").GetString() ?? "") == Model);
        }
        catch { return false; }
    }

    public async Task<IReadOnlyList<string>> ModelsAsync(CancellationToken ct = default)
    {
        try
        {
            /* Die Adresse kommt vom Endpunktdienst, nicht aus der festen
               Konfiguration des Klienten. Bei Tunnelbetrieb ist es eine lokale
               Adresse, hinter der eine gemietete GPU steht — der Aufrufer muss
               das nicht wissen. */
            var (basis, anmeldung) = await endpunkte.ZugangAsync(ct);

            using var res = await http.HolenAsync($"{basis}/api/tags", anmeldung, ct);
            if (!res.IsSuccessStatusCode) return [];

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));

            /* Nur die Namen, sortiert. Welche davon Werkzeuge beherrschen,
               steht nirgends in der Antwort — das lässt sich nur ausprobieren,
               und die Oberfläche sagt es beim gemessenen Stand dazu. */
            return doc.RootElement.GetProperty("models").EnumerateArray()
                      .Select(m => m.GetProperty("name").GetString() ?? "")
                      .Where(n => n.Length > 0)
                      .OrderBy(n => n)
                      .ToList();
        }
        catch { return []; }
    }

    public async Task<ReasoningAnswer> AskAsync(
        IReadOnlyList<ChatTurn> history, string question,
        string? sprachcode = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        /* Eine eigene Frist für die ganze Anfrage.

           Sechs Runden mit Denkmodus können unter Last eine Viertelstunde
           dauern. Läuft dabei die Zeitüberschreitung des Klienten ab, endet der
           Aufruf in einer Ausnahme und der Anrufer bekommt 500 — ohne die
           Werkzeugausgaben, die bis dahin schon dastanden.

           Mit eigener Frist bricht der Agent selbst ab und liefert, was er hat.
           Ein Teilergebnis mit dem Hinweis „Zeit abgelaufen" ist mehr wert als
           ein Serverfehler. */
        using var frist = CancellationTokenSource.CreateLinkedTokenSource(ct);
        frist.CancelAfter(TimeSpan.FromMinutes(12));

        ct = frist.Token;

        var messages = new List<object>
        {
            new { role = "system", content = SystemPrompt(Sprachname(loc.Erkenne(question) ?? sprachcode)) }
        };

        // Nur die letzten Züge mitgeben. Ein Gespräch über zwanzig Fragen
        // sprengt sonst das Kontextfenster, und die frühen Züge tragen zur
        // aktuellen Frage meist nichts bei.
        foreach (var t in history.TakeLast(8))
            messages.Add(new { role = t.Role, content = t.Content });

        messages.Add(new { role = "user", content = question });

        var traces = new List<ToolTrace>();
        var runde = 0;

        /* Was schon abgefragt wurde, wird nicht noch einmal abgefragt.

           Auf „Was wird aktuell über Bitcoin geschrieben?“ rief das Modell
           sechsmal hintereinander dasselbe Werkzeug mit denselben Argumenten
           auf, verbrauchte alle Runden und lieferte am Ende gar nichts. Es
           hatte die Antwort bereits — es hat sie nur nicht als Antwort
           erkannt.

           Statt den Aufruf zu wiederholen bekommt es das gespeicherte Ergebnis
           mit dem Hinweis zurück, dass es dieselbe Frage schon gestellt hat.
           Das kostet keine Zeit und bringt es weiter. */
        var gesehen = new Dictionary<string, string>(StringComparer.Ordinal);

        while (runde < MaxRounds)
        {
            if (ct.IsCancellationRequested)
                return new ReasoningAnswer(
                    $"Die Frist von zwölf Minuten ist abgelaufen — der Agent hat "
                    + $"{runde} Runden gebraucht und keine Antwort gebildet. Die bisherigen "
                    + "Abfragen stehen unten; sie enthalten oft schon, was gesucht war. "
                    + "Eine engere Frage oder ein Modell ohne Denkmodus hilft.",
                    traces, runde, Model, sw.Elapsed.TotalSeconds);

            runde++;

            /* Denken an oder aus — und warum die Voreinstellung „an" ist.

               qwen3 erzeugt vor der Antwort einen Gedankengang. Abgeschaltet
               wird es achtmal schneller: 17 statt 164 Sekunden. Gemessen an der
               Antwort ist das ein schlechter Handel.

               Mit Denken gibt es die Urteile wörtlich wieder, samt „schlägt den
               Stillstand, aber nicht die blosse Drift". Ohne Denken kam:

                   „Die Modelle sind in einem Sperrbereich, was bedeutet, dass
                    sie nicht direkt zum Handeln verwendet werden können."

               Das ist keine Zusammenfassung, sondern ein Missverständnis — der
               Sperrbereich ist der Zeitraum, auf dem gemessen wird, kein
               Handelsverbot. Und alle Zahlen fehlen.

               Wer schnelle, ungefähre Antworten will, schaltet es ab. Die
               Voreinstellung ist die genaue. */
            var body = new
            {
                model = Model,
                stream = false,
                think = Think,
                messages,
                tools = ToolSchemas(),
                options = new { temperature = 0.2 }
            };

            var (basis, anmeldung) = await endpunkte.ZugangAsync(ct);

            HttpResponseMessage res;

            try
            {
                res = await http.SendenAsync($"{basis}/api/chat", body, anmeldung, ct);
            }
            catch (OperationCanceledException)
            {
                return new ReasoningAnswer(
                    "Die Frist ist abgelaufen, während das Modell antwortete. Was bis dahin "
                    + "abgefragt wurde, steht unten.",
                    traces, runde, Model, sw.Elapsed.TotalSeconds);
            }
            catch (HttpRequestException ex)
            {
                return new ReasoningAnswer(
                    $"Das Modell ist nicht erreichbar: {ex.Message[..Math.Min(200, ex.Message.Length)]}",
                    traces, runde, Model, sw.Elapsed.TotalSeconds);
            }

            using var _ = res;

            if (!res.IsSuccessStatusCode)
            {
                var fehler = await res.Content.ReadAsStringAsync(ct);
                log.LogWarning("Modell antwortet mit {Code}: {Body}", res.StatusCode, fehler);

                return new ReasoningAnswer(
                    $"Das Modell {Model} hat nicht geantwortet ({(int)res.StatusCode}): "
                    + (fehler.Length > 400 ? fehler[..400] : fehler),
                    traces, runde, Model, sw.Elapsed.TotalSeconds);
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var msg = doc.RootElement.GetProperty("message");

            var inhalt = msg.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

            var calls = msg.TryGetProperty("tool_calls", out var tc)
                        && tc.ValueKind == JsonValueKind.Array
                ? tc.EnumerateArray().ToList()
                : [];

            if (calls.Count == 0)
                return new ReasoningAnswer(inhalt, traces, runde, Model, sw.Elapsed.TotalSeconds);

            messages.Add(new { role = "assistant", content = inhalt, tool_calls = ParseCalls(calls) });

            foreach (var call in calls)
            {
                var fn = call.GetProperty("function");
                var name = fn.GetProperty("name").GetString() ?? "";

                var args = fn.TryGetProperty("arguments", out var a) ? a.ToString() : "{}";

                /* Der Schlüssel darf nicht an der Reihenfolge der Felder hängen.

                   Das Modell schickte dieselbe Abfrage dreimal, jedes Mal mit
                   anders sortierten Feldern — als Zeichenkette verglichen waren
                   das drei verschiedene, und die Dublettensperre griff nicht.
                   Sortiert man die Felder, ist es wieder dieselbe. */
                var schluessel = $"{name}|{Normalisiere(args)}";

                string ergebnis;

                if (gesehen.TryGetValue(schluessel, out var schon))
                {
                    ergebnis = "DIESE ABFRAGE HAST DU BEREITS GESTELLT. Hier ist das "
                             + "Ergebnis noch einmal. Rufe dieses Werkzeug nicht erneut "
                             + "mit denselben Argumenten auf — beantworte die Frage jetzt "
                             + "aus dem, was hier steht.\n\n" + schon;
                }
                else
                {
                    try
                    {
                        ergebnis = await RunToolAsync(name, args, ct);
                        gesehen[schluessel] = ergebnis;
                    }
                    catch (Exception ex)
                    {
                        // Ein fehlgeschlagenes Werkzeug ist kein Grund abzubrechen —
                        // das Modell soll die Fehlermeldung sehen und es anders
                        // versuchen können.
                        ergebnis = $"Fehler: {ex.Message}";
                    }
                }

                traces.Add(new ToolTrace(name, args,
                    ergebnis.Length > 4000 ? ergebnis[..4000] + " …" : ergebnis));

                messages.Add(new { role = "tool", tool_name = name, content = ergebnis });
            }
        }

        return new ReasoningAnswer(
            "Ich habe die zulässige Zahl an Werkzeugaufrufen erreicht, ohne zu einer "
            + "Antwort zu kommen. Die bisherigen Abfragen stehen unten — vielleicht hilft "
            + "eine engere Frage.",
            traces, runde, Model, sw.Elapsed.TotalSeconds);
    }

    // ------------------------------------------------------------ Anweisung --

    /// <summary>
    /// Aus dem Kürzel den Namen der Sprache, wie das Modell ihn versteht.
    ///
    /// <para><b>Der Name kommt aus der Sprachdatei</b> (<c>name="Italiano"</c>)
    /// und nicht aus einer Zuordnungstabelle im Quelltext. Damit wirkt eine neu
    /// abgelegte <c>loc.res.it.xml</c> sofort auch hier — eine Tabelle wäre die
    /// zweite Stelle, die man beim Hinzufügen einer Sprache pflegen müsste, und
    /// genau die vergisst man.</para>
    ///
    /// <para>Unbekannt oder nicht gesetzt heisst Deutsch. Ein Rückfall auf
    /// „irgendeine" Sprache gibt es nicht: Die Antwort in einer Sprache zu
    /// bekommen, die man nicht liest, ist schlimmer als eine in der falschen,
    /// die man erwartet hat.</para>
    /// </summary>
    private string Sprachname(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "Deutsch";

        try
        {
            return loc.Sprachen().FirstOrDefault(s =>
                string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase))
                ?.Name ?? "Deutsch";
        }
        catch (Exception ex)
        {
            /*  Die Sprachwahl darf die Antwort nicht kosten. Klemmt die
                Ablage, wird deutsch geantwortet -- nicht gar nicht.          */
            log.LogWarning(ex, "Sprachname für {Code} nicht ermittelbar", code);
            return "Deutsch";
        }
    }

    /*  Die Sprachregel steht als EIGENE, kurze Zeichenkette vor der
        Grundanweisung -- nicht als Interpolation in ihr.

        Der Grund ist eine Falle, die heute noch nicht zuschnappt: Ein
        interpoliertes Roh-Literal deutet jede geschweifte Klammer als
        Platzhalter. Im Prompt steht derzeit keine; sobald aber jemand ein
        JSON-Beispiel hineinschreibt -- und das ist bei einem Werkzeug-Agenten
        nur eine Frage der Zeit --, scheitert die Uebersetzung an einer Stelle,
        die mit der Sprache nichts zu tun hat. So bleibt die Grundanweisung
        buchstaeblich.                                                        */
    private static string SystemPrompt(string sprache) =>
        $"ANTWORTSPRACHE: {sprache}. Du antwortest ausschliesslich in dieser "
        + "Sprache.\n\n"
        + "Die Regeln unten gelten unverändert in jeder Sprache. Sie sind "
        + "Anweisung an dich und nicht Teil der Antwort. Übersetze auch die "
        + "Beschriftungen aus den Werkzeugausgaben — aber niemals die Zahlen "
        + "und niemals die Symbole der Werte.\n\n"
        + Grundanweisung;

    private const string Grundanweisung =
        """
        Du bist der Analyse-Assistent von CRSOFT.StockCrawler, einem System zur
        Marktbeobachtung und Prognose. Du antwortest sachlich und knapp.

        REGELN, die nicht verhandelbar sind:

        1. Du erfindest keine Zahlen. Jede Zahl in deiner Antwort stammt aus einem
           Werkzeugaufruf. Weißt du etwas nicht, rufst du ein Werkzeug auf oder sagst,
           dass du es nicht weißt.

        2. Zu jeder Prognose nennst du, was sie im Sperrbereich wert war. Das
           Fehlerverhältnis sagt, ob das Modell besser ist als die Annahme, der Kurs
           bleibe stehen. Ist es 1 oder größer, MUSST du sagen, dass das Modell diese
           Annahme nicht schlägt und die Zahl daher keine Handelsgrundlage ist.

        3. Es gibt ZWEI Latten, nicht eine. Die erste ist der Stillstand ("der Kurs
           bleibt stehen"). Die zweite ist die blosse Drift: die mittlere Rendite des
           Trainingszeitraums, eine einzige Zahl. Ueber lange Horizonte steigen Kurse
           im Mittel, deshalb schlaegt jedes Modell, das nur "aufwaerts" sagt, den
           Stillstand. Gemessen wurde: Bei 250 Tagen kam das Modell auf
           Fehlerverhaeltnis 0,9604 und 64,3 % Richtung, die blosse Drift auf 0,9139
           und 75,1 %. Das Modell war also SCHLECHTER als eine einzige Zahl. Nenne
           immer beide Latten, wenn du eine Guete beschreibst.

        4. Gleichzeitigkeit ist kein Vorlauf. Dieses System hat mehrfach gemessen, dass
           Kurse sich GEMEINSAM bewegen, nicht NACHEINANDER. Ein hoher Zusammenhang bei
           einem Zeitabstand um null erlaubt keine Vorhersage.

        5. Ein Fundstellen-Treffer aus der Wissenssammlung ist eine Textstelle, die zur
           Frage passt — kein Beleg, dass sie stimmt. Du nennst Buch und Seite.

        6. Du gibst keine Anlageempfehlung. Du beschreibst, was gemessen wurde.

        Was das System kann, in Stichworten:
        - Deep-Learning-Modelle in drei Bändern (kurz 1–5 Tage, mittel 10–60, lang 120–250)
        - Kurvendiskussion: Hoch-, Tief-, Wende-, Sattelpunkte, Steigungs- und
          Krümmungsausbrüche, Sprünge — je Wert und Datum, mit Stufe 1 bis 100
        - Verknüpfungen: welche Werte auffällig oft gleichzeitig Ereignisse haben
        - Wissenssammlung aus hochgeladener Fachliteratur
        - Nachrichtensammlung aus beobachteten Adressen

        WELCHES WERKZEUG WOFUER — die Wahl war die haeufigste Fehlerquelle:

            "Wie steht X?" / "Was macht der Kurs"        -> kurs
            "Was sagt das Modell zu X?"                  -> prognose
            "Welche Modelle gibt es / taugen sie?"       -> modellzustand
            "Auffaellige Stellen / Ausschlaege bei X"    -> kurvenereignisse
            "Was bewegt sich zusammen mit X?"            -> verknuepfungen
            "Was steht im Buch / in der Literatur zu Z?" -> wissen
            "Was wird ueber X geschrieben / berichtet?"  -> nachrichten
            "Welche Werte gibt es?"                      -> werteliste

        Rufe Werkzeuge auf, bevor du antwortest. Rufe dasselbe Werkzeug NIE zweimal
        mit denselben Argumenten auf -- wenn das Ergebnis schon dasteht, beantworte
        die Frage damit. Fasse am Ende kurz zusammen.
        """;

    // ------------------------------------------------------------ Werkzeuge --

    private static object[] ToolSchemas() =>
    [
        Tool("kurs", "Letzter Kurs eines Wertes und die Bewegung der letzten Tage.",
             ("symbol", "string", "Symbol, z. B. NVDA oder BTC-USD", true)),

        Tool("prognose", "Vorhersage aller Deep-Learning-Bänder für einen Wert, "
                       + "samt der im Sperrbereich gemessenen Güte.",
             ("symbol", "string", "Symbol des Wertes", true)),

        Tool("modellzustand", "Welche Modelle geladen sind und was sie im Sperrbereich "
                            + "geleistet haben."),

        Tool("kurvenereignisse", "Auffällige Stellen im Kursverlauf eines Wertes: "
                               + "Hoch-, Tief-, Wendepunkte, Ausbrüche, Sprünge.",
             ("symbol", "string", "Symbol des Wertes", true),
             ("art", "string", "Optional: hochpunkt, tiefpunkt, wendepunkt, sattelpunkt, "
                             + "steigungsausbruch, kruemmungsausbruch, sprung", false),
             ("anzahl", "integer", "Wie viele, Standard 10", false)),

        Tool("verknuepfungen", "Werte, deren auffällige Stellen zeitlich oft mit denen "
                             + "eines anderen zusammenfallen.",
             ("symbol", "string", "Optional: nur Paare mit diesem Wert", false),
             ("anzahl", "integer", "Wie viele, Standard 15", false)),

        /* Die beiden Suchwerkzeuge brauchen kontrastierende Beschreibungen.

           Mit „durchsucht die Fachliteratur“ und „durchsucht die Nachrichten“
           griff das Modell wiederholt zum falschen: Auf „Was wird aktuell über
           Bitcoin geschrieben?" rief es sechsmal `wissen` auf. Erst wenn jede
           Beschreibung sagt, wofür das ANDERE Werkzeug zuständig ist, wird die
           Wahl eindeutig. */
        Tool("wissen", "Durchsucht ZEITLOSE Fachliteratur: hochgeladene Bücher über "
                     + "Handelspsychologie, Marktmikrostruktur, Strategien. Für Fragen nach "
                     + "Begriffen, Theorien und Verfahren. NICHT für aktuelle Ereignisse — "
                     + "dafür ist `nachrichten` da.",
             ("frage", "string", "Begriff oder Thema aus der Fachliteratur", true)),

        Tool("nachrichten", "Durchsucht AKTUELLE Meldungen der letzten Tage aus Reuters, "
                          + "WSJ, CNBC, CoinDesk, Notenbanken und weiteren Feeds. Für Fragen "
                          + "wie: was wird ueber X geschrieben, was ist passiert, gibt es "
                          + "Neuigkeiten zu Y. NICHT fuer Lehrbuchwissen - dafuer ist `wissen` "
                          + "da.",
             ("frage", "string", "Wert, Ereignis oder Thema", true)),

        Tool("werteliste", "Welche Werte verfolgt werden.",
             ("suche", "string", "Optional: Teil des Symbols oder Namens", false))
    ];

    private static object Tool(string name, string beschreibung,
                               params (string Name, string Typ, string Doc, bool Pflicht)[] p)
    {
        var props = new Dictionary<string, object>();
        var pflicht = new List<string>();

        foreach (var x in p)
        {
            props[x.Name] = new { type = x.Typ, description = x.Doc };
            if (x.Pflicht) pflicht.Add(x.Name);
        }

        return new
        {
            type = "function",
            function = new
            {
                name,
                description = beschreibung,
                parameters = new { type = "object", properties = props, required = pflicht }
            }
        };
    }

    /* Die Aufrufe unveraendert zurueckspielen.

       Der erste Entwurf baute sie neu und machte aus `arguments` eine
       Zeichenkette. Ollama erwartet dort ein Objekt und antwortete in der
       zweiten Runde mit 400 -- der Agent rief also genau einmal ein Werkzeug
       auf und brach danach ab. Am einfachsten ist es, gar nichts umzubauen:
       Was das Modell geschickt hat, geht unveraendert zurueck. */
    private static object[] ParseCalls(List<JsonElement> calls) =>
        calls.Select(c => (object)JsonSerializer.Deserialize<JsonElement>(c.GetRawText()))
             .ToArray();

    private async Task<string> RunToolAsync(string name, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var a = doc.RootElement;

        /* Argumentnamen tolerant lesen.

           Das Modell hält sich nicht an das Schema: Auf die Frage nach
           auffälligen Stellen von BTC-USD rief es `prognose` mit
           `{"wert":"BTC-USD","sperrbereich":"letzte 7 Tage","qualitaet":"hohe"}`
           auf -- „wert" statt „symbol", dazu zwei erfundene Felder. Ein
           strenger Leser findet dann nichts und meldet „Kein verfolgter Wert
           mit dem Symbol ." Das Modell versucht es erneut, mit denselben
           Namen, und verbraucht alle Runden.

           Die Namen, die ein Modell plausibel wählt, sind absehbar. Sie
           mitzulesen kostet nichts und rettet den Aufruf. Erfundene
           Zusatzfelder werden schlicht übergangen. */
        string? Str(params string[] namen)
        {
            foreach (var k in namen)
                if (a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString();

            /* Zweiter Versuch ohne Rücksicht auf Groß- und Kleinschreibung —
               „Symbol" gegen „symbol" ist keine Absicht, sondern Zufall. */
            foreach (var f in a.EnumerateObject())
                if (namen.Any(k => string.Equals(k, f.Name, StringComparison.OrdinalIgnoreCase))
                    && f.Value.ValueKind == JsonValueKind.String)
                    return f.Value.GetString();

            return null;
        }

        int Int(string k, int d) => a.TryGetProperty(k, out var v)
            && (v.ValueKind == JsonValueKind.Number ? v.TryGetInt32(out var n) : int.TryParse(v.GetString(), out n))
            ? n : d;

        // Die Namen, die ein Modell plausibel wählt — alle gelten.
        string[] symbolNamen = ["symbol", "wert", "ticker", "name", "asset", "kurs"];
        string[] frageNamen = ["frage", "suche", "query", "thema", "begriff", "text"];

        return name switch
        {
            "kurs" => await KursAsync(Str(symbolNamen), ct),
            "prognose" => await PrognoseAsync(Str(symbolNamen), ct),
            "modellzustand" => Modellzustand(),
            "kurvenereignisse" => await EreignisseAsync(
                Str(symbolNamen), Str("art", "typ", "kind"), Int("anzahl", 10), ct),
            "verknuepfungen" => await VerknuepfungenAsync(Str(symbolNamen), Int("anzahl", 15), ct),
            "wissen" => await SucheAsync("knowledge", Str(frageNamen), ct),
            "nachrichten" => await SucheAsync("semantic", Str(frageNamen), ct),
            "werteliste" => await ListeAsync(Str(frageNamen), ct),
            _ => $"Unbekanntes Werkzeug: {name}"
        };
    }

    /// <summary>
    /// Sucht einen Wert. Bei mehrdeutigen Eingaben wird der erste passende
    /// genommen — ein Modell schreibt „Bitcoin" statt „BTC-USD".
    /// </summary>
    /// <summary>
    /// Bringt die Argumente in eine feste Form, damit dieselbe Abfrage auch
    /// dieselbe bleibt, wenn das Modell die Felder anders sortiert.
    /// </summary>
    private static string Normalisiere(string argsJson)
    {
        try
        {
            using var d = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);

            if (d.RootElement.ValueKind != JsonValueKind.Object) return argsJson;

            return string.Join("|", d.RootElement.EnumerateObject()
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => $"{f.Name.ToLowerInvariant()}={f.Value}"));
        }
        catch
        {
            return argsJson;
        }
    }

    private async Task<Core.Models.Asset?> FindeAsync(string? symbol, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        var alle = await assets.GetTrackedAsync(ct);

        return alle.FirstOrDefault(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            ?? alle.FirstOrDefault(x => x.Symbol.StartsWith(symbol, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> KursAsync(string? symbol, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return "Kein Symbol übergeben. Das Feld heißt `symbol`, zum Beispiel "
                 + "{\"symbol\": \"NVDA\"}.";

        var a = await FindeAsync(symbol, ct);

        if (a is null)
            return $"Kein verfolgter Wert mit dem Symbol {symbol}. Mit `werteliste` "
                 + "nachsehen, welche es gibt.";

        var reihe = await bars.GetAsync(a.AssetId, BarInterval.Daily,
                                        DateTime.UtcNow.AddDays(-60), DateTime.UtcNow, ct);

        if (reihe.Count < 2) return $"Für {a.Symbol} liegen keine aktuellen Bars vor.";

        var letzte = reihe[^1];

        string Aend(int n) => reihe.Count > n
            ? $"{(letzte.Close / reihe[^(n + 1)].Close - 1) * 100:+0.00;-0.00} %"
            : "–";

        return $"{a.Symbol} ({a.Name}), {a.AssetClass}\n"
             + $"Letzter Schluss {letzte.Close:F4} am {letzte.TsUtc:yyyy-MM-dd}\n"
             + $"1 Tag {Aend(1)} · 5 Tage {Aend(5)} · 20 Tage {Aend(20)}\n"
             + $"Bars in den letzten 60 Tagen: {reihe.Count}";
    }

    private async Task<string> PrognoseAsync(string? symbol, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return "Kein Symbol übergeben. Das Feld heißt `symbol`.";

        var a = await FindeAsync(symbol, ct);

        if (a is null)
            return $"Kein verfolgter Wert mit dem Symbol {symbol}. Mit `werteliste` "
                 + "nachsehen, welche es gibt.";

        if (!deep.IsLoaded) return "Kein Deep-Learning-Modell geladen.";

        var sb = new StringBuilder($"Vorhersage für {a.Symbol}:\n");

        foreach (var info in deep.Models)
        {
            var w = await features.BuildWindowAsync(a.AssetId, BarInterval.Daily, info.SeqLen, ct);

            if (w is null)
            {
                sb.AppendLine($"[{info.Band}] zu wenig Historie ({info.SeqLen} Bars nötig)");
                continue;
            }

            var p = deep.Predict(info.Band, a.AssetId, w.Rows.ToArray(), w.LastClose);

            if (p.Count == 0)
            {
                sb.AppendLine($"[{info.Band}] Modell kennt diesen Wert nicht");
                continue;
            }

            var traegt = info.CarriesAny();

            sb.AppendLine($"[{info.Band}] {info.BandLabel}");
            sb.AppendLine($"  Im Sperrbereich: Fehlerverhältnis {info.TestErrorRatio:F4}, "
                        + $"Richtung {info.TestHitRate * 100:F2} % — "
                        + (traegt ? "TRÄGT" : "TRÄGT NICHT: " + info.Verdict()));

            // Die Driftlatte je Horizont danebenstellen, sonst liest der Agent
            // ein Fehlerverhältnis von 0,96 als Erfolg.
            for (var k = 0; k < info.Horizons.Count; k++)
            {
                if (k >= info.DriftErrorRatioByHorizon.Count) break;

                sb.AppendLine($"    h={info.Horizons[k]}: Modell "
                            + $"{(k < info.TestErrorRatioByHorizon.Count ? info.TestErrorRatioByHorizon[k] : double.NaN):F4}"
                            + $"/{(k < info.TestHitRateByHorizon.Count ? info.TestHitRateByHorizon[k] * 100 : double.NaN):F1} % "
                            + $"gegen blosse Drift {info.DriftErrorRatioByHorizon[k]:F4}"
                            + $"/{info.DriftHitRateByHorizon[k] * 100:F1} % — "
                            + (info.Carries(k) ? "Modell besser" : "DRIFT BESSER"));
            }

            foreach (var x in p)
            {
                var aussage = x.StdDev > 1e-12 ? Math.Abs(x.PredictedReturn) / x.StdDev : 0;

                sb.AppendLine($"  {x.HorizonBars} Bars: {x.PredictedClose:F4} "
                            + $"({x.ChangePct:+0.00;-0.00} %), Streuung ±{(Math.Exp(x.StdDev) - 1) * 100:F2} %, "
                            + $"Aussagekraft {aussage:F2}"
                            + (aussage < 1 ? " — Vorzeichen trägt nicht" : ""));
            }
        }

        var letzte = (await bars.GetAsync(a.AssetId, BarInterval.Daily,
                                          DateTime.UtcNow.AddDays(-10), DateTime.UtcNow, ct))
                     .LastOrDefault();

        if (letzte is not null)
            sb.AppendLine($"Letzter Kurs {letzte.Close:F4} vom {letzte.TsUtc:yyyy-MM-dd}");

        return sb.ToString();
    }

    private string Modellzustand()
    {
        if (!deep.IsLoaded) return "Kein Modell geladen.";

        var sb = new StringBuilder();

        foreach (var m in deep.Models)
            sb.AppendLine($"[{m.Band}] {m.Version}, Horizonte {string.Join(",", m.Horizons)}, "
                        + $"Fenster {m.SeqLen}, {m.AssetIds.Count} Werte. "
                        + $"Sperrbereich: Fehlerverhältnis {m.TestErrorRatio:F4}, "
                        + $"Richtung {m.TestHitRate * 100:F2} %. "
                        + $"Training bis {m.TrainUntil[..Math.Min(10, m.TrainUntil.Length)]}. "
                        + $"Urteil: {m.Verdict()}");

        var fehlt = new[] { "kurz", "mittel", "lang" }.Where(b => deep.Model(b) is null).ToList();
        if (fehlt.Count > 0) sb.AppendLine($"Nicht trainiert: {string.Join(", ", fehlt)}");

        return sb.ToString();
    }

    private async Task<string> EreignisseAsync(
        string? symbol, string? art, int anzahl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return "Kein Symbol übergeben. Das Feld heißt `symbol`.";

        var a = await FindeAsync(symbol, ct);

        if (a is null)
            return $"Kein verfolgter Wert mit dem Symbol {symbol}. Mit `werteliste` "
                 + "nachsehen, welche es gibt.";

        var p = await curve.PageAsync(null, 1, Math.Clamp(anzahl, 1, 50),
                                      a.AssetId, art, null, 0, null, null, "stufe", ct);

        if (p.Rows.Count == 0)
            return $"Für {a.Symbol} liegen keine Kurvenereignisse vor. "
                 + "Wurde ein Durchgang gerechnet?";

        var sb = new StringBuilder($"Auffällige Stellen bei {a.Symbol} "
                                 + $"(Lauf {p.RunId}, {p.Total} insgesamt), stärkste zuerst:\n");

        foreach (var r in p.Rows)
            sb.AppendLine($"  {r.TsUtc:yyyy-MM-dd} {r.EventType} "
                        + $"(Richtung {(r.Sign >= 0 ? "+" : "-")}), Stufe {r.Severity:F0}, "
                        + $"Kurs {r.ClosePrice:F4}, "
                        + $"Steigung {(Math.Exp(r.Slope) - 1) * 100:+0.000;-0.000} % je Bar");

        return sb.ToString();
    }

    private async Task<string> VerknuepfungenAsync(string? symbol, int anzahl, CancellationToken ct)
    {
        int? id = null;

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var a = await FindeAsync(symbol, ct);
            if (a is null) return $"Kein verfolgter Wert mit dem Symbol {symbol}.";
            id = a.AssetId;
        }

        var top = (await curve.LinksAsync(null, Math.Clamp(anzahl, 1, 40), id, ct)).ToList();

        if (top.Count == 0) return "Keine Verknüpfung gefunden.";

        var sb = new StringBuilder("Werte mit auffällig gleichzeitigen Ereignissen:\n");

        foreach (var x in top)
        {
            double lag = x.median_lag_bars;
            double anteil = x.lead_share;

            var vorlauf = Math.Abs(lag) >= 1 && (anteil > 0.65 || anteil < 0.35);

            sb.AppendLine($"  {x.symbol_a} / {x.symbol_b} ({x.type_a}/{x.type_b}): "
                        + $"Faktor {x.lift:F2}× über der Erwartung, {x.pairs} Treffer, "
                        + $"Median-Abstand {lag:+0.0;-0.0} Bars, {anteil * 100:F0} % danach — "
                        + (vorlauf ? "möglicher Vorlauf"
                                   : "GLEICHZEITIG, kein Vorlauf, nicht für Prognosen"));
        }

        return sb.ToString();
    }

    private async Task<string> SucheAsync(string saeule, string? frage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(frage)) return "Keine Suchfrage angegeben.";

        var t = await knowledge.SearchAsync(saeule, frage, 5, ct);

        if (t.Count == 0)
            return saeule == "knowledge"
                ? "Nichts gefunden. Ist Fachliteratur hochgeladen und eingebettet?"
                : "Nichts gefunden. Sind Adressen eingetragen und abgeholt?";

        var sb = new StringBuilder();
        sb.AppendLine($"Fundstellen zu \u201e{frage}\u201c:");

        foreach (var h in t)
        {
            var txt = h.Content.Length > 700 ? h.Content[..700] + " …" : h.Content;

            sb.AppendLine($"— {h.Title}"
                        + (h.PageFrom is not null ? $", Seite {h.PageFrom}" : "")
                        + $" (Ähnlichkeit {h.Score:F3}):");
            sb.AppendLine("  " + txt.Replace("\n", " "));
        }

        return sb.ToString();
    }

    private async Task<string> ListeAsync(string? suche, CancellationToken ct)
    {
        var alle = await assets.GetTrackedAsync(ct);

        var gefiltert = string.IsNullOrWhiteSpace(suche)
            ? alle
            : alle.Where(x =>
                x.Symbol.Contains(suche, StringComparison.OrdinalIgnoreCase)
                || (x.Name ?? "").Contains(suche, StringComparison.OrdinalIgnoreCase)).ToList();

        if (gefiltert.Count == 0) return "Kein Wert passt.";

        var sb = new StringBuilder($"{gefiltert.Count} verfolgte Werte");

        if (gefiltert.Count > 60)
        {
            sb.AppendLine(" (die ersten 60):");
            gefiltert = gefiltert.Take(60).ToList();
        }
        else sb.AppendLine(":");

        foreach (var g in gefiltert.GroupBy(x => x.AssetClass))
            sb.AppendLine($"{g.Key}: {string.Join(", ", g.Select(x => x.Symbol))}");

        return sb.ToString();
    }
}
