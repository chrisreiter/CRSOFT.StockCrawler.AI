using System.Data.Common;

namespace Ingest.Infrastructure.Datenbank;

/// <summary>Welches Datenbanksystem hinter der Verbindung steht.</summary>
public enum Datenbanksystem
{
    SqlServer,
    Postgres,
}

/// <summary>
/// Die Stellen, an denen sich SQL Server und PostgreSQL im SQL unterscheiden —
/// und nur diese.
///
/// <para><b>Warum ein Objekt und kein Übersetzer.</b> Ein Umschreiber, der
/// T-SQL zur Laufzeit in Postgres-SQL verwandelt, trifft 90 % der Fälle und
/// rechnet beim Rest still falsch: <c>LOG()</c> ist in Postgres Basis 10, ein
/// <c>TOP</c> in einer Unterabfrage hat sein <c>ORDER BY</c> woanders, ein
/// <c>+</c> ist mal Addition und mal Verkettung. Deshalb steht hier jeder
/// Unterschied als benanntes Token, das der Autor der Abfrage sichtbar in
/// sein Literal schreibt: <c>{d.Jetzt}</c>, <c>{d.Wahr}</c>,
/// <c>{d.PlusTage("-@tage", d.Jetzt)}</c>. Was in der Datei steht, ist bis
/// auf diese Token das SQL, das läuft — auf beiden Systemen.</para>
///
/// <para><b>Was hier bewusst fehlt.</b> Alles, was in beiden Systemen gleich
/// geht, bekommt kein Token, sondern wird portabel geschrieben:
/// <c>COALESCE</c> statt <c>ISNULL</c>, <c>"spalte"</c> statt
/// <c>[spalte]</c>, <c>OFFSET 0 ROWS FETCH NEXT n ROWS ONLY</c> statt
/// <c>TOP n</c>, <c>CONCAT()</c> statt <c>+</c>, <c>CAST</c> statt
/// <c>CONVERT</c>. <c>MERGE</c> kennt Postgres seit 15 in derselben Form.
/// Die Liste der Token soll kurz bleiben; wächst sie, ist meist die Abfrage
/// das Problem und nicht der Dialekt.</para>
///
/// <para><b>Zwei Fallen, die kein Token löst</b> und die beim Schreiben
/// einer Abfrage mitgedacht werden müssen: Postgres vergleicht Zeichenketten
/// <i>case-sensitive</i> (SQL Server in der Standardkollation nicht), und
/// Postgres sortiert <c>NULL</c> bei <c>DESC</c> <i>zuoberst</i> (SQL Server
/// zuunterst). Für das zweite gibt es <see cref="NullsLast"/>; für das erste
/// hilft nur <c>LOWER()</c> auf beiden Seiten oder eine Spalte, die von
/// vornherein normiert abgelegt wird.</para>
/// </summary>
public abstract class SqlDialekt
{
    public abstract Datenbanksystem System { get; }
    public bool IstPostgres  => System == Datenbanksystem.Postgres;
    public bool IstSqlServer => System == Datenbanksystem.SqlServer;

    /// <summary>Aktueller UTC-Zeitpunkt als Ausdruck.</summary>
    public abstract string Jetzt { get; }

    /// <summary>Wahrheitswerte für Vergleiche mit BIT/boolean-Spalten:
    /// <c>WHERE aktiv = {d.Wahr}</c>. SQL Server kennt kein <c>TRUE</c>,
    /// Postgres kein <c>bool = 1</c>.</summary>
    public abstract string Wahr { get; }
    public abstract string Falsch { get; }

    /// <summary>Zeitpunkt plus n Tage/Stunden/Minuten. <paramref name="n"/> ist
    /// ein SQL-Ausdruck (Parameter, Spalte, Zahl), <paramref name="basis"/> ebenso.</summary>
    public abstract string PlusTage(string n, string basis);
    public abstract string PlusStunden(string n, string basis);
    public abstract string PlusMinuten(string n, string basis);

    /// <summary>Ganze Tage bzw. Sekunden zwischen zwei Zeitpunkten (<c>bis − von</c>).</summary>
    public abstract string TageZwischen(string von, string bis);
    public abstract string SekundenZwischen(string von, string bis);

    /// <summary>Natürlicher Logarithmus. In SQL Server heisst er <c>LOG</c>,
    /// in Postgres <c>LN</c> — und <c>LOG</c> ist dort Basis 10.</summary>
    public abstract string Ln(string x);

