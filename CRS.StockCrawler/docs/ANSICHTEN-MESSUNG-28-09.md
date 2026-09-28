# Wo die Anwendung auf dem Mesh-Node steht — gemessen am 28.09.2026

Alle lesenden Ansichten, die die Oberfläche tatsächlich aufruft, einmal
durchgemessen. Anlass: Nach zwei Tagen Arbeit an der Kursansicht war der
Eindruck entstanden, das System sei fertig. Es ist ungefähr zur Hälfte fertig,
und ohne diese Messung hätte niemand gewusst, welche Hälfte.

## Bedingungen

Anwendung und Node im Normalbetrieb, Zeitplan an, **kein** Import, beide
Zwischenspeicher vorgewärmt (351 s). Node mit den Fixes vom selben Tag
(Limit-Durchreichung, Zählen ohne Rekonstruktion). Ein Aufruf je Ansicht,
Zeitlimit 60 Sekunden. Die Endpunktliste stammt aus `app.js` — es ist die
Fläche, die ein Benutzer wirklich anfasst, nicht meine Auswahl.

## Ergebnis in einem Satz

**27 von 53 Ansichten sind gut, 11 sind langsam, 15 sind unbrauchbar oder
kaputt.**

## Gut (unter 200 ms)

| Ansicht | |
| --- | ---: |
| Kursspeicher | 7 ms |
| Neuzugänge | 7 ms |
| Grundschwingungen | 8 ms |
| Kurven-Läufe | 8 ms |
| Anbieter | 13 ms |
| Zeitplan | 15 ms |
| Säulen-Verdienst | 15 ms |
| Benutzerliste | 15 ms |
| Deep-Status | 16 ms |
| Herde-Zeitzonen | 17 ms |
| **Chart, 5 Werte + Prognose** | **24 ms** |
| Reasoning-Urteile | 26 ms |
| Güte-Übersicht | 27 ms |
| Läufe | 31 ms |
| Herde-Auslöser | 32 ms |
| Oberflächenzustand | 34 ms |
| Werteliste | 35 ms |
| **Chart, 1 Wert** | **37 ms** |
| Hausmeister | 43 ms |
| Werte-Facetten | 75 ms |
| Analyse-Führende | 82 ms |
| Verfolgte Werte | 103 ms |
| Fluss-Gruppen | 106 ms |
| Fluss-Rotationspaare | 135 ms |
| Einzelwert-Reihe | 142 ms |
| Fluss-Markt | 184 ms |
| Chart, stündlich | 193 ms |

Die Kursansicht ist damit erledigt — und schneller, als sie es auf dem SQL
Server war.

## Langsam (0,5 bis 10 Sekunden)

| Ansicht | | vermutete Ursache |
| --- | ---: | --- |
| Chart + Rückblick | 538 ms | `forecast_track` je Treffer rekonstruiert |
| Depot-Bestand | 1.870 ms | Aggregate über `invest_buchung` |
| Säulengewichte | 2.047 ms | unbeschränktes Aggregat |
| Prognose je Wert | 2.061 ms | Verbund `forecast` × `forecast_score` |
| Autopilot-Rangfolge | 2.120 ms | Aggregate über beide Grosstabellen |
| Treffsicherheit | 4.214 ms | Aggregat ohne Werteinschränkung |
| Fluss-Beiträge | 4.737 ms | `price_bar` mit `SUM` + `GROUP BY` |
| Herde-Umkehr | 7.359 ms | `crossing` (7,2 Mio) mit Aggregaten |
| Reasoning-Journal | **500** nach 8.472 ms | |
| Kurven-Ereignisse | 8.937 ms | `curve_event` mit Sortierung |

## Unbrauchbar oder kaputt

| Ansicht | | |
| --- | ---: | --- |
| **Startseite** | 24.635 ms | eine Kachel läuft in ihre 20-s-Frist |
| Bestandszahlen | 14.051 ms | mehrere unbeschränkte `count(*)` |
| Day-Trading heute | **500** nach 17,6 s | |
| Kurven-Verknüpfungen | 27.075 ms | |
| Kurven-Statistik | 29.067 ms | vier Aggregate + `GROUP BY` über 243.312 Zeilen |
| Prognosestand | **500** nach 34,1 s | `MAX()` ohne einschränkendes WHERE |
| Güte-Lernkurve | **500** nach 34,1 s | |
| Güte-Mischvergleich | **500** nach 36,1 s | drei Tabellen, acht Aggregate |
| Autopilot | **500** nach 45,1 s | |
| Langfrist | **500** nach 57,9 s | `price_bar` `GROUP BY … HAVING` |
| Analyse-Korrelationen | **Zeitlimit** 60 s | |
| Depot-Tausch | **Zeitlimit** 60 s | `price_bar` selbstverbunden, `MAX` + `GROUP BY` |
| Spektral-Moden | **Zeitlimit** 60 s | |
| Spektral-Führende | **Zeitlimit** 60 s | |

