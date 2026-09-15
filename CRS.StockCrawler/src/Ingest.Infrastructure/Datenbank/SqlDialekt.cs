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

    /// <summary>Ganze Tage zwischen zwei Zeitpunkten (<c>bis − von</c>).</summary>
    public abstract string TageZwischen(string von, string bis);

    /// <summary>Natürlicher Logarithmus. In SQL Server heisst er <c>LOG</c>,
    /// in Postgres <c>LN</c> — und <c>LOG</c> ist dort Basis 10.</summary>
    public abstract string Ln(string x);

    /// <summary>Runden auf n Stellen. Postgres rundet <c>double precision</c>
    /// nur ohne Stellenangabe; mit Stellen braucht es <c>numeric</c>.</summary>
    public abstract string Runden(string x, string stellen);

    /// <summary>Stichprobenstandardabweichung als Aggregat.</summary>
    public abstract string Stdabw(string x);

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

    public override string Ln(string x) => $"LOG({x})";
    public override string Runden(string x, string stellen) => $"ROUND({x}, {stellen})";
    public override string Stdabw(string x) => $"STDEV({x})";

    public override string RueckgabeVor(string spalten)
        => "OUTPUT " + string.Join(", ", spalten.Split(',').Select(s => "INSERTED." + s.Trim()));
    public override string RueckgabeNach(string spalten) => "";

    public override string OuterApplyVor  => "OUTER APPLY";
    public override string OuterApplyNach => "";
    public override string NullsLast      => "";

    public override string Temp(string name)       => "#" + name;
    public override string CreateTemp(string name) => "CREATE TABLE #" + name;

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

    public override string Ln(string x) => $"LN({x})";
    public override string Runden(string x, string stellen) => $"ROUND(CAST({x} AS NUMERIC), {stellen})";
    public override string Stdabw(string x) => $"STDDEV_SAMP({x})";

    public override string RueckgabeVor(string spalten)  => "";
    public override string RueckgabeNach(string spalten) => "RETURNING " + spalten;

    public override string OuterApplyVor  => "LEFT JOIN LATERAL";
    public override string OuterApplyNach => "ON TRUE";
    public override string NullsLast      => "NULLS LAST";

    public override string Temp(string name)       => name;
    public override string CreateTemp(string name) => "CREATE TEMP TABLE " + name;

    public override string TypZeit => "TIMESTAMP(0)";
    public override string TypBool => "BOOLEAN";
    public override string TypText(int laenge) => $"VARCHAR({laenge})";
    public override string MergeSperre => "";

    public override string Aufruf(string routine, params string[] parameter)
        => $"SELECT * FROM {routine}(" + string.Join(", ", parameter.Select(p => "@" + p)) + ")";

    public override bool IstDoppelterSchluessel(DbException ex)
        => ex is Npgsql.PostgresException p && p.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation;
}