    /// <summary>Runden auf n Stellen. Postgres rundet <c>double precision</c>
    /// nur ohne Stellenangabe; mit Stellen braucht es <c>numeric</c>.</summary>
    public abstract string Runden(string x, string stellen);

    /// <summary>Stichprobenstandardabweichung als Aggregat.</summary>
    public abstract string Stdabw(string x);

    /// <summary>
    /// Mitgliedschaft in einer Parameterliste. SQL Server: <c>x IN @ids</c>,
    /// das Dapper zu <c>IN (@ids1, @ids2, …)</c> aufzaehlt. Bei Npgsql tut Dapper
    /// das NICHT — es reicht das Array als einen Parameter durch und erwartet
    /// <c>x = ANY(@ids)</c>; ein <c>IN @ids</c> kommt dort als <c>IN $1</c> an
    /// und ist ein Syntaxfehler. <paramref name="parameter"/> ohne <c>@</c>.
    /// </summary>
    public abstract string In(string ausdruck, string parameter);

    /// <summary>
    /// Ein Loeschschritt mit Obergrenze — die Form dafuer ist in beiden
    /// Systemen eine andere, nicht nur ein anderer Name.
    ///
    /// <para><b>Warum blockweise geloescht wird.</b> Ein einziges DELETE ueber
    /// sechs Millionen Zeilen sperrt die Tabelle fuer die Dauer des Vorgangs
    /// und laesst das Transaktionsprotokoll auf ein Vielfaches der geloeschten
    /// Datenmenge anwachsen. Waehrenddessen steht der stuendliche Kursabruf.
    /// In Bloecken laeuft es laenger und stoert niemanden.</para>
    /// </summary>
    /// <param name="tabelle">Voll qualifiziert, z. B. <c>dbo.forecast_component</c>.</param>
    /// <param name="schluessel">Die Schluesselspalte, ueber die Postgres die
    /// Auswahl begrenzt. In SQL Server ungenutzt — dort gibt es <c>DELETE TOP</c>.</param>
    /// <param name="bedingung">Der WHERE-Teil ohne das Wort WHERE.</param>
    /// <param name="parameter">Name des Zahlparameters ohne <c>@</c>.</param>
    public abstract string LoescheBlock(string tabelle, string schluessel,
                                        string bedingung, string parameter);

    /// <summary>
    /// Median je Gruppe. In SQL Server ist <c>PERCENTILE_CONT</c> eine
    /// Fensterfunktion (<c>DISTINCT … OVER (PARTITION BY)</c>), in Postgres ein
    /// geordnetes Aggregat (<c>GROUP BY</c>) — die Abfrage hat also eine andere
    /// Form, nicht nur einen anderen Funktionsnamen. Deshalb liefert der
    /// Dialekt Kopf und Schluss:
    /// <code>
    /// {d.MedianSelect("tag", "wert", "median")} FROM t WHERE … {d.MedianGroupBy("tag")}
    /// </code>
    /// Das Ergebnis hat je Gruppe genau eine Zeile mit den Spalten
    /// <paramref name="gruppe"/> und <paramref name="alias"/>.
    /// </summary>
    public abstract string MedianSelect(string gruppe, string spalte, string alias);
    public abstract string MedianGroupBy(string gruppe);

    /// <summary>
    /// SHA-256 eines Textes als Binärwert, passend zur berechneten Spalte
    /// <c>origin_hash</c>. Die Bytes unterscheiden sich zwischen den Systemen
    /// (SQL Server hasht UTF-16, Postgres UTF-8) — das ist unerheblich, weil der
    /// Wert nur innerhalb einer Datenbank verglichen wird und dort beide Seiten
    /// denselben Ausdruck benutzen.
    /// </summary>
    public abstract string Sha256Text(string x);

    /// <summary>
    /// Eingefügte oder geänderte Werte zurückgeben. SQL Server schreibt
    /// <c>OUTPUT INSERTED.id</c> <i>vor</i> <c>VALUES</c>, Postgres
    /// <c>RETURNING id</c> <i>nach</i> allem — deshalb ein Paar. Ein
    /// <c>INSERT</c> schreibt sich so:
    /// <code>
    /// INSERT INTO dbo.t (a, b) {d.RueckgabeVor("id")} VALUES (@a, @b) {d.RueckgabeNach("id")}
    /// </code>
    /// <paramref name="spalten"/> ohne Präfix; SQL Server ergänzt
    /// <c>INSERTED.</c> selbst.
    /// </summary>
    public abstract string RueckgabeVor(string spalten);
    public abstract string RueckgabeNach(string spalten);

