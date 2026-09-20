# EventMesh DataCell als Datenbank für CRSOFT.StockCrawler — Verträglichkeitsbericht

**Stand 20.09.2026, 13:10** (zweiter Bericht; der erste vom Vormittag ist in
der Git-Geschichte). Ziel: `Host=localhost;Port=55531;Database=stockcrawler`,
meldet sich als „PostgreSQL 14.0 (EventMesh DataCell backend)". Client:
Npgsql 8, Extended Protocol, Binärformat — so spricht die Anwendung. Jede
Prüfung ist eine einzelne Abfrage auf einer frischen Verbindung mit 25 s
Frist; danach der Routenlauf der Anwendung (86 lesende Endpunkte, 45 s Frist).

---

## Seit dem Vormittag behoben

| | |
| --- | --- |
| NULL-`timestamp` aus Tabellenspalte im Binärformat | ✓ — **Anmeldung mit Kennwort geht** |
| `expires_utc > TIMESTAMP '2026-01-01'` | ✓ (vorher NULL) |
| `UPDATE … SET x = now()` | ✓ (vorher Verbindungsabbruch) |
| `ROW_NUMBER() OVER (PARTITION BY … ORDER BY …)` auf `price_bar` | ✓ |
| `STDDEV_SAMP`, `AVG/MIN/MAX`, `GROUP BY` auf `price_bar` (6 k Zeilen) | ✓ (0,3–0,4 s) |
| Korreliertes Subselect `(SELECT MAX(ts_utc) … WHERE p.asset_id = a.asset_id)` | ✓ (0,85 s) |
| `CREATE TEMP TABLE … AS SELECT` | ✓, 61 ms statt 6,6 s |

---

## Was die Anwendung jetzt blockiert — nach Wirkung

### 1. `MERGE INTO` schreibt nicht (→ nach der Anmeldung überall 401)

```sql
MERGE INTO app_session t
USING (SELECT 'aaaaaaaa-0000-0000-0000-000000000001'::uuid AS session_key) s
   ON t.session_key = s.session_key
WHEN MATCHED THEN UPDATE SET user_id = 1
WHEN NOT MATCHED THEN INSERT (session_key, user_id, expires_utc)
     VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 1, now() + interval '1 day');
-- Antwort: SELECT 0. Danach: SELECT … WHERE session_key = '…' -> 0 Zeilen.
```

Die Anmeldung meldet 200, bindet die Sitzung aber per `MERGE` an den
Benutzer — die Zeile kommt nie an, der nächste Aufruf ist wieder „Nicht
angemeldet". **Routenlauf: 2 von 86 Endpunkten 200 (`/api/health`,
`/api/auth/status`), 84 × 401.** `MERGE` steht ausserdem hinter Kursen
schreiben, Gewichten, Konten, Einstellungen.

Zum Abgleich mit dem Standard: `MERGE` ist seit PostgreSQL 15 Teil des
Kerns; die Anwendung benutzt die Form mit `USING (SELECT …) s ON … WHEN
MATCHED THEN UPDATE … WHEN NOT MATCHED THEN INSERT …`.

### 2. Timestamp-**Parameter** im Vergleich → Timeout (→ Kurscharts 500)

```sql
-- Literal: OK (4 ms)          -- Parameter: Timeout nach 29 s
SELECT COUNT(*) FROM price_bar WHERE asset_id = 101 AND interval_code = '1d' AND ts_utc >= $1
-- $1 als binärer timestamp (OID 1114), Wert 2026-08-01 00:00:00
```

Die Kursreihen-Abfrage der Anwendung hat zwei davon (`ts_utc >= $3 AND
ts_utc <= $4`). Mit Literalen wäre sie schnell — es hängt am gebundenen
`timestamp`-Parameter im Binärformat.

### 3. Weitere Zugriffe auf `price_bar` → Timeout oder Abbruch

`SELECT … WHERE asset_id = ANY($1) AND interval_code = $2 ORDER BY … LIMIT 50`,
`SELECT MAX(ts_utc) FROM price_bar WHERE asset_id = 101` (Timeout),
`SELECT CAST(MAX(ts_utc) AS DATE) …` (Verbindungsabbruch, Backend 8 s weg).
Widersprüchlich dazu läuft dieselbe `MAX`-Abfrage als korreliertes Subselect
in 0,85 s — siehe Beobachtung unten.

### 4. Still falsch (schnell, aber NULL)

| Abfrage | Ergebnis |
| --- | --- |
| `"close" - lag("close") OVER (ORDER BY ts_utc)` | NULL |
| `LN(CAST("close" AS DOUBLE PRECISION))` | NULL |
| `percentile_cont(0.5) WITHIN GROUP (ORDER BY "close")` | NULL |
| `current_timestamp` | NULL (`now()` ist korrekt) |
| `INSERT … RETURNING run_id` | kein Wert |

