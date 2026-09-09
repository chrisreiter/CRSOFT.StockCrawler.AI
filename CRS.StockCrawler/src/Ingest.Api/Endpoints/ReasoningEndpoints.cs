using Ingest.Infrastructure.Services;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Api.Endpoints;

public sealed record ChatMessage(string Role, string Content);
/// <param name="Sprache">
/// Kürzel der eingestellten Oberflächensprache. Fehlt es, wird deutsch
/// geantwortet — dasselbe Verhalten wie vor der Sprachumstellung.
/// </param>
public sealed record ChatRequest(string Frage, ChatMessage[]? Verlauf, string? Sprache = null);

/// <summary>
/// Säule „Reasoning": ein Gesprächsagent auf allem, was das System weiß.
///
/// <b>Warum die Werkzeugaufrufe mit ausgeliefert werden.</b> Eine Antwort in
/// Prosa lässt sich nicht prüfen — man müsste dem Modell glauben. Mit den
/// Aufrufen daneben steht jede Zahl in der Antwort neben der Abfrage, aus der
/// sie stammt. Wer misstraut, kann nachsehen; und das soll man hier.
/// </summary>
public static class ReasoningEndpoints
{
    public static void MapReasoningEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/reasoning").WithTags("Reasoning");

        /* Das Modell wechseln. Wirkt sofort und für alle Sitzungen — es ist
           eine Eigenschaft der Anwendung, nicht des Fensters. */
        g.MapPost("/modell", (IReasoningService svc, string name, bool? denken = null) =>
        {
            ReasoningService.Model = name;

            if (denken is not null) ReasoningService.Think = denken.Value;

            return Results.Ok(new
            {
                modell = ReasoningService.Model,
                denken = ReasoningService.Think,
                hinweis = "Gilt ab der nächsten Frage. Ob das Modell Werkzeugaufrufe "
                        + "beherrscht, zeigt sich erst beim Fragen — Ollama sagt es nicht "
                        + "im Voraus."
            });
        });

        g.MapGet("/health", async (IReasoningService svc, CancellationToken ct) =>
        {
            var ok = await svc.IsAvailableAsync(ct);

            return Results.Ok(new
            {
                bereit = ok,
                modell = ReasoningService.Model,
                denken = ReasoningService.Think,
                verfuegbar = await svc.ModelsAsync(ct),
                werkzeuge = svc.ToolNames,

                hinweis = ok
                    ? "Der Agent darf keine Zahl erfinden. Jede Zahl in seiner Antwort "
                    + "stammt aus einem Werkzeugaufruf, und die Aufrufe stehen unter "
                    + "jeder Antwort."
                    : $"Das Modell {ReasoningService.Model} ist nicht geladen. Mit "
                    + $"`ollama pull {ReasoningService.Model}` holen oder oben ein anderes "
                    + "wählen."
            });
        });

        /* Die Tagesübersicht.

           Bewusst kein Aufruf ans Sprachmodell: Was hier steht, sind Messwerte
           aus den Säulen. Ein Modell dazwischenzuschalten hieße, Zahlen durch
           eine Formulierung zu ersetzen, die man nicht mehr nachrechnen kann. */
        g.MapGet("/heute", async (IBriefingService svc, ISqlConnectionFactory factory,
                                  string? gewichte = null,
                                  string? ids = null,
                                  CancellationToken ct = default) =>
        {
            /* Die Gewichte kommen aus der Oberfläche, weil sie dort eingestellt
               werden und im Sitzungszustand liegen. Sie hier erneut aus der
               Datenbank zu holen hieße, zwei Quellen der Wahrheit zu haben. */
            /* Ohne Parameter gelten die abgelegten Gewichte.

               Vorher standen sie hier auf null, wenn niemand welche mitschickte
               -- und die Tagesübersicht meldete das als Zustand des Systems
               statt als fehlende Eingabe. Wer den Browser wechselte, sah eine
               Anwendung, in der keine Säule zählt. */
            var w = await PillarWeightEndpoints.AusParameterOderAblageAsync(factory, gewichte, ct);

            var assetIds = ids?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                               .Select(x => int.TryParse(x, out var v) ? v : -1)
                               .Where(v => v > 0).ToArray();

            var b = await svc.BuildAsync(w, assetIds, ct);

            return Results.Ok(new
            {
                stand = b.GeneratedUtc,
                zusammenfassung = b.Summary,
                gewichte = b.PillarWeights,

                punkte = b.Items.Select(i => new
                {
                    art = i.Kind,
                    saeule = i.Pillar,
                    i.Title,
                    i.Detail,
                    gewicht = i.Weight,
                    i.Symbol,
                    i.Link
                }),

                einschraenkungen = b.Caveats
            });
        });