    /// <summary>
    /// Seitliche Unterabfrage je Zeile. SQL Server: <c>OUTER APPLY (…) a</c>,
    /// Postgres: <c>LEFT JOIN LATERAL (…) a ON TRUE</c>. Wieder ein Paar:
    /// <code>
    /// FROM dbo.t {d.OuterApplyVor} (SELECT …) a {d.OuterApplyNach}
    /// </code>
    /// </summary>
    public abstract string OuterApplyVor { get; }
    public abstract string OuterApplyNach { get; }

    /// <summary><c>CROSS APPLY</c> bzw. <c>CROSS JOIN LATERAL</c> — ohne Nachsatz,
    /// weil ein CROSS JOIN kein <c>ON</c> braucht.</summary>
    public abstract string CrossApply { get; }

    /// <summary>
    /// Anhängsel an einen absteigenden Sortierschlüssel, damit <c>NULL</c> auf
    /// beiden Systemen zuunterst landet. SQL Server tut das ohnehin und kennt
    /// die Syntax nicht; Postgres braucht <c>NULLS LAST</c>.
    /// </summary>
    public abstract string NullsLast { get; }

    /// <summary>Temporäre Tabelle: Name in Verweisen (<c>#x</c> bzw. <c>x</c>)
    /// und der Kopf der Anweisung, die sie anlegt.</summary>
    public abstract string Temp(string name);
    public abstract string CreateTemp(string name);

    /// <summary>
    /// <c>SELECT … INTO #x FROM …</c> gibt es in Postgres nicht; dort heisst es
    /// <c>CREATE TEMP TABLE x AS SELECT … FROM …</c>. Wieder ein Paar:
    /// <code>
    /// {d.SelectIntoVor("x")}SELECT a, b {d.SelectIntoNach("x")} FROM dbo.t
    /// </code>
    /// </summary>
    public abstract string SelectIntoVor(string name);
    public abstract string SelectIntoNach(string name);

    /// <summary>
    /// Spaltentypen für Tabellen, die im Code angelegt werden (Stage-Tabellen).
    /// <c>INT</c>, <c>BIGINT</c>, <c>DECIMAL(p,s)</c>, <c>FLOAT</c> und
    /// <c>VARCHAR(n)</c> heissen in beiden Systemen gleich und brauchen kein
    /// Token. Diese drei nicht: <c>TIMESTAMP</c> ist in SQL Server eine
    /// Zeilenversion, <c>BIT</c> ist in Postgres eine Bitfolge, und
    /// <c>NVARCHAR</c> kennt Postgres nicht.
    /// </summary>
    public abstract string TypZeit { get; }
    public abstract string TypBool { get; }
    public abstract string TypText(int laenge);

    /// <summary>Sperrhinweis am Ziel eines <c>MERGE</c>, damit zwei gleichzeitige
    /// Läufe sich nicht gegenseitig den Schlüssel wegnehmen. SQL Server:
    /// <c>WITH (HOLDLOCK)</c>; Postgres kennt keine Tabellenhinweise und
    /// meldet die Kollision als 23505, die der Aufrufer wiederholt.</summary>
    public abstract string MergeSperre { get; }

    /// <summary>
    /// Aufruf einer gespeicherten Routine als Text-Befehl. SQL Server:
    /// <c>EXEC dbo.x @a = @a, @b = @b</c>. Postgres kennt in
    /// <c>CommandType.StoredProcedure</c> nur <c>CALL</c>, und Prozeduren
    /// liefern dort keine Zeilen — die Routinen sind deshalb Funktionen und
    /// werden mit <c>SELECT * FROM dbo.x(@a, @b)</c> gerufen. Die Parameter
    /// müssen in der Reihenfolge der Signatur stehen.
    /// </summary>
    public abstract string Aufruf(string routine, params string[] parameter);

