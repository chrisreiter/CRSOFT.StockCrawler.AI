using Ingest.Infrastructure.Services;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Säulen „Knowledge" und „Semantik": Text hereinholen, einbetten, wiederfinden.
///
/// Beide teilen sich diesen Satz Endpunkte und unterscheiden sich über den
/// Parameter <c>saeule</c>. Die Aufgabe ist dieselbe — Text zerlegen, einbetten,
/// durchsuchbar machen —, nur die Quelle ist eine andere: hochgeladene Bücher
/// hier, beobachtete Kanäle dort. Zwei Umsetzungen hießen zwei Zerlegungen und
/// beim nächsten Modellwechsel zwei Änderungen, von denen eine vergessen wird.
/// </summary>
public static class KnowledgeEndpoints
{
    /// <summary>Größte hinnehmbare Datei. Ein Fachbuch liegt weit darunter.</summary>
    private const long MaxBytes = 200L * 1024 * 1024;

    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/knowledge").WithTags("Wissen");

        /* Läuft die Umgebung überhaupt? Ohne diese Auskunft sieht ein
           fehlgeschlagenes Einbetten aus wie ein Fehler der Anwendung, obwohl
           schlicht Ollama oder Qdrant nicht gestartet ist. */
        g.MapGet("/health", async (IKnowledgeService svc, CancellationToken ct) =>
            Results.Ok(await svc.HealthAsync(ct)));

        /* Eine hochgeladene Datei ausliefern.

           Ohne diesen Endpunkt ist ein hochgeladenes Buch nicht erreichbar: In
           der Datenbank steht ein Pfad auf D:, und den kann ein Browser nicht
           öffnen. Ein Treffer ohne Weg zur Quelle ist aber nur eine halbe
           Antwort — man kann ihn nicht nachschlagen.

           Ausgeliefert wird ausschließlich aus dem Ablageverzeichnis. Der Pfad
           kommt zwar aus der eigenen Datenbank und nicht vom Aufrufer, aber ein
           Endpunkt, der eine beliebige Datei des Rechners herausgibt, sobald
           irgendwo ein Pfad falsch in die Datenbank gerät, ist es nicht wert. */
        g.MapGet("/datei/{sourceId:int}", async (IKnowledgeService svc, int sourceId,
                                                 CancellationToken ct) =>
        {
            var pfad = await svc.FilePathAsync(sourceId, ct);

            if (pfad is null || !File.Exists(pfad))
                return Results.NotFound(new { error = "Keine Datei zu dieser Quelle" });

            var typ = Path.GetExtension(pfad).ToLowerInvariant() switch
            {
                ".pdf" => "application/pdf",
                ".md" => "text/markdown; charset=utf-8",
                ".csv" => "text/csv; charset=utf-8",
                _ => "text/plain; charset=utf-8"
            };

            /* Im Browser anzeigen, nicht herunterladen: Der Verweis springt auf
               eine Seite im PDF, und das kann nur der eingebaute Betrachter. */
            return Results.File(pfad, typ, enableRangeProcessing: true);
        });

        /* Abgleich zwischen Qdrant und der Datenbank. */
        g.MapPost("/aufraeumen", async (IKnowledgeService svc, string saeule = "knowledge",
                                        CancellationToken ct = default) =>
        {
            var (geprueft, entfernt) = await svc.PurgeOrphansAsync(Normalize(saeule), ct);

            return Results.Ok(new
            {
                geprueft,
                entfernt,

                hinweis = entfernt == 0
                    ? "Keine verwaisten Vektoren — Qdrant und Datenbank stimmen überein."
                    : $"{entfernt} Vektoren ohne zugehörigen Abschnitt entfernt. Solche "
                    + "Punkte findet die Suche, schlägt in der Datenbank nach und liefert "
                    + "nichts — man sieht keine Fehlermeldung, sondern weniger Treffer."
            });
        });

        g.MapGet("/sources", async (IKnowledgeService svc, string saeule = "knowledge",
                                    CancellationToken ct = default) =>
        {
            var l = await svc.ListAsync(Normalize(saeule), ct);

            return Results.Ok(l.Select(s => new
            {
                s.SourceId,
                art = s.Kind,
                s.Title,
                herkunft = s.Kind == "file" ? Path.GetFileName(s.Origin) : s.Origin,
                s.ContentType,
                groesseMb = Math.Round(s.Bytes / 1024.0 / 1024.0, 2),
                aufgenommen = s.AddedUtc,
                eingebettet = s.IndexedUtc,
                zuletztGeprueft = s.LastCheckedUtc,
                aktiv = s.Active,
                abstandMinuten = s.PollMinutes,
                abschnitte = s.Chunks,

                /* Bei einem Feed zaehlt nicht, wie viele Abschnitte ER hat --
                   er hat keine. Es zaehlt, wie viele Artikel aus ihm
                   entstanden sind. */
                artikel = s.Articles,
                s.Region,
                erschienen = s.PublishedUtc,
                s.Status
            }));
        });

