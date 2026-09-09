using System.Text.Json;
using Dapper;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>Ein abgelegtes Gespräch — Frage, Antwort und die Werkzeugspur dazu.</summary>
public sealed record ReasoningEintrag(
    int LogId, DateTime AskedUtc, string? Wer, string Frage, string Antwort,
    string? Modell, string? Endpunkt, decimal? Sekunden, int? Runden,
    string? Werkzeuge, bool Gemerkt, string? Notiz);

public interface IReasoningLogService
{
    Task<int> SchreibeAsync(int? userId, string frage, string antwort, string? modell,
                            string? endpunkt, double sekunden, int runden,
                            object? werkzeuge, CancellationToken ct = default);

    Task<IReadOnlyList<ReasoningEintrag>> ListeAsync(
        string? suche, bool nurGemerkte, int limit, int versatz,
        CancellationToken ct = default);

    Task<ReasoningEintrag?> EinzelnAsync(int logId, CancellationToken ct = default);

    Task<bool> MerkenAsync(int logId, bool gemerkt, string? notiz,
                           CancellationToken ct = default);

    Task<bool> LoeschenAsync(int logId, CancellationToken ct = default);

    /// <summary>Räumt alte, nicht gemerkte Einträge weg.</summary>
    Task<int> AufraeumenAsync(int aelterAlsTage, CancellationToken ct = default);
}

/// <summary>
/// Legt jede Reasoning-Antwort ab, damit man sie nachlesen kann.
///
/// <para><b>Warum das nötig war.</b> Ein Gespräch lebte ausschliesslich im Browser-Tab. Ein
/// Neuladen löschte es, ein zweiter Rechner sah es nie — und eine Antwort, für die das Modell
/// vier Minuten gerechnet hat, war damit weg. Das „Tagesjournal" auf derselben Seite ist etwas
/// anderes: Es entsteht ohne Sprachmodell aus Abfragen.</para>
///
/// <para><b>Warum automatisch und nicht auf Knopfdruck.</b> Ein „Speichern"-Knopf setzt voraus,
/// dass man vor dem Lesen weiss, ob die Antwort es wert ist. Das weiss man nie. Geschrieben wird
/// deshalb jede Frage; geräumt wird hinterher.</para>
///
/// <para><b>Das Schreiben darf die Antwort nie verhindern.</b> Die teure Ressource ist die
/// Rechenzeit, die schon verbraucht ist. Scheitert die Ablage — Datenbank weg, Spalte zu kurz —,
/// bekommt der Fragende trotzdem seine Antwort und im Protokoll steht, was schiefging.</para>
/// </summary>
public sealed class ReasoningLogService : IReasoningLogService
{
    private readonly ISqlConnectionFactory _factory;
    private readonly ILogger<ReasoningLogService> _log;