    /// <summary>
    /// <c>UPDATE</c> mit Verknüpfung. T-SQL: <c>UPDATE a SET … FROM dbo.t a, q WHERE …</c>;
    /// Postgres: <c>UPDATE dbo.t a SET … FROM q WHERE …</c>. Die Zielspalten im
    /// <c>SET</c> bleiben unqualifiziert, das nehmen beide:
    /// <code>
    /// {d.UpdateZiel("dbo.t", "a")} SET x = q.x {d.UpdateQuelle("dbo.t", "a")} q WHERE q.id = a.id
    /// </code>
    /// </summary>
    public abstract string UpdateZiel(string tabelle, string alias);
    public abstract string UpdateQuelle(string tabelle, string alias);

    /// <summary>Ob die Ausnahme eine Verletzung eines eindeutigen Schlüssels
    /// meldet (SQL Server 2601/2627, Postgres 23505).</summary>
    public abstract bool IstDoppelterSchluessel(DbException ex);
}

public sealed class SqlServerDialekt : SqlDialekt
{
    public override Datenbanksystem System => Datenbanksystem.SqlServer;

    public override string Jetzt  => "SYSUTCDATETIME()";
    public override string Wahr   => "1";
    public override string Falsch => "0";

    public override string PlusTage(string n, string basis)    => $"DATEADD(day, {n}, {basis})";
    public override string PlusStunden(string n, string basis) => $"DATEADD(hour, {n}, {basis})";
    public override string PlusMinuten(string n, string basis) => $"DATEADD(minute, {n}, {basis})";
    public override string TageZwischen(string von, string bis) => $"DATEDIFF(day, {von}, {bis})";
    public override string SekundenZwischen(string von, string bis) => $"DATEDIFF(second, {von}, {bis})";

    public override string Ln(string x) => $"LOG({x})";
    public override string Runden(string x, string stellen) => $"ROUND({x}, {stellen})";
    public override string Stdabw(string x) => $"STDEV({x})";
    public override string In(string ausdruck, string parameter) => $"{ausdruck} IN @{parameter}";
    public override string LoescheBlock(string tabelle, string schluessel, string bedingung, string parameter)
        => $"DELETE TOP (@{parameter}) FROM {tabelle} WHERE {bedingung}";
    public override string UpdateZiel(string tabelle, string alias)   => $"UPDATE {alias}";
    public override string UpdateQuelle(string tabelle, string alias) => $"FROM {tabelle} {alias},";
    public override string MedianSelect(string gruppe, string spalte, string alias)
        => $"SELECT DISTINCT {gruppe}, PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY {spalte}) OVER (PARTITION BY {gruppe}) AS {alias}";
    public override string MedianGroupBy(string gruppe) => "";
    public override string Sha256Text(string x) => $"CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', {x}))";

    public override string RueckgabeVor(string spalten)
        => "OUTPUT " + string.Join(", ", spalten.Split(',').Select(s => "INSERTED." + s.Trim()));
    public override string RueckgabeNach(string spalten) => "";

    public override string OuterApplyVor  => "OUTER APPLY";
    public override string OuterApplyNach => "";
    public override string CrossApply     => "CROSS APPLY";
    public override string NullsLast      => "";

    public override string Temp(string name)       => "#" + name;
    public override string CreateTemp(string name) => "CREATE TABLE #" + name;
    public override string SelectIntoVor(string name)  => "";
    public override string SelectIntoNach(string name) => "INTO #" + name;

    public override string TypZeit => "DATETIME2(0)";
    public override string TypBool => "BIT";
    public override string TypText(int laenge) => $"NVARCHAR({laenge})";
    public override string MergeSperre => "WITH (HOLDLOCK)";

    public override string Aufruf(string routine, params string[] parameter)
        => parameter.Length == 0
            ? $"EXEC {routine}"
            : $"EXEC {routine} " + string.Join(", ", parameter.Select(p => $"@{p} = @{p}"));

    public override bool IstDoppelterSchluessel(DbException ex)
        => ex is Microsoft.Data.SqlClient.SqlException s && s.Number is 2601 or 2627;
}

public sealed class PostgresDialekt : SqlDialekt
{
    public override Datenbanksystem System => Datenbanksystem.Postgres;

    /*  timestamp ohne Zeitzone, wie die DATETIME2-Spalten in SQL Server: Die
        Anwendung rechnet durchgehend in UTC und legt die Zeitzone nicht in
        der Datenbank ab. Siehe DbVerbindung zum Npgsql-Schalter dazu.        */
    public override string Jetzt  => "(now() AT TIME ZONE 'utc')";
    public override string Wahr   => "TRUE";
    public override string Falsch => "FALSE";

