# EventMesh DataCell als Datenbank für CRSOFT.StockCrawler — Verträglichkeitsbericht

**Stand 20.09.2026, 11:50.** Ziel: `Host=localhost;Port=55531;Database=stockcrawler`
(meldet sich als „PostgreSQL 14.0 (EventMesh DataCell backend)"). Client:
Npgsql 8, Extended Protocol, Binärformat — genau so spricht die Anwendung.
Jede Prüfung ist eine einzelne Abfrage auf einer frischen Verbindung mit
25 s Frist; nach einem Verbindungsabbruch wurde gewartet, bis das Backend
wieder annahm. Der Wortlaut jeder Abfrage steht am Ende.

Schema und Daten sind vollständig vorhanden (47 Tabellen, 702 Werte,
3,99 Mio Kursbars, aktueller Stand der SQL-Server-Datenbank).

---

## Was die Anwendung derzeit blockiert — nach Wirkung sortiert

### 1. Jeder Zugriff auf `price_bar` läuft in den Timeout (Kurscharts → 500)

Auch die kleinste Abfrage antwortet nicht innerhalb von 29–55 Sekunden:

```sql
SELECT MAX(ts_utc) FROM price_bar WHERE asset_id = 101;                      -- Timeout
SELECT asset_id, ts_utc, "close" FROM price_bar
 WHERE asset_id = ANY('{101,1}') AND interval_code = '1d'
 ORDER BY asset_id, ts_utc DESC LIMIT 50;                                    -- Timeout
SELECT interval_code, COUNT(*) FROM price_bar WHERE asset_id = 101
 GROUP BY interval_code;                                                     -- Timeout
```

Auf demselben Datenbestand in PostgreSQL 18 kosten diese Abfragen 1–20 ms
(Index `(asset_id, interval_code, ts_utc)`). Nach mehreren solcher Abfragen
war das Backend 57 bis 167 s nicht erreichbar. Vermutung: kein nutzbarer
Index auf `price_bar`, Vollscan über 4 Mio Zeilen je Abfrage, und der Scan
kippt den Prozess. **Alle** Fenster- und Aggregatfunktionen (LAG, STDDEV,
PERCENTILE, AVG/MIN/MAX, LATERAL, korreliertes Subselect) wurden auf
`price_bar` geprüft und sind deshalb unten als Timeout eingetragen — ob sie
funktionieren, lässt sich erst sagen, wenn der Tabellenzugriff steht.

Betroffen in der App: Kurscharts, Depot, Prognose, Analyse, Startseite —
praktisch alles.

### 2. NULL-`timestamp` aus einer Tabellenspalte im Binärformat (Anmeldung → 500)

```sql
SELECT failed_logins, locked_until_utc FROM app_user WHERE login = 'chris';
```

`locked_until_utc` ist NULL. Npgsql liest `failed_logins = 0` korrekt und
bricht bei der nächsten Spalte ab: *„Buffer requirement is larger than the
remaining length of the value"*. Im Binärformat muss ein NULL als Länge
`-1` ohne Nutzbytes kommen; hier kommt offenbar eine Länge ≥ 0 mit weniger
als 8 Bytes. Bemerkenswert: `SELECT CAST(NULL AS timestamp)` als Ausdruck
ist korrekt, NULL in `text`- und `numeric`-Spalten ebenfalls (`display_name`,
`expires_utc` in `app_session` ging sogar). Der Fehler hängt also an der
konkreten Spalte/Zeile — möglicherweise am gespeicherten Repräsentanten
eines NULL-Timestamps aus der Replikation (SQL-Server-`DATETIME2` → NULL).

Betroffen: jede Anmeldung mit Kennwort. Der Gastzugang (ohne Datenbank)
funktioniert.

### 3. `INSERT … RETURNING` liefert keinen Wert

```sql
INSERT INTO freq_run (interval_code) VALUES ('zz') RETURNING run_id;        -- NULL
```

Die App holt so jeden neu vergebenen Schlüssel (Läufe, Klassen, Benutzer,
Buchungen). Ohne Rückgabe bricht jeder Schreibpfad nach dem ersten INSERT ab.

### 4. `UPDATE … SET spalte = now()` schließt die Verbindung

```sql
BEGIN; UPDATE app_user SET last_login_utc = now() WHERE login = 'chris'; ROLLBACK;
```

Nach 48 s Verbindungsabbruch, Backend 32 s weg. Die App schreibt Zeitstempel
in jeder Anmeldung, jedem Lauf, jeder Bewertung.

### 5. Vergleich mit `TIMESTAMP`-Literal liefert NULL statt Zahl

```sql
SELECT COUNT(*) FROM app_session WHERE expires_utc > TIMESTAMP '2026-01-01';  -- NULL
SELECT COUNT(*) FROM app_session WHERE expires_utc > '2026-01-01';            -- 20 (richtig)
```

Stiller Fehler — keine Meldung, falsches Ergebnis.

### 6. Die zwölf Routinen aus `infra/pgsql/011_routinen.sql` fehlen

`information_schema.routines` ist leer; `SELECT * FROM dbo.letzter_kurs(101)`
liefert 0 Zeilen statt eines Fehlers. Die App ruft sie für Depotbewertung,
Autopilot, Bewertung, Kurvendiskussion. Ob PL/pgSQL unterstützt wird, ist
noch offen — die Datei lässt sich einspielen, sobald 1–4 stehen.

### 7. Kleinere Punkte

- `current_timestamp` liefert NULL (`now()` und `now() AT TIME ZONE 'utc'` sind seit heute korrekt).
- `MERGE INTO … WHEN MATCHED THEN UPDATE` liefert kein Ergebnis; ob geschrieben wurde, ist unklar.
- `CREATE TEMP TABLE … AS SELECT` funktioniert, braucht aber 6,6 s für 4 Zeilen.

---

## Seit dem letzten Stand behoben (Vergleich zu 19.09.)

`now() AT TIME ZONE 'utc'` als Wert und im Vergleich, `INTERVAL '1 day'` und
Arithmetik damit, Text-Literal im Zeitvergleich, `= ANY(array)`, CTE mit
Aggregat, `sha256`, `ORDER BY` mit `CASE`, `ROW_NUMBER() OVER (ORDER BY)`.

## Was funktioniert

Extended Protocol mit `int`-, `text`-, `int[]`-Parametern; `COUNT`, `CAST`,
`SUM(CASE …)`, `GROUP BY` auf kleinen Tabellen, `JOIN`, `CTE`, `OFFSET/FETCH`
mit Parameter, `COALESCE`/`CASE`, `LOWER()`, `sha256`, Transaktionen,
`CREATE TEMP TABLE`.

---

## Vollständige Tabelle

| Gruppe | Prüfung | Ergebnis |
| --- | --- | --- |
| A Grundlagen | SELECT 1 | OK: 1 · 31 ms |
| A Grundlagen | COUNT über asset | OK: 702 · 15 ms |
| A Grundlagen | int-Parameter | OK: NVDA · 98 ms |
| A Grundlagen | text-Parameter | OK: 101 · 18 ms |
| A Grundlagen | int[]-Parameter = ANY | OK: 3 · 43 ms |
| B NULL binär | NULL timestamp aus Tabelle (locked_until_utc) | FEHLER InvalidOperationException: The reader is closed |
| B NULL binär | NULL timestamp neben Wert (2 Spalten) | 1 Zeilen; erste: 0 | LESEFEHLER:ArgumentOutOfRangeException · 3 ms |
| B NULL binär | CAST(NULL AS timestamp) | 1 Zeilen; erste: NULL · 0 ms |
| B NULL binär | CAST(NULL AS int) | 1 Zeilen; erste: NULL · 0 ms |
| B NULL binär | CAST(NULL AS text) | 1 Zeilen; erste: NULL · 0 ms |
| B NULL binär | CAST(NULL AS numeric) | 1 Zeilen; erste: NULL · 0 ms |
| B NULL binär | CAST(NULL AS boolean) | 1 Zeilen; erste: NULL · 0 ms |
| B NULL binär | NULL numeric aus Tabelle (adj_close/volume) | TIMEOUT nach 29 s (keine Antwort) |
| B NULL binär | NULL text aus Tabelle (display_name) | 1 Zeilen; erste: chris | NULL · 4 ms |
| B NULL binär | NULL timestamp in app_session (expires_utc) | 1 Zeilen; erste: 6e6038c4-2625-44ec-b433-1120ddc449e8 | NULL · 6 ms |
| C Zeit | now() | OK: 20/09/2026 09:46:56 · 2 ms |
| C Zeit | now() AT TIME ZONE 'utc' | OK: 20/09/2026 09:46:56 · 0 ms |
| C Zeit | current_timestamp | NULL/LEER · 0 ms |
| C Zeit | CAST(now() AS timestamp) | OK: 20/09/2026 09:46:56 · 0 ms |
| C Zeit | INTERVAL '1 day' | OK: ivl:86400 · 0 ms |
| C Zeit | now() + INTERVAL | OK: 2026-09-21T09:46:56.1173140Z · 0 ms |
| C Zeit | now() + (-3) * INTERVAL '1 day' | OK: 2026-09-17T09:46:56.1177655Z · 0 ms |
| C Zeit | Vergleich Spalte > now() | OK: 17 · 1 ms |
| C Zeit | Vergleich Spalte > now() AT TIME ZONE 'utc' | OK: 17 · 2 ms |
| C Zeit | Vergleich Spalte > TIMESTAMP-Literal | NULL/LEER · 11 ms |
| C Zeit | Vergleich Spalte > '2026-01-01' (Text-Literal) | OK: 20 · 1 ms |
| C Zeit | Vergleich Spalte >= timestamp-PARAMETER | TIMEOUT nach 29 s (keine Antwort) |
| C Zeit | Zwei timestamp-Parameter (Kursreihe, wie die App) | TIMEOUT nach 29 s (keine Antwort) |
| C Zeit | Kursreihe OHNE Zeitfilter (nur ids, LIMIT) | TIMEOUT nach 29 s (keine Antwort) |
| C Zeit | MAX(ts_utc) | TIMEOUT nach 29 s (keine Antwort) |
| C Zeit | CAST(ts AS DATE) | TIMEOUT nach 29 s (keine Antwort) |
| D Fenster/Aggregate | ROW_NUMBER() OVER (ORDER BY) | OK: 1 · 10 ms |
| D Fenster/Aggregate | ROW_NUMBER() OVER (PARTITION BY … ORDER BY) | TIMEOUT nach 33 s (keine Antwort) |
| | *(Backend war weg, zurück nach 57 s)* | |
| D Fenster/Aggregate | LAG() OVER | TIMEOUT nach 29 s (keine Antwort) |
| D Fenster/Aggregate | STDDEV_SAMP (30 Zeilen) | TIMEOUT nach 29 s (keine Antwort) |
| D Fenster/Aggregate | AVG / MIN / MAX | TIMEOUT nach 29 s (keine Antwort) |
| D Fenster/Aggregate | LN() | TIMEOUT nach 29 s (keine Antwort) |
| D Fenster/Aggregate | PERCENTILE_CONT WITHIN GROUP | TIMEOUT nach 29 s (keine Antwort) |
| D Fenster/Aggregate | GROUP BY klein (asset) | 2 Zeilen; erste: True | 645 · 110 ms |
| D Fenster/Aggregate | GROUP BY ein Wert (price_bar, 6k Zeilen) | TIMEOUT nach 29 s (keine Antwort) |
| D Fenster/Aggregate | SUM(CASE …) | OK: 645 · 11 ms |
| E Struktur | JOIN zwei Tabellen | OK: 18 · 10 ms |
| E Struktur | LEFT JOIN LATERAL | TIMEOUT nach 29 s (keine Antwort) |
| E Struktur | Korreliertes Subselect | TIMEOUT nach 29 s (keine Antwort) |
| E Struktur | CTE + COUNT | OK: 4 · 54 ms |
| E Struktur | ORDER BY mit CASE (wie /api/assets/tracked) | 5 Zeilen; erste: 1 · 28 ms |
| E Struktur | ORDER BY ohne CASE | 5 Zeilen; erste: 1 · 27 ms |
| E Struktur | OFFSET/FETCH mit Parameter | 2 Zeilen; erste: BTC-USD · 9 ms |
| E Struktur | COALESCE / CASE | 1 Zeilen; erste: 1 | ja · 18 ms |
| E Struktur | LOWER() im WHERE | OK: 1 · 1 ms |
| E Struktur | sha256(bytea) | OK: ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f200… · 3 ms |
| F Schreiben | INSERT … RETURNING (Rollback) | NULL/LEER · 109 ms |
| F Schreiben | UPDATE mit now() (Rollback) | VERBINDUNG GESCHLOSSEN nach 48,5 s (Exception while reading from stream); Backend wieder da nach 32 s |
| F Schreiben | MERGE INTO (Rollback) | NULL/LEER · 10 ms |
| F Schreiben | CREATE TEMP TABLE AS SELECT | OK: 4 · 6642 ms |
| G Routinen | Anzahl Funktionen in public/dbo | OK: 0 · 1 ms |
| G Routinen | dbo.letzter_kurs(101) | LEER (0 Zeilen) · 7 ms |

## Abfragen im Wortlaut

- **SELECT 1**: `SELECT 1`
- **COUNT über asset**: `SELECT CAST(COUNT(*) AS INT) FROM dbo.asset`
- **int-Parameter**: `SELECT symbol FROM dbo.asset WHERE asset_id = @id`
- **text-Parameter**: `SELECT asset_id FROM dbo.asset WHERE symbol = @s`
- **int[]-Parameter = ANY**: `SELECT CAST(COUNT(*) AS INT) FROM dbo.asset WHERE asset_id = ANY(@ids)`
- **NULL timestamp aus Tabelle (locked_until_utc)**: `SELECT locked_until_utc FROM dbo.app_user WHERE login = 'chris'`
- **NULL timestamp neben Wert (2 Spalten)**: `SELECT failed_logins, locked_until_utc FROM dbo.app_user WHERE login = 'chris'`
- **CAST(NULL AS timestamp)**: `SELECT CAST(NULL AS timestamp) AS t`
- **CAST(NULL AS int)**: `SELECT CAST(NULL AS int) AS i`
- **CAST(NULL AS text)**: `SELECT CAST(NULL AS text) AS s`
- **CAST(NULL AS numeric)**: `SELECT CAST(NULL AS numeric) AS d`
- **CAST(NULL AS boolean)**: `SELECT CAST(NULL AS boolean) AS b`
- **NULL numeric aus Tabelle (adj_close/volume)**: `SELECT "close", adj_close, volume FROM dbo.price_bar WHERE asset_id = 101 AND interval_code = '1d' ORDER BY ts_utc DESC LIMIT 1`
- **NULL text aus Tabelle (display_name)**: `SELECT login, display_name FROM dbo.app_user WHERE login = 'chris'`
- **NULL timestamp in app_session (expires_utc)**: `SELECT session_key, expires_utc FROM dbo.app_session WHERE expires_utc IS NULL LIMIT 1`
- **now()**: `SELECT now()`
- **now() AT TIME ZONE 'utc'**: `SELECT (now() AT TIME ZONE 'utc')`
- **current_timestamp**: `SELECT current_timestamp`
- **CAST(now() AS timestamp)**: `SELECT CAST(now() AS timestamp)`
- **INTERVAL '1 day'**: `SELECT INTERVAL '1 day'`
- **now() + INTERVAL**: `SELECT now() + INTERVAL '1 day'`
- **now() + (-3) * INTERVAL '1 day'**: `SELECT now() + (-3) * INTERVAL '1 day'`
- **Vergleich Spalte > now()**: `SELECT CAST(COUNT(*) AS INT) FROM dbo.app_session WHERE expires_utc > now()`
- **Vergleich Spalte > now() AT TIME ZONE 'utc'**: `SELECT CAST(COUNT(*) AS INT) FROM dbo.app_session WHERE expires_utc > (now() AT TIME ZONE 'utc')`
- **Vergleich Spalte > TIMESTAMP-Literal**: `SELECT CAST(COUNT(*) AS INT) FROM dbo.app_session WHERE expires_utc > TIMESTAMP '2026-01-01'`
- **Vergleich Spalte > '2026-01-01' (Text-Literal)**: `SELECT CAST(COUNT(*) AS INT) FROM dbo.app_session WHERE expires_utc > '2026-01-01'`
- **Vergleich Spalte >= timestamp-PARAMETER**: `SELECT CAST(COUNT(*) AS INT) FROM dbo.price_bar WHERE asset_id = 101 AND interval_code = '1d' AND ts_utc >= @von`
- **Zwei timestamp-Parameter (Kursreihe, wie die App)**: `SELECT asset_id, ts_utc, "close" FROM dbo.price_bar WHERE asset_id = ANY(@ids) AND interval_code = @iv AND ts_utc >= @von AND ts_utc <= @bis ORDER BY asset_id, ts_utc`
- **Kursreihe OHNE Zeitfilter (nur ids, LIMIT)**: `SELECT asset_id, ts_utc, "close" FROM dbo.price_bar WHERE asset_id = ANY(@ids) AND interval_code = @iv ORDER BY asset_id, ts_utc DESC LIMIT 50`
- **MAX(ts_utc)**: `SELECT MAX(ts_utc) FROM dbo.price_bar WHERE asset_id = 101`
- **CAST(ts AS DATE)**: `SELECT CAST(MAX(ts_utc) AS DATE) FROM dbo.price_bar WHERE asset_id = 101`
- **ROW_NUMBER() OVER (ORDER BY)**: `SELECT row_number() OVER (ORDER BY asset_id) FROM dbo.asset LIMIT 1`
- **ROW_NUMBER() OVER (PARTITION BY … ORDER BY)**: `SELECT asset_id, row_number() OVER (PARTITION BY asset_id ORDER BY ts_utc DESC) rn FROM dbo.price_bar WHERE asset_id = 101 AND interval_code = '1d' ORDER BY ts_utc DESC LIMIT 2`
- **LAG() OVER**: `SELECT "close" - lag("close") OVER (ORDER BY ts_utc) FROM dbo.price_bar WHERE asset_id = 101 AND interval_code = '1d' ORDER BY ts_utc DESC LIMIT 1`
- **STDDEV_SAMP (30 Zeilen)**: `SELECT stddev_samp("close") FROM (SELECT "close" FROM dbo.price_bar WHERE asset_id = 101 AND interval_code = '1d' ORDER BY ts_utc DESC LIMIT 30) t`
- **AVG / MIN / MAX**: `SELECT AVG("close"), MIN("close"), MAX("close") FROM dbo.price_bar WHERE asset_id = 101 AND interval_code = '1d'`
- **LN()**: `SELECT LN(CAST("close" AS DOUBLE PRECISION)) FROM dbo.price_bar WHERE asset_id = 101 AND interval_code = '1d' ORDER BY ts_utc DESC LIMIT 1`
- **PERCENTILE_CONT WITHIN GROUP**: `SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY "close") FROM dbo.price_bar WHERE asset_id = 101 AND interval_code = '1d'`
- **GROUP BY klein (asset)**: `SELECT is_tracked, CAST(COUNT(*) AS INT) FROM dbo.asset GROUP BY is_tracked`
- **GROUP BY ein Wert (price_bar, 6k Zeilen)**: `SELECT interval_code, CAST(COUNT(*) AS INT) FROM dbo.price_bar WHERE asset_id = 101 GROUP BY interval_code`
- **SUM(CASE …)**: `SELECT CAST(SUM(CASE WHEN is_tracked THEN 1 ELSE 0 END) AS INT) FROM dbo.asset`
- **JOIN zwei Tabellen**: `SELECT COUNT(*) FROM dbo.app_session s JOIN dbo.app_user u ON u.user_id = s.user_id`
- **LEFT JOIN LATERAL**: `SELECT k.ts_utc FROM dbo.asset a LEFT JOIN LATERAL (SELECT ts_utc FROM dbo.price_bar p WHERE p.asset_id = a.asset_id ORDER BY ts_utc DESC LIMIT 1) k ON TRUE WHERE a.asset_id = 101`
- **Korreliertes Subselect**: `SELECT (SELECT MAX(ts_utc) FROM dbo.price_bar p WHERE p.asset_id = a.asset_id) FROM dbo.asset a WHERE a.asset_id = 101`
- **CTE + COUNT**: `WITH b AS (SELECT asset_id FROM dbo.asset WHERE asset_id < 5) SELECT CAST(COUNT(*) AS INT) FROM b`
- **ORDER BY mit CASE (wie /api/assets/tracked)**: `SELECT asset_id FROM dbo.asset WHERE is_tracked = TRUE ORDER BY asset_class, CASE WHEN market_cap_rank IS NULL THEN 1 ELSE 0 END, market_cap_rank, symbol LIMIT 5`
- **ORDER BY ohne CASE**: `SELECT asset_id FROM dbo.asset WHERE is_tracked = TRUE ORDER BY asset_class, market_cap_rank, symbol LIMIT 5`
- **OFFSET/FETCH mit Parameter**: `SELECT symbol FROM dbo.asset ORDER BY asset_id OFFSET 0 ROWS FETCH NEXT (@n) ROWS ONLY`
- **COALESCE / CASE**: `SELECT COALESCE(market_cap_rank, -1), CASE WHEN is_tracked THEN 'ja' ELSE 'nein' END FROM dbo.asset WHERE asset_id = 101`
- **LOWER() im WHERE**: `SELECT user_id FROM dbo.app_user WHERE LOWER(login) = LOWER(@l)`
- **sha256(bytea)**: `SELECT encode(sha256('abc'::bytea), 'hex')`
- **INSERT … RETURNING (Rollback)**: `BEGIN; INSERT INTO dbo.freq_run (interval_code) VALUES ('zz') RETURNING run_id; ROLLBACK;`
- **UPDATE mit now() (Rollback)**: `BEGIN; UPDATE dbo.app_user SET last_login_utc = now() WHERE login = 'chris'; ROLLBACK;`
- **MERGE INTO (Rollback)**: `BEGIN; MERGE INTO dbo.pillar_weight t USING (SELECT 'learning' AS pillar) s ON t.pillar = s.pillar WHEN MATCHED THEN UPDATE SET weight = t.weight; ROLLBACK;`
- **CREATE TEMP TABLE AS SELECT**: `CREATE TEMP TABLE t_probe AS SELECT asset_id FROM dbo.asset WHERE asset_id < 5; SELECT CAST(COUNT(*) AS INT) FROM t_probe`
- **Anzahl Funktionen in public/dbo**: `SELECT CAST(COUNT(*) AS INT) FROM information_schema.routines WHERE routine_schema IN ('public','dbo')`
- **dbo.letzter_kurs(101)**: `SELECT * FROM dbo.letzter_kurs(101)`


---

## Reproduktion ohne die Anwendung

```
psql -h localhost -p 55531 -U cire -d stockcrawler
-- 1  	iming on
SELECT MAX(ts_utc) FROM price_bar WHERE asset_id = 101;
-- 2  (Binärformat: über PREPARE/EXECUTE mit einem Treiber, z. B. Npgsql, psycopg mit binary cursors)
SELECT failed_logins, locked_until_utc FROM app_user WHERE login = 'chris';
-- 3
BEGIN; INSERT INTO freq_run (interval_code) VALUES ('zz') RETURNING run_id; ROLLBACK;
-- 4
BEGIN; UPDATE app_user SET last_login_utc = now() WHERE login = 'chris'; ROLLBACK;
-- 5
SELECT COUNT(*) FROM app_session WHERE expires_utc > TIMESTAMP '2026-01-01';
```

Das Prüfprogramm (C#, Npgsql) liegt unter `%TEMP%\sc-port\Probe\Program.cs`
auf dem Entwicklungsrechner und lässt sich nach jedem Backend-Stand in einer
Minute wiederholen.