        /* Das Tagesjournal.

           Wie die Übersicht ohne Sprachmodell: Jede Zahl stammt aus einer
           Abfrage, der Satz drumherum ist Vorlage. Ein Journal, das man einem
           Blog vorwirft, muss nachrechenbar bleiben. */
        g.MapGet("/journal", async (IJournalService svc, ISqlConnectionFactory factory,
                                    string? gewichte = null,
                                    string? ids = null,
                                    string format = "json",
                                    CancellationToken ct = default) =>
        {
            /* Ohne Parameter gelten die abgelegten Gewichte.

               Vorher standen sie hier auf null, wenn niemand welche mitschickte
               -- und die Tagesübersicht meldete das als Zustand des Systems
               statt als fehlende Eingabe. Wer den Browser wechselte, sah eine
               Anwendung, in der keine Säule zählt. */
            var w = await PillarWeightEndpoints.AusParameterOderAblageAsync(factory, gewichte, ct);

            var assetIds = ids?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                               .Select(x => int.TryParse(x, out var v) ? v : -1)
                               .Where(v => v > 0).ToArray();

            var j = await svc.BuildAsync(w, assetIds, ct);

            /* Markdown als eigenes Format, nicht in JSON verpackt: So lässt es
               sich unmittelbar in ein Blog stellen, ohne dass jemand es erst
               auspacken muss. */
            if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase)
                || format.Equals("md", StringComparison.OrdinalIgnoreCase))
                return Results.Text(j.Markdown, "text/markdown; charset=utf-8");

