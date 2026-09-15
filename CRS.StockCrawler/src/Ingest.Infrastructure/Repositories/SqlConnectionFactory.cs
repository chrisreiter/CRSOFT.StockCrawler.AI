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
        _connectionString = system == Datenbanksystem.Postgres
            ? MitSuchpfad(connectionString)
            : connectionString;
        Dialekt = system switch
        {
            Datenbanksystem.SqlServer => new SqlServerDialekt(),
            Datenbanksystem.Postgres  => new PostgresDialekt(),
            _ => throw new ArgumentOutOfRangeException(nameof(system), system, "Unbekanntes Datenbanksystem."),
        };

        EnumHandler.Registrieren();

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

            /*  Und eine Falle, die KEIN Schalter loest, sondern nur die Schreibweise
                der Abfragen: Dapper setzt fuer DateTime-Parameter absichtlich
                keinen DbType (damit SqlClient aus dem Wert datetime2 statt
                datetime ableitet). Ist der Wert null, kommt der Parameter bei
                Npgsql ohne Typ an, und Postgres bestimmt den Typ an der ERSTEN
                Verwendung. "(@von IS NULL OR ts >= @von)" scheitert deshalb mit
                42P08 "could not determine data type of parameter"; "(ts >= @von
                OR @von IS NULL)" geht, weil der Vergleich den Typ liefert.
                Gemessen: DateTime? null scheitert, int? null und string null
                gehen, ein Dapper-TypeHandler hilft nicht (er wird fuer null
                nicht gerufen). Alle zwanzig Stellen stehen seither in der
                zweiten Reihenfolge.                                           */
        }
    }

    /*  Das Schema heisst dbo, damit die 300 "dbo."-Praefixe im Code fuer beide
        Systeme gelten. Unqualifizierte Namen (Stage-Tabellen ausgenommen, die
        sind temporaer) sollen trotzdem dort landen -- also der Suchpfad auf
        der Verbindung, falls die Zeichenfolge keinen setzt. Wer einen setzt,
        behaelt seinen.                                                       */
    private static string MitSuchpfad(string cs)
    {
        var b = new NpgsqlConnectionStringBuilder(cs);
        if (string.IsNullOrWhiteSpace(b.SearchPath)) b.SearchPath = "dbo,public";
        return b.ConnectionString;
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
