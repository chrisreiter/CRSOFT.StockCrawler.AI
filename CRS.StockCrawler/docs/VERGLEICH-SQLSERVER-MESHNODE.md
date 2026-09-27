# SQL Server gegen EventMesh DataCell — gemessen am 27./28.09.2026

Die Anwendung lief bis zum 27.09.2026 auf SQL Server und läuft seither auf
dem selbst entwickelten EventMesh-DataCell-Node (PostgreSQL-Drahtprotokoll,
Port 55561). Dieses Dokument hält fest, wie sich beide unter derselben Last
verhalten, was den Unterschied ausmacht und was dagegen unternommen wurde.

Die Messlatte war ausdrücklich gesetzt: **Die Kursansicht muss mindestens so
schnell laden wie mit dem SQL Server, und der Arbeitsspeicher darf dabei
nicht über dessen Verbrauch steigen.**

## Wie gemessen wurde

Beide Systeme tragen dieselben Daten:

| Tabelle | SQL Server | Node |
| --- | ---: | ---: |
| `price_bar` | 4.031.495 | 4.038.721 |
| `forecast` | 1.264.033 | 1.264.033 |
| `forecast_score` | 483.588 | 483.588 |

Gemessen wird **dieselbe Anwendung**, nur mit anderer Verbindungszeichenfolge
(`Datenbank__System` plus `ConnectionStrings__Sql` beziehungsweise
`ConnectionStrings__Postgres`). Der Zeitplan ist dabei **abgeschaltet**
(`Ingest__SchedulerEnabled=false`) — ein nebenherlaufender Tageslauf
verfälscht jede Messung, und genau das ist im Verlauf dieser Arbeit zweimal
passiert.

Gemessen werden die Aufrufe, die die Kursansicht tatsächlich absetzt.

**Zwei Fallen, in die ich dabei selbst gelaufen bin** — sie gehören zur
Methode:

1. **Mit dem Treiber der Anwendung messen, nicht mit `psql`.** `psql`
   verschickt einfache Abfragen mit Literalen, Npgsql verschickt
   Parse/Bind/Execute. Eine Abfrage, die über `psql` 4 ms kostete, kostete
   über Npgsql 150 ms. Ich habe daraufhin eine „Optimierung" gebaut, die
   dreimal langsamer war als der Zustand davor.
2. **Die Anwendung vor dem Übersetzen beenden.** Läuft sie, scheitert das
   Kopieren der DLLs mit `MSB3027`, der Übersetzungsschritt meldet trotzdem
   „0 Errors", und man misst danach den alten Stand. Dreimal passiert.

## Ausgangslage

Bester von je vier Läufen, Millisekunden:

| Aufruf | SQL Server | Node | Faktor |
| --- | ---: | ---: | ---: |
| Chart, 1 Wert, 12 Monate | 21 | 144 | 6,9 |
| + Prognose | 33 | 3.855 | 116,8 |
| + Prognose + Rückblick | 43 | 7.339 | 170,7 |
| Chart, 5 Werte + Prognose | 109 | 23.034 | 211,3 |
| Chart, 1 Wert, stündlich, 3 Monate | 24 | 173 | 7,2 |
| **Arbeitsspeicher Datenbank** | **2,10 GB** | **0,44 GB** | 0,21 |

Bemerkenswert schon hier: Der Node braucht ein Fünftel des Arbeitsspeichers.
Das ist das Polster, aus dem die spätere Lösung bezahlt wurde.

## Was der Node gut kann — und was nicht

Alle Zahlen auf einem unbelasteten Node, jede Abfrage mit eigenem Literalwert,
damit der Ergebnisspeicher des Node nicht mitmisst.

### Sequentiell lesen ist schnell

| | |
| --- | ---: |
| `count(*)` über `forecast` (1.264.033 Zeilen) | 778 ms |
| 200.000 Zeilen ausliefern (nach der Pufferkorrektur) | 664 ms |
| **Aufwand je Zeile** | **3,3 µs** |

### Über einen Index gefundene Zeilen sind teuer

| | | je Zeile |
| --- | ---: | ---: |
| 997 Bewertungen, 3 Spalten | 60 ms | 60 µs |
| 250 Kursbars, 8 Spalten | 208 ms | 832 µs |

