# Datenbank: SQL Server und PostgreSQL

Die Anwendung läuft auf **beiden** Systemen, umschaltbar über die
Konfiguration. Geprüft am 15.09.2026 mit SQL Server 2025 und PostgreSQL 18
(EDB Advanced Server 18.2, Redwood-Modus aus): 84 lesende Routen und alle
Schreibpfade auf beiden Systemen, dazu der Frischinstallations-Pfad auf einer
leeren Postgres-Datenbank.

**PostgreSQL ist der einfachere Einstieg.** Kostenlos, ohne Grössengrenze,
auf jedem System in fünf Minuten da. SQL Server bleibt vollständig
unterstützt — die Produktivinstanz läuft weiter darauf.

---

## Umschalten

`src/Ingest.Api/appsettings.json`:

```jsonc
"ConnectionStrings": {
  // "Sql": "Server=.\\SQLSERVER;Database=stockcrawler;User Id=stockcrawler;Password=…;TrustServerCertificate=true",
  "Postgres": "Host=localhost;Port=5432;Database=stockcrawler;Username=stockcrawler;Password=…"
},
"Datenbank": { "System": "" }
```

Regel (`DependencyInjection.VerbindungAusKonfiguration`):

1. Ist `Datenbank:System` gesetzt (`SqlServer` | `Postgres`), gilt es.
2. Sonst Postgres, sobald `ConnectionStrings:Postgres` nicht leer ist.
3. Sonst SQL Server.

Zum Wechseln also die eine Zeile auskommentieren, die andere freigeben. Der
.NET-Konfigurationsleser erlaubt `//`-Kommentare in der Datei;
`deploy/2-install-target.ps1` entfernt sie, bevor PowerShell sie liest.

Umgebungsvariablen überschreiben die Datei wie üblich:
`ConnectionStrings__Postgres=…`.

## Einrichten

| | SQL Server | PostgreSQL |
| --- | --- | --- |
| Schema einspielen | `infra/apply-sql.ps1` → `infra/sql/*.sql` (35 Migrationen) | `infra/apply-pgsql.ps1` → `infra/pgsql/*.sql` (3 Dateien) |
| Voraussetzung | Anmeldung `stockcrawler` mit `dbcreator` | `CREATE ROLE stockcrawler LOGIN PASSWORD '…'; CREATE DATABASE stockcrawler OWNER stockcrawler;` |
| Routinen | Prozeduren | Funktionen (`SELECT * FROM dbo.x(…)`) |
| Schema | `dbo` | `dbo` — damit die 300 `dbo.`-Präfixe im Code für beide gelten |

Das Postgres-Schema (`010_schema.sql`) ist **aus dem Katalog der laufenden
SQL-Server-Datenbank erzeugt**, nicht aus den 35 Migrationen von Hand
nachgebaut: Der Katalog ist der Stand, den die Migrationen erreicht haben, und
kennt keine Reihenfolgefehler. `011_routinen.sql` sind die zwölf aus dem Code
gerufenen Routinen als PL/pgSQL, `012_stammdaten.sql` die Zeilen, die die
Migrationen nebenbei anlegen (Säulengewichte, Autopilot-Strategien, Konten).

**Ab jetzt kommen Schemaänderungen paarweise**: eine Datei unter `infra/sql/`
und eine unter `infra/pgsql/`. Wer nur eine schreibt, bricht das andere System
— und zwar erst zur Laufzeit.

---

## Wie zweisprachiges SQL geschrieben wird

Der Kern steht in `src/Ingest.Infrastructure/Datenbank/SqlDialekt.cs`, und die
Regel ist kurz: **Portables portabel schreiben, echte Unterschiede als
benanntes Token.** Kein Übersetzer zur Laufzeit — was in der Datei steht, ist
bis auf die Token das SQL, das läuft.

### Portabel schreiben (kein Token)

| statt | so |
| --- | --- |
| `[spalte]` | `"spalte"` |
| `ISNULL(a, b)` | `COALESCE(a, b)` |
| `SELECT TOP n … ORDER BY x` | `… ORDER BY x OFFSET 0 ROWS FETCH NEXT (n) ROWS ONLY` — **mit Klammern** um Parameter |
| `MERGE dbo.t` | `MERGE INTO dbo.t` (Postgres ≥ 15 kennt MERGE) |
| `CONVERT(date, x)` | `CAST(x AS DATE)` |
| `a + 'text'` | `CONCAT(a, 'text')` |
| `COUNT(*)` in einen `int` | `CAST(COUNT(*) AS INT)` — Postgres liefert `bigint` |
| `SUM(CASE … THEN 1 ELSE 0 END)` | ebenso `CAST(… AS INT)` |
| `TINYINT` | `SMALLINT` |
| `SET NOCOUNT ON` | weglassen |
| `DECLARE @x = …` | Wert in C# rechnen und als Parameter geben |
| `x IN @liste` | `{d.In("x", "liste")}` — siehe unten, das ist ein Token |