Alle Fehler sind derselbe: `Timeout during reading attempt` nach rund 36
Sekunden. Es sind nicht vierzehn Fehler, sondern einer — vierzehnmal
getroffen.

## Das gemeinsame Muster

Fast jede langsame oder kaputte Ansicht fährt **ein Aggregat oder ein
`GROUP BY` über eine grosse Tabelle, oft über einen Verbund**. Fast jede
schnelle Ansicht liest einzelne Zeilen über einen Index — oder kommt aus dem
Zwischenspeicher der Anwendung.

Zwei Sonderfälle mit eigener Wurzel:

1. **Ein Aggregat OHNE Einschränkung ist teuer, unabhängig von der
   Tabellengrösse.** Gemessen an `dbo.invest_buchung` mit **28 Zeilen**:
   `count(*)` ohne Filter 1.771 ms, mit `WHERE depot = 'aktiv'` 2,2 ms.
   Faktor 600 in die falsche Richtung. Davon lebt „Bestandszahlen".

2. **Der erste Zugriff auf eine Tabelle nach einem Node-Neustart ist sehr
   teuer.** Dieselbe 28-Zeilen-Zählung kostete unmittelbar nach dem Neustart
   **166.572 ms**, danach 1.771 ms.

## Was daraus folgt

Für die Datenbankentwicklung, nach Wirkung:

1. **Aggregat/`GROUP BY` über einen Verbund**, wo die Einschränkung auf der
   anderen Tabelle sitzt. Trifft Startseite, Güte-Ansichten, Autopilot,
   Prognose je Wert — die Mehrzahl der kaputten Ansichten.
2. **Aggregat ohne Einschränkung** (fixer Vorlauf ~1,4 s).
3. **Zeilen je Indextreffer rekonstruieren** statt gebündelt. Das ist der
   Rest der Chart-Zeit und der Rückblick.
4. **`MAX`/`MIN` ohne einschränkendes WHERE** läuft in den Scan-Schutz.

Für die Anwendung: **nichts davon ist app-seitig zu lösen, und es soll auch
nicht.** Dieselben Abfragen liefen auf dem SQL Server jahrelang ohne
Beschwerde. Die Zwischenspeicher der Kursansicht sind eine Notmassnahme und
gehören auf den Prüfstand, sobald Punkt 3 fällt.

## Wie wieder gemessen wird

Die Endpunktliste steht in der Sitzungsdokumentation; der Ablauf ist: anmelden,
Sitzung mit einer Probe prüfen (sonst misst man 53-mal einen 401 — auch das ist
hier passiert), dann jede Ansicht einmal mit Zeitlimit. **Vorher die
Vorwärmung abwarten und keinen Import laufen lassen**, sonst misst man das
Umfeld statt der Anwendung.

---

# Zweite Messung, nach dem Deploy vom 28.09. 11:11 Uhr

Dieselben 53 Ansichten, derselbe Ablauf, dieselben Bedingungen — nur mit den
drei Node-Änderungen des Vormittags. Die erste Messung oben ist der
Vergleichsstand.

## Ergebnis in einem Satz

**Acht Ansichten sind deutlich schneller, zwei sind von „kaputt" auf „langsam"
gewandert, keine ist schlechter geworden — und die neun schwersten Fälle stehen
unverändert.**

## Was messbar besser wurde

| Ansicht | vorher | nachher | |
| --- | ---: | ---: | ---: |
| Fluss-Beiträge | 4.737 ms | 41 ms | ×115 |
| Autopilot-Rangfolge | 2.120 ms | 115 ms | ×18 |
| Kurven-Ereignisse | 8.937 ms | 510 ms | ×17 |
| Herde-Umkehr | 7.359 ms | 1.379 ms | ×5,3 |
| Analyse-Korrelationen | Zeitlimit 60 s | 23.523 ms | erstmals eine Antwort |
| Autopilot | **500** nach 45,1 s | 25.841 ms | von kaputt auf langsam |
| Depot-Bestand | 1.870 ms | 965 ms | ×1,9 |
| Analyse-Kreuzungen | 895 ms | 480 ms | ×1,9 |

