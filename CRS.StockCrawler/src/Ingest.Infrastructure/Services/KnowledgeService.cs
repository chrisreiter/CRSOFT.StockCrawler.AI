using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace Ingest.Infrastructure.Services;

/// <summary>Eine Quelle, so wie das Raster sie zeigt.</summary>
public sealed record KnowledgeSource(
    int SourceId, string Kind, string Pillar, string Title, string Origin,
    string? ContentType, long Bytes, DateTime AddedUtc, DateTime? IndexedUtc,
    DateTime? LastCheckedUtc, bool Active, int? PollMinutes, int Chunks, string? Status,
    int? ParentSourceId = null, DateTime? PublishedUtc = null, string? Region = null,
    int Articles = 0);

/// <summary>Was ein Durchgang über die Feeds bewegt hat.</summary>
public sealed record FeedRunResult(
    int Feeds, int NewArticles, int Embedded, int Failed, IReadOnlyList<string> Notes);

/// <summary>Ein Treffer der Wissenssuche, mit Text und Herkunft.</summary>
public sealed record KnowledgeHit(
    long ChunkId, int SourceId, string Title, string Origin, string Kind,
    int Ordinal, int? PageFrom, int? PageTo,
    int? CharFrom, int? CharTo, string? Anchor,
    DateTime? OccurredUtc, double Score, string Content);

public interface IKnowledgeService
{
    Task<IReadOnlyList<KnowledgeSource>> ListAsync(string pillar, CancellationToken ct = default);

    Task<KnowledgeSource> AddFileAsync(
        string pillar, string fileName, string? contentType, Stream content,
        CancellationToken ct = default);

    Task<KnowledgeSource> AddWebAsync(
        string pillar, string url, string? title, int pollMinutes,
        CancellationToken ct = default);

    /// <summary>Trägt die kuratierte Startliste einer Säule ein.</summary>
    Task<(int Angelegt, int Vorhanden, IReadOnlyList<string> Fehler)> AddCuratedAsync(
        string pillar, CancellationToken ct = default);

    /// <summary>
    /// Holt alle fälligen Feeds, legt neue Artikel an und bettet sie ein.
    /// Bereits bekannte Artikel bleiben unberührt.
    /// </summary>
    Task<FeedRunResult> RefreshFeedsAsync(
        string pillar, int maxArticlesPerFeed = 25, CancellationToken ct = default);

    Task<(int Chunks, string Note)> IndexAsync(
        int sourceId, bool force = false, CancellationToken ct = default);

    Task<int> RefreshWebAsync(string pillar, CancellationToken ct = default);

    Task SetActiveAsync(int sourceId, bool active, CancellationToken ct = default);
    Task DeleteAsync(int sourceId, CancellationToken ct = default);

    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(
        string pillar, string query, int limit, CancellationToken ct = default);

    Task<object> HealthAsync(CancellationToken ct = default);

    /// <summary>Pfad einer hochgeladenen Datei — oder null.</summary>
    Task<string?> FilePathAsync(int sourceId, CancellationToken ct = default);

    /// <summary>Entfernt Vektoren, zu denen es keinen Abschnitt mehr gibt.</summary>
    Task<(int Geprueft, int Entfernt)> PurgeOrphansAsync(
        string pillar, CancellationToken ct = default);
}

