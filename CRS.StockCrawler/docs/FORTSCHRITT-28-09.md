# Was sich am 28.09.2026 geändert hat — und was der Tag gekostet hat

Ein Tag Arbeit an der Frage „warum ist die Anwendung auf dem Mesh-Node
langsam". Die Antwort war am Ende eine andere als die Frage.

## Das Wichtigste in drei Sätzen

Der grösste Fund des Tages ist **kein Tempoproblem, sondern ein
Korrektheitsfehler**: Ein Spaltenalias hat das `ORDER BY` still ausgehebelt,
und 47 Abfragen dieser Anwendung waren betroffen. Die Tempoarbeit hat
gemessen etwas gebracht — die Vorwärmung fiel von 278 auf 199 Sekunden, die
Startseite von 40 auf 24 Sekunden, Bestandszahlen von 28 auf 13 Sekunden bei
einem Fünftel des Speichers. Und **zwei meiner eigenen Messungen waren
falsch**, beide aus demselben Grund: Ich habe den Messaufbau nicht gegen sich
selbst geprüft.

## 1. Der Korrektheitsfehler (Befund 11)

Ein `AS`-Alias auf der **ersten** projizierten Spalte liess das `ORDER BY`
verschwinden:

```sql
SELECT run_id,      job_name, started_utc … ORDER BY run_id DESC FETCH NEXT 1
  → 11250 | update:1h | 28.09. 09:08      richtig, jüngster Lauf

SELECT run_id AS X, job_name, started_utc … ORDER BY run_id DESC FETCH NEXT 1
  → 11126 | update:1h | 25.09. 11:07      falsche Zeile, drei Tage alt
```

Die Werte sind in sich stimmig — es ist keine kaputte Zeile, sondern die
**falsche**. Kein Fehler, Status 200. Die Sortierung wird nicht falsch
ausgeführt, sondern verworfen.

**Warum er so lange unsichtbar war:** Bei `WHERE` auf den Primärschlüssel
stimmt alles, denn wo genau eine Zeile herauskommt, ist Sortierung
bedeutungslos. Das ist der Normalfall der meisten Abfragen.

**Die Reichweite:** 47 Abfragen, darunter acht der Form „jüngste Zeile" —
letzter Lauf, jüngste Prognose, letzter Kurs, neuester Kurvenlauf. Und die
Werteliste selbst, deren Spaltenliste mit `asset_id AS AssetId` beginnt: Sie
kam trotz `ORDER BY market_cap_rank` in beliebiger Reihenfolge zurück.

**Der Beleg an der folgenreichsten Stelle** — dem EUR/USD-Kurs, mit dem das
Depot umrechnet:

```
mit Alias (wie in der App):  1,13765645 | 28/09/2026    nach dem Fix
ohne Alias (Referenz):       1,13765645 | 28/09/2026
```

Identisch, und es ist die jüngste Bar. Vorher hätte das Depot mit einem
beliebigen historischen Wechselkurs gerechnet.

Behoben in der Datenbankentwicklung. Prüfstein vorher 2 von 8 bestanden,
nachher 7 von 8.

## 2. Was am Tempo gemessen besser wurde

| | vorher | nachher |
| --- | ---: | ---: |
| Vorwärmung Kursspeicher | 138 s | **92 s** |
| Vorwärmung Prognosespeicher | 140 s | **107 s** |
| Node nach der Vorwärmung | 0,53 GB | **0,23 GB** |
| Startseite | 39,95 s | **24,07 s** |
| Bestandszahlen | 27,69 s / 0,74 GB | **13,25 s / 0,15 GB** |
| Kurven-Statistik | 27,0 s | **14,7 s** |
| Güte-Lernkurve | 500 nach 36 s | **200 nach 15,6 s** |
| Fluss-Beiträge | 4.737 ms | **41 ms** |
| Autopilot-Rangfolge | 2.120 ms | **115 ms** |
| Kurven-Ereignisse | 8.937 ms | **510 ms** |

Drei Node-Änderungen tragen das: die gebündelte Zeilenrekonstruktion (eine
Anweisung statt einer je Spalte), der Semi-Join und die streamende
Aggregation über Verbünde.