## Was unverändert kaputt ist

Alle mit demselben Muster — Aggregat oder `GROUP BY` über einen Verbund:

| Ansicht | |
| --- | ---: |
| Langfrist | **500** nach 56,9 s |
| Spektral-Moden | Zeitlimit 60 s |
| Spektral-Führende | Zeitlimit 60 s |
| Güte-Mischvergleich | **500** nach 36,1 s |
| Prognosestand | **500** nach 34,1 s |
| Güte-Lernkurve | **500** nach 34,1 s |
| Kurven-Statistik | 27.048 ms |
| Kurven-Verknüpfungen | 23.949 ms |
| Bestandszahlen | 14.073 ms |
| Startseite | 26.100 ms |

## Eine Zeile dieser Messung war falsch, und zwar in die schmeichelhafte Richtung

Die Startseite stand im Rohprotokoll mit **8 ms** — ein Faktor 3.079 gegenüber
den 24.635 ms der ersten Messung. Das wäre der mit Abstand grösste Erfolg des
Tages gewesen und ist keiner: `/api/start/` hält sein Ergebnis **fünf Minuten**,
und mein eigener Probeaufruf kurz vorher hatte den Speicher gefüllt. Kalt
nachgemessen sind es **26.100 ms**, warm 13 ms.

Gegenüber 24.635 ms ist das unverändert bis minimal schlechter. Die Kachel steht
genau dort, wo sie stand.

**Die Lehre: Ein Messaufbau muss die Zwischenspeicher der Anwendung kennen, die
er durchmisst** — sonst misst er sie statt der Datenbank. Bei den beiden
vorgewärmten Speichern (Kurse, Prognosen) war mir das bewusst, sie sind in
beiden Läufen gleich warm. Den Fünf-Minuten-Speicher des Startseiten-Endpunkts
hatte ich vergessen, obwohl ich ihn selbst gebaut habe. Ein Faktor 3.079 ist
dabei das freundlichste Ergebnis: Er ist so unplausibel, dass er auffällt. Ein
Faktor 3 wäre durchgegangen.

## Speicher: eine Spitze, kein Leck

Während der Messung erreichte der Node **4,92 GB**, später bei einem einzelnen
Startseiten-Aufruf **6,26 GB** — deutlich über den 3,34 GB des SQL Servers und
damit über der Vorgabe. Im 15-Sekunden-Protokoll fällt er danach wieder:

```
11:26:22   3,43 GB
11:26:37   0,89 GB
11:26:52   0,59 GB
…
11:32:45   0,61 GB   (nach dem 6,26-GB-Ausschlag)
```

Es ist also **kein Leck, sondern eine Spitze**, und sie entsteht genau bei den
Abfragen, die am Zwischenzeilen-Budget scheitern. Das ist derselbe Befund von
der Speicherseite: Was 1,5 Millionen Zwischenzeilen materialisiert, braucht
dafür auch den Speicher. Im Ruhezustand liegt der Node bei **0,58 bis 0,61 GB**
gegen 3,34 GB beim SQL Server.

Die Vorgabe „nie grösser als der SQL Server" ist damit im Betrieb eingehalten
und in der Spitze verletzt. Beides gehört in denselben Satz, sonst ist es keine
Aussage.

## Was die Zahlen für die Datenbankentwicklung bedeuten

Die acht Verbesserungen und die zehn offenen Fälle trennen sauber: Verbessert
hat sich, was **je Treffer rekonstruiert** oder **mit Limit gelesen** wird. Nicht
verbessert hat sich **kein einziger** Fall von „Aggregat über einen Verbund".
Das war vor dem Deploy die Vermutung und ist jetzt gemessen — die
Startseiten-Kachel ist der Prüfstein dafür, und ihre zwei Kardinalitäten
(336.753 und 1.203.327, Summe 1.540.080 gegen ein Budget von 1.500.000) sagen
auch, warum: Beide Seiten werden materialisiert, bevor verbunden wird, und die
Summe reisst das Budget um 2,7 %.

---