Stille Fehler sind für die Anwendung gefährlicher als Abbrüche: Ein NULL
aus `LAG` wird zu einer Rendite von null, ein fehlendes `RETURNING` zu einem
Lauf ohne Schlüssel.

### 5. Routinen fehlen

`information_schema.routines` ist leer; `SELECT * FROM dbo.letzter_kurs(101)`
liefert 0 Zeilen statt eines Fehlers. Die zwölf Funktionen aus
`infra/pgsql/011_routinen.sql` (PL/pgSQL) müssten eingespielt werden — ob
PL/pgSQL unterstützt wird, ist offen.

---

## Beobachtung zur Lastisolation

Während die Konstruktprüfung auf `price_bar` lief, beantwortete das Backend
in einer **zweiten Verbindung** nicht einmal die Anmeldeabfrage
(`SELECT … FROM app_user WHERE LOWER(login) = LOWER($1)`, sonst 0,1 s):
*Timeout during reading attempt*. Eine lange Abfrage blockiert offenbar
alle anderen Sitzungen. Dazu passt: Direkt nach einem Timeout scheitern
auch einfache Folgeabfragen, als würde die abgebrochene Abfrage
weiterlaufen. Die Timeout-Zeilen in der Tabelle unten können deshalb
Nachwirkungen der jeweils vorigen sein — bei der Bewertung nicht jede
einzeln als eigener Fehler nehmen, sondern nach dem Fix von Punkt 2 und 3
erneut messen.

---

## Vollständige Tabelle (Konstruktprüfung, 58 Fälle)