        /* Datei hochladen. */
        g.MapPost("/upload", async (IKnowledgeService svc, HttpRequest req,
                                    string saeule = "knowledge",
                                    CancellationToken ct = default) =>
        {
            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "Erwartet wird ein Formular mit Dateien" });

            var form = await req.ReadFormAsync(ct);

            if (form.Files.Count == 0)
                return Results.BadRequest(new { error = "Keine Datei im Formular" });

            var pillar = Normalize(saeule);

            var angelegt = new List<object>();
            var abgelehnt = new List<object>();

            foreach (var f in form.Files)
            {
                var ext = Path.GetExtension(f.FileName).ToLowerInvariant();

                /* Nur, was sich zuverlässig in Text verwandeln lässt. Eine
                   .docx anzunehmen und dann daran zu scheitern wäre die
                   schlechtere Antwort als sie gleich abzulehnen — der Nutzer
                   erführe es erst beim Einbetten. */
                if (ext is not (".pdf" or ".txt" or ".md" or ".csv"))
                {
                    abgelehnt.Add(new { f.FileName, grund = "nur PDF, TXT, MD und CSV" });
                    continue;
                }

                if (f.Length > MaxBytes)
                {
                    abgelehnt.Add(new { f.FileName, grund = $"größer als {MaxBytes / 1024 / 1024} MB" });
                    continue;
                }

                await using var s = f.OpenReadStream();

                var src = await svc.AddFileAsync(pillar, f.FileName, f.ContentType, s, ct);

                angelegt.Add(new { src.SourceId, src.Title, groesse = f.Length });
            }