# Dritte Messung, nach dem Korrektheits-Deploy (`9cd113a`, 28.09. 13:12 Uhr)

Zwei Fixes am Node: die Alias-Auflösung im `ORDER BY` (Befund 11) und das
gebündelte Zusammensetzen der Zeilen. Beide zielen auf **Richtigkeit**, nicht
auf Tempo. Gemessen wurde trotzdem vollständig — eine Korrektur, die nebenbei
etwas verlangsamt, muss man sehen.

## Bedingungen

Node um 13:12:32 mit den neuen Binärdateien gestartet, Anwendung um 13:14:11
mit `Ingest__NachholenNachAusfall=false`. Der Nachholimport war vorher
vollständig durchgelaufen (646 Werte, 0 Fehler, 3.337 Zeilen, 5.825 s). Beide
Zwischenspeicher vorgewärmt, **kein** Import während der Messung, nächster
Stundenlauf erst 54 Minuten später.

## Ergebnis

**Keine einzige Statusverschlechterung.** 43× 200, 7× 500, 2× Zeitlimit, 1×
400 (Analyse-Matrix, die einen Parameter braucht — kein Fehler des Backends).
30 Ansichten unter 200 ms.

Bewegt hat sich:

| Ansicht | vorher | nachher | |
| --- | ---: | ---: | --- |
| Kurven-Statistik | 27.048 ms | **14.712 ms** | −12,3 s |
| Depot-Bestand | 965 ms | **665 ms** | −300 ms |
| Analyse-Kreuzungen | 480 ms | 661 ms | +181 ms |

Der Rest liegt im Rauschen.

## Der unerwartete Nebenertrag

Von einem reinen Korrektheits-Fix war **kein** Tempo-Effekt erwartet. Die
Halbierung der Kurven-Statistik und die Vorwärmzeiten sprechen dafür, dass das
gebündelte Zusammensetzen der Zeilen mehr bringt als gedacht:

| | ursprünglich | vor dem Fix | nach dem Fix |
| --- | ---: | ---: | ---: |
| Kursspeicher vorwärmen | 97 s | 138 s | **92 s** |
| Prognosespeicher vorwärmen | 228 s | 140 s | **107 s** |
| Node nach der Vorwärmung | — | 0,53 GB | **0,23 GB** |

Zusammen 199 s statt 278 s, und der Node braucht danach **weniger als die
Hälfte** des Speichers. Beides passt zum selben Mechanismus: Eine Anweisung je
Zeilenmenge statt einer je Spalte.

## Was die Korrektheit betrifft — der eigentliche Zweck

Der Prüfstein (`npgprobe`, acht Fälle mit eigenem Soll je Fall):

| | vorher | nachher |
| --- | ---: | ---: |
| bestanden | 2 | **7** |
| falsch | 6 | **1** |

Am Symptom, mit dem der Befund sichtbar wurde:

```
vorher:  {"runId":0,     "startedUtc":"0001-01-01T00:00:00"}
nachher: {"runId":11255, "startedUtc":"2026-09-28T09:33:18",
          "finishedUtc":"2026-09-28T11:10:23",
          "okCount":646, "errCount":0, "rowsWritten":3337}
```

Und an der folgenreichsten Stelle der Anwendung, dem Umrechnungskurs des
Depots (`InvestService:966`) — die Abfrage wörtlich wie im Quelltext gegen
dieselbe ohne Alias:

```
mit Alias (wie in der App):  1,13765645 | 28/09/2026 00:00:00
ohne Alias (Referenz):       1,13765645 | 28/09/2026 00:00:00
```

Identisch, und es ist die jüngste Bar. Vorher hätte diese Abfrage einen
beliebigen historischen Wechselkurs liefern können.

## Ein Fall bleibt offen

`SELECT run_id, job_name, started_utc FROM dbo.ingest_run OFFSET 0 ROWS FETCH
NEXT 1 ROWS ONLY` — ohne WHERE, ohne ORDER BY — liefert weiterhin NULL im
Primärschlüssel. Dass die Zeile eine beliebige ist, ist ohne `ORDER BY`
zulässig; ein leerer Primärschlüssel ist es nicht. In der Anwendung kommt
diese Form auf keiner Tabelle mit sparsamen Spalten vor, der Fehler zeigt also
derzeit niemandem eine falsche Zahl.

## Die Lehre dieses Durchgangs