**Faktor 17 bis 250 gegenüber dem sequentiellen Lesen.** Genau davon lebt eine
Kursansicht: Sie holt ein paar hundert Zeilen über einen Index. Die Ursache
liegt im Aufbau des Store — jede Zeile wird aus einzelnen Zellen
zusammengesetzt, und der Aufwand wächst mit Zeilen **mal** Spalten.

### Aggregate nutzen die Einschränkung nicht

`forecast_score`, 483.588 Zeilen, Index auf `(asset_id, horizon_hours)`, je
Wert 997 Treffer, drei verschiedene Werte:

| Abfrage | 241 | 242 | 243 |
| --- | ---: | ---: | ---: |
| `count(*) WHERE asset_id = N` | 2,7 ms | 1,7 ms | 2,7 ms |
| `horizon_hours, count(*) … GROUP BY` | 103,3 ms | 110,2 ms | 112,9 ms |
| `count(*), avg(abs_pct_error)` | 101,6 ms | 110,3 ms | 103,3 ms |

Ein nacktes `count(*)` ist schnell; sobald ein `GROUP BY` oder ein zweites
Aggregat dazukommt, **Faktor 40**.

### `= ANY` fällt ab dem zweiten Wert zurück

| | |
| --- | ---: |
| `= ANY(ARRAY[201])` | 176 ms |
| `= ANY(ARRAY[202…206])`, fünf Werte | 1.083 ms |
| `= ANY(ARRAY[210…219])`, zehn Werte | 1.036 ms |
| `asset_id = 207`, einzeln | 169 ms |

Nicht linear in der Zahl der Werte, sondern ein Sprung auf einen vollen
Durchlauf. Fünf Einzelabfragen sind schneller als eine Sammelabfrage.

### Jede weitere Bedingung kostet, auch entlang des Primärschlüssels

`price_bar`, Primärschlüssel `(asset_id, interval_code, ts_utc)`:

| | |
| --- | ---: |
| `WHERE asset_id = 231` | 6,8 ms |
| `WHERE asset_id = 232 AND interval_code = '1d'` | 103,6 ms |
| `… AND ts_utc >= '2025-09-27'` | 221,8 ms |

Bei einem Präfix-Index müsste es umgekehrt sein.

### Ohne führende Schlüsselspalte ist eine Abfrage unbrauchbar

| | |
| --- | ---: |
| `count(*) WHERE interval_code='1d' AND ts_utc >= '2024-09-27'` (318.837 Zeilen) | 52.430 ms |
| dieselbe Menge, 3 Spalten ausliefern | 76.245 ms |
| dieselbe Menge, 8 Spalten | 144.685 ms |

52 Sekunden nur zum Zählen. Ein Vorwärmspeicher über eine einzige grosse
Abfrage ist damit nicht machbar; gefüllt wird je Wert.

## Was am Node behoben wurde

Der Ausgabepfad des Drahtprotokolls schrieb **jede Datenzeile einzeln auf den
Socket** und nahm dafür ein **prozessweites** Schreibschloss. Beides ist
behoben (`PgWireServer.cs`).

Der Nachweis, dass es der Ausgabepfad war und nicht der Scan — dieselbe
Tabelle, einmal 100.000 Zeilen zurückgeben, einmal eine, bei *mehr*
Scanaufwand:

| | Wanduhr | Benutzerzeit | Kernzeit |
| --- | ---: | ---: | ---: |
| 100.000 Zeilen zurückgeben | 1.604 ms | 391 ms | **1.172 ms** |
| 1 Zeile zurückgeben (voller Scan) | 2.188 ms | 2.094 ms | **63 ms** |

Die Kernzeit hängt an den **zurückgegebenen** Zeilen, nicht an den gelesenen —
die Signatur eines Systemaufrufs je Zeile.

Wirkung:

| | vorher | nachher |
| --- | ---: | ---: |
| 25.000 Zeilen | 871 ms | 151 ms |
| 100.000 Zeilen | 1.699 ms | 357 ms |
| 200.000 Zeilen | 3.243 ms | 845 ms |
| `SELECT 1` × 100.000 | 1.635 ms | 133 ms |
| 1,26 Mio Zeilen × 3 Spalten | 24.561 ms | 4.908 ms |
| davon Kernzeit | ~19.000 ms | 188 ms |

## Was in der Anwendung geändert wurde

### Ein Fehler, der nichts mit dem Node zu tun hatte

`SqlBulkCopy.BulkCopyTimeout` zählt **Sekunden**,
`NpgsqlCopyTextWriter.Timeout` zählt **Millisekunden**. Beide bekamen dieselbe
Variable. Auf dem SQL Server war der Code damit richtig; auf dem Node lief der
COPY-Schreiber mit 120 Millisekunden Zeitlimit, und ein COPY über 250
Kursbars braucht dort 82 bis 197 ms. Folge: **88 von 88 Werten scheiterten**,
die jüngste Tagesbar stand drei Tage in der Vergangenheit. Nach der Korrektur:
0 Fehler, 708 statt 129 Tagesbars seit dem 25.09.

Der Fehler existierte seit jeher und war auf dem SQL Server unsichtbar.

### Abfragen umgestellt

| Stelle | vorher | nachher |
| --- | --- | --- |
| `GetManyAsync` | `= ANY(@ids)` | je Wert eine Abfrage |
| `GetAccuracyAsync` | `GROUP BY` in SQL | Zeilen holen, in C# summieren |
| `GetHistoryAsync` | CTE mit `ROW_NUMBER()` | zwei eingeschränkte Lesevorgänge |
| `GetLatestAsync` | CTE mit `ROW_NUMBER()` | eine Abfrage, Auswahl in C# |

Dazu Migration 047: `forecast_score` trägt jetzt `asset_id` und
`horizon_hours` selbst. Die Bewertung einer Prognose gehört unveränderlich zu
genau einem Wert und Horizont; das über einen Verbund herzuleiten war auch
unabhängig vom Backend der Umweg. Der Verbund kostete gemessen 14,1 s für
sieben Zeilen.

### Zwei eigene Fehlgriffe, beide durch Messen gefunden

**`QueryUnbufferedAsync` ist nicht sparsam, sondern langsam.** Dapper zahlt je
Zeile eine eigene asynchrone Fortsetzung:

| | 993 Zeilen |
| --- | ---: |
| rohes ADO.NET | 93–151 ms |
| `QueryAsync` (gepuffert) | 68–85 ms |
| `QueryUnbufferedAsync` | **2.108–2.151 ms** |

**Neun kleine Indexsuchen sind schlechter als eine grosse Abfrage** — über
`psql` sah es umgekehrt aus:

| | über psql | über Npgsql |
| --- | ---: | ---: |
| je Horizont eine Suche (9 Abfragen) | ~40 ms | 1.426–1.947 ms |
| eine Abfrage, 2.392 Zeilen | 500 ms | 466–489 ms |

### Zwischenspeicher

Was sich nur ändert, wenn ein Lauf es schreibt, wird im Arbeitsspeicher
gehalten:

| Speicher | Inhalt | verworfen bei |
| --- | --- | --- |
| `Kursspeicher` | Kursbars je Wert und Intervall | `PriceBarRepository.UpsertAsync` |
| `Prognosespeicher` | jüngste Prognosen, Treffsicherheit, Rückblicksverlauf, Rückrechnungsspur | `InsertAsync`, `ScoreAsync`, `BulkWriteAsync`, `ClearAsync` |

Verworfen wird **an den Schreibstellen**, nicht über eine Frist. Eine Frist
wäre entweder zu kurz, um zu sparen, oder zu lang — und dann zeigte die
Ansicht nach dem Stundenlauf alte Kurse.

Gedeckelt wird nach **Zeilen**, nicht nach Einträgen: Ein Wert mit
Stundendaten ist tausendmal so gross wie einer mit einem Jahr Tagesdaten.

Der `Vorwaermer` füllt beide nach dem Start im Hintergrund, in vier
Durchgängen nach Häufigkeit der Ansicht: Tagesbars, Prognosen, Stundenbars,
Rückblick.

### Speicherbereinigung