### Token (`d` ist der `SqlDialekt` der Fabrik)

| Token | SQL Server | Postgres |
| --- | --- | --- |
| `{d.Jetzt}` | `SYSUTCDATETIME()` | `(now() AT TIME ZONE 'utc')` |
| `{d.Wahr}` / `{d.Falsch}` | `1` / `0` | `TRUE` / `FALSE` |
| `{d.PlusTage("-@n", d.Jetzt)}` | `DATEADD(day, …)` | `… + (…) * INTERVAL '1 day'` |
| `{d.TageZwischen(a, b)}` | `DATEDIFF(day, a, b)` | `CAST(b AS DATE) - CAST(a AS DATE)` |
| `{d.Ln(x)}` | `LOG(x)` | `LN(CAST(x AS DOUBLE PRECISION))` |
| `{d.Runden(x, n)}` | `ROUND(x, n)` | `CAST(ROUND(CAST(x AS NUMERIC), n) AS DOUBLE PRECISION)` |
| `{d.Stdabw(x)}` | `STDEV(x)` | `STDDEV_SAMP(x)` |
| `{d.In("x", "ids")}` | `x IN @ids` | `x = ANY(@ids)` |
| `{d.RueckgabeVor("id")} … {d.RueckgabeNach("id")}` | `OUTPUT INSERTED.id` vor `VALUES` | `RETURNING id` am Ende |
| `{d.OuterApplyVor} (…) k {d.OuterApplyNach}` | `OUTER APPLY (…) k` | `LEFT JOIN LATERAL (…) k ON TRUE` |
| `{d.CrossApply}` | `CROSS APPLY` | `CROSS JOIN LATERAL` |
| `{d.Temp("x")}` / `{d.CreateTemp("x")}` | `#x` / `CREATE TABLE #x` | `x` / `CREATE TEMP TABLE x` |
| `{d.SelectIntoVor("x")}SELECT … {d.SelectIntoNach("x")} FROM` | `SELECT … INTO #x FROM` | `CREATE TEMP TABLE x AS SELECT … FROM` |
| `{d.TypZeit}` / `{d.TypBool}` / `{d.TypText(n)}` | `DATETIME2(0)` / `BIT` / `NVARCHAR(n)` | `TIMESTAMP(0)` / `BOOLEAN` / `VARCHAR(n)` |
| `{d.MergeSperre}` | `WITH (HOLDLOCK)` | leer |
| `{d.MedianSelect(g, x, alias)} FROM … {d.MedianGroupBy(g)}` | `PERCENTILE_CONT … OVER (PARTITION BY g)` mit `DISTINCT` | `PERCENTILE_CONT … WITHIN GROUP` mit `GROUP BY` |
| `{d.UpdateZiel("dbo.t", "a")} SET … {d.UpdateQuelle("dbo.t", "a")} q WHERE …` | `UPDATE a SET … FROM dbo.t a, q` | `UPDATE dbo.t a SET … FROM q` |
| `{d.Sha256Text("@x")}` | `HASHBYTES('SHA2_256', @x)` | `sha256(dbo.utf8(@x))` |
| `d.Aufruf("dbo.p", "a", "b")` | `EXEC dbo.p @a = @a, @b = @b` | `SELECT * FROM dbo.p(@a, @b)` |
| `d.IstDoppelterSchluessel(ex)` | Fehler 2601/2627 | SQLSTATE 23505 |

Ein Literal mit Token ist ein interpoliertes Raw-Literal (`$"""…"""`).
Massenschreiben geht über `Massenkopie.SchreibeAsync` (SqlBulkCopy bzw.
`COPY FROM STDIN`), nie über `SqlBulkCopy` direkt.

---

## Die Fallen, die kein Token löst

Alle gemessen, keine vermutet. Die meisten davon hat der erste Lauf gegen
Postgres gezeigt — 39 von 84 Routen rot, nach dem rein syntaktischen Durchgang.

**`(@von IS NULL OR ts >= @von)` scheitert in Postgres mit 42P08.** Dapper
setzt für `DateTime`-Parameter absichtlich keinen `DbType`; ist der Wert
`null`, kommt der Parameter bei Npgsql ohne Typ an, und Postgres bestimmt den
Typ an der **ersten** Verwendung. `@von IS NULL` sagt nichts über den Typ.
Umgekehrt geht es: `(ts >= @von OR @von IS NULL)`. Ein Dapper-`TypeHandler`
hilft nicht, er wird für `null` nicht gerufen. `int?` und `string` sind nicht
betroffen — nur `DateTime?`.