**Der Vorher-Lauf ist nicht Bürokratie, er ist die Kontrolle.** Ich hätte ihn
beinahe übersprungen, weil die Ursache ja bereits gefunden und der Fix bereits
gebaut war. Genau dieser Lauf — am ruhigen Node, ohne jede Schreiblast, mit
allen sechs Fehlern — hat die zwischenzeitliche Erklärung widerlegt, der
Fehler entstehe durch gleichzeitiges Schreiben. Ohne ihn stünde eine plausible,
gut belegte und falsche Ursache in diesem Dokument.

---

# Vierte Messung: der Semi-Join (`a015c05`, 28.09. 13:36 Uhr)

Gezielt die acht Ansichten, auf die der Fix zielt. Nur
`EventMesh.Sql.Node.dll` ersetzt, `EventMesh.Infrastructure.dll` unverändert —
die Zahl ist damit gegen den Korrektheits-Stand isoliert.

## Ergebnis

| Ansicht | vorher | nachher | |
| --- | ---: | ---: | --- |
| Güte-Lernkurve | **500** nach 36,1 s | **200** nach 17,7 s | von kaputt auf langsam |
| Kurven-Verknüpfungen | 29.084 ms | 21.265 ms | −7,8 s |
| Kurven-Statistik | 23.909 ms | 20.716 ms | −3,2 s |
| **Startseite (kalt)** | 26.145 ms | **26.130 ms** | **unverändert** |
| Prognosestand | 500 nach 34,1 s | 500 nach 34,1 s | unverändert |
| Güte-Mischvergleich | 500 nach 36,1 s | 500 nach 34,1 s | unverändert |
| Langfrist | Zeitlimit | Zeitlimit | unverändert |

## Die Kachel, auf die es ankam, hat sich nicht bewegt

Die Startseite scheitert mit **derselben** Meldung wie vorher:
`Query erzeugt ueber 1.500.000 (Zwischen-)Zeilen`. Der Grund steht in der
Abfrage selbst:

```sql
SELECT CAST(COUNT(*) AS INT),
       CAST(SUM(CASE WHEN s.direction_correct = true THEN 1 ELSE 0 END) AS INT)
  FROM dbo.forecast_score s
  JOIN dbo.forecast f ON f.forecast_id = s.forecast_id
 WHERE f.model_version = 'ens-1'
   AND s.scored_at_utc >= (now() - INTERVAL '30 days')
```

**Das ist kein zeilenliefernder Verbund, sondern ein Aggregat über einen
Verbund.** Es gibt keine Join-Ausgabe, die ein Semi-Join reduzieren könnte —
gebraucht werden zwei Skalare. Dieselbe Form haben Prognosestand,
Güte-Mischvergleich und Langfrist. **Vier der fünf hartnäckigen Ansichten sind
Aggregate über Verbünde**, und keine davon hat sich bewegt.

Das deckt sich mit dem Muster der ersten Messung — dort stand schon, dass fast
jede langsame oder kaputte Ansicht ein Aggregat über eine grosse Tabelle fährt,
oft über einen Verbund. Nach vier Messungen ist das die einzige Kategorie, die
sich durch keinen der bisherigen Eingriffe bewegt hat.

## Der Speicher geht in die falsche Richtung

Während derselben acht Ansichten, im Fünf-Sekunden-Takt gemessen:

| | Spitze | Ruhe danach |
| --- | ---: | ---: |
| vor dem Semi-Join | 8,76 GB | 4,66 GB |
| nach dem Semi-Join | **10,48 GB** | 2,74 GB |

Zum Vergleich: SQL Server 3,34 GB, der Node nach der Vorwärmung 0,23 GB.

Ob der Semi-Join das verursacht, ist **nicht** belegt — der Nachher-Lauf hatte
einen Verwerfungsdurchgang vor sich, den der Vorher-Lauf nicht hatte.
Ausschliessen lässt es sich aber auch nicht, und die Richtung stimmt nicht.

**Daraus folgt die wichtigere Einsicht dieses Durchgangs:** Solange eine
einzelne Abfrage zehn Gigabyte ziehen darf, ist jede Tempo-Verbesserung auf
Sand gebaut. Der Node braucht **0,23 GB** für seinen gesamten vorgewärmten
Betrieb und **10,48 GB** für acht Ansichten. Nicht die Dauer dieser Abfragen
ist das eigentliche Problem, sondern dass sie unbegrenzt Speicher belegen
dürfen — die Vorgabe „nie grösser als der SQL Server" ist damit um das
Dreifache gerissen.

