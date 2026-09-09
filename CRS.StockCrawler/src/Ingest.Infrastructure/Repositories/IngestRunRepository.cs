using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Core.Models;

namespace Ingest.Infrastructure.Repositories;

public sealed class IngestRunRepository : IIngestRunRepository
{
    private readonly ISqlConnectionFactory _factory;

    public IngestRunRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<long> StartAsync(string jobName, ProviderId? provider, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO dbo.ingest_run (job_name, provider)
            OUTPUT INSERTED.run_id
            VALUES (@jobName, @provider)
            """, new { jobName, provider = provider.HasValue ? (byte?)provider.Value : null },
            cancellationToken: ct));
    }

    public async Task FinishAsync(long runId, int ok, int err, int rows, string? note, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.ingest_run
               SET finished_utc = SYSUTCDATETIME(),
                   ok_count = @ok, err_count = @err,
                   rows_written = @rows, note = @note
             WHERE run_id = @runId
            """,
            // Note ist auf 4000 Zeichen begrenzt; lange Fehlerlisten abschneiden.
            new { runId, ok, err, rows, note = note?.Length > 3900 ? note[..3900] + "..." : note },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<IngestRun>> GetRecentAsync(int last, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<IngestRun>(new CommandDefinition("""
            SELECT TOP (@last)
                   run_id AS RunId, job_name AS JobName, provider AS Provider,
                   started_utc AS StartedUtc, finished_utc AS FinishedUtc,
                   ok_count AS OkCount, err_count AS ErrCount,
                   rows_written AS RowsWritten, note AS Note
              FROM dbo.ingest_run
             ORDER BY run_id DESC
            """, new { last }, cancellationToken: ct));

        return rows.ToList();
    }
}
