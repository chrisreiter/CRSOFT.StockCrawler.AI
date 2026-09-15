using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Ingest.Infrastructure.Datenbank;

/// <summary>
/// Viele Zeilen auf einmal in eine Tabelle schreiben — <c>SqlBulkCopy</c> auf
/// SQL Server, <c>COPY … FROM STDIN</c> auf PostgreSQL. Die Spalten der
/// <see cref="DataTable"/> heissen wie die Zielspalten; mehr Abbildung gibt es
/// nicht, und mehr brauchen die Aufrufer nicht.
///
/// <para><b>Warum COPY im Textformat und nicht binär.</b> Das Binärformat
/// verlangt je Spalte den exakten Postgres-Typ; eine <c>double</c>-Spalte der
/// Tabelle gegen eine <c>numeric</c>-Spalte der Datenbank bricht mit einer
/// Meldung ab, die den Fehler nicht nennt. Im Textformat wandelt Postgres
/// selbst, so wie bei einem INSERT — und ist dabei immer noch um Grössen-
/// ordnungen schneller als einzelne Zeilen. Der Preis ist das Maskieren von
/// Tabulator, Zeilenumbruch und Backslash, und das steht unten in acht
/// Zeilen.</para>
/// </summary>
public static class Massenkopie
{
    public static Task SchreibeAsync(
        DbConnection conn, DataTable tabelle, string ziel, int zeitlimitSekunden,
        CancellationToken ct = default)
        => conn switch
        {
            SqlConnection sql  => SqlServerAsync(sql, tabelle, ziel, zeitlimitSekunden, ct),
            NpgsqlConnection pg => PostgresAsync(pg, tabelle, ziel, zeitlimitSekunden, ct),
            _ => throw new NotSupportedException(
                $"Massenkopie kennt den Verbindungstyp {conn.GetType().Name} nicht."),
        };

    private static async Task SqlServerAsync(
        SqlConnection conn, DataTable tabelle, string ziel, int zeitlimit, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = ziel,
            BatchSize = 10_000,
            BulkCopyTimeout = zeitlimit,
        };

        foreach (DataColumn c in tabelle.Columns)
            bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);

        await bulk.WriteToServerAsync(tabelle, ct);
    }

    private static async Task PostgresAsync(
        NpgsqlConnection conn, DataTable tabelle, string ziel, int zeitlimit, CancellationToken ct)
    {
        var spalten = string.Join(", ",
            tabelle.Columns.Cast<DataColumn>().Select(c => "\"" + c.ColumnName + "\""));

        await using var schreiber = await conn.BeginTextImportAsync(
            $"COPY {ziel} ({spalten}) FROM STDIN (FORMAT TEXT)", ct);
        schreiber.Timeout = zeitlimit;

        var zeile = new StringBuilder(256);
        foreach (DataRow r in tabelle.Rows)
        {
            zeile.Clear();
            for (var i = 0; i < tabelle.Columns.Count; i++)
            {
                if (i > 0) zeile.Append('\t');
                Anhaengen(zeile, r[i]);
            }
            zeile.Append('\n');
            await schreiber.WriteAsync(zeile.ToString().AsMemory(), ct);
        }
    }

    /*  Textformat von COPY: NULL ist \N, Trenner ist Tabulator, Zeilenende
        ist \n -- und genau diese drei plus der Backslash selbst muessen im
        Inhalt maskiert werden. Zahlen und Zeiten invariant, sonst haengt das
        Ergebnis an der Kultur des Rechners, auf dem der Dienst laeuft.       */
    private static void Anhaengen(StringBuilder sb, object wert)
    {
        switch (wert)
        {
            case null or DBNull:
                sb.Append("\\N"); break;
            case bool b:
                sb.Append(b ? 't' : 'f'); break;
            case DateTime t:
                sb.Append(t.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)); break;
            case DateOnly d:
                sb.Append(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
            case double x:
                sb.Append(x.ToString("R", CultureInfo.InvariantCulture)); break;
            case float x:
                sb.Append(x.ToString("R", CultureInfo.InvariantCulture)); break;
            case decimal x:
                sb.Append(x.ToString(CultureInfo.InvariantCulture)); break;
            case byte[] bytes:
                sb.Append("\\\\x").Append(Convert.ToHexString(bytes)); break;
            case IFormattable f:
                sb.Append(f.ToString(null, CultureInfo.InvariantCulture)); break;
            case string s:
                foreach (var ch in s)
                    sb.Append(ch switch
                    {
                        '\\' => "\\\\",
                        '\t' => "\\t",
                        '\n' => "\\n",
                        '\r' => "\\r",
                        /*  Postgres kann in text kein NUL ablegen (22021); SQL
                            Server in NVARCHAR schon. Beim Kopieren eines
                            SQL-Server-Bestands kommen sie deshalb vor -- aus
                            PDF-Extraktion -- und werden hier gestrichen statt
                            den ganzen Block scheitern zu lassen.             */
                        '\0' => "",
                        _ => ch.ToString(),
                    });
                break;
            default:
                sb.Append(Convert.ToString(wert, CultureInfo.InvariantCulture)); break;
        }
    }
}