Die nächste Arbeit am Backend ist deshalb nicht die nächste Verbund-Variante,
sondern **streamende Aggregation über den Verbund** (Hash auf die kleinere
Seite, die grössere durchstreamen, `COUNT`/`SUM` direkt falten, nie
materialisieren) **und eine Speicherschranke je Abfrage**.

---

# Fünfte Messung: die Speicher-Schranke (`b36aff6`, 28.09. 15:50 Uhr)

Nach der Erkenntnis der vierten Messung — nicht die Dauer der Aggregate ist
das eigentliche Problem, sondern dass sie unbegrenzt Speicher belegen dürfen —
bekam der Node eine harte Grenze: Über 2.800 MB verwalteten Speicher bricht
eine Abfrage **fangbar** ab, statt in Richtung Speichernot zu laufen.

## Sie greift, und das ist sichtbar

Die Startseiten-Kachel bricht jetzt sofort ab:

```
Query zieht ueber 2800 MB verwalteten Speicher — abgebrochen
(Node-Speichergrenze). Bitte WHERE/LIMIT einschraenken.
```

**500 nach 76 ms** statt 26 Sekunden Mahlen. Für sich genommen ein Gewinn: Die
Abfrage verbrennt keine halbe Minute mehr, bevor sie aufgibt.

## Sie hält die Grenze trotzdem nicht

Dieselben acht Ansichten, Fünf-Sekunden-Takt:

| | Spitze |
| --- | ---: |
| vor dem Semi-Join | 8,76 GB |
| mit Semi-Join | 10,48 GB |
| **mit Schranke** | **9,68 GB** |

Acht Prozent besser, und weiterhin das Dreifache der Vorgabe von 3,34 GB.

## Warum — zwei Kandidaten ausgeschlossen, einer bleibt

Am selben Prozess kurz nacheinander:

```
Spitze waehrend der Abfragen     9,68 GB
WorkingSet64 kurz danach         3,36 GB
PrivateMemorySize64              3,34 GB
im Leerlauf nach der Vorwaermung 0,23 GB
```

1. **Kein Leck.** Der Speicher wird freigegeben; die Spitze ist transient.
2. **Nicht die Server-Speicherbereinigung.** `eventmesh-sql.runtimeconfig.json`
   führt `System.GC.Server: false` — Workstation-GC läuft bereits. (In der
   Anwendung war genau das der Hebel, 0,93 auf 0,14 GB; deshalb zuerst dort
   nachgesehen.)

Was bleibt: **Die Schranke misst den verwalteten Heap je Abfrage, die Vorgabe
gilt für den Arbeitssatz des Prozesses.** Dazwischen liegen native Puffer, die
Caches der Speicher-Engine und vor allem freigegebener, aber nicht an das
Betriebssystem zurückgegebener Speicher. Eine Grenze von 2,8 GB je Abfrage
kann einen Arbeitssatz von 9,68 GB erzeugen, ohne je auszulösen — bei mehreren
Abfragen hintereinander erst recht.

## Was daraus folgt

**Die Schranke behandelt das Symptom und ist trotzdem richtig** — als Netz.
Sie soll bleiben, und ihr Wert soll NICHT heruntergesetzt werden: 2,8 GB je
Abfrage ist plausibel, und tiefer bräche Abfragen ab, die heute funktionieren.

Die Massnahme, die den Speicher gar nicht erst entstehen lässt, ist die
**streamende Aggregation über den Verbund**: einen Hash nur über die
Join-Schlüssel der reinen Filterseite bauen, die andere Seite durchstreamen
und `COUNT`/`SUM` direkt falten. Speicherbedarf ist dann Hash plus
Akkumulatoren statt der vollen Zwischenmenge — und sie ist damit Tempo und
Grenze zugleich.

---

# Sechste Messung: die streamende Aggregation — und die Korrektur meines
# eigenen Messaufbaus

Die streamende Aggregation (`8bf94a7`) wurde deployed, gemessen, **für kaputt
befunden und zurückgerollt** — und dieses Urteil war falsch. Der Weg dorthin
gehört in dieses Dokument, weil er die vorherigen fünf Messungen relativiert.

## Was zuerst gemessen wurde