    public override string PlusTage(string n, string basis)    => $"({basis} + ({n}) * INTERVAL '1 day')";
    public override string PlusStunden(string n, string basis) => $"({basis} + ({n}) * INTERVAL '1 hour')";
    public override string PlusMinuten(string n, string basis) => $"({basis} + ({n}) * INTERVAL '1 minute')";
    public override string TageZwischen(string von, string bis) => $"(CAST({bis} AS DATE) - CAST({von} AS DATE))";
    public override string SekundenZwischen(string von, string bis) => $"EXTRACT(EPOCH FROM ({bis} - {von}))";

    /*  Immer ueber double: In SQL Server liefert LOG() stets float, in Postgres
        liefert ln(numeric) numeric -- und ein Datensatz, der double erwartet,
        bekaeme decimal. Gefunden an AVG(ABS(ln(close/lag))) der Rangfolge.   */
    public override string Ln(string x) => $"LN(CAST({x} AS DOUBLE PRECISION))";
    /*  Zurueck nach double: In SQL Server gibt ROUND(float, n) float, und die
        Datensaetze erwarten double. Alle zehn Aufrufer runden Gleitkommawerte. */
    public override string Runden(string x, string stellen) => $"CAST(ROUND(CAST({x} AS NUMERIC), {stellen}) AS DOUBLE PRECISION)";
    public override string Stdabw(string x) => $"STDDEV_SAMP(CAST({x} AS DOUBLE PRECISION))";
    public override string In(string ausdruck, string parameter) => $"{ausdruck} = ANY(@{parameter})";

    /*  Postgres kennt kein DELETE TOP. Die Auswahl wird ueber den Schluessel
        begrenzt; ein CTID-Trick waere schneller, haengt aber an der physischen
        Zeilenlage und ueberlebt kein VACUUM zwischen den Bloecken.          */
    public override string LoescheBlock(string tabelle, string schluessel, string bedingung, string parameter)
        => $"DELETE FROM {tabelle} WHERE {schluessel} IN "
         + $"(SELECT {schluessel} FROM {tabelle} WHERE {bedingung} LIMIT @{parameter})";

    public override string UpdateZiel(string tabelle, string alias)   => $"UPDATE {tabelle} {alias}";
    public override string UpdateQuelle(string tabelle, string alias) => "FROM";
    public override string MedianSelect(string gruppe, string spalte, string alias)
        => $"SELECT {gruppe}, PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY {spalte}) AS {alias}";
    public override string MedianGroupBy(string gruppe) => "GROUP BY " + gruppe;
    public override string Sha256Text(string x) => $"sha256(dbo.utf8({x}))";

    public override string RueckgabeVor(string spalten)  => "";
    public override string RueckgabeNach(string spalten) => "RETURNING " + spalten;

    public override string OuterApplyVor  => "LEFT JOIN LATERAL";
    public override string OuterApplyNach => "ON TRUE";
    public override string CrossApply     => "CROSS JOIN LATERAL";
    public override string NullsLast      => "NULLS LAST";

    public override string Temp(string name)       => name;
    public override string CreateTemp(string name) => "CREATE TEMP TABLE " + name;
    public override string SelectIntoVor(string name)  => "CREATE TEMP TABLE " + name + " AS ";
    public override string SelectIntoNach(string name) => "";

    public override string TypZeit => "TIMESTAMP(0)";
    public override string TypBool => "BOOLEAN";
    public override string TypText(int laenge) => $"VARCHAR({laenge})";
    public override string MergeSperre => "";

    public override string Aufruf(string routine, params string[] parameter)
        => $"SELECT * FROM {routine}(" + string.Join(", ", parameter.Select(p => "@" + p)) + ")";

    public override bool IstDoppelterSchluessel(DbException ex)
        => ex is Npgsql.PostgresException p && p.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation;
}

public static class DialektErweiterungen
{
    private static readonly SqlServerDialekt SqlServer = new();
    private static readonly PostgresDialekt Postgres = new();

    /// <summary>Der Dialekt zu einer offenen Verbindung — für Methoden, die nur
    /// die Verbindung bekommen und keine Fabrik.</summary>
    public static SqlDialekt Dialekt(this DbConnection conn) => conn switch
    {
        Npgsql.NpgsqlConnection => Postgres,
        Microsoft.Data.SqlClient.SqlConnection => SqlServer,
        _ => throw new NotSupportedException($"Kein Dialekt für {conn.GetType().Name}."),
    };
}