/// <summary>
/// Holt Text herein — aus hochgeladenen Dateien und aus beobachteten Adressen —,
/// zerlegt ihn, bettet ihn ein und macht ihn durchsuchbar.
///
/// <b>Warum beide Säulen denselben Dienst benutzen.</b> „Knowledge" lädt Bücher
/// hoch, „Semantik" beobachtet Kanäle. Das ist dieselbe Aufgabe mit
/// verschiedener Quelle: Text hereinholen, zerlegen, einbetten, wiederfinden.
/// Zwei Umsetzungen hießen zwei Zerlegungen, zwei Einbettungswege und beim
/// nächsten Modellwechsel zwei Änderungen — mit der Aussicht, dass eine davon
/// vergessen wird. Getrennt bleiben sie über die Sammlung und das Feld
/// <c>pillar</c>.
///
/// <b>Zur Zerlegung.</b> Geschnitten wird an Absatzgrenzen, nicht nach fester
/// Zeichenzahl, und mit Überlappung. Ein Schnitt mitten im Satz erzeugt zwei
/// Abschnitte, von denen keiner die Aussage vollständig enthält — beide sind
/// dann bei der Suche wertlos, und zwar genau bei den Aussagen, die über einen
/// Absatz hinausgehen. Das sind bei einem Fachbuch die interessanten.
/// </summary>
public sealed class KnowledgeService(
    ISqlConnectionFactory factory,
    IEmbeddingClient embedder,
    IVectorStore vectors,
    IHttpClientFactory httpFactory,
    ILogger<KnowledgeService> log,
    IOllamaEndpointService? endpunkte = null) : IKnowledgeService
{
    /// <summary>Wohin die Vektoren gehen. Je Säule eine eigene Sammlung.</summary>
    private static string Collection(string pillar) =>
        pillar == "semantic" ? "crs_semantik" : "crs_wissen";

    /// <summary>Wo hochgeladene Dateien liegen.</summary>
    private static string FileRoot =>
        Environment.GetEnvironmentVariable("CRS_KNOWLEDGE_DIR")
        ?? @"D:\_data\stockcrawler\knowledge";

    // ------------------------------------------------------------ Liste ---

    public async Task<IReadOnlyList<KnowledgeSource>> ListAsync(
        string pillar, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        return (await conn.QueryAsync<KnowledgeSource>(new CommandDefinition(
            """
            /* Artikel erscheinen NICHT als eigene Zeilen im Raster.

               Bei zwanzig Feeds und stündlichem Lauf wären das nach einer
               Woche tausende Zeilen, und die Übersicht über die Quellen wäre
               dahin. Gezeigt wird der Feed mit der Zahl seiner Artikel; die
               Artikel selbst findet man über die Suche, wo sie hingehören. */
            SELECT s.source_id AS SourceId, s.kind AS Kind, s.pillar AS Pillar,
                   s.title AS Title, s.origin AS Origin, s.content_type AS ContentType,
                   s.bytes AS Bytes, s.added_utc AS AddedUtc, s.indexed_utc AS IndexedUtc,
                   s.last_checked_utc AS LastCheckedUtc, s.active AS Active,
                   s.poll_minutes AS PollMinutes, s.chunks AS Chunks, s.status AS Status,
                   s.parent_source_id AS ParentSourceId, s.published_utc AS PublishedUtc,
                   s.region AS Region,
                   ISNULL(a.n, 0) AS Articles
              FROM dbo.knowledge_source s
              OUTER APPLY (SELECT COUNT(*) n FROM dbo.knowledge_source k
                            WHERE k.parent_source_id = s.source_id) a
             WHERE s.pillar = @pillar AND s.parent_source_id IS NULL
             ORDER BY s.added_utc DESC;
            """, new { pillar }, cancellationToken: ct))).ToList();
    }

    // ---------------------------------------------------------- Aufnahme --

    public async Task<KnowledgeSource> AddFileAsync(
        string pillar, string fileName, string? contentType, Stream content,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(FileRoot);

        var safe = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
        var ziel = Path.Combine(FileRoot, $"{Guid.NewGuid():N}_{safe}");

        long bytes;

        await using (var fs = File.Create(ziel))
        {
            await content.CopyToAsync(fs, ct);
            bytes = fs.Length;
        }

        await using var conn = await factory.OpenAsync(ct);

        var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO dbo.knowledge_source
                (kind, pillar, title, origin, content_type, bytes, status)
            OUTPUT INSERTED.source_id
            VALUES ('file', @pillar, @title, @origin, @ct, @bytes, 'aufgenommen, noch nicht eingebettet');
            """,
            new { pillar, title = Path.GetFileNameWithoutExtension(fileName), origin = ziel, ct = contentType, bytes },
            cancellationToken: ct));

        return (await ListAsync(pillar, ct)).First(x => x.SourceId == id);
    }

    public async Task<KnowledgeSource> AddWebAsync(
        string pillar, string url, string? title, int pollMinutes,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("Adresse muss mit http:// oder https:// beginnen", nameof(url));

        await using var conn = await factory.OpenAsync(ct);

        /* Dieselbe Adresse zweimal einzutragen ist kein Fehler, sondern der
           Normalfall.

           Ein Sammellauf über eine Quellenliste stößt zwangsläufig auf schon
           Bekanntes -- beim erneuten Einlesen derselben arXiv-Auswahl waren es
           alle neununddreißig. Der eindeutige Index UX_ks_origin fing das ab,
           aber die Ausnahme schlug bis zum Aufrufer durch: neununddreißig
           Antworten mit Status 500, obwohl nichts kaputt war. Der Aufrufer kann
           daraus nicht ablesen, dass alles in Ordnung ist.

           Richtig ist, die vorhandene Quelle zurückzugeben. Die Operation wird
           damit idempotent -- dieselbe Regel wie bei den Abschnittskennungen:
           Doppeltes unmöglich machen, statt es hinterher aufzuräumen. */
        var vorhanden = await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT source_id FROM dbo.knowledge_source WHERE pillar = @pillar AND origin = @origin",
            new { pillar, origin = url }, cancellationToken: ct));

        if (vorhanden is { } alt2)
            return (await ListAsync(pillar, ct)).First(x => x.SourceId == alt2);

        int id;
        try
        {
            id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                INSERT INTO dbo.knowledge_source
                    (kind, pillar, title, origin, poll_minutes, status)
                OUTPUT INSERTED.source_id
                VALUES ('web', @pillar, @title, @origin, @poll, 'eingetragen, noch nicht abgerufen');
                """,
                new
                {
                    pillar,
                    title = string.IsNullOrWhiteSpace(title) ? uri.Host : title,
                    origin = url,
                    poll = Math.Clamp(pollMinutes, 15, 10080)
                }, cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            /* Zwei gleichzeitige Läufe: Zwischen Prüfung und Einfügen hat der
               andere geschrieben. Auch das ist kein Fehler. */
            return (await ListAsync(pillar, ct)).First(x =>
                string.Equals(x.Origin, url, StringComparison.OrdinalIgnoreCase));
        }

        return (await ListAsync(pillar, ct)).First(x => x.SourceId == id);
    }

    // -------------------------------------------------------- Einbettung --

    public async Task<(int Chunks, string Note)> IndexAsync(
        int sourceId, bool force = false, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var src = await conn.QuerySingleOrDefaultAsync<KnowledgeSource>(new CommandDefinition(
            """
            SELECT source_id AS SourceId, kind AS Kind, pillar AS Pillar, title AS Title,
                   origin AS Origin, content_type AS ContentType, bytes AS Bytes,
                   added_utc AS AddedUtc, indexed_utc AS IndexedUtc,
                   last_checked_utc AS LastCheckedUtc, active AS Active,
                   poll_minutes AS PollMinutes, chunks AS Chunks, status AS Status,
                   parent_source_id AS ParentSourceId, published_utc AS PublishedUtc,
                   region AS Region, 0 AS Articles
              FROM dbo.knowledge_source WHERE source_id = @id;
            """, new { id = sourceId }, cancellationToken: ct));

        if (src is null) throw new InvalidOperationException("Quelle unbekannt");

        if (!await embedder.IsAvailableAsync(ct))
        {
            /* Diese Meldung hat einmal einen halben Tag gekostet.

               Sie nannte die zwei naheliegenden Ursachen -- Ollama aus, Modell fehlt --
               und verschwieg die tatsaechliche: Der gewaehlte Endpunkt zeigte auf eine
               zerstoerte GPU. Ollama lief, das Modell lag bereit, und die Meldung schickte
               die Suche in die falsche Richtung. Wer eine Ursache raet, muss sagen, worauf
               er sich bezieht. */
            var wo = endpunkte?.Aktiv.Name ?? "unbekannt";

            var stoerung = endpunkte?.Stoerung is { } st
                ? $" — Endpunkt „{st.Name}“ ist gestört ({st.Grund})"
                : "";

            return (0, $"Kein Einbetten möglich: {embedder.Model} über Endpunkt „{wo}“ "
                     + $"nicht erreichbar{stoerung}");
        }

        if (!await vectors.IsAvailableAsync(ct))
            return (0, "Qdrant antwortet nicht");

        var (text, seiten) = src.Kind == "file"
            ? await ReadFileAsync(src.Origin, ct)
            : await ReadRemoteAsync(src.Origin, ct);

        if (string.IsNullOrWhiteSpace(text))
        {
            await StatusAsync(sourceId, "kein Text gefunden", ct);
            return (0, "kein Text gefunden");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..64];

        var alt = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT content_hash FROM dbo.knowledge_source WHERE source_id = @id;",
            new { id = sourceId }, cancellationToken: ct));

        /* Unverändert? Dann nichts tun.

           Ohne diese Prüfung bettet jeder Durchgang jede beobachtete Seite neu
           ein, auch wenn sich dort seit Wochen nichts getan hat — das kostet
           Rechenzeit für ein Ergebnis, das schon dasteht. */
        /* `force` gibt es, weil sich nicht nur die Quelle aendern kann,
           sondern auch das Verfahren: Nach einer geaenderten Zerlegung oder
           einem anderen Einbettungsmodell ist der alte Bestand veraltet,
           obwohl der Text derselbe ist. Ohne diesen Schalter muesste man die
           Quelle loeschen und neu hochladen. */
        if (!force && alt == hash && src.Chunks > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.knowledge_source SET last_checked_utc = SYSUTCDATETIME() WHERE source_id = @id;",
                new { id = sourceId }, cancellationToken: ct));

            return (src.Chunks, "unverändert seit dem letzten Durchgang");
        }

        var abschnitte = Chunk(text, seiten);

        if (abschnitte.Count == 0)
        {
            await StatusAsync(sourceId, "Text zu kurz für einen Abschnitt", ct);
            return (0, "Text zu kurz");
        }

        /* Nur ein Lauf je Quelle.

           Zwei gleichzeitige Läufe auf derselben Quelle löschen beide den alten
           Bestand und schreiben danach beide den neuen — das Ergebnis ist die
           doppelte Menge Abschnitte, und zwar ohne jede Fehlermeldung. Genau
           das ist passiert: Zwei Einbettungsläufe nebeneinander gestartet, und
           sechs Quellen hatten hinterher exakt doppelt so viele Abschnitte, wie
           ihre eigene Zählung meldete.

           Die Sperre steckt in einer bedingten UPDATE-Anweisung: Sie greift nur,
           wenn nicht schon ein Lauf vermerkt ist. Wer sie nicht bekommt, bricht
           ab, statt zu warten — ein zweiter Lauf würde ohnehin dasselbe tun.

           Die halbe Stunde Verfallszeit ist da, damit ein abgestürzter Lauf die
           Quelle nicht dauerhaft blockiert. */
        var sperre = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.knowledge_source
               SET status = 'wird eingebettet', last_checked_utc = SYSUTCDATETIME()
             WHERE source_id = @id
               AND (status <> 'wird eingebettet'
                    OR last_checked_utc IS NULL
                    OR last_checked_utc < DATEADD(MINUTE, -30, SYSUTCDATETIME()));
            """, new { id = sourceId }, cancellationToken: ct));

        if (sperre == 0)
        {
            log.LogInformation("Quelle {Id} wird bereits eingebettet — übersprungen", sourceId);
            return (src.Chunks, "läuft bereits");
        }

        /* Ab hier hält dieser Lauf die Sperre — und muss sie in jedem Fall
           wieder lösen.

           Der erste Entwurf setzte sie vor der Arbeit und löste sie nur auf dem
           Erfolgspfad. Bricht etwas dazwischen ab, bleibt die Quelle eine halbe
           Stunde blockiert, und jeder Versuch antwortet mit „läuft bereits“ —
           obwohl nichts läuft. Genau das ist beim ersten Lauf passiert. */
        try
        {
            return await EinbettenAsync(conn, src, sourceId, abschnitte, hash, ct);
        }
        catch (Exception ex)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.knowledge_source
                   SET status = @s
                 WHERE source_id = @id AND status = 'wird eingebettet';
                """,
                new
                {
                    id = sourceId,
                    s = "Fehler: " + ex.Message[..Math.Min(300, ex.Message.Length)]
                }, cancellationToken: ct));

            throw;
        }
    }

    /// <summary>
    /// Der eigentliche Einbettungslauf. Getrennt, damit der Aufrufer die Sperre
    /// in jedem Fall wieder lösen kann.
    /// </summary>
    private async Task<(int Chunks, string Note)> EinbettenAsync(
        Microsoft.Data.SqlClient.SqlConnection conn,
        KnowledgeSource src, int sourceId,
        List<Abschnitt> abschnitte, string hash, CancellationToken ct)
    {
        var collection = Collection(src.Pillar);
        await vectors.EnsureCollectionAsync(collection, embedder.Dimensions, ct);

        log.LogInformation("Bearbeite {N} Abschnitte aus {Titel}", abschnitte.Count, src.Title);

        /* Was bereits eingebettet ist, bleibt liegen.

           Die Kennung eines Abschnitts ergibt sich aus Quelle, Nummer und
           Inhalt. Ändert sich der Text nicht, ändert sich die Kennung nicht —
           und dann muss er weder neu eingebettet noch neu geschrieben werden.
           Bei einem erzwungenen Neuaufbau nach einer Verfahrensänderung ist das
           der Unterschied zwischen Minuten und Sekunden. */
        var vorher = (await conn.QueryAsync<(int Ordinal, byte[] Hash, Guid VectorId)>(
            new CommandDefinition(
                """
                SELECT ordinal, chunk_hash, vector_id
                  FROM dbo.knowledge_chunk WHERE source_id = @id;
                """, new { id = sourceId }, cancellationToken: ct))).ToList();

        var bekannt = vorher
            .Where(x => x.Hash is not null)
            .ToDictionary(x => x.Ordinal, x => Convert.ToHexString(x.Hash));

        /* Die alten Kennungen mitführen.

           Ändert sich der Inhalt eines Abschnitts -- oder die
           Verarbeitungsfassung --, bekommt er eine NEUE Kennung. Der Eintrag in
           SQL wird überschrieben, der alte Punkt in Qdrant aber nicht: Er hat
           eine Kennung, die kein neuer Lauf je wiedertrifft.

           Gemessen nach der Umstellung auf Fassung 2: 18.875 Vektoren gegen
           6.625 Abschnitte. Zwölftausend Waisen, und die Suche lieferte
           daraufhin ein Drittel der Treffer -- ohne Fehlermeldung, denn ein
           Punkt ohne Text verschwindet still aus der Ergebnisliste. */
        var alteKennungen = vorher.ToDictionary(x => x.Ordinal, x => x.VectorId);
        var abgeloest = new List<Guid>();

        var punkte = new List<(Guid, float[], Dictionary<string, object>)>();

        var unveraendert = 0;
        var geschrieben = 0;

        const int block = 64;

        for (var i = 0; i < abschnitte.Count; i += block)
        {
            ct.ThrowIfCancellationRequested();

            var teil = abschnitte.Skip(i).Take(block).ToList();

            // Erst prüfen, was überhaupt neu ist — dann nur das einbetten.
            var offen = new List<(int Ordinal, Abschnitt A, string AbschnittHash)>();

            for (var k = 0; k < teil.Count; k++)
            {
                var ord = i + k;
                var abschnittHash = ChunkHash(sourceId, ord, teil[k].Text);

                if (bekannt.TryGetValue(ord, out var alt2) && alt2 == abschnittHash)
                {
                    unveraendert++;
                    continue;
                }

                offen.Add((ord, teil[k], abschnittHash));
            }

            if (offen.Count == 0) continue;

            var vec = await embedder.EmbedAsync(offen.Select(x => x.A.Text).ToList(), ct);

            for (var k = 0; k < offen.Count; k++)
            {
                var (ord, a, abschnittHash) = offen[k];

                /* Die Kennung des Vektors ist der halbe Inhalts-Hash.

                   Damit ist sie überall dieselbe: in SQL, in Qdrant, bei jedem
                   Lauf. Ein zweiter Lauf überschreibt denselben Punkt, statt
                   einen weiteren anzulegen — Duplikate werden unmöglich, statt
                   nachträglich entfernt zu werden. */
                var vid = HashToGuid(abschnittHash);

                // Trug dieser Platz vorher eine andere Kennung, ist der alte
                // Punkt ab jetzt eine Waise und muss fort.
                if (alteKennungen.TryGetValue(ord, out var alteId) && alteId != vid)
                    abgeloest.Add(alteId);

                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    MERGE dbo.knowledge_chunk AS z
                    USING (SELECT @src AS source_id, @ord AS ordinal) AS q
                       ON z.source_id = q.source_id AND z.ordinal = q.ordinal
                    WHEN MATCHED THEN UPDATE SET
                        page_from = @pf, page_to = @pt, content = @content,
                        tokens = @tok, occurred_utc = @occ,
                        char_from = @cf, char_to = @cto, anchor = @anker,
                        embedded_utc = SYSUTCDATETIME(),
                        vector_id = @vid, chunk_hash = @hash
                    WHEN NOT MATCHED THEN INSERT
                        (source_id, ordinal, page_from, page_to, content, tokens,
                         occurred_utc, char_from, char_to, anchor,
                         embedded_utc, vector_id, chunk_hash)
                        VALUES (@src, @ord, @pf, @pt, @content, @tok, @occ,
                                @cf, @cto, @anker,
                                SYSUTCDATETIME(), @vid, @hash);
                    """,
                    new
                    {
                        src = sourceId,
                        ord,
                        pf = a.PageFrom,
                        pt = a.PageTo,
                        cf = a.CharFrom,
                        cto = a.CharTo,
                        anker = a.Anchor,
                        content = a.Text,
                        tok = a.Text.Length / 4,
                        occ = src.PublishedUtc,
                        vid,
                        hash = Convert.FromHexString(abschnittHash)
                    }, cancellationToken: ct));

                punkte.Add((vid, vec[k], new Dictionary<string, object>
                {
                    ["source_id"] = sourceId,
                    ["pillar"] = src.Pillar,
                    ["title"] = src.Title,
                    ["ordinal"] = ord
                }));

                geschrieben++;
            }
        }

        if (punkte.Count > 0) await vectors.UpsertAsync(collection, punkte, ct);

        /* Erst schreiben, dann die abgelösten fortnehmen.

           In dieser Reihenfolge, nicht umgekehrt: Bricht der Lauf zwischen
           beiden Schritten ab, stehen kurzzeitig zu viele Punkte da -- das
           kostet Platz. Andersherum stünden zu wenige da, und die Suche fände
           den Abschnitt nicht mehr. Zu viel ist reparierbar, zu wenig fällt
           niemandem auf. */
        if (abgeloest.Count > 0)
        {
            log.LogInformation("{N} abgelöste Vektoren entfernt", abgeloest.Count);

            for (var i = 0; i < abgeloest.Count; i += 2000)
                await vectors.DeleteByIdsAsync(
                    collection, abgeloest.Skip(i).Take(2000).ToList(), ct);
        }

        /* Überzählige Abschnitte fortnehmen.

           Wird eine Quelle kürzer — ein Artikel gekürzt, eine Zerlegung
           gröber —, bleiben sonst die Abschnitte hinter dem neuen Ende stehen
           und die Suche findet Text, den es nicht mehr gibt. Gelöscht wird nur
           dieser Überhang, nicht der gesamte Bestand. */
        var ueberhang = (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT vector_id FROM dbo.knowledge_chunk WHERE source_id = @id AND ordinal >= @n;",
            new { id = sourceId, n = abschnitte.Count }, cancellationToken: ct))).ToList();

        if (ueberhang.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.knowledge_chunk WHERE source_id = @id AND ordinal >= @n;",
                new { id = sourceId, n = abschnitte.Count }, cancellationToken: ct));

            await vectors.DeleteByIdsAsync(collection, ueberhang, ct);
        }

        log.LogInformation(
            "{Titel}: {Neu} geschrieben, {Alt} unverändert, {Weg} entfernt",
            src.Title, geschrieben, unveraendert, ueberhang.Count);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.knowledge_source
               SET chunks = @n, indexed_utc = SYSUTCDATETIME(),
                   last_checked_utc = SYSUTCDATETIME(),
                   content_hash = @hash, status = @s
             WHERE source_id = @id;
            """,
            new
            {
                n = abschnitte.Count,
                hash,
                s = $"{abschnitte.Count} Abschnitte · {geschrieben} neu, "
                  + $"{unveraendert} unverändert ({embedder.Model})",
                id = sourceId
            }, cancellationToken: ct));

        return (abschnitte.Count,
            geschrieben == 0
                ? $"{abschnitte.Count} Abschnitte, alle unverändert — nichts neu eingebettet"
                : $"{abschnitte.Count} Abschnitte, davon {geschrieben} neu eingebettet");
    }

    public async Task<int> RefreshWebAsync(string pillar, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        /* Nur, was auch fällig ist. Eine Adresse mit Abstand 60 Minuten alle
           fünf Minuten abzurufen ist gegenüber der Gegenstelle unhöflich und
           bringt nichts. */
        var faellig = (await conn.QueryAsync<int>(new CommandDefinition(
            """
            SELECT source_id
              FROM dbo.knowledge_source
             WHERE pillar = @pillar AND kind = 'web' AND active = 1
               AND (last_checked_utc IS NULL
                    OR DATEADD(MINUTE, ISNULL(poll_minutes, 240), last_checked_utc) <= SYSUTCDATETIME());
            """, new { pillar }, cancellationToken: ct))).ToList();

        var n = 0;

        foreach (var id in faellig)
        {
            try
            {
                var (chunks, _) = await IndexAsync(id, false, ct);
                if (chunks > 0) n++;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Quelle {Id} konnte nicht aufgefrischt werden", id);
                await StatusAsync(id, "Fehler: " + ex.Message[..Math.Min(300, ex.Message.Length)], ct);
            }
        }

        return n;
    }

    // ------------------------------------------------------------ Feeds ---

    public async Task<FeedRunResult> RefreshFeedsAsync(
        string pillar, int maxArticlesPerFeed = 25, CancellationToken ct = default)
    {
        var notes = new List<string>();

        if (!await embedder.IsAvailableAsync(ct))
            return new FeedRunResult(0, 0, 0, 0, ["Ollama antwortet nicht"]);

        if (!await vectors.IsAvailableAsync(ct))
            return new FeedRunResult(0, 0, 0, 0, ["Qdrant antwortet nicht"]);

        await using var conn = await factory.OpenAsync(ct);

        /* Nur faellige Feeds. Eine Quelle mit Abstand 60 Minuten alle fuenf
           Minuten abzurufen ist gegenueber der Gegenstelle unhoeflich und
           bringt nichts. */
        var faellig = (await conn.QueryAsync<(int SourceId, string Origin, string Title, string? Region)>(
            new CommandDefinition(
                """
                SELECT source_id, origin, title, region
                  FROM dbo.knowledge_source
                 WHERE pillar = @pillar AND kind = 'feed' AND active = 1
                   AND parent_source_id IS NULL
                   AND (last_checked_utc IS NULL
                        OR DATEADD(MINUTE, ISNULL(poll_minutes, 120), last_checked_utc)
                           <= SYSUTCDATETIME());
                """, new { pillar }, cancellationToken: ct))).ToList();

        var http = httpFactory.CreateClient("browser");

        int neu = 0, eingebettet = 0, fehler = 0;

        foreach (var feed in faellig)
        {
            ct.ThrowIfCancellationRequested();

            List<FeedItem> items;

            try
            {
                using var res = await http.GetAsync(feed.Origin, ct);

                if (!res.IsSuccessStatusCode)
                {
                    await StatusAsync(feed.SourceId,
                        $"Feed antwortet mit {(int)res.StatusCode}", ct);
                    fehler++;
                    continue;
                }

                var xml = await res.Content.ReadAsStringAsync(ct);

                if (!FeedReader.LooksLikeFeed(xml))
                {
                    await StatusAsync(feed.SourceId,
                        "Antwort ist kein RSS/Atom — Adresse prüfen", ct);
                    fehler++;
                    continue;
                }

                items = FeedReader.Parse(xml, feed.Origin).ToList();
            }
            catch (Exception ex)
            {
                await StatusAsync(feed.SourceId,
                    "Fehler: " + ex.Message[..Math.Min(280, ex.Message.Length)], ct);
                fehler++;
                continue;
            }

            if (items.Count == 0)
            {
                await StatusAsync(feed.SourceId, "Feed war leer", ct);
                continue;
            }

            /* Nur was noch nicht bekannt ist.

               Die Eindeutigkeit haengt an der Adresse des Artikels, nicht an
               seinem Titel: Titel werden nachtraeglich geaendert, Adressen
               nicht. In der Datenbank sichert das der eindeutige Index ueber
               (pillar, origin_hash) ab -- die Pruefung hier spart nur den
               vergeblichen Versuch. */
            var bekannt = (await conn.QueryAsync<string>(new CommandDefinition(
                """
                SELECT origin FROM dbo.knowledge_source
                 WHERE parent_source_id = @id;
                """, new { id = feed.SourceId }, cancellationToken: ct)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var frisch = items
                .Where(i => !bekannt.Contains(i.Url))
                .OrderByDescending(i => i.PublishedUtc ?? DateTime.MinValue)
                .Take(Math.Clamp(maxArticlesPerFeed, 1, 200))
                .ToList();

            foreach (var item in frisch)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var id = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                        """
                        INSERT INTO dbo.knowledge_source
                            (kind, pillar, title, origin, parent_source_id,
                             published_utc, region, status)
                        OUTPUT INSERTED.source_id
                        SELECT 'article', @pillar, @title, @origin, @parent,
                               @published, @region, 'neu, noch nicht eingebettet'
                         WHERE NOT EXISTS (
                             SELECT 1 FROM dbo.knowledge_source
                              WHERE pillar = @pillar
                                AND origin_hash = CONVERT(VARBINARY(32),
                                        HASHBYTES('SHA2_256', @origin)));
                        """,
                        new
                        {
                            pillar,
                            title = item.Title.Length > 380 ? item.Title[..380] : item.Title,
                            origin = item.Url,
                            parent = feed.SourceId,
                            published = item.PublishedUtc,
                            /* Die Region erbt der Artikel von seinem Feed.

                               Aus der Meldung selbst ist sie nicht zu holen,
                               und ohne sie liesse sich eine Frage nicht auf
                               einen Markt begrenzen -- die Uebersicht zeigte
                               dann nur Fragezeichen. */
                            region = feed.Region
                        }, cancellationToken: ct));

                    // Kein Satz entstanden heisst: Die Adresse gab es schon,
                    // nur unter einem anderen Feed. Das ist kein Fehler.
                    if (id is null) continue;

                    neu++;

                    var (chunks, _) = await IndexAsync(id.Value, false, ct);
                    if (chunks > 0) eingebettet++;
                }
                catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
                {
                    /* Zwei gleichzeitige Läufe -- kein Fehler.

                       Das `WHERE NOT EXISTS` oben ist ein Prüfen-dann-Schreiben, und
                       genau dazwischen schreibt der andere Lauf. Der eindeutige Index
                       fängt es ab; hier gehört es als das behandelt, was es ist: Der
                       Artikel ist schon da.

                       Beobachtet am 26.08.2026, als ein von Hand angestossener
                       Stundenlauf mit dem planmässigen um :08 zusammenfiel. Als Fehler
                       gezählt sah das nach einem kaputten Feed aus und stand mit
                       vollem Stapelabzug im Protokoll — für einen Vorgang, der genau
                       richtig ausgegangen ist.

                       `AddWebAsync` behandelt denselben Fall seit jeher so; nur diese
                       Stelle fehlte. */
                    continue;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Artikel {Url} übersprungen", item.Url);
                    fehler++;
                }
            }

            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.knowledge_source
                   SET last_checked_utc = SYSUTCDATETIME(), status = @s
                 WHERE source_id = @id;
                """,
                new
                {
                    id = feed.SourceId,
                    s = $"{items.Count} Einträge gelesen, {frisch.Count} neu"
                }, cancellationToken: ct));

            notes.Add($"{feed.Title}: {frisch.Count} neu von {items.Count}");
        }

        return new FeedRunResult(faellig.Count, neu, eingebettet, fehler, notes);
    }

    // -------------------------------------------------------- Startliste ---

    public async Task<(int Angelegt, int Vorhanden, IReadOnlyList<string> Fehler)>
        AddCuratedAsync(string pillar, CancellationToken ct = default)
    {
        /* Die Liste steht als Datei neben der Anwendung, nicht im Code.

           Wer eine Quelle ergaenzt oder streicht, soll das ohne Uebersetzen
           tun koennen -- und man soll sehen koennen, was eingelesen wird,
           ohne den Quelltext zu lesen. */
        var pfad = Ablage.Quellen;

        if (!File.Exists(pfad))
            return (0, 0, [$"Quellendatei nicht gefunden: {pfad}"]);

        using var doc = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(pfad, ct));

        var schluessel = pillar == "semantic" ? "semantik" : "knowledge";

        if (!doc.RootElement.TryGetProperty(schluessel, out var liste))
            return (0, 0, [$"Kein Abschnitt \u201e{schluessel}\u201c in der Quellendatei"]);

        await using var conn = await factory.OpenAsync(ct);

        int angelegt = 0, vorhanden = 0;
        var fehler = new List<string>();

        foreach (var q in liste.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            var url = q.GetProperty("url").GetString() ?? "";
            var titel = q.TryGetProperty("titel", out var t) ? t.GetString() ?? url : url;
            var region = q.TryGetProperty("region", out var r) ? r.GetString() : null;

            var abstand = q.TryGetProperty("abstandMinuten", out var a)
                ? a.GetInt32() : 120;

            // Nachrichten kommen als Feed, Fachliteratur als einzelne Datei.
            var kind = pillar == "semantic" ? "feed" : "web";

            try
            {
                var id = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                    """
                    INSERT INTO dbo.knowledge_source
                        (kind, pillar, title, origin, poll_minutes, region, status)
                    OUTPUT INSERTED.source_id
                    SELECT @kind, @pillar, @title, @origin, @poll, @region,
                           'eingetragen, noch nicht abgerufen'
                     WHERE NOT EXISTS (
                         SELECT 1 FROM dbo.knowledge_source
                          WHERE pillar = @pillar
                            AND origin_hash = CONVERT(VARBINARY(32),
                                    HASHBYTES('SHA2_256', @origin)));
                    """,
                    new { kind, pillar, title = titel, origin = url, poll = abstand, region },
                    cancellationToken: ct));

                if (id is null) vorhanden++; else angelegt++;
            }
            catch (Exception ex)
            {
                fehler.Add($"{titel}: {ex.Message[..Math.Min(160, ex.Message.Length)]}");
            }
        }

        return (angelegt, vorhanden, fehler);
    }

    // ------------------------------------------------------------ Suche ---

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(
        string pillar, string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        if (!await embedder.IsAvailableAsync(ct) || !await vectors.IsAvailableAsync(ct)) return [];

        var q = (await embedder.EmbedAsync([query], ct))[0];

        var treffer = await vectors.SearchAsync(
            Collection(pillar), q, Math.Clamp(limit, 1, 50), null, ct);

        if (treffer.Count == 0) return [];

        await using var conn = await factory.OpenAsync(ct);

        var ids = treffer.Select(t => t.Id).ToArray();

        /* Eine Abfrage, ein Satz Spalten.

           Zwei getrennte Abfragen -- eine fuer den Text, eine fuer die
           Zuordnung Vektor zu Abschnitt -- waren der erste Entwurf und ein
           unnoetiger Umweg: Die Zuordnung steht in derselben Zeile. */
        var rows = (await conn.QueryAsync<ChunkRow>(new CommandDefinition(
            """
            SELECT c.vector_id AS VectorId, c.chunk_id AS ChunkId, c.source_id AS SourceId,
                   s.title AS Title, s.origin AS Origin, s.kind AS Kind,
                   c.ordinal AS Ordinal,
                   c.page_from AS PageFrom, c.page_to AS PageTo,
                   c.char_from AS CharFrom, c.char_to AS CharTo, c.anchor AS Anchor,
                   ISNULL(c.occurred_utc, s.published_utc) AS OccurredUtc,
                   c.content AS Content
              FROM dbo.knowledge_chunk c
              JOIN dbo.knowledge_source s ON s.source_id = c.source_id
             WHERE c.vector_id IN @ids;
            """, new { ids }, cancellationToken: ct))).ToDictionary(r => r.VectorId);

        // In der Reihenfolge der Aehnlichkeit ausgeben, nicht in der der
        // Datenbank -- die Sortierung IST hier die Antwort.
        var result = new List<KnowledgeHit>(treffer.Count);

        foreach (var t in treffer)
        {
            if (!rows.TryGetValue(t.Id, out var r)) continue;

            result.Add(new KnowledgeHit(
                r.ChunkId, r.SourceId, r.Title, r.Origin, r.Kind, r.Ordinal,
                r.PageFrom, r.PageTo, r.CharFrom, r.CharTo, r.Anchor,
                r.OccurredUtc, Math.Round(t.Score, 4), r.Content));
        }

        return result;
    }

    // ------------------------------------------------------------ Pflege --

    public async Task SetActiveAsync(int sourceId, bool active, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.knowledge_source SET active = @a WHERE source_id = @id;",
            new { a = active, id = sourceId }, cancellationToken: ct));
    }

    public async Task DeleteAsync(int sourceId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var src = await conn.QuerySingleOrDefaultAsync<(string Pillar, string Kind, string Origin)>(
            new CommandDefinition(
                "SELECT pillar, kind, origin FROM dbo.knowledge_source WHERE source_id = @id;",
                new { id = sourceId }, cancellationToken: ct));

        if (src.Pillar is null) return;

        await vectors.DeleteBySourceAsync(Collection(src.Pillar), sourceId, ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.knowledge_source WHERE source_id = @id;",
            new { id = sourceId }, cancellationToken: ct));

        // Die Datei erst nach dem Datenbanksatz — andersherum bliebe bei einem
        // Fehler ein Satz stehen, dessen Datei fehlt.
        if (src.Kind == "file" && File.Exists(src.Origin))
        {
            try { File.Delete(src.Origin); }
            catch (Exception ex) { log.LogWarning(ex, "Datei {Pfad} blieb liegen", src.Origin); }
        }
    }

    /// <summary>
    /// Entfernt Vektoren, zu denen es keinen Abschnitt mehr gibt.
    ///
    /// <b>Warum es das überhaupt braucht.</b> Solange die Kennung eines
    /// Abschnitts zufällig war, hinterließ jeder Neuaufbau seine Vorgänger in
    /// Qdrant — die alten Punkte hatten Kennungen, die kein neuer Lauf je
    /// wiedertrifft. Die Suche findet solche Punkte, schlägt in SQL nach, und
    /// weil dort nichts steht, verschwindet der Treffer stillschweigend. Man
    /// sieht keine Fehlermeldung, sondern weniger Treffer.
    ///
    /// Mit der inhaltsabhängigen Kennung entstehen keine neuen Waisen mehr.
    /// Dieses Werkzeug räumt die alten fort — und bleibt danach als Prüfung
    /// stehen, denn ein Abgleich, den man jederzeit fahren kann, ist mehr wert
    /// als die Annahme, es könne nicht mehr vorkommen.
    /// </summary>
    public async Task<(int Geprueft, int Entfernt)> PurgeOrphansAsync(
        string pillar, CancellationToken ct = default)
    {
        if (!await vectors.IsAvailableAsync(ct)) return (0, 0);

        var collection = Collection(pillar);

        var inQdrant = await vectors.AllIdsAsync(collection, ct);
        if (inQdrant.Count == 0) return (0, 0);

        await using var conn = await factory.OpenAsync(ct);

        var inSql = (await conn.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT c.vector_id
              FROM dbo.knowledge_chunk c
              JOIN dbo.knowledge_source s ON s.source_id = c.source_id
             WHERE s.pillar = @pillar;
            """, new { pillar }, cancellationToken: ct))).ToHashSet();

        var waisen = inQdrant.Where(id => !inSql.Contains(id)).ToList();

        if (waisen.Count > 0)
        {
            log.LogInformation("{N} verwaiste Vektoren in {Sammlung} entfernt",
                waisen.Count, collection);

            // In Blöcken, sonst wird der Rumpf bei zehntausenden Kennungen groß.
            for (var i = 0; i < waisen.Count; i += 2000)
                await vectors.DeleteByIdsAsync(
                    collection, waisen.Skip(i).Take(2000).ToList(), ct);
        }

        return (inQdrant.Count, waisen.Count);
    }

    public async Task<string?> FilePathAsync(int sourceId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var pfad = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT origin FROM dbo.knowledge_source WHERE source_id = @id AND kind = 'file';",
            new { id = sourceId }, cancellationToken: ct));

        if (pfad is null) return null;

        /* Nur aus dem Ablageverzeichnis ausliefern.

           Der Pfad kommt aus der eigenen Datenbank und nicht vom Aufrufer — ein
           Ausbruch ist also unwahrscheinlich. Aber ein Endpunkt, der jede
           beliebige Datei des Rechners herausgibt, sobald irgendwo ein Pfad
           falsch hineingerät, ist es nicht wert. */
        var voll = Path.GetFullPath(pfad);
        var wurzel = Path.GetFullPath(FileRoot);

        return voll.StartsWith(wurzel, StringComparison.OrdinalIgnoreCase) ? voll : null;
    }

    public async Task<object> HealthAsync(CancellationToken ct = default)
    {
        var ollama = await embedder.IsAvailableAsync(ct);
        var qdrant = await vectors.IsAvailableAsync(ct);

        return new
        {
            ollama,
            modell = embedder.Model,
            dimensionen = embedder.Dimensions,
            qdrant,
            vektorenWissen = qdrant ? await vectors.CountAsync("crs_wissen", ct) : 0,
            vektorenSemantik = qdrant ? await vectors.CountAsync("crs_semantik", ct) : 0,
            ablage = FileRoot
        };
    }

    // ------------------------------------------------------------ intern --

    /// <summary>
    /// Die Kennung eines Abschnitts: Quelle, Nummer und Inhalt.
    ///
    /// Sie muss über Läufe hinweg gleich bleiben und darf sich ändern, sobald
    /// sich der Text ändert. Beides zusammen macht das Schreiben idempotent.
    /// </summary>
    /// <summary>
    /// Fassung der Verarbeitung. Bei jeder Änderung an Extraktion, Zerlegung
    /// oder den Feldern eines Abschnitts hochzählen.
    ///
    /// <b>Warum das nötig ist.</b> Der Hash kannte zuerst nur den Text. Als die
    /// Positionsspalten dazukamen, änderte sich der Text nicht — also meldete
    /// jeder Lauf „alle unverändert" und schrieb nichts, während
    /// <c>char_from</c> und <c>anchor</c> leer blieben. Der Hash muss deshalb
    /// nicht nur sagen, WAS in einem Abschnitt steht, sondern auch, WIE er
    /// entstanden ist. Sonst ist die Ersparnis eines übersprungenen Laufs damit
    /// erkauft, dass eine Verbesserung nie ankommt.
    /// </summary>
    private const int VerarbeitungsFassung = 2;

    private static string ChunkHash(int sourceId, int ordinal, string text)
    {
        var roh = $"v{VerarbeitungsFassung}|{sourceId}|{ordinal}|{text}";

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(roh)));
    }

    /// <summary>
    /// Macht aus dem Hash eine Kennung, die Qdrant annimmt.
    ///
    /// Qdrant verlangt eine UUID oder eine Zahl. Die ersten sechzehn Bytes des
    /// SHA-256 sind dafür mehr als genug: Bei den Größenordnungen hier — einige
    /// Millionen Abschnitte — ist ein Zusammenstoß praktisch ausgeschlossen.
    /// </summary>
    private static Guid HashToGuid(string hexHash) =>
        new(Convert.FromHexString(hexHash)[..16]);

    private async Task StatusAsync(int id, string status, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.knowledge_source
               SET status = @s, last_checked_utc = SYSUTCDATETIME()
             WHERE source_id = @id;
            """, new { s = status, id }, cancellationToken: ct));
    }

    /// <summary>Liest Text aus einer Datei. Bei PDF mit Seitenzuordnung.</summary>
    private static async Task<(string Text, List<(int Page, int Offset)>? Pages)> ReadFileAsync(
        string pfad, CancellationToken ct)
    {
        if (!File.Exists(pfad)) return ("", null);

        if (pfad.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder();
            var seiten = new List<(int, int)>();

            using var pdf = PdfDocument.Open(pfad);

            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();

                seiten.Add((page.Number, sb.Length));
                sb.AppendLine(SeitenText(page));
                sb.AppendLine();
            }

            return (sb.ToString(), seiten);
        }

        return (await File.ReadAllTextAsync(pfad, ct), null);
    }

    /// <summary>
    /// Der Text einer Seite ohne Kopf- und Fußzeile.
    ///
    /// <b>Warum über die Geometrie und nicht über den Text.</b> Der erste
    /// Versuch strich Zeilen, die auf vielen Seiten wortgleich vorkommen. Das
    /// scheiterte doppelt: PdfPigs <c>page.Text</c> liefert die ganze Seite als
    /// eine Zeichenkette ohne Zeilenumbrüche, es gibt also keine Zeilen zu
    /// vergleichen — und eine Fußzeile trägt meist die Seitenzahl mit und ist
    /// deshalb nie wortgleich.
    ///
    /// Über die Lage ist es eindeutig: Was im obersten und untersten Zwanzigstel
    /// der Seite steht, ist Kolumnentitel oder Fußzeile. Das gilt für jedes
    /// Dokument, ohne dass man es auf eines einstellen müsste.
    ///
    /// <b>Was dabei bewusst mit verlorengeht.</b> Fußnoten stehen zum Teil
    /// ebenfalls unten. Für die Ähnlichkeitssuche ist das kein Verlust: „Vgl.
    /// Kahneman/Tversky (1979), S. 263ff." trägt keine Aussage, die man suchen
    /// könnte, verschiebt aber jeden Vektor, in dem sie steht.
    /// </summary>
    private static string SeitenText(UglyToad.PdfPig.Content.Page page)
    {
        var hoehe = page.Height;
        if (hoehe <= 0) return page.Text;

        var unten = hoehe * 0.06;
        var oben = hoehe * 0.94;

        var woerter = page.GetWords()
            .Where(w =>
            {
                var y = w.BoundingBox.Bottom;
                return y > unten && y < oben;
            })
            .ToList();

        if (woerter.Count == 0) return page.Text;

        /* Zeilen aus den Wortkästen zurückgewinnen: Wörter mit annähernd
           gleicher Grundlinie gehören zusammen. Ohne diesen Schritt entstünde
           wieder eine einzige lange Zeichenkette, und die Absatzzerlegung
           hätte nichts zu greifen. */
        var zeilen = woerter
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3.0))
            .OrderByDescending(g => g.Key)
            .Select(g => string.Join(' ', g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));

        var text = string.Join('\n', zeilen);

        /* Fußnotenzeilen, die es trotzdem in den Textkörper geschafft haben.
           Sie beginnen mit einer hochgestellten Nummer und einem Zitierkürzel —
           ein Muster, das im Fließtext nicht vorkommt. */
        text = Regex.Replace(text, @"^\s*\d{1,4}\s+(Vgl\.|vgl\.|Quelle:|Ibid|Ebd\.).*$", "",
                             RegexOptions.Multiline);

        return text;
    }

    /// <summary>
    /// Holt etwas aus dem Netz — Seite, reiner Text oder PDF.
    ///
    /// <b>Warum die Fallunterscheidung hierher gehört.</b> Eine Adresse sagt
    /// nicht, was hinter ihr steckt. Die offen zugänglichen Arbeiten dieser
    /// Sammlung sind PDFs, die Bücher aus dem Project Gutenberg reiner Text,
    /// Nachrichtenseiten HTML — und alle drei kommen über dieselbe Schnittstelle
    /// herein. Ein PDF durch die HTML-Reinigung zu schicken liefert
    /// Byte-Salat, den niemand als Fehler erkennt: Es entstehen Abschnitte,
    /// sie werden eingebettet, und die Suche findet später Unsinn mit
    /// ordentlich aussehenden Ähnlichkeitswerten.
    /// </summary>
    private async Task<(string Text, List<(int Page, int Offset)>? Pages)> ReadRemoteAsync(
        string url, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("browser");

        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return ("", null);

        var typ = res.Content.Headers.ContentType?.MediaType ?? "";

        var istPdf = typ.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                  || url.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

        if (!istPdf)
            return (await ReadWebAsync(url, ct), null);

        /* PdfPig braucht eine Datei oder einen suchbaren Strom. Das PDF landet
           deshalb erst auf der Platte -- und wird danach aufgeräumt, denn der
           Text steht anschließend in der Datenbank und die Datei wird nicht
           mehr gebraucht. */
        Directory.CreateDirectory(FileRoot);

        var tmp = Path.Combine(FileRoot, $"tmp_{Guid.NewGuid():N}.pdf");

        try
        {
            await using (var fs = File.Create(tmp))
                await res.Content.CopyToAsync(fs, ct);

            return await ReadFileAsync(tmp, ct);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception ex) { log.LogDebug(ex, "Zwischendatei {Pfad} blieb liegen", tmp); }
        }
    }

    /// <summary>Holt eine Seite und macht aus dem HTML lesbaren Text.</summary>
    private async Task<string> ReadWebAsync(string url, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("browser");

        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return "";

        var html = await res.Content.ReadAsStringAsync(ct);

        /* Reiner Text geht ungefiltert durch.

           Der erste Entwurf schickte alles durch die HTML-Reinigung und den
           Fließtext-Filter. Bei den gemeinfreien Büchern aus dem Project
           Gutenberg zerstörte das den Inhalt vollständig: Deren Text ist hart
           auf siebzig Zeichen umbrochen, also ist JEDE Zeile zu kurz für den
           Filter — von zwölf Büchern kam eines durch, der Rest meldete „Text
           zu kurz". Ein Filter, der für HTML gebaut ist, darf nicht auf etwas
           losgelassen werden, das kein HTML ist. */
        var typ = res.Content.Headers.ContentType?.MediaType ?? "";

        var istHtml = typ.Contains("html", StringComparison.OrdinalIgnoreCase)
                   || (typ.Length == 0 && html.TrimStart().StartsWith('<'));

        if (!istHtml) return Entfalten(html);

        /* Eine grobe Reinigung, kein HTML-Parser.

           Für den Zweck reicht sie: Was hier gesucht wird, ist Fließtext für
           die Ähnlichkeitssuche, nicht die Dokumentstruktur. Ein vollständiger
           Parser wäre eine weitere Abhängigkeit für einen Gewinn, den die
           Einbettung ohnehin einebnet. Skripte und Formatvorlagen müssen aber
           raus — sie sind lang, bedeutungsfrei und verschieben jeden Vektor. */
        html = Regex.Replace(html, @"<script\b[^>]*>.*?</script>", " ",
                             RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style\b[^>]*>.*?</style>", " ",
                             RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<(br|/p|/div|/li|/h\d)\s*>", "\n",
                             RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", " ");

        html = System.Net.WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, @"[ \t]+", " ");
        html = Regex.Replace(html, @"\n\s*\n\s*\n+", "\n\n");

        return NurFliesstext(html);
    }

    /// <summary>
    /// Macht aus hart umbrochenem Text wieder Absätze.
    ///
    /// Ein Zeilenumbruch innerhalb eines Absatzes wird zum Leerzeichen, eine
    /// Leerzeile bleibt Absatzgrenze. Ohne diesen Schritt trennt die Zerlegung
    /// an jeder Zeile statt an jedem Absatz, und ein Abschnitt endet mitten im
    /// Satz.
    /// </summary>
    private static string Entfalten(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var absaetze = Regex.Split(text, @"\n\s*\n")
            .Select(a => Regex.Replace(a, @"\s*\n\s*", " ").Trim())
            .Where(a => a.Length > 0);

        return string.Join("\n\n", absaetze);
    }

    /// <summary>
    /// Behält vom Seitentext nur das, was nach Fließtext aussieht.
    ///
    /// <b>Warum das nötig ist.</b> Der erste Lauf über echte Nachrichtenseiten
    /// lieferte auf jede Frage Abschnitte wie „Skip Navigation Markets
    /// Pre-Markets U.S. Markets Europe Markets China Markets Asia Markets World
    /// Markets" — das Navigationsmenü. Es steht auf jeder Seite derselben
    /// Quelle, es ist lang, und es hat keinen Inhalt. Eingebettet zieht es alle
    /// Vektoren einer Quelle in dieselbe Ecke, und die Suche findet danach
    /// vor allem, von welcher Seite ein Text stammt.
    ///
    /// <b>Woran man es erkennt.</b> Nicht an Schlüsselwörtern — die ändern sich
    /// je Seite —, sondern an der <i>Struktur</i>: Ein Menü besteht aus kurzen
    /// Bezeichnungen ohne Satzzeichen. Fließtext hat Sätze, also Punkte. Die
    /// Zeichendichte je Satzende trennt beides zuverlässig und ohne Wissen über
    /// die einzelne Seite.
    /// </summary>
    private static string NurFliesstext(string text)
    {
        var behalten = new List<string>();

        /* Bewertet werden ABSÄTZE, nicht Zeilen.

           Zeilenweise zu urteilen war der Fehler: Ein hart umbrochener Text hat
           lauter kurze Zeilen, von denen keine für sich einen Satz enthält —
           der Filter warf dann alles weg. Ein Absatz dagegen ist die kleinste
           Einheit, die eine Aussage tragen kann, und zwar in beiden Fällen: bei
           umbrochenem Buchtext wie bei einem HTML-Block. */
        var absaetze = System.Text.RegularExpressions.Regex
            .Split(text, @"\n\s*\n")
            .Select(a => System.Text.RegularExpressions.Regex
                          .Replace(a, @"\s*\n\s*", " ").Trim())
            .Where(a => a.Length > 0);

        foreach (var roh in absaetze)
        {
            var z = roh.Trim();

            // Zu kurz für einen Satz, aber lang genug, um in die Zerlegung zu
            // geraten — typisch für Menüpunkte und Schaltflächen.
            if (z.Length < 60) continue;

            var satzenden = z.Count(c => c is '.' or '!' or '?' or '…');

            /* Ein Satzende je 220 Zeichen ist großzügig: Deutsche Schachtelsätze
               kommen auf 150 bis 200 Zeichen, ein Menü auf keines. */
            if (satzenden == 0 || z.Length / satzenden > 220) continue;

            /* Zusätzlich ein Blick auf die Wortlänge. Menüs bestehen aus
               Substantiven; Fließtext hat Artikel, Präpositionen und
               Konjunktionen und damit einen spürbaren Anteil kurzer Wörter. */
            var woerter = z.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (woerter.Length < 12) continue;

            var kurze = woerter.Count(w => w.Length <= 3);
            if (kurze * 100 / woerter.Length < 12) continue;

            behalten.Add(z);
        }

        var ergebnis = string.Join("\n\n", behalten).Trim();

        /* Bleibt nichts übrig, war die Seite entweder eine reine Übersicht oder
           die Erkennung hat danebengegriffen. Dann lieber den Rohtext als gar
           nichts — ein leerer Artikel wäre eine Lücke, die man später nicht
           mehr bemerkt. */
        return ergebnis.Length > 200 ? ergebnis : text.Trim();
    }

    /// <summary>Eine Abschnittszeile, wie sie aus SQL kommt.</summary>
    private sealed record ChunkRow(
        Guid VectorId, long ChunkId, int SourceId, string Title, string Origin,
        string Kind, int Ordinal, int? PageFrom, int? PageTo,
        int? CharFrom, int? CharTo, string? Anchor,
        DateTime? OccurredUtc, string Content);

    /// <summary>
    /// Ein Abschnitt mit seiner Herkunft im Quelltext.
    ///
    /// <paramref name="CharFrom"/> und <paramref name="CharTo"/> sind
    /// Zeichenpositionen im extrahierten Text — sie sagen, WO im Buch die
    /// Stelle steht. <paramref name="Anchor"/> sind die ersten Worte, mit denen
    /// ein Browser über <c>#:~:text=…</c> dorthin springt; das überlebt auch
    /// eine nachträglich geänderte Quelle, in der sich alle Positionen
    /// verschoben haben.
    /// </summary>
    private sealed record Abschnitt(
        string Text, int? PageFrom, int? PageTo,
        int CharFrom = 0, int CharTo = 0, string Anchor = "");

    /// <summary>
    /// Zerlegt Text in überlappende Abschnitte an Absatzgrenzen.
    /// </summary>
    private static List<Abschnitt> Chunk(
        string text, List<(int Page, int Offset)>? pages,
        int ziel = 1400, int ueberlappung = 200)
    {
        var result = new List<Abschnitt>();

        /* Die Absätze werden MIT ihrer Position im Ausgangstext geführt.

           Der erste Entwurf zerlegte nur und warf die Herkunft weg. Ein Treffer
           ohne Fundstelle ist aber eine Behauptung: Wer ihn nachschlagen will,
           müsste das ganze Buch durchsehen — und genau das soll die Suche
           ersparen. */
        var absaetze = new List<(string Text, int Offset)>();

        var pos = 0;

        foreach (var roh in Regex.Split(text, @"\n\s*\n"))
        {
            var t = roh.Trim();

            if (t.Length > 0)
            {
                // Wo der getrimmte Absatz im Ausgangstext wirklich beginnt.
                var innen = roh.IndexOf(t[0]);
                absaetze.Add((t, pos + (innen < 0 ? 0 : innen)));
            }

            pos += roh.Length + 2;
        }

        if (absaetze.Count == 0) return result;

        int? SeiteBei(int offset)
        {
            if (pages is null || pages.Count == 0) return null;

            var lo = 0;
            for (var i = 0; i < pages.Count; i++)
                if (pages[i].Offset <= offset) lo = i; else break;

            return pages[lo].Page;
        }

        /* Der Textanker: die ersten Worte des Abschnitts.

           Sechs bis acht Worte sind genug, um eine Stelle eindeutig zu treffen,
           und kurz genug, um in eine Adresszeile zu passen. Zeilenumbrüche und
           doppelte Leerzeichen müssen raus — der Browser vergleicht wörtlich. */
        static string Anker(string t)
        {
            var sauber = Regex.Replace(t, @"\s+", " ").Trim();

            /* Am Wortanfang beginnen, nicht mitten im Satz.

               Durch die Überlappung fängt ein Abschnitt oft mit dem Ende des
               vorigen an — also mit einem Punkt, einem Komma oder einem halben
               Wort. Der Browser vergleicht wörtlich; ein Anker, der mit „. Some
               cautious" beginnt, trifft nichts. Gesucht wird deshalb der erste
               Großbuchstabe nach einem Satzende, ersatzweise der erste
               Buchstabe überhaupt. */
            var satz = Regex.Match(sauber, @"(?<=[.!?]\s)[A-ZÄÖÜ]");

            var ab = satz.Success && satz.Index < 240
                ? satz.Index
                : Math.Max(0, sauber.IndexOfAny(
                    "ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÜabcdefghijklmnopqrstuvwxyzäöüß".ToCharArray()));

            sauber = sauber[ab..];

            var worte = sauber.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                              .Take(8)
                              .ToArray();

            var anker = string.Join(' ', worte);

            return anker.Length > 180 ? anker[..180] : anker;
        }

        void Fertig(string inhalt, int von, int bis)
        {
            var t = inhalt.Trim();
            if (t.Length < 100) return;

            result.Add(new Abschnitt(t, SeiteBei(von), SeiteBei(bis), von, bis, Anker(t)));
        }

        var sb = new StringBuilder();
        var start = absaetze[0].Offset;

        foreach (var (abs, offset) in absaetze)
        {
            /* Ein einzelner Absatz über der Zielgröße wird hart geteilt. Das
               kommt bei Tabellen und Literaturverzeichnissen vor; ihn ganz zu
               lassen hieße, einen Abschnitt zu erzeugen, der das Modell
               überfordert und dessen Vektor nichts mehr bedeutet. */
            if (abs.Length > ziel * 2)
            {
                if (sb.Length > 0)
                {
                    Fertig(sb.ToString(), start, offset);
                    sb.Clear();
                }

                for (var i = 0; i < abs.Length; i += ziel)
                {
                    var laenge = Math.Min(ziel, abs.Length - i);
                    Fertig(abs.Substring(i, laenge), offset + i, offset + i + laenge);
                }

                start = offset + abs.Length;
                continue;
            }

            if (sb.Length + abs.Length > ziel && sb.Length > 0)
            {
                var inhalt = sb.ToString();
                Fertig(inhalt, start, offset);

                /* Überlappung: das Ende des fertigen Abschnitts wandert an den
                   Anfang des nächsten. Eine Aussage, die genau auf der Grenze
                   steht, ist damit in mindestens einem Abschnitt vollständig.

                   Der Anfang zählt dabei ab dem übernommenen Rest, nicht ab
                   dem neuen Absatz — sonst zeigte die Fundstelle hinter den
                   Text, der im Abschnitt steht. */
                sb.Clear();

                var rest = inhalt.TrimEnd();

                if (rest.Length > ueberlappung)
                {
                    sb.Append(rest[^ueberlappung..]).Append("\n\n");
                    start = Math.Max(0, offset - ueberlappung);
                }
                else start = offset;
            }

            sb.Append(abs).Append("\n\n");
        }

        Fertig(sb.ToString(), start, pos);

        /* Abschnitte ohne Aussage aussortieren.

           Der erste Durchgang über eine wissenschaftliche Arbeit lieferte auf
           jede Frage Fußnotenapparat und Literaturverzeichnis: „Vgl. Grinblatt,
           et al. (2012), S. 360f." — Text, der aus Namen, Jahreszahlen und
           Seitenangaben besteht. Solche Abschnitte haben keinen Inhalt, aber
           einen Vektor, und der liegt ungefähr in der Mitte von allem. Damit
           sind sie zu jeder Frage mittelmäßig ähnlich und verdrängen die
           Abschnitte, die tatsächlich etwas sagen. */
        return result.Where(Brauchbar).ToList();
    }

    /// <summary>Trennt Fließtext von Fußnotenapparat und Verzeichnissen.</summary>
    private static bool Brauchbar(Abschnitt a)
    {
        var t = a.Text;
        if (t.Length < 120) return false;

        var ziffern = t.Count(char.IsDigit) / (double)t.Length;

        // Ein Verzeichnis besteht zu einem Fünftel aus Zahlen; Fließtext
        // erreicht das selbst mit Kursangaben kaum.
        if (ziffern > 0.18) return false;

        var zitate = Regex.Matches(t, @"(Vgl\.|vgl\.|et al\.|S\.\s*\d|ebd\.|Ibid)").Count;

        if (zitate * 400.0 / t.Length > 3) return false;

        /* Literaturverzeichnis.

           Der Ziffernfilter allein reicht nicht: Ein Verzeichniseintrag wie
           „Charness, G./Gneezy, U. (2012): Strong Evidence for Gender
           Differences in Risk Taking" ist voller langer Wörter und hat wenige
           Ziffern — er kam beim ersten Versuch als bester Treffer auf die
           Frage nach Risikomanagement. Sein Kennzeichen ist etwas anderes: die
           dichte Folge von Jahreszahlen in Klammern, eine je Eintrag. */
        var jahre = Regex.Matches(t, @"\((19|20)\d{2}[a-z]?\)").Count;

        if (jahre * 400.0 / t.Length > 2.0) return false;

        /* Inhaltswörter: mindestens sechs Buchstaben, keine Ziffern. Ein
           Fußnotenapparat hat davon fast keine — er besteht aus Kürzeln,
           Eigennamen und Zahlen. */
        var woerter = Regex.Matches(t, @"[A-Za-zÄÖÜäöüß]{6,}").Count;

        return woerter * 400.0 / t.Length >= 4;
    }
}