Mit `8bf94a7` lieferten sieben von acht Ansichten 500, vier davon waren
vorher in Ordnung. Naheliegende Ursache geprüft und ausgeschlossen: Die
Speicher-Schranke testweise von 2,8 auf 6 GB angehoben — dasselbe Bild. Also
zurückgerollt.

**Nach der Rückrollung war es genauso schlecht.** Derselbe Node-Stand, der
eine Stunde vorher vier Ansichten mit 200 geliefert hatte, lieferte jetzt
acht Mal 500.

## Die Ursache: der Messaufbau selbst

Der **zweite Durchgang ist schlechter als der erste**. Im Aufwärmdurchgang
lieferten Kurven-Statistik, Kurven-Verknüpfungen und Bestandszahlen noch
Ergebnisse; im Messdurchgang unmittelbar danach alle drei 500. Der Heap
schrumpft zwischen den Abfragen nicht, und die Speicher-Schranke greift mit
jedem weiteren Durchgang früher.

**Acht schwere Ansichten hintereinander sind ein anderer Test als acht
einzelne.** Alle Sequenz-Messungen dieses Dokuments — auch die Spitzen von
8,76, 10,48 und 9,68 GB — messen deshalb zu einem Teil die Reihenfolge und
nicht die Ansicht.

## Die saubere Messung: ein frischer Node je Ansicht

| Ansicht | `b36aff6` | `8bf94a7` (Streaming) | |
| --- | --- | --- | --- |
| Bestandszahlen | 200 / 27,69 s / 0,74 GB | **200 / 13,25 s / 0,15 GB** | halbe Zeit, ein Fünftel Speicher |
| Startseite | 200 / 39,95 s / 0,63 GB | **200 / 24,07 s / 0,67 GB** | 40 % schneller |
| Güte-Lernkurve | 200 / 15,60 s / 1,10 GB | 200 / 15,90 s / 1,08 GB | unverändert |
| Prognosestand | 500 / 34,06 s / 3,76 GB | 500 / 47,64 s / 3,92 GB | greift nicht |
| Güte-Mischvergleich | 500 / 47,97 s / 4,41 GB | 500 / 47,76 s / 4,57 GB | greift nicht |

**Die streamende Aggregation ist also eine Verbesserung**, und Bestandszahlen
ist ihr sauberster Beleg: halbe Zeit bei einem Fünftel des Speichers. Dass
Prognosestand und Güte-Mischvergleich nicht profitieren, war vorhergesagt —
sie haben drei Tabellen beziehungsweise `GROUP BY` und nehmen den
Rückfallpfad.

## Der wichtigste Einzelbefund dieser Messung

**Eine einzelne schwere Ansicht kostet 0,15 bis 1,10 GB.** Die 9,68 und
10,48 GB entstanden ausschliesslich dadurch, dass acht davon hintereinander
liefen. Die Vorgabe von 3,34 GB wird von einer einzelnen Ansicht **nicht**
gerissen — mit genau zwei Ausnahmen, und die sind der verbleibende harte
Rest: Prognosestand 3,92 GB und Güte-Mischvergleich 4,57 GB.

Damit ist die Empfehlung aus der fünften Messung („Speicherschranke, weil
eine einzelne Abfrage zehn Gigabyte ziehen darf") **in dieser Schärfe
falsch**. Keine einzelne Abfrage zieht zehn Gigabyte. Die Schranke bleibt
trotzdem sinnvoll — als Netz für die zwei Ansichten, die es einzeln tun.

## Ein zweiter Fehler im Protokoll, gefunden und behoben

Der erste Einzeltest meldete für die Güte-Lernkurve „500 nach 36 ms" mit
`Timeout during reading attempt`. Das sah nach einem Befund aus und war der
Aufbau: **Nach einem Node-Neustart hält der Verbindungspool der Anwendung
tote Verbindungen, und die erste Anfrage scheitert sofort.** Seither steht im
Protokoll ein Verwurfsaufruf nach jedem Neustart; dieselbe Ansicht lieferte
danach 200 in 15,6 s.

## Die Lehre

**Ein Messaufbau muss gegen sich selbst kontrolliert werden.** In dieser
Datei steht seit Wochen, dass eine Hintergrundaufgabe gegen den BETRIEB
gemessen werden muss und nicht gegen ein ruhiges System. Hier war die
störende Hintergrundlast der eigene vorherige Messdurchgang — und der
Störfaktor war nicht das Umfeld, sondern die Methode.