**`x IN @ids` ist bei Npgsql ein Syntaxfehler.** Dapper zählt die Liste nur
für SqlClient zu `IN (@ids1, @ids2, …)` auf; bei Npgsql reicht es das Array
als **einen** Parameter durch, und `IN $1` ist ungültig. Deshalb `{d.In}`.

**`AssetClass : byte` passt in Postgres in keinen Konstruktor.** Postgres hat
kein `tinyint`; die kleinste Ganzzahl ist `smallint`, Npgsql liefert `short`,
und Dapper verlangt für eine Aufzählung im Konstruktor exakt ihren Grundtyp.
`EnumHandler.cs` registriert Typbehandler für `AssetClass`, `ProviderId`,
`CorpActionType` — für beide Systeme, unter SQL Server ändert sich nichts.

**`COUNT(*)` ist in Postgres `bigint`, `ROUND(double, n)` gibt es nicht,
`LN(numeric)` liefert `numeric`.** Jedes davon lässt einen Datensatz-
Konstruktor still nicht mehr passen. Die Token casten zurück.

**Postgres vergleicht Zeichenketten case-sensitive, SQL Server in der
Standardkollation nicht.** Anmeldename und Symbol werden deshalb auf beiden
Seiten mit `LOWER`/`UPPER` verglichen; der eindeutige Index auf
`app_user.login` liegt in Postgres auf `LOWER(login)`.

**Postgres sortiert `NULL` bei `DESC` zuoberst.** `{d.NullsLast}` hinter den
Sortierschlüssel, wo es zählt.

**`LOG()` ist in Postgres Basis 10.** In SQL Server der natürliche
Logarithmus. Ein stiller Faktor 2,3 in jeder Rendite — deshalb `{d.Ln}`.

**`PERCENTILE_CONT` hat eine andere Abfrageform**, nicht nur einen anderen
Namen: Fensterfunktion mit `DISTINCT` gegen geordnetes Aggregat mit
`GROUP BY`. Deshalb ein Token-Paar, das den ganzen Schritt liefert.

**NUL-Zeichen in Text lehnt Postgres ab (22021).** SQL Server nimmt sie in
`NVARCHAR` klaglos; PDF-Extraktion liefert sie gelegentlich. Sie werden beim
Zerlegen entfernt und in der Massenkopie gestrichen. Gefunden beim Kopieren
des Bestands, Zeile 6.937 von 42.219.

**`convert_to()` ist nur `STABLE`**, eine generierte Spalte verlangt
`IMMUTABLE` — deshalb `dbo.utf8()` als Hülle für `origin_hash`.

**`date`-Spalten kommen als `DateOnly`**, und Dapper kennt `DateOnly` weder
als Parameter noch als Ergebnis. Beim Lesen auf `{d.TypZeit}` casten.

**Npgsql und `DateTime.Kind`.** Npgsql ab 6 verlangt `Kind=Utc` für
`timestamptz` und alles andere für `timestamp`. Die Anwendung rechnet in UTC,
liest aber `Kind=Unspecified` zurück. `EnableLegacyTimestampBehavior` in der
Verbindungsfabrik stellt das SqlClient-Verhalten her; **alle Zeitspalten sind
`timestamp` ohne Zeitzone.**

**`$Host` ist in PowerShell reserviert.** `apply-pgsql.ps1` heisst der
Parameter deshalb `-Server`.

**Einmal beobachtet, nicht geklärt:** Der Kurvendiskussions-Lauf brach beim
`COPY` von 617.345 Verknüpfungen mit einem Npgsql-Schreib-Timeout ab — der
Server las 30 Sekunden nicht vom Stream, unmittelbar nach einem 178-Sekunden-
Lauf der Bot-Muster. Der zweite Versuch lief in 233 s durch.

---

## Was noch nicht portiert ist

- **Die 35 SQL-Server-Migrationen bleiben die Geschichte des Schemas.** Das
  Postgres-Schema beginnt beim heutigen Stand; es gibt keinen Weg, eine alte
  Postgres-Datenbank Migration für Migration nachzuziehen, weil es keine
  alten Postgres-Datenbanken gibt.
- **Bestand von SQL Server nach Postgres kopieren** ist kein Bestandteil der
  Anwendung. Es geht mit jedem Werkzeug, das Tabellen liest und `COPY`
  schreibt; die Identity-Sequenzen müssen danach mit `setval` nachgezogen
  werden. Beim Kopieren der Produktivdatenbank (43 Tabellen, 30 Mio Zeilen)
  hat das rund zehn Minuten gedauert.
- `deploy/` (Auslieferungspaket) kennt nur SQL Server.