            return Results.Ok(new
            {
                angelegt,
                abgelehnt,
                hinweis = "Hochladen legt die Quelle nur ab. Das Einbetten wird getrennt "
                        + "gestartet — es dauert bei einem Fachbuch Minuten und soll den "
                        + "Upload nicht blockieren."
            });
        }).DisableAntiforgery();

        /* Adresse eintragen. */
        g.MapPost("/web", async (IKnowledgeService svc, WebSourceRequest r,
                                 string saeule = "semantic",
                                 CancellationToken ct = default) =>
        {
            try
            {
                var src = await svc.AddWebAsync(
                    Normalize(saeule), r.Url, r.Title, r.PollMinutes ?? 240, ct);

                return Results.Ok(new { src.SourceId, src.Title, src.Origin });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        /* Eine Quelle einbetten. */
        g.MapPost("/index/{sourceId:int}", async (IKnowledgeService svc, int sourceId,
                                                  bool neu = false,
                                                  CancellationToken ct = default) =>
        {
            try
            {
                var (n, note) = await svc.IndexAsync(sourceId, neu, ct);
                return Results.Ok(new { abschnitte = n, hinweis = note });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        /* ------------------------------------------------- alles einbetten --

           Warum drei Umfaenge und nicht ein Knopf: Von 504 nie eingebetteten
           Semantik-Quellen stehen 469 auf „kein Text gefunden". Dort scheiterte die
           Textgewinnung, nicht das Einbetten -- ein erneuter Versuch laedt dieselbe
           Seite noch einmal und scheitert genauso. Ein Knopf, der das mitnimmt,
           verbringt 93 % seiner Zeit mit Fehlschlaegen. */

        g.MapPost("/alle", (IEinbettungsLauf lauf, string saeule = "knowledge",
                            string umfang = "fehlend") =>
        {
            var u = umfang.ToLowerInvariant() switch
            {
                "alles" => Einbettungsumfang.Alles,
                "auchleere" => Einbettungsumfang.AuchLeere,
                _ => Einbettungsumfang.Fehlend
            };

            var s = Normalize(saeule);

            if (!lauf.Starten(s, u))
                return Results.BadRequest(new
                {
                    error = "Für diese Säule läuft bereits ein Sammellauf.",
                    stand = lauf.Stand(s)
                });

            return Results.Ok(new
            {
                gestartet = s,
                umfang = u.ToString(),

                hinweis = u switch
                {
                    Einbettungsumfang.Fehlend =>
                        "Nur Quellen, die nie durchgekommen sind — ohne die mit "
                        + "„kein Text gefunden“. Das geht schnell.",
                    Einbettungsumfang.AuchLeere =>
                        "Auch die mit „kein Text gefunden“. Sinnvoll nach einer Änderung "
                        + "an der Textgewinnung, sonst verlorene Zeit — die meisten werden "
                        + "wieder scheitern.",
                    _ =>
                        "Alles neu, auch Vorhandenes. Jede Quelle wird erneut geladen; "
                        + "bei tausenden dauert das entsprechend."
                }
            });
        });

        g.MapGet("/alle/stand", (IEinbettungsLauf lauf, string saeule = "knowledge") =>
        {
            var st = lauf.Stand(Normalize(saeule));

            return st is null
                ? Results.Ok(new { laeuft = false, nieGelaufen = true })
                : Results.Ok(new
                {
                    st.Saeule, st.Umfang, st.Laeuft, st.Gesamt, st.Erledigt,
                    st.Eingebettet, st.Abschnitte, st.OhneText, st.Fehler,
                    st.Aktuell, st.BegonnenUtc, st.BeendetUtc, st.Meldung,
                    anteil = Math.Round(st.Anteil, 3),

                    /* „ohne Text" ist kein Fehler des Laufs, sondern ein Befund ueber die
                       Quelle. Getrennt gezaehlt, sonst liest sich ein sauberer Lauf wie
                       ein kaputter. */
                    bilanz = st.Laeuft ? null
                        : $"{st.Eingebettet} von {st.Gesamt} eingebettet, "
                        + $"{st.Abschnitte} Abschnitte. {st.OhneText} ohne Text, "
                        + $"{st.Fehler} fehlgeschlagen."
                });
        });

        g.MapPost("/alle/abbrechen", (IEinbettungsLauf lauf, string saeule = "knowledge") =>
            lauf.Abbrechen(Normalize(saeule))
                ? Results.Ok(new { abgebrochen = Normalize(saeule) })
                : Results.BadRequest(new { error = "Kein Lauf für diese Säule." }));

        /* Die kuratierte Startliste eintragen. */
        g.MapPost("/kuratiert", async (IKnowledgeService svc, string saeule = "semantic",
                                       CancellationToken ct = default) =>
        {
            var (angelegt, vorhanden, fehler) = await svc.AddCuratedAsync(Normalize(saeule), ct);

            return Results.Ok(new
            {
                angelegt,
                vorhanden,
                fehler,
                hinweis = "Eingetragen heißt noch nicht eingelesen. Für Nachrichten den "
                        + "Feed-Lauf starten, für Fachliteratur die Quellen einzeln einbetten "
                        + "— beides dauert, und beides soll nicht am Klick hängen."
            });
        });

        /* Feeds abholen: neue Artikel anlegen und einbetten. */
        g.MapPost("/feeds", async (IKnowledgeService svc, string saeule = "semantic",
                                   int proFeed = 25,
                                   CancellationToken ct = default) =>
        {
            var r = await svc.RefreshFeedsAsync(Normalize(saeule), proFeed, ct);

            return Results.Ok(new
            {
                feeds = r.Feeds,
                neueArtikel = r.NewArticles,
                eingebettet = r.Embedded,
                fehler = r.Failed,
                meldungen = r.Notes,

                hinweis = r.Feeds == 0
                    ? "Kein Feed war fällig. Jede Quelle hat ihren eigenen Abstand; "
                    + "vor dessen Ablauf wird sie nicht erneut abgerufen."
                    : "Bekannte Artikel bleiben unberührt — eindeutig ist die Adresse, "
                    + "nicht der Titel. Titel werden nachträglich geändert, Adressen nicht."
            });
        });

        /* Alle fälligen Adressen einer Säule auffrischen. Das ist der Griff,
           an dem später der Zeitplan zieht. */
        g.MapPost("/refresh", async (IKnowledgeService svc, string saeule = "semantic",
                                     CancellationToken ct = default) =>
        {
            var n = await svc.RefreshWebAsync(Normalize(saeule), ct);

            return Results.Ok(new
            {
                aufgefrischt = n,
                hinweis = "Nur Adressen, deren Abstand abgelaufen ist. Unverändert gebliebene "
                        + "Seiten werden anhand ihrer Prüfsumme erkannt und nicht erneut "
                        + "eingebettet."
            });
        });

        g.MapPost("/active/{sourceId:int}", async (IKnowledgeService svc, int sourceId,
                                                   bool aktiv, CancellationToken ct) =>
        {
            await svc.SetActiveAsync(sourceId, aktiv, ct);
            return Results.Ok(new { sourceId, aktiv });
        });

        g.MapDelete("/{sourceId:int}", async (IKnowledgeService svc, int sourceId,
                                              CancellationToken ct) =>
        {
            await svc.DeleteAsync(sourceId, ct);
            return Results.Ok(new { geloescht = sourceId });
        });

        /* Suchen. */
        g.MapGet("/search", async (IKnowledgeService svc, string frage,
                                   string saeule = "knowledge", int limit = 8,
                                   CancellationToken ct = default) =>
        {
            var t = await svc.SearchAsync(Normalize(saeule), frage, limit, ct);

            return Results.Ok(new
            {
                frage,
                treffer = t.Select(h => new
                {
                    h.ChunkId,
                    h.SourceId,
                    h.Title,
                    quelle = h.Origin.StartsWith("http") ? h.Origin : Path.GetFileName(h.Origin),

                    /* Die Fundstelle in drei Formen, weil keine allein reicht:
                       die Seite für PDFs, die Zeichenposition für alles andere,
                       und die Abschnittsnummer als letzte Rückfallebene. */
                    fundstelle = new
                    {
                        seite = h.PageFrom,
                        seiteBis = h.PageTo != h.PageFrom ? h.PageTo : null,
                        zeichenVon = h.CharFrom,
                        zeichenBis = h.CharTo,
                        abschnitt = h.Ordinal,
                        beschreibung = Fundstelle(h)
                    },

                    verweis = Verweis(h),
                    h.OccurredUtc,
                    aehnlichkeit = h.Score,
                    text = h.Content.Length > 1200 ? h.Content[..1200] + " …" : h.Content
                }),

                hinweis = "Die Ähnlichkeit ist der Kosinus zwischen den Vektoren, nicht ein "
                        + "Maß für Richtigkeit. Ein Abschnitt kann inhaltlich passen und "
                        + "trotzdem falsch sein — die Quelle steht deshalb bei jedem Treffer."
            });
        });
    }

    /// <summary>
    /// Die Fundstelle in Worten — was in der Oberfläche neben dem Treffer steht.
    /// </summary>
    private static string Fundstelle(KnowledgeHit h)
    {
        if (h.PageFrom is not null and > 0)
            return h.PageTo is not null && h.PageTo != h.PageFrom
                ? $"Seite {h.PageFrom}–{h.PageTo}"
                : $"Seite {h.PageFrom}";

        if (h.CharFrom is not null and > 0)
        {
            /* Zeichen sind für Menschen unhandlich. Tausend Zeichen sind grob
               eine halbe Seite — das lässt sich einschätzen, eine Zahl wie
               „ab Zeichen 184.320“ nicht. */
            var seite = h.CharFrom.Value / 2000 + 1;
            return $"ungefähr Seite {seite} (Zeichen {h.CharFrom:n0})";
        }

        return $"Abschnitt {h.Ordinal + 1}";
    }

    /// <summary>
    /// Der Weg zur Stelle — je nach Art der Quelle ein anderer.
    ///
    /// <b>PDF:</b> <c>#page=N</c>, das versteht jeder eingebaute Betrachter.
    /// <b>Alles andere:</b> ein Textanker <c>#:~:text=…</c>. Der ist stabiler
    /// als eine Zeichenposition — wird die Quelle nachträglich geändert,
    /// verschiebt sich jede Position, der Wortlaut aber meist nicht.
    /// </summary>
    private static string Verweis(KnowledgeHit h)
    {
        var istDatei = h.Kind == "file";

        var basis = istDatei
            ? $"/api/knowledge/datei/{h.SourceId}"
            : h.Origin;

        var istPdf = h.Origin.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                  || (istDatei && h.PageFrom is not null);

        if (istPdf && h.PageFrom is not null and > 0)
            return $"{basis}#page={h.PageFrom}";

        if (!string.IsNullOrWhiteSpace(h.Anchor))
        {
            /* Der Anker muss zweimal kodiert werden: einmal für die Adresse,
               und Bindestriche zusätzlich, weil „-,-“ in der Fragmentsyntax
               eine eigene Bedeutung hat. */
            var anker = Uri.EscapeDataString(h.Anchor).Replace("-", "%2D");
            return $"{basis}#:~:text={anker}";
        }

        return basis;
    }

    /// <summary>
    /// Nur zwei Säulen sind vorgesehen. Alles andere landet bei „knowledge",
    /// statt eine dritte Sammlung anzulegen, die niemand füllt.
    /// </summary>
    private static string Normalize(string s) =>
        s.Equals("semantic", StringComparison.OrdinalIgnoreCase)
        || s.Equals("semantik", StringComparison.OrdinalIgnoreCase)
            ? "semantic" : "knowledge";
}

public sealed record WebSourceRequest(string Url, string? Title, int? PollMinutes);