**Anwendungsseitig:** Der Kursspeicher wurde bei jedem Schreibvorgang
verworfen. Der Tageslauf schreibt für alle 646 Werte, also war er danach
vollständig leer — und Analyse, Prognose und Autopilot holten unmittelbar
darauf 318.590 Bars einzeln wieder aus der Datenbank. Gemessen: **92 Sekunden
für Daten, welche die Anwendung selbst gerade geschrieben hatte.** Er wird
jetzt fortgeschrieben statt weggeworfen; im Betrieb belegt (646 Reihen vor
dem Schreibvorgang, 646 danach, eine Ergänzung). Kein zusätzlicher Speicher —
dieselbe Datenmenge unter demselben Budget, sie wird nur nicht verworfen.

## 3. Der Import

```
update:1d: 646 ok, 0 Fehler, 3.337 Zeilen in 5.825 s
```

Null Poolfehler über 97 Minuten Dauerschreiblast, Node durchgehend zwischen
0,58 und 0,80 GB. **Schreiben ist auf diesem Backend billig** — das ist der
klarste Einzelbefund des Tages und die Gegenprobe zu allem, was langsam ist.

## 4. Was offen bleibt

**Zwei Ansichten reissen die Speichervorgabe einzeln:**

```
Prognosestand         500 / 47,6 s / 3,92 GB
Guete-Mischvergleich  500 / 47,8 s / 4,57 GB
```

Beide haben drei Tabellen beziehungsweise `GROUP BY` und nehmen den
Rückfallpfad, auf dem die streamende Aggregation nicht greift. Das ist der
verbleibende harte Rest.

**Die Startseiten-Kachel liefert weiterhin nichts** — aber aus einem anderen
Grund als heute früh. Sie ist von „sprengt das Zeilenbudget" auf „braucht
mehr als 20 Sekunden" gewandert; die 20 Sekunden sind ihre eigene Frist,
damit eine langsame Quelle nicht die ganze Seite aufhält. Der nächste Hebel
ist, beim Aufbau des Schlüssel-Hashes nur die Schlüsselspalte zu lesen statt
vollständiger Zeilen.

**Ein Kandidat, den ich bewusst NICHT gebaut habe:** Der Prognosespeicher
wird beim Schreiben ebenso verworfen wie der Kursspeicher, und das kostet
gemessen 107 Sekunden je Prognoselauf. Fortschreiben wäre dort aber nicht
ohne Weiteres sicher: Geschrieben wird je Wert und Horizont **einzeln**,
während „Neueste" die vollständige Menge über alle Horizonte ist. Ein
abgebrochener Lauf würde eine unvollständige Menge als aktuell
zwischenspeichern — eine falsche Prognose, die aussieht wie eine frische.
Der Nutzen ist gemessen, das Risiko benannt; gebaut wird es, wenn jemand die
Vollständigkeit sauber signalisieren kann.

## 5. Zwei eigene Fehler, und beide haben dieselbe Wurzel

