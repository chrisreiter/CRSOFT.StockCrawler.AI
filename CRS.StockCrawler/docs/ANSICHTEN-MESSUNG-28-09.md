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