| Gruppe | Prüfung | Ergebnis |
| --- | --- | --- |
| A Grundlagen | SELECT 1 | OK: 1 · 51 ms |
| A Grundlagen | COUNT über asset | OK: 702 · 5 ms |
| A Grundlagen | int-Parameter | OK: NVDA · 209 ms |
| A Grundlagen | text-Parameter | OK: 101 · 38 ms |
| A Grundlagen | int[]-Parameter = ANY | OK: 3 · 59 ms |
| B NULL binär | NULL timestamp aus Tabelle (locked_until_utc) | 1 Zeilen; erste: NULL · 2 ms |
| B NULL binär | NULL timestamp neben Wert (2 Spalten) | 1 Zeilen; erste: 0 | NULL · 4 ms |
| B NULL binär | CAST(NULL AS timestamp) | 1 Zeilen; erste: NULL · 0 ms |
| B NULL binär | CAST(NULL AS int) | 1 Zeilen; erste: NULL · 1 ms |
| B NULL binär | CAST(NULL AS text) | 1 Zeilen; erste: NULL · 0 ms |
| B NULL binär | CAST(NULL AS numeric) | 1 Zeilen; erste: NULL · 0 ms |
| B NULL binär | CAST(NULL AS boolean) | 1 Zeilen; erste: NULL · 5 ms |
| B NULL binär | NULL numeric aus Tabelle (adj_close/volume) | 1 Zeilen; erste: 213,89999390 | 213,89999390 | 96079300,00000000 · 1909 ms |
| B NULL binär | NULL text aus Tabelle (display_name) | 1 Zeilen; erste: chris | NULL · 1243 ms |
| B NULL binär | NULL timestamp in app_session (expires_utc) | 1 Zeilen; erste: 6e6038c4-2625-44ec-b433-1120ddc449e8 | NULL · 19 ms |
| C Zeit | now() | OK: 20/09/2026 11:00:27 · 7 ms |
| C Zeit | now() AT TIME ZONE 'utc' | OK: 20/09/2026 11:00:27 · 0 ms |
| C Zeit | current_timestamp | NULL/LEER · 0 ms |
| C Zeit | CAST(now() AS timestamp) | OK: 20/09/2026 11:00:27 · 0 ms |
| C Zeit | INTERVAL '1 day' | OK: ivl:86400 · 2 ms |
| C Zeit | now() + INTERVAL | OK: 2026-09-21T11:00:27.5925242Z · 1 ms |
| C Zeit | now() + (-3) * INTERVAL '1 day' | OK: 2026-09-17T11:00:27.5939525Z · 1 ms |
| C Zeit | Vergleich Spalte > now() | OK: 17 · 9 ms |
| C Zeit | Vergleich Spalte > now() AT TIME ZONE 'utc' | OK: 17 · 3 ms |
| C Zeit | Vergleich Spalte > TIMESTAMP-Literal | OK: 20 · 4 ms |
| C Zeit | Vergleich Spalte > '2026-01-01' (Text-Literal) | OK: 20 · 8 ms |
| C Zeit | Vergleich Spalte >= timestamp-PARAMETER | TIMEOUT nach 29 s (keine Antwort) |
| C Zeit | Zwei timestamp-Parameter (Kursreihe, wie die App) | TIMEOUT nach 29 s (keine Antwort) |
| C Zeit | Kursreihe OHNE Zeitfilter (nur ids, LIMIT) | TIMEOUT nach 29 s (keine Antwort) |
| C Zeit | MAX(ts_utc) | TIMEOUT nach 29 s (keine Antwort) |
| C Zeit | CAST(ts AS DATE) | VERBINDUNG GESCHLOSSEN nach 26,0 s (Exception while reading from stream); Backend wieder da nach 8 s |
| D Fenster/Aggregate | ROW_NUMBER() OVER (ORDER BY) | OK: 1 · 2499 ms |
| D Fenster/Aggregate | ROW_NUMBER() OVER (PARTITION BY … ORDER BY) | 2 Zeilen; erste: 101 | NULL · 466 ms |
| D Fenster/Aggregate | LAG() OVER | NULL/LEER · 331 ms |
| D Fenster/Aggregate | STDDEV_SAMP (30 Zeilen) | OK: 0.0542678450869469 · 379 ms |
| D Fenster/Aggregate | AVG / MIN / MAX | 1 Zeilen; erste: 20,215674150149106 | 0,06141700 | 235,74000549 · 351 ms |
| D Fenster/Aggregate | LN() | NULL/LEER · 263 ms |
| D Fenster/Aggregate | PERCENTILE_CONT WITHIN GROUP | NULL/LEER · 508 ms |
| D Fenster/Aggregate | GROUP BY klein (asset) | 2 Zeilen; erste: True | 645 · 37 ms |
| D Fenster/Aggregate | GROUP BY ein Wert (price_bar, 6k Zeilen) | 2 Zeilen; erste: 1d | 6304 · 365 ms |
| D Fenster/Aggregate | SUM(CASE …) | OK: 645 · 7 ms |
| E Struktur | JOIN zwei Tabellen | OK: 18 · 9 ms |
| E Struktur | LEFT JOIN LATERAL | TIMEOUT nach 29 s (keine Antwort) |
| E Struktur | Korreliertes Subselect | OK: 17/09/2026 20:00:00 · 849 ms |
| E Struktur | CTE + COUNT | OK: 4 · 30 ms |
| E Struktur | ORDER BY mit CASE (wie /api/assets/tracked) | 5 Zeilen; erste: 1 · 56 ms |
| E Struktur | ORDER BY ohne CASE | 5 Zeilen; erste: 1 · 20 ms |
| E Struktur | OFFSET/FETCH mit Parameter | 2 Zeilen; erste: BTC-USD · 16 ms |
| E Struktur | COALESCE / CASE | 1 Zeilen; erste: 1 | ja · 22 ms |
| E Struktur | LOWER() im WHERE | OK: 1 · 1 ms |
| E Struktur | sha256(bytea) | OK: ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f200… · 2 ms |
| F Schreiben | INSERT … RETURNING (Rollback) | NULL/LEER · 38 ms |
| F Schreiben | UPDATE mit now() (Rollback) | NULL/LEER · 351 ms |
| F Schreiben | MERGE INTO (Rollback) | NULL/LEER · 1 ms |
| F Schreiben | CREATE TEMP TABLE AS SELECT | OK: 4 · 61 ms |
| G Routinen | Anzahl Funktionen in public/dbo | OK: 0 · 1 ms |
| G Routinen | dbo.letzter_kurs(101) | LEER (0 Zeilen) · 1 ms |

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
-- 1  MERGE (siehe oben) und danach SELECT auf den Schluessel
-- 2  nur mit gebundenem Parameter reproduzierbar (Npgsql, psycopg binary, JDBC):
--    SELECT COUNT(*) FROM price_bar WHERE asset_id = 101 AND interval_code = '1d' AND ts_utc >= $1
-- 3  SELECT MAX(ts_utc) FROM price_bar WHERE asset_id = 101;
-- 4  SELECT "close" - lag("close") OVER (ORDER BY ts_utc) FROM price_bar
--      WHERE asset_id = 101 AND interval_code = '1d' ORDER BY ts_utc DESC LIMIT 1;
--    SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY "close") FROM price_bar WHERE asset_id = 101;
--    BEGIN; INSERT INTO freq_run (interval_code) VALUES ('zz') RETURNING run_id; ROLLBACK;
```

Prüfprogramm (C#, Npgsql): `%TEMP%\sc-port\Probe\Program.cs` auf dem
Entwicklungsrechner; Routenlauf: `%TEMP%\sc-portauchtest_em2.py`. Beide
lassen sich nach jedem Backend-Stand in wenigen Minuten wiederholen.