**Der erste: Ich habe eine Sequenz gemessen und die Zahlen einzeln
zugeordnet.** Acht schwere Ansichten hintereinander — der Speicher schrumpft
zwischen ihnen nicht, die Schranke greift mit jedem Durchgang früher, der
zweite Durchgang war messbar schlechter als der erste. Daraus habe ich
gemeldet, der Node reisse die Speichervorgabe dreifach (9,68 GB) und die
streamende Aggregation sei eine Verschlechterung („sieben von acht kaputt").

Mit einem frischen Node **je Ansicht** gemessen:

| | Sequenz | einzeln |
| --- | ---: | ---: |
| Startseite | 500 | 200 / 0,63 GB |
| Bestandszahlen | 500 | 200 / 0,15 GB |
| Güte-Lernkurve | 500 | 200 / 1,08 GB |
| RAM-Spitze | 9,68 GB | **0,15 bis 1,10 GB** |

Beide Meldungen waren falsch. Die streamende Aggregation ist eine
Verbesserung, und die Speichervorgabe wird von einer einzelnen Ansicht nicht
gerissen — nur von den zwei oben genannten.

**Der zweite: Mein Einzeltest-Protokoll erzeugte selbst Phantomfehler.** Nach
einem Node-Neustart hält der Verbindungspool der Anwendung tote Verbindungen;
die erste Anfrage scheitert sofort mit `Timeout during reading attempt`. Das
sah aus wie ein Befund („500 nach 36 ms") und war mein Aufbau. Seither ein
Verwurfsaufruf nach jedem Neustart.

**Die gemeinsame Wurzel:** In beiden Fällen habe ich den Messaufbau nicht
gegen sich selbst kontrolliert. In `CLAUDE.md` steht seit Wochen, dass eine
Hintergrundaufgabe gegen den BETRIEB gemessen werden muss und nicht gegen ein
ruhiges System — hier war die störende Hintergrundlast mein eigener
vorheriger Messdurchgang.

## 6. Und eine Lehre über die Messmethode selbst

Die Ansicht „Läufe" stand in zwei aufeinanderfolgenden Messungen unter „gut":
31 ms, dann 24 ms, beide Male Status 200. Sie lieferte die ganze Zeit leere
Zeilen — `{"runId":0,"startedUtc":"0001-01-01T00:00:00"}`.

**Ein Messaufbau, der Statuscode und Dauer prüft, kann eine falsche Antwort
per Bauart nicht sehen.** Die Aussage „27 von 53 Ansichten sind gut" ist
deshalb eine Aussage über **Tempo**, nicht über Richtigkeit — und sie war so
nicht gemeint. Wer das nächste Mal misst, braucht neben Status und Dauer eine
Prüfung auf Plausibilität des Inhalts, und sei es nur „ist das erste Feld
nicht leer".

## 7. Der letzte Schritt: Key-Only-Build-Hash

Der Verbund-Aufbau rekonstruierte 1,2 Millionen Prognosezeilen vollständig,
nur um eine einzige Spalte zu ziehen. Mit später Materialisierung liest er
nur noch die Schlüsselspalte.

**Beide Stände nacheinander im selben Fenster, gleicher Datenbestand, je
frischer Node:**

| Ansicht | ohne Key-Only | mit Key-Only | |
| --- | --- | --- | --- |
| Startseite | 37,87 s / 0,85 GB | **13,52 s / 0,15 GB** | 2,8× schneller |
| Bestandszahlen | 28,20 s / 0,77 GB | **14,14 s / 0,15 GB** | 2,0× schneller |
| Güte-Lernkurve | 15,12 s / 1,11 GB | 29,35 s / 1,08 GB | 2× langsamer |

Der Speicher bestätigt den Mechanismus: 0,85 auf 0,15 GB. Weniger Spalten
lesen heisst weniger rekonstruieren heisst weniger Speicher. **Netto klar im
Plus**, deshalb bleibt er im Einsatz; die Güte-Lernkurve ist der benannte
Preis.

## 8. Die Startseite: eine andere Diagnose als gedacht

Die Seite kam auf 13,5 Sekunden und damit unter die 20-Sekunden-Frist ihrer
Kacheln. Ein kalter Aufruf danach brauchte jedoch 26,2 Sekunden, und dabei
fielen **alle sechs Kacheln** aus — auch „Markt", „Depot" und
„Nachrichten", die mit dem Prognose-Verbund nichts zu tun haben.

**Damit ist das Problem der Startseite nicht eine Kachel mit einem schlechten
Verbund, sondern die Gleichzeitigkeit.** Die Seite feuert acht Abfragen
parallel gegen den Node; unter dieser Last verfehlt jede ihre eigene Frist.
Eine einzelne dieser Abfragen ist schnell genug — acht gleichzeitig sind es
nicht.

Das deckt sich mit dem Sequenzbefund aus Abschnitt 5 und ist dessen
Parallel-Variante. Wer die Startseite reparieren will, hat zwei Wege: die
Kacheln nacheinander statt gleichzeitig holen, oder dem Node beibringen,
gleichzeitige Abfragen besser zu verschränken. Der erste Weg ist
anwendungsseitig und billig, der zweite gehört in die Datenbank.

## 9. Was diese Zahlen wert sind — und was nicht

**Absolutwerte über die Zeit tragen an diesem System nicht.** Dieselbe
Abfrage auf demselben Stand lag um 17:05 bei 24,07 s und um 17:36 bei
37,87 s. Dazwischen hatte ein abgebrochener Stundenlauf Daten geschrieben;
der Bestand war gewachsen.

Drei Störquellen haben an diesem Tag je eine Aussage verdorben:

1. **Die Sequenz** — acht schwere Ansichten hintereinander degradieren
   einander. Abgefangen durch einen frischen Node je Messung.
2. **Der Zeitplan** — ein mitlaufender Stundenlauf. Abgefangen durch einen
   Wachhund im Messskript, der abbricht, wenn im Protokoll ein Lauf steht.
3. **Der wachsende Bestand** — dagegen hilft kein Skript, sondern nur die
   Regel: **Zwei Stände vergleicht man hintereinander im selben Fenster, nie
   gegen eine Zahl von vorhin.**

Die Tabelle in Abschnitt 7 ist so gemessen und deshalb belastbar. Einzelne
Absolutzahlen aus früheren Abschnitten gelten nur für ihren Zeitpunkt.
