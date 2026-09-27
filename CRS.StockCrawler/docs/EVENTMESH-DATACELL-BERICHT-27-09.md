# EventMesh DataCell — dritter Bericht, 27.09.2026

**Node:** `Host=localhost;Port=55561;Database=stockcrawler`, meldet sich als
„PostgreSQL 14.0 (EventMesh DataCell backend)". **Client:** Npgsql 8,
Extended Protocol — so spricht die Anwendung.

Der zweite Bericht steht in [EVENTMESH-DATACELL-BERICHT.md](EVENTMESH-DATACELL-BERICHT.md);
dieser hier ist der Stand nach einem Tag gemeinsamer Arbeit mit der
Node-Session, an dem rund zwölf Befunde gemeldet und acht davon behoben
wurden.

---

## Zuerst: ein Messfehler auf unserer Seite

**`psql` taugt nicht zum Messen dieses Backends.** Der Node kennt
`PREPARE`/`EXECUTE` nicht — er antwortet darauf mit `SELECT 0` und führt
nichts aus. Wer darüber testet, bekommt *jede* Prüfung falsch negativ.

Ich habe auf dieser Grundlage gemeldet, parametrisiertes `MERGE` schreibe
nicht. Das war falsch: Über echtes Npgsql schreibt es einwandfrei. Der
tatsächliche Fehler lag woanders (siehe unten, „berechneter Ausdruck").

**Regel daraus:** Dieses Backend wird mit dem Treiber gemessen, den die
Anwendung benutzt — sonst misst man den Treiber, nicht die Datenbank.

---

## Behoben im Lauf des Tages

| Befund | Wirkung vorher |
| --- | --- |
| Berechneter Ausdruck wird beim Schreiben verworfen | **Anmelden unmöglich** |
| `IDENTITY` beim `CREATE TABLE` verworfen | jede eingefügte Zeile ohne Schlüssel |
| `INSERT … RETURNING` liefert NULL | Laufprotokollierung blind |
| Protokoll-Desynchronisation (`ReadyForQuery` ausser der Reihe) | Verbindung *und* Anwendungsprozess tot |
| `COPY … FROM STDIN` nicht implementiert | Bulk-Import schrieb nichts |
| Gleichheit auf Zeitstempel liefert 0 Zeilen | stille Datenlücken |
| Bereichsbedingungen (`>=`, `<=`) | harter Abbruch |
| `COUNT(DISTINCT …)` ignorierte `DISTINCT` | falsche Zahlen in der Oberfläche |
| `IN (v1,…,vn)` nicht als selektiver Treiber | Chartabfrage 48 s statt ms |

### Der gefährlichste: berechneter Ausdruck beim Schreiben

```sql
INSERT INTO dbo.app_session (session_key, user_id, expires_utc)
VALUES ('X', 1, (now() AT TIME ZONE 'utc') + INTERVAL '14 day');
```

Gespeichert wurde **`jetzt`**, nicht `jetzt + 14 Tage` — die Intervall­addition
fiel weg, ohne Fehler und ohne Warnung. Derselbe Ausdruck in einem `SELECT`
rechnete richtig.

Die Wirkung war nicht etwa eine falsche Anzeige, sondern: **Anmelden
unmöglich.** Der Sitzungslesevorgang verlangt `expires_utc > jetzt`; stand
dort der Entstehungszeitpunkt, war jede Sitzung im Moment ihrer Entstehung
abgelaufen. Der Login antwortete mit `200` und der richtigen Rolle, die
nächste Anfrage kannte niemanden, die Oberfläche fiel wortlos aufs Formular
zurück — **es sah aus wie ein falsches Kennwort.**

Das ist das Muster, das dieses Backend so schwer zu prüfen macht: Es bricht
nicht ab, es antwortet falsch.

---

## Offen — nach Gewicht

### 1. Der Prozess beendet sich bei breiten Abfragen

Vier Mal an einem Tag. Zuletzt:

```sql
SELECT count(*) FROM dbo.price_bar
 WHERE asset_id IN (<646 ids>) AND interval_code='1d'
   AND ts_utc >= '2026-09-13';
```

→ Speicher steigt auf 6,19 GB, keine Antwort binnen 60 s, **Prozess weg**.
Erwartet wären 5.700 Zeilen.

Der Selbstschutz (54000 statt OOM) greift für den Anti-Join, für diese Form
nicht. **Eine Datenbank, die eine einzelne Abfrage nicht überlebt, nimmt
allen anderen die Datenbank weg.** Das Materialisierungslimit sollte am
tatsächlichen Verbrauch hängen, nicht am erkannten Abfragetyp.

### 2. Bereich über viele Werte ist unbenutzbar

| Abfrage | |
| --- | ---: |
| `interval_code='1d' AND ts_utc >= <vor 14 Tagen>` (5.742 Zeilen) | **44 s** |
| dieselbe mit `ROW_NUMBER() OVER (PARTITION BY asset_id …)` | **Timeout** |
| dieselbe mit `IN (646 ids)` | **Prozesstod** |

Die Ursache ist bekannt und von der Node-Seite beschrieben: Je Wert werden
alle Zeilen rekonstruiert und erst danach auf das Zeitfenster gefiltert. Bei
646 Werten sind das knapp vier Millionen Zeilen für 5.742 gesuchte.

**Es gibt auf Anwendungsseite keine Umformulierung, die das umgeht.** Ich
habe drei versucht: ohne Fensterfunktion, mit ausgeschriebener `IN`-Liste,
mit `UNION ALL` je Wert. Alle scheitern an derselben Stelle.

Betroffen sind fünf der acht Kacheln unserer Startseite — Lage, Markt,
Nachrichten, Depot, Prognose. Sie alle fragen „alle Werte, letzte N Tage",
die Grundform jeder Marktübersicht.

### 3. Kursreihen skalieren linear

| | |
| --- | ---: |
| 1 Wert, 12 Monate Tagesbars | 963 ms |
| 5 Werte | 1.907 ms |
| 40 Werte (hochgerechnet) | ~15 s |

Zum Vergleich SQL Server: unter 100 ms für alle vierzig, weil der Index auf
`(asset_id, interval_code, ts_utc)` vierzig kurze Bereichssuchen erlaubt.

Parallele Einzelabfragen bringen wenig (1.190 ms statt 1.907 ms für fünf) —
der Aufwand liegt nicht im Roundtrip.

### 4. Kleineres, gemeldet und notiert

- `CASE WHEN spalte > jetzt THEN 1 ELSE 0 END` nimmt bei `spalte IS NULL`
  den `THEN`-Zweig. **Ein Vergleich mit NULL muss UNKNOWN ergeben, nie
  TRUE.** Wirkung bei uns: Die Benutzerverwaltung zeigte das einzige
  Verwalterkonto dauerhaft als „gesperrt", während die Anmeldung lief.
- Schreibpfad kann den aktuellen Zeilenwert nicht lesen: `COALESCE(@neu,
  spalte)` und `CASE … ELSE spalte END` schreiben Unsinn. Bei uns hätte das
  beim Ändern eines Anzeigenamens den Kennworthash gelöscht.
- Spalten-`DEFAULT` wird nicht angewandt (`started_utc DEFAULT now()` bleibt
  NULL).
- `information_schema.columns` ignoriert `WHERE` **und** die Spaltenauswahl.
- `INSERT … ON CONFLICT DO UPDATE` schreibt nichts, ohne Fehler.
- `count(*) FILTER (WHERE …)` → `NullReferenceException`.
- OR-Kette über dieselbe Spalte → `NullReferenceException` nach 37 s.
- `left()`, `length()` liefern Leerwerte bei gefüllter Spalte.

---

## Was die Anwendung daraufhin gelernt hat

Diese Änderungen sind **auch auf SQL Server richtig** — keine ist ein blosser
Umweg:

| Vorher | Jetzt | Warum es überall besser ist |
| --- | --- | --- |
| Anti-Join für „offene Prognosen" | `gesamt − bewertet` | exakt wegen `PRIMARY KEY (forecast_id)`, und war auch dort die teuerste Abfrage der Seite |
| `GROUP BY interval_code` über 4 Mio. Zeilen | zwei Abfragen mit Gleichheit | es gibt genau zwei Intervalle |
| `(a = @p OR b = @p OR @p IS NULL)` | zwei getrennte Abfragen | ein Filter, der sich selbst abschaltet, verhindert überall die Indexnutzung |
| `CASE`/`COALESCE` über Spaltenwerte im `UPDATE` | Werte in C# rechnen | unabhängig davon, welche Uhr die Datenbank führt |
| Einzelfristen je Abfrage | ein Gesamtbudget je Seite | eine Übersichtsseite darf nie länger dauern als die Geduld dessen, der sie öffnet |
| Start bricht ohne Datenbank ab | Start läuft, Fehler im Protokoll | ein Dienst wartet auf seine Abhängigkeit, statt sich zu beenden |
| 500 mit Aufrufliste | 200 mit erklärendem Satz | der Fehler liegt nicht in der Anwendung, und der Betrachter kann ihn nicht reparieren |
| bis zu 9.000 Rasterpunkte je Chart | bei 2.000 gedeckelt | ein Diagramm ist 1.500 Bildpunkte breit |

**Die eine Stelle, die ein reiner Umweg ist und zurückgebaut gehört:**
`PriceBarRepository.GetManyAsync` schreibt die Werteliste als
`IN (201,202,…)` aus, statt `asset_id = ANY(@ids)` über `SqlDialekt.In` zu
gehen — weil `= ANY` auf dem Node in 80 s Timeout läuft, `IN` aber in 2,5 s
antwortet. Das ist die einzige Stelle mit zusammengesetztem SQL im Projekt
und nur deshalb unbedenklich, weil es ausschliesslich um `int` geht.
