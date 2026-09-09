using Dapper;
using Ingest.Core.Abstractions;

namespace Ingest.Infrastructure.Repositories;

/// <summary>Wem der gemerkte Zustand gehört.</summary>
public enum StateOwner
{
    /// <summary>Einer Sitzung, wiedererkannt über ein Cookie.</summary>
    Session = 0,

    /// <summary>Einem angemeldeten Benutzer.</summary>
    User = 1
}

/// <summary>Ein gemerkter Bereich der Oberfläche.</summary>
public sealed record StateEntry(string Area, string Payload, DateTime UpdatedUtc);

public interface IAppStateRepository
{
    /// <summary>
    /// Legt die Sitzung an, falls sie neu ist, und vermerkt, dass sie eben
    /// gesehen wurde.
    /// </summary>
    Task TouchSessionAsync(Guid sessionKey, CancellationToken ct = default);

    /// <summary>Zu welchem Benutzer gehört diese Sitzung — falls schon einer.</summary>
    Task<int?> GetSessionUserAsync(Guid sessionKey, CancellationToken ct = default);

    /// <summary>
    /// Der gemerkte Zustand.
    ///
    /// Ist ein Benutzer angemeldet, gilt dessen Zustand; die Sitzung dient
    /// dann nur noch als Rückfallebene für Bereiche, die er auf diesem Gerät
    /// eingestellt, aber noch nie angemeldet gespeichert hat.
    /// </summary>
    Task<IReadOnlyList<StateEntry>> GetAsync(Guid sessionKey, int? userId,
                                             CancellationToken ct = default);

    Task SaveAsync(StateOwner owner, string ownerKey, string area, string payload,
                   CancellationToken ct = default);

    Task<int> ClearAsync(StateOwner owner, string ownerKey, CancellationToken ct = default);

    /// <summary>
    /// Schreibt den Zustand einer Sitzung auf einen Benutzer um — der Schritt,
    /// der beim Anmelden fällig wird. Vorhandener Benutzerzustand bleibt
    /// stehen: was jemand angemeldet eingestellt hat, wiegt schwerer als das,
    /// was auf einem einzelnen Gerät herumliegt.
    /// </summary>
    Task<int> AdoptSessionAsync(Guid sessionKey, int userId, CancellationToken ct = default);
}

public sealed class AppStateRepository : IAppStateRepository
{
    private readonly ISqlConnectionFactory _factory;

    public AppStateRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task TouchSessionAsync(Guid sessionKey, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.app_session AS t
            USING (SELECT @key AS session_key) AS s
              ON t.session_key = s.session_key
            WHEN MATCHED THEN
              UPDATE SET last_seen_utc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
              INSERT (session_key) VALUES (s.session_key);
            """, new { key = sessionKey }, cancellationToken: ct));
    }

    public async Task<int?> GetSessionUserAsync(Guid sessionKey, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT user_id FROM dbo.app_session WHERE session_key = @key;",
            new { key = sessionKey }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<StateEntry>> GetAsync(Guid sessionKey, int? userId,
                                                          CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        /* Beide Ebenen holen und je Bereich die höherwertige gewinnen lassen.
           ROW_NUMBER statt zweier Abfragen, damit die Auswahl in der Datenbank
           stattfindet und nicht erst im Speicher. */
        var rows = await conn.QueryAsync<StateEntry>(new CommandDefinition("""
            WITH candidates AS (
              SELECT area, payload, updated_utc,
                     ROW_NUMBER() OVER (PARTITION BY area ORDER BY owner_kind DESC) AS rn
              FROM dbo.app_state
              WHERE (owner_kind = 0 AND owner_key = @sessionKey)
                 OR (owner_kind = 1 AND owner_key = @userKey AND @userKey IS NOT NULL)
            )
            SELECT area AS Area, payload AS Payload, updated_utc AS UpdatedUtc
            FROM candidates
            WHERE rn = 1;
            """,
            new
            {
                sessionKey = sessionKey.ToString(),
                userKey = userId?.ToString()
            },
            cancellationToken: ct));

        return rows.ToList();
    }

    public async Task SaveAsync(StateOwner owner, string ownerKey, string area, string payload,
                                CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.app_state AS t
            USING (SELECT @kind AS owner_kind, @key AS owner_key, @area AS area) AS s
              ON t.owner_kind = s.owner_kind AND t.owner_key = s.owner_key AND t.area = s.area
            WHEN MATCHED THEN
              UPDATE SET payload = @payload, updated_utc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
              INSERT (owner_kind, owner_key, area, payload)
              VALUES (s.owner_kind, s.owner_key, s.area, @payload);
            """,
            new { kind = (byte)owner, key = ownerKey, area, payload },
            cancellationToken: ct));
    }

    public async Task<int> ClearAsync(StateOwner owner, string ownerKey, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.app_state WHERE owner_kind = @kind AND owner_key = @key;",
            new { kind = (byte)owner, key = ownerKey }, cancellationToken: ct));
    }

    public async Task<int> AdoptSessionAsync(Guid sessionKey, int userId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.app_session SET user_id = @userId WHERE session_key = @key;

            INSERT INTO dbo.app_state (owner_kind, owner_key, area, payload)
            SELECT 1, @userKey, s.area, s.payload
            FROM dbo.app_state AS s
            WHERE s.owner_kind = 0 AND s.owner_key = @sessionKey
              AND NOT EXISTS (SELECT 1 FROM dbo.app_state AS u
                              WHERE u.owner_kind = 1 AND u.owner_key = @userKey
                                AND u.area = s.area);
            """,
            new
            {
                key = sessionKey,
                sessionKey = sessionKey.ToString(),
                userId,
                userKey = userId.ToString()
            },
            cancellationToken: ct));
    }
}