    public ReasoningLogService(ISqlConnectionFactory factory, ILogger<ReasoningLogService> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task<int> SchreibeAsync(
        int? userId, string frage, string antwort, string? modell, string? endpunkt,
        double sekunden, int runden, object? werkzeuge, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _factory.OpenAsync(ct);

            return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                INSERT INTO dbo.reasoning_log
                    (frage, antwort, modell, endpunkt, sekunden, runden, werkzeuge, user_id)
                OUTPUT INSERTED.log_id
                VALUES (@frage, @antwort, @modell, @endpunkt, @sekunden, @runden,
                        @werkzeuge, @userId)
                """,
                new
                {
                    frage,
                    antwort,
                    modell,
                    endpunkt,
                    sekunden = (decimal)Math.Round(sekunden, 1),
                    runden,
                    werkzeuge = werkzeuge is null ? null : JsonSerializer.Serialize(werkzeuge),
                    userId
                },
                cancellationToken: ct));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reasoning-Antwort konnte nicht abgelegt werden");
            return 0;
        }
    }

    public async Task<IReadOnlyList<ReasoningEintrag>> ListeAsync(
        string? suche, bool nurGemerkte, int limit, int versatz, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        /* Die Suche geht in die Abfrage, nicht dahinter. Wer die neuesten 200 holt und danach
           filtert, findet einen älteren Treffer nie — dieselbe Falle wie bei `LinksAsync`,
           wo der Agent daraufhin wahrheitswidrig meldete, es gebe keine Verknüpfungen. */
        var zeilen = await conn.QueryAsync<ReasoningEintrag>(new CommandDefinition(
            """
            SELECT  l.log_id      AS LogId,
                    l.asked_utc   AS AskedUtc,
                    u.login       AS Wer,
                    l.frage       AS Frage,
                    l.antwort     AS Antwort,
                    l.modell      AS Modell,
                    l.endpunkt    AS Endpunkt,
                    l.sekunden    AS Sekunden,
                    l.runden      AS Runden,
                    l.werkzeuge   AS Werkzeuge,
                    l.gemerkt     AS Gemerkt,
                    l.notiz       AS Notiz
              FROM  dbo.reasoning_log l
              LEFT  JOIN dbo.app_user u ON u.user_id = l.user_id
             WHERE  (@nurGemerkte = 0 OR l.gemerkt = 1)
               AND  (@suche IS NULL
                     OR l.frage   LIKE '%' + @suche + '%'
                     OR l.antwort LIKE '%' + @suche + '%'
                     OR l.notiz   LIKE '%' + @suche + '%')
             ORDER  BY l.asked_utc DESC, l.log_id DESC
            OFFSET  @versatz ROWS FETCH NEXT @limit ROWS ONLY
            """,
            new
            {
                suche = string.IsNullOrWhiteSpace(suche) ? null : suche.Trim(),
                nurGemerkte = nurGemerkte ? 1 : 0,
                limit = Math.Clamp(limit, 1, 200),
                versatz = Math.Max(0, versatz)
            },
            cancellationToken: ct));

        return zeilen.ToList();
    }

    public async Task<ReasoningEintrag?> EinzelnAsync(int logId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        return await conn.QuerySingleOrDefaultAsync<ReasoningEintrag>(new CommandDefinition(
            """
            SELECT  l.log_id AS LogId, l.asked_utc AS AskedUtc, u.login AS Wer,
                    l.frage AS Frage, l.antwort AS Antwort, l.modell AS Modell,
                    l.endpunkt AS Endpunkt, l.sekunden AS Sekunden, l.runden AS Runden,
                    l.werkzeuge AS Werkzeuge, l.gemerkt AS Gemerkt, l.notiz AS Notiz
              FROM  dbo.reasoning_log l
              LEFT  JOIN dbo.app_user u ON u.user_id = l.user_id
             WHERE  l.log_id = @logId
            """,
            new { logId }, cancellationToken: ct));
    }

    public async Task<bool> MerkenAsync(int logId, bool gemerkt, string? notiz,
                                        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        /* Die Notiz nur überschreiben, wenn eine mitkommt: Ein Klick auf „merken" soll nicht
           die Notiz löschen, die jemand vorher geschrieben hat. */
        var n = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.reasoning_log
               SET gemerkt = @gemerkt,
                   notiz   = COALESCE(@notiz, notiz)
             WHERE log_id = @logId
            """,
            new { logId, gemerkt, notiz }, cancellationToken: ct));

        return n > 0;
    }

    public async Task<bool> LoeschenAsync(int logId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var n = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.reasoning_log WHERE log_id = @logId",
            new { logId }, cancellationToken: ct));

        return n > 0;
    }

    public async Task<int> AufraeumenAsync(int aelterAlsTage, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        /* Gemerkte Einträge überleben das Aufräumen — das ist der ganze Zweck des Häkchens. */
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM dbo.reasoning_log
             WHERE gemerkt = 0
               AND asked_utc < DATEADD(day, -@tage, SYSUTCDATETIME())
            """,
            new { tage = Math.Max(1, aelterAlsTage) }, cancellationToken: ct));
    }
}
