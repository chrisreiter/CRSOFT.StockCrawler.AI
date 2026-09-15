using System.Data.Common;
using Ingest.Infrastructure.Datenbank;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Ingest.Infrastructure.Repositories;

/// <summary>
/// Öffnet die Datenbankverbindung und sagt, welcher Dialekt darüber gesprochen
/// wird. Der Name ist historisch — „Sql" meint hier die Sprache, nicht das
/// Produkt: Dahinter steht SQL Server oder PostgreSQL, je nach Konfiguration.
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>Geöffnete Verbindung. Als <see cref="DbConnection"/>, damit
    /// kein Aufrufer an einen Treiber gebunden ist; Dapper arbeitet darauf.</summary>
    Task<DbConnection> OpenAsync(CancellationToken ct = default);

    /// <summary>Die Unterschiede, die ins SQL geschrieben werden müssen —
    /// siehe <see cref="SqlDialekt"/>.</summary>
    SqlDialekt Dialekt { get; }
}

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;
    private readonly Datenbanksystem _system;

    public SqlDialekt Dialekt { get; }

    public SqlConnectionFactory(Datenbanksystem system, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Verbindungszeichenfolge fehlt.", nameof(connectionString));

        _system = system;
        _connectionString = connectionString;
        Dialekt = system switch
        {
            Datenbanksystem.SqlServer => new SqlServerDialekt(),
            Datenbanksystem.Postgres  => new PostgresDialekt(),
            _ => throw new ArgumentOutOfRangeException(nameof(system), system, "Unbekanntes Datenbanksystem."),
        };

        if (system == Datenbanksystem.Postgres)
        {
            /*  Npgsql ab Version 6 verlangt fuer jeden DateTime, dass sein Kind
                zur Spalte passt: Utc nur in timestamptz, alles andere nur in
                timestamp. SqlClient kennt diese Unterscheidung nicht, und die
                Anwendung auch nicht -- sie rechnet in UTC, liest DateTime2 mit
                Kind=Unspecified zurueck und vergleicht munter. Mit dem Schalter
                verhaelt sich Npgsql wie SqlClient: timestamp ohne Zeitzone,
                kein Blick auf Kind, kein Umrechnen. Das Schema legt deshalb
                alle Zeitspalten als timestamp an, nicht als timestamptz.

                Der Schalter muss vor dem ersten Npgsql-Aufruf gesetzt sein;
                die Fabrik ist ein Singleton und entsteht davor.              */
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        }
    }

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        DbConnection conn = _system == Datenbanksystem.Postgres
            ? new NpgsqlConnection(_connectionString)
            : new SqlConnection(_connectionString);

        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }
}