            return Results.Ok(new
            {
                tag = j.ForDateUtc,
                j.Title,
                j.Lead,

                abschnitte = j.Sections.Select(a => new
                {
                    ueberschrift = a.Heading,
                    text = a.Body,
                    notiz = a.Note
                }),

                quellen = j.Sources.Distinct().Take(20),
                markdown = j.Markdown
            });
        });

        g.MapPost("/ask", async (HttpContext ctx, IReasoningService svc,
                                 IReasoningLogService journal,
                                 IOllamaEndpointService endpunkte,
                                 ChatRequest r, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(r.Frage))
                return Results.BadRequest(new { error = "Keine Frage" });

            if (!await svc.IsAvailableAsync(ct))
                return Results.BadRequest(new
                {
                    error = "Das Sprachmodell ist nicht erreichbar",
                    hinweis = "Läuft Ollama? Ist das Modell geladen?"
                });

            var verlauf = (r.Verlauf ?? [])
                .Where(m => m.Role is "user" or "assistant")
                .Select(m => new ChatTurn(m.Role, m.Content))
                .ToList();

            var a = await svc.AskAsync(verlauf, r.Frage, r.Sprache, ct);

            var werkzeugspur = a.Tools
                .Select(t => new { t.Name, argumente = t.Arguments, ergebnis = t.Result })
                .ToList();

            /* Abgelegt wird JEDE Antwort, nicht nur die auf Knopfdruck gemerkte.

               Ein „Speichern"-Knopf setzte voraus, dass man vor dem Lesen weiss, ob die
               Antwort es wert ist -- das weiss man nie. Und die teure Ressource ist die
               Rechenzeit, die an dieser Stelle schon verbraucht ist: Vier Minuten Denken
               wegzuwerfen, weil niemand vorher auf einen Knopf gedrückt hat, wäre die
               teuerste Sparsamkeit der Anwendung.

               Der Dienst schluckt seine eigenen Fehler. Eine Antwort, die da ist, darf
               nicht daran scheitern, dass die Ablage klemmt. */
            var logId = await journal.SchreibeAsync(
                ctx.Benutzer()?.UserId, r.Frage, a.Text, a.Model,
                endpunkte.Aktiv.Name, a.Seconds, a.Rounds, werkzeugspur, ct);

            return Results.Ok(new
            {
                antwort = a.Text,
                runden = a.Rounds,
                modell = a.Model,
                sekunden = Math.Round(a.Seconds, 1),
                logId,

                /* Die Werkzeugspur ist kein Debug-Zubehör, sondern Teil der
                   Antwort. Ohne sie wäre nicht zu unterscheiden, ob eine Zahl
                   gemessen oder erzeugt wurde. */
                werkzeuge = werkzeugspur
            });
        });

        MapJournal(g);
    }

    /* ============================================================= Journal ==

       Ein Gespräch lebte bis hierher ausschliesslich im Browser-Tab: Neuladen löschte es,
       ein zweiter Rechner sah es nie. Das „Tagesjournal" oben ist etwas anderes -- es
       entsteht OHNE Sprachmodell aus Abfragen.

       Mitgeschrieben wird auch die Werkzeugspur. Ohne sie wäre eine abgelegte Antwort eine
       Behauptung: Der ganze Sinn dieser Säule ist, dass jede Zahl aus einem Werkzeugaufruf
       stammt und nachprüfbar bleibt.                                                     */

    private static void MapJournal(RouteGroupBuilder g)
    {
        var j = g.MapGroup("/log").WithTags("Reasoning-Journal");

        j.MapGet("/", async (IReasoningLogService svc, string? suche, bool? nurGemerkte,
                             int? limit, int? versatz, CancellationToken ct) =>
        {
            var liste = await svc.ListeAsync(suche, nurGemerkte ?? false,
                                             limit ?? 25, versatz ?? 0, ct);

            return Results.Ok(new
            {
                eintraege = liste.Select(e => new
                {
                    e.LogId, e.AskedUtc, e.Wer, e.Frage, e.Modell, e.Endpunkt,
                    e.Sekunden, e.Runden, e.Gemerkt, e.Notiz,

                    /* In der Liste nur der Anfang der Antwort. Wer alles will, holt den
                       einzelnen Eintrag -- sonst schleppt eine Übersicht über fünfzig
                       Gespräche jedes Mal deren vollständigen Text mit. */
                    anriss = e.Antwort.Length > 220 ? e.Antwort[..220] + " …" : e.Antwort,
                    werkzeugzahl = Zaehle(e.Werkzeuge)
                }),

                hinweis = "Abgelegt wird jede Frage. Gemerkte Einträge überleben das "
                        + "Aufräumen, alle anderen lassen sich nach Tagen wegräumen."
            });
        });

        j.MapGet("/{logId:int}", async (IReasoningLogService svc, int logId,
                                        CancellationToken ct) =>
        {
            var e = await svc.EinzelnAsync(logId, ct);

            return e is null
                ? Results.NotFound(new { error = $"Kein Eintrag {logId}." })
                : Results.Ok(new
                {
                    e.LogId, e.AskedUtc, e.Wer, e.Frage, e.Antwort, e.Modell, e.Endpunkt,
                    e.Sekunden, e.Runden, e.Gemerkt, e.Notiz,
                    werkzeuge = Spur(e.Werkzeuge),

                    /* Zum Weiterverwenden -- dieselbe Form wie beim Tagesjournal. Die
                       Werkzeugspur gehört mit hinein: Eine herauskopierte Antwort ohne
                       ihre Belege ist genau das, wovor die Säule warnt. */
                    markdown = AlsMarkdown(e)
                });
        });

        j.MapPost("/{logId:int}/merken", async (IReasoningLogService svc, int logId,
                                                bool? gemerkt, string? notiz,
                                                CancellationToken ct) =>
            await svc.MerkenAsync(logId, gemerkt ?? true, notiz, ct)
                ? Results.Ok(new { logId, gemerkt = gemerkt ?? true })
                : Results.NotFound(new { error = $"Kein Eintrag {logId}." }));

        j.MapDelete("/{logId:int}", async (IReasoningLogService svc, int logId,
                                           CancellationToken ct) =>
            await svc.LoeschenAsync(logId, ct)
                ? Results.Ok(new { geloescht = logId })
                : Results.NotFound(new { error = $"Kein Eintrag {logId}." }));

        j.MapPost("/aufraeumen", async (IReasoningLogService svc, int? tage,
                                        CancellationToken ct) =>
        {
            var weg = await svc.AufraeumenAsync(tage ?? 30, ct);

            return Results.Ok(new
            {
                entfernt = weg,
                hinweis = $"Nicht gemerkte Einträge älter als {tage ?? 30} Tage entfernt. "
                        + "Gemerkte bleiben."
            });
        });
    }

    private static int Zaehle(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;

        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(json);
            return d.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? d.RootElement.GetArrayLength() : 0;
        }
        catch { return 0; }
    }

    /// <summary>Die Werkzeugspur als lesbares Objekt — oder leer, wenn sie unlesbar ist.</summary>
    private static object[] Spur(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<object[]>(json) ?? [];
        }
        catch { return []; }
    }

    private static string AlsMarkdown(ReasoningEintrag e)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"## {e.Frage}").AppendLine();
        sb.AppendLine($"*{e.AskedUtc:dd.MM.yyyy HH:mm} UTC · {e.Modell} · "
                    + $"{e.Endpunkt} · {e.Sekunden:0.#} s · {e.Runden} Runden*").AppendLine();
        sb.AppendLine(e.Antwort).AppendLine();

        if (!string.IsNullOrWhiteSpace(e.Notiz))
            sb.AppendLine($"> {e.Notiz}").AppendLine();

        var spur = Spur(e.Werkzeuge);
        if (spur.Length > 0)
        {
            sb.AppendLine("### Werkzeugaufrufe").AppendLine();
            foreach (var w in spur) sb.AppendLine($"- `{w}`");
        }

        return sb.ToString();
    }
}
