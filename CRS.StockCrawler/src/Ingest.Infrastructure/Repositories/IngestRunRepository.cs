using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Ingest.Infrastructure.Datenbank;

namespace Ingest.Infrastructure.Repositories;

public sealed class IngestRunRepository : IIngestRunRepository
{
    private readonly ISqlConnectionFactory _factory;
    private SqlDialekt d => _factory.Dialekt;

    public IngestRunRepository(ISqlConnectionFactory factory) => _factory = factory;

    /*  Der Startzeitpunkt wird GESCHRIEBEN, nicht dem Spaltenstandard
        überlassen — und `finished_utc` unten genauso.

        Zwei verschiedene Defekte, beide am 28.09.2026 an einer Probezeile
        nachgestellt:

          INSERT … (job_name, provider) VALUES (…)          -- Spaltenstandard
          UPDATE … SET finished_utc = now() at time zone 'utc'
            → started_utc = NULL,  finished_utc = 00:42:55

          INSERT … (job_name, provider, started_utc) VALUES (…, @jetzt)
          UPDATE … SET finished_utc = @jetzt
            → started_utc = 22:42:55,  finished_utc = 22:42:55

        Erstens greift der Spaltenstandard auf dem EventMesh-DataCell-Backend
        beim INSERT nicht — `started_utc` bleibt leer, ohne Fehler und ohne
        Meldung. Zweitens liefert `now() at time zone 'utc'` dort die
        ORTSZEIT: 00:42:55 statt 22:42:55, zwei Stunden in der Zukunft. Ein
        so gesetzter Zeitstempel, verglichen mit einem aus C# geschriebenen,
        ist damit systematisch falsch — und zwar in die gefährlichere
        Richtung, denn er sieht neuer aus, als er ist.

        Die Folge des ersten Defekts war teuer und sah nach etwas ganz anderem
        aus. `NachholenAsync` entscheidet über `MAX(started_utc) … AND
        finished_utc IS NOT NULL`, ob ein Tageslauf versäumt wurde. Ohne
        `started_utc` findet es nie einen erledigten Lauf und holt bei JEDEM
        Start nach — an diesem Abend bei jedem einzelnen Neustart ein Lauf über
        646 Werte und 35 Minuten. Ich habe das stundenlang für richtiges
        Verhalten gehalten, weil die Meldung „Tages- und Stundenlauf versäumt"
        ja stimmte.

        Dieselbe Familie wie der Sitzungsfehler vom Vortag, wo `expires_utc =
        jetzt + 14 Tage` still zu `jetzt` wurde und damit jede Anmeldung im
        Moment ihrer Entstehung ablief. Und dieselbe Lehre, die seither in
        CLAUDE.md steht: Was in C# gerechnet wird, hängt an keiner Zusage der
        Datenbank.                                                             */
    public async Task<long> StartAsync(string jobName, ProviderId? provider, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        return await conn.ExecuteScalarAsync<long>(new CommandDefinition($"""
            INSERT INTO dbo.ingest_run (job_name, provider, started_utc)
            {d.RueckgabeVor("run_id")} VALUES (@jobName, @provider, @jetzt) {d.RueckgabeNach("run_id")}
            """, new
            {
                jobName,
                provider = provider.HasValue ? (byte?)provider.Value : null,
                jetzt = DateTime.UtcNow,
            },
            cancellationToken: ct));
    }

    public async Task FinishAsync(long runId, int ok, int err, int rows, string? note, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.ingest_run
               SET finished_utc = @jetzt,
                   ok_count = @ok, err_count = @err,
                   rows_written = @rows, note = @note
             WHERE run_id = @runId
            """,
            // Note ist auf 4000 Zeichen begrenzt; lange Fehlerlisten abschneiden.
            new
            {
                runId, ok, err, rows,
                jetzt = DateTime.UtcNow,
                note = note?.Length > 3900 ? note[..3900] + "..." : note,
            },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<IngestRun>> GetRecentAsync(int last, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<IngestRun>(new CommandDefinition("""
            SELECT run_id AS RunId, job_name AS JobName, provider AS Provider,
                   started_utc AS StartedUtc, finished_utc AS FinishedUtc,
                   ok_count AS OkCount, err_count AS ErrCount,
                   rows_written AS RowsWritten, note AS Note
              FROM dbo.ingest_run
             ORDER BY run_id DESC OFFSET 0 ROWS FETCH NEXT @last ROWS ONLY
            """, new { last }, cancellationToken: ct));

        return rows.ToList();
    }
}