ASP.NET wählt von sich aus die Server-Variante der Speicherbereinigung: ein
Heap je Kern, späte Sammlung. Nach dem Vorwärmen standen **0,93 GB** im
Arbeitsspeicher für rund 38 MB echte Daten, und der Wert sank auch in Ruhe
nicht. Mit `ServerGarbageCollection=false`: **0,14 GB** bei gleichem Inhalt.

## Ergebnis

### Die Kursansicht, wie der Benutzer sie erlebt

Erster Aufruf je Wert, sechs verschiedene Werte, beide Speicher vollständig
vorgewärmt. Angegeben ist der Median, Millisekunden:

| Aufruf | SQL Server, Anwendung vorher | Node, Anwendung vorher | **Node, heute** |
| --- | ---: | ---: | ---: |
| Chart, 1 Wert, 12 Monate | 21 | 144 | **26** |
| + Prognose | 33 | 3.855 | **16** |
| + Prognose + Rückblick | 43 | 7.339 | **24** |
| Chart, 5 Werte + Prognose | 109 | 23.034 | **70** |
| Chart, 1 Wert, stündlich, 3 Monate | 24 | 173 | **35** |

Gegenüber dem Stand, mit dem diese Arbeit begann, ist der Node in drei von
fünf Fällen schneller als der SQL Server es war, in einem gleichauf und in
einem (stündlich) etwas langsamer. Der Prognosepfad, der mit Faktor 117
begann, liegt heute bei der Hälfte der SQL-Server-Zeit.

### Und die ehrliche Gegenprobe

Die Zwischenspeicher helfen **beiden** Systemen. Wer nur die Spalten „SQL
Server vorher" und „Node heute" vergleicht, vergleicht zwei Änderungen auf
einmal. Deshalb dieselbe Reihe noch einmal, **dieselbe optimierte Anwendung**,
einmal gegen jedes Backend:

| Aufruf | SQL Server | Node |
| --- | ---: | ---: |
| Chart, 1 Wert, 12 Monate | 37 | **26** |
| + Prognose | 27 | **16** |
| + Prognose + Rückblick | 29 | **24** |
| Chart, 5 Werte + Prognose | **68** | 70 |
| Chart, 1 Wert, stündlich, 3 Monate | **22** | 35 |

Auch bei gleicher Ausgangslage liegt der Node in drei von fünf Fällen vorn,
in einem gleichauf, in einem zurück. Die Stundenreihen sind seine schwächste
Stelle — dort liegen je Wert 2.160 Zeilen statt 250, und der Preis je über
den Index gefundene Zeile schlägt entsprechend durch.

### Arbeitsspeicher

Die Auflage war, den Verbrauch des SQL Servers nicht zu überschreiten.

| | SQL Server | Node |
| --- | ---: | ---: |
| Datenbankprozess | **3,34 GB** | **0,42 GB** |
| Anwendung (mit allen Zwischenspeichern) | 0,33 GB | 0,34 GB |
| **zusammen** | **3,67 GB** | **0,76 GB** |

Der Node braucht **ein Fünftel**. Gehalten werden dabei 750.840 Kursbars,
646 Prognosesätze, 646 Güteauswertungen und 646 Rückblicksverläufe. Der
SQL-Server-Prozess ist im Lauf der Messungen von 2,10 auf 3,34 GB gewachsen;
er füllt seinen Pufferbereich, wie er soll.

**Und unter Last**, denn eine Zahl aus dem Leerlauf wäre keine Antwort auf
die Auflage. Gemessen während eines nachgeholten Tageslaufs über 646 Werte,
alle 55 Sekunden, Node-Prozess:

| Zeit | | Zeit | |
| --- | ---: | --- | ---: |
| 00:10:53 | **4,25 GB** | 00:19:10 | 0,71 GB |
| 00:11:48 | 0,62 GB | 00:21:01 | 0,58 GB |
| 00:13:39 | 0,63 GB | 00:22:52 | 0,53 GB |
| 00:15:29 | 0,65 GB | 00:24:42 | 0,55 GB |
| 00:17:20 | 0,66 GB | 00:28:23 | 0,55 GB |

Über den ganzen Lauf **0,53 bis 0,71 GB**, dazu eine einzelne Spitze von
4,25 GB, die binnen einer Minute wieder abgebaut war. Die Anwendung selbst
blieb durchgehend bei 0,13 GB.

Die Spitze ist beobachtet, aber nicht zugeordnet — im Protokoll steht zu
diesem Zeitpunkt nichts Auffälliges. Sie gehört trotzdem hierher: Wer nur
den Mittelwert nennt, verschweigt, dass es sie gibt. Zum Vergleich hält der
SQL-Server-Prozess seine 3,34 GB **dauerhaft**, auch im Leerlauf.

### Was der Node länger braucht

Das Vorwärmen nach dem Start, im Hintergrund:

| Durchgang | SQL Server | Node |
| --- | ---: | ---: |
| Stundenreihen (646 Werte) | 21 s | 117 s |
| Rückblick (646 Werte) | 26 s | 167 s |

Fünf- bis sechsmal so lange — dieselbe Ursache wie überall: der Preis je über
einen Index gefundene Zeile. Für den Benutzer ist das unsichtbar, weil es im
Hintergrund läuft; als Kennzahl ist es der ehrlichste Ausdruck des
verbleibenden Unterschieds.

## Was offen bleibt

Am Node, dort gemeldet und dort zu beheben:

1. **Aggregat und `GROUP BY` reichen die Einschränkung nicht durch** (Faktor
   40). Betrifft nicht nur die Prognosen, sondern jede Kennzahl der Anwendung.
2. **Zeilen werden je Treffer einzeln rekonstruiert** statt gebündelt (60 bis
   832 µs je Zeile gegen 3,3 µs sequentiell). Das ist die Ursache, gegen die
   die Zwischenspeicher nur ein Gegenmittel sind.
3. **`IN (…)` mit vielen Kennungen** skaliert multiplikativ: 2.124 Kennungen
   kosteten 138,6 s, ein Einzelzugriff 1,8 ms.
4. **Der Plattenbedarf** liegt bei 37 GB gegen rund 3 GB beim SQL Server —
   der Arbeitsspeicher ist vorbildlich, die Platte nicht.
5. **Ein `MAX()` ohne führende Schlüsselspalte läuft in den Scan-Schutz.**
   Gemessen am 28.09.2026:

   ```
   SELECT MAX(ts_utc) FROM dbo.price_bar WHERE interval_code = '1d'
   → ERROR: Scan ueber 'price_bar' liefert ueber 1.500.000 Zeilen —
            bitte WHERE/LIMIT einschraenken.
   ```

   Der Schutz ist richtig, er trifft hier nur eine Frage, die billig zu
   beantworten wäre: Der grösste Wert einer indizierten Spalte steht am Rand
   des Index. Betroffen ist die Prognoseansicht (`/api/forecast/stand` und
   `/auffrischen`) und die Kachel „Prognose" der Startseite — nicht die
   Kursansicht. Beide fangen den Fehler ab und zeigen die Kachel als gestört;
   der Zustand bestand schon vor dieser Arbeit.

   Rückmeldung aus der Datenbankentwicklung: Der rückwärts laufende
   Indexzugriff für `MIN`/`MAX` **existiert dort bereits** und war genau für
   diese Form gebaut (einmal 63 s auf Millisekunden). Diese Abfrage erreicht
   ihn nur nicht — ein Weichenproblem, kein fehlendes Verfahren.

In der Anwendung:

5. Die Zwischenspeicher sind **eine Umgehung**, kein Entwurf. Fällt Punkt 2
   am Node, gehören sie auf den Prüfstand — nicht ersatzlos weg, aber ihre
   Grössen und das Vorwärmen wären dann anders zu begründen.
6. Weitere Stellen der Anwendung fahren Aggregate mit Einschränkung und sind
   damit von Punkt 1 betroffen; die Kursansicht ist nur die sichtbarste.
   Besonders `PrognosegueteService.RanglisteAsync` (acht Aggregate über den
   Verbund zweier Grosstabellen, siebenmal hintereinander gerufen),
   `StartEndpoints.PrognoseAsync` und `BriefingService.WartungAsync`.
