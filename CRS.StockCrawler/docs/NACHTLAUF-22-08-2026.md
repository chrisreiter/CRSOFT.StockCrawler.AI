# Nachtlauf vom 22. August 2026

**CRSOFT.StockCrawler** — Prüfung aller Säulen, Bereinigung, Verdrahtung der
Gewichte, Journal.

---

## 1. Alle Säulen geprüft

28 Prüfungen über sieben Säulen und den Zeitplan. **25 in Ordnung**, drei
Ausreißer — und die deckten zwei echte Fehler auf.

### Falsche API-Pfade lieferten HTTP 200

Der Rückfall auf die Startseite galt auch für `/api/*`. Ein vertippter Pfad
bekam damit die HTML-Seite mit **Status 200** statt 404 — und der eigene
Prüflauf meldete drei falsche Pfade als bestanden.

Das ist die unangenehmste Art von Fehler: Ein Test, der nur auf den Statuscode
sieht, wird bestätigt. `/api/{**rest}` fällt jetzt auf 404.

### 12.250 verwaiste Vektoren

18.875 Vektoren in Qdrant gegen 6.625 Abschnitte in SQL. Ursache: Bei der
Umstellung auf die inhaltsabhängige Kennung (`VerarbeitungsFassung = 2`) bekam
jeder Abschnitt eine **neue** Kennung. Der Eintrag in SQL wurde überschrieben,
der alte Punkt in Qdrant nicht — er hatte eine Kennung, die kein neuer Lauf je
wiedertrifft.

Die Folge war still: Die Suche fand solche Punkte, schlug in SQL nach, fand
nichts, und der Treffer verschwand aus der Liste. Auf „Herdenverhalten" kam
**ein** Treffer statt vier. Keine Fehlermeldung, nur weniger Ergebnisse.

Behoben an zwei Stellen: Beim Schreiben werden abgelöste Kennungen mitgeführt
und danach entfernt; `POST /api/knowledge/aufraeumen` gleicht beide Seiten ab.

---

## 2. 39 tote Werte abgeschaltet

Von 324 verfolgten Werten:

| | |
| ---: | --- |
| 12 | keine einzige Tagesbar |
| 22 | letzte Bar älter als 30 Tage |
| 2 | Kurs unter einem Promille des Hochs |
| 17 | weniger als 250 Tagesbars |
| **39** | **betroffen insgesamt** |

Sie beherrschten die Tagesübersicht mit Scheinbefunden: JUP-USD meldete eine
Steigung von **−58 Prozent je Bar** bei Kurs 0,0000 und Stufe 100 von 100.
Formal richtig — gemessen an der üblichen Schwankung dieser Reihe *ist* das
außergewöhnlich. Nur beschreibt es das Ende eines Wertes, nicht das Verhalten
eines Marktes.

`GET /api/hygiene/stumm` findet sie mit vier getrennt benannten Gründen,
`POST /api/hygiene/stumm/abschalten` nimmt sie aus der Verfolgung — mit
Probelauf als Voreinstellung, denn das ändert jede Auswertung über „alle Werte".

Danach: 285 verfolgte Werte, Auswertungen neu gerechnet.

---

## 3. Die Säulengewichte waren wirkungslos

**Der schwerwiegendste Befund der Nacht.** Die Regler in der Oberfläche wurden
gespeichert und von nichts gelesen — die Prognose kam allein aus
`ForecastService`/`Ensemble` mit dessen eigenen gelernten Gewichten. Wer an
ihnen zog, änderte nichts an den Zahlen im Diagramm.

Das ist die schlimmste Art von Bedienelement: eines, das aussieht, als täte es
etwas.

### Wie jetzt gemischt wird

Nicht nach Gewicht allein, sondern nach **Gewicht × Verdienst**. Der Verdienst
kommt aus dem Sperrbereich:

- **Ensemble**: aus der Trefferquote der bereits bewerteten Prognosen dieses
  Horizonts, 0,523 als Nullpunkt
- **Deep Learning**: null, solange das Band die blosse Drift nicht schlägt

Hat **keine** Säule Verdienst, zählt das eingestellte Gewicht allein — und die
Antwort sagt dazu, dass der Rückhalt fehlt. Der erste Entwurf mittelte
gleich; das war falsch, denn es nimmt dem Nutzer die Kontrolle genau dort, wo
das System ihm nichts Besseres anzubieten hat.

### Nachgewiesen

NVDA, ein Tag voraus:

| Gewichte | Ergebnis |
| --- | ---: |
| learning 100, deep 0 | −0,4504 % |
| learning 50, deep 50 | −0,1895 % |
| learning 0, deep 100 | +0,0720 % |

Bei einer Stunde bleibt es bei −0,0289 % unabhängig vom Regler — dort hat das
Ensemble Verdienst 0,48 und setzt sich durch, und das Deep-Modell hat diesen
Horizont gar nicht.

Im Diagramm wirken die Gewichte über den Schalter **„Säulengewichte anwenden"**.

### Und dabei ein Leistungsproblem

Erster Messwert: **29 Sekunden für einen Wert, 130 für fünf.** Ursache:
`BuildWindowAsync` lädt die Kurse *aller* verfolgten Werte — das ist die Natur
der Sache, denn die Merkmale eines Wertes enthalten den Markt um ihn herum. Bei
fünf Werten und drei Bändern sind das fünfzehn Ladungen derselben Daten.

Ein Zwischenspeicher mit fünf Minuten Haltbarkeit, verworfen nach jedem
Kursabruf:

| | vorher | nachher |
| --- | ---: | ---: |
| ein Wert | 29 s | 11 s |
| fünf Werte | 130 s | **4 s** |

---

## 4. Kein kleineres Reasoning-Modell

`nemotron3:33b` braucht ein bis zwei Minuten je Runde. Geprüft wurden die
lokal vorhandenen Alternativen:

| Modell | Werkzeugaufrufe | Runde 1 | Runde 2 |
| --- | --- | ---: | ---: |
| nemotron3:33b | ja | 98 s | 20 s |
| qwen3-vl:8b | ja | 68 s | 40 s |
| qwen3-vl:4b | ja | 25 s | 33 s |
| granite3.2-vision | **nein** | — | — |

`qwen3-vl:4b` sah nach der Antwort aus — halb so langsam, Werkzeuge
funktionieren. **Im Einsatz hat es die Aussage umgedreht:**

> *„Fehlerrate von 1,0034 (nahezu perfekt) […] Schlägt die Annahme, dass sich
> nichts ändere […] Ideal für kurze Handelssignale"*

Ein Fehlerverhältnis über 1 heißt das genaue Gegenteil, und das Werkzeug hatte
es wörtlich so geliefert. Ein Modell, das Verneinungen und Schwellen nicht
hält, ist in dieser Anwendung **gefährlicher als ein langsames** — die ganze
Skepsis-Mechanik ist darauf gebaut, dass die Einschränkung mitgesagt wird.

Bleibt der Weg über mehr Rechenleistung.

---

## 5. vast.ai angebunden

Nach dem Vorbild aus `docuproc`: Ollama-Endpunkte mit **SSH-Tunnel im eigenen
Prozess**, ohne `ssh.exe`.

**Warum ein Tunnel.** Der nach außen abgebildete Port einer gemieteten Maschine
spricht reines HTTP — Fragen und Analysewerte gingen im Klartext durchs Netz.
Die Alternative, der Nutzer hält ein SSH-Fenster offen, ist keine Lösung,
sondern eine Fehlerquelle.

Der lokale Port kommt vom Betriebssystem. Eine feste Nummer würde mit einem
lokal laufenden Ollama auf 11434 kollidieren — und genau das ist der Normalfall.

```
GET    /api/ollama/endpunkte
POST   /api/ollama/endpunkte      {Id, Name, SshHost, SshPort, NutztTunnel, …}
POST   /api/ollama/waehlen?id=…
POST   /api/ollama/pruefen?id=…
DELETE /api/ollama/endpunkte/{id}
```

Reasoning **und** Einbettung holen ihre Adresse je Aufruf vom Endpunktdienst —
eine einmal gesetzte `BaseAddress` liesse sich später nicht mehr wechseln.

Der private Schlüssel wird unter `{app}\secrets` und `~/.ssh` gesucht
(`id_ed25519`, `id_rsa`, `id_ecdsa`).

> Bei vast.ai gilt ein Kontoschlüssel nur für **neu erstellte** Instanzen. Eine
> laufende bekommt ihn nicht nachträglich — bei „Permission denied" hilft nur
> eine neue Instanz.

---

## 6. Kurvendiskussion: zentriert kann „heute" nicht

Die jüngste gefundene Stelle war vom **11. August**, die jüngste Kursbar vom
**22.** Elf Tage Lücke — genau die halbe Fensterbreite.

Ein zentriert geglätteter Lauf kann die letzten `halbfenster` Bars
**grundsätzlich** nicht bewerten: Sein Fenster reicht dort über das Ende der
Reihe hinaus. Ein frisch gerechneter zentrierter Lauf hilft nicht.

Die Tagesübersicht nimmt jetzt den jüngsten **kausalen** Lauf. Der reicht bis
zum 21. August und liefert 52 Stellen ab Stufe 70 aus den letzten vierzehn
Tagen.

Dazu: Ein einzelner Einbruch löst mehrere Arten zugleich aus — Sprung,
Steigungsausbruch und Abbremsen beschreiben dieselbe Bewegung von drei Seiten.
Gebündelt wird je Wert und Tag; die übrigen Arten stehen dahinter.

---

## 7. Tagesübersicht und Journal

Beide auf der Reasoning-Seite, beide **ohne Sprachmodell**. Jede Zahl stammt aus
einer Abfrage; der Satz drumherum ist Vorlage, nicht Erzeugnis. Nach dem Befund
aus Abschnitt 4 ist das keine Stilfrage.

**Die Übersicht** gewichtet jeden Punkt mit *Auffälligkeit × Gewicht der Säule*.
Ohne das stünde ein Kurvenereignis der Stufe 100 neben einer Modellvorhersage,
die nachweislich nichts taugt, und beide sähen gleich wichtig aus.

**Das Journal** liefert Markdown zum unmittelbaren Weiterverwenden. Drei
Mängel, die beim ersten Lauf auffielen und behoben sind:

- Dieselbe Meldung erschien mehrfach — Reuters liefert sie, MarketWatch
  übernimmt sie, Yahoo spiegelt beide. Jetzt je Schlagzeile ein Eintrag.
- Die Ähnlichkeitssuche zu SCCO fand eine Analyse über **Konsumgüter-ETFs**.
  Sie liefert immer etwas, denn sie ordnet nach Nähe, nicht nach Eignung.
  Schwelle 0,55; darunter steht, dass nichts Passendes gefunden wurde und wie
  weit der nächstgelegene Text entfernt war.
- SHIB stand mit „Kurs 0,0000" da. Stellen richten sich jetzt nach der
  Größenordnung.

**Keine Handelsempfehlung** — nicht aus Vorsicht, sondern weil die Messung sie
nicht trägt.

---

## 8. Der Optimierungsversuch — und was er ergab

Aus den Messungen folgte genau ein Eingriff: **die Drift vor dem Lernen aus der
Zielgröße nehmen.** Wenn sie den größten Teil ausmacht, verbraucht das Netz
seine Kapazität darauf, sie nachzubilden.

| Horizont | roh, Epoche 1 | entdriftet, Epoche 1 |
| ---: | ---: | ---: |
| 10 | 1,0454 | **1,0354** |
| 20 | 1,0632 | **1,0417** |
| 60 | 1,0935 | **1,0757** |

Ein kleiner, durchgängiger Gewinn — und dann Überanpassung: 1,0509 → 1,1068 →
1,1856. Der frühe Abbruch hält Epoche 1.

Im Sperrbereich:

| Horizont | Modell | Richtung | blosse Drift | Richtung |
| ---: | ---: | ---: | ---: | ---: |
| 10 | 1,0567 | 52,2 % | **0,9960** | **55,5 %** |
| 20 | 1,0531 | 52,1 % | **0,9927** | **57,5 %** |
| 60 | 1,0058 | 58,9 % | **0,9757** | **63,4 %** |

**Die Drift schlägt es weiterhin in jedem Horizont, auf beiden Maßen.**

Der Befund hat sich damit verschoben, und das ist der eigentliche Ertrag der
Nacht. Vorher: *Die Modelle haben die Drift gelernt.* Jetzt: **Über die Drift
hinaus steckt in diesen Merkmalen nichts Lernbares.** Das ist die schärfere
Aussage, und sie sagt auch, wo es nicht weitergeht — nicht in einem größeren
Netz, nicht in mehr Epochen.

## 9. Der Fragenkatalog

Acht Fragen, jede mit einer Prüfung, die nicht liest sondern nachrechnet.
Erster Durchlauf **drei von acht** — und jeder Fehler war meiner, nicht der des
Modells:

| Befund | Behebung |
| --- | --- |
| `wissen` und `nachrichten` nicht unterscheidbar beschrieben | Beschreibungen sagen, wofür das *andere* zuständig ist |
| sechsfach derselbe Aufruf ohne Fortschritt | gespeichertes Ergebnis statt Wiederholung |
| Modell schickt `wert` statt `symbol` | sechs Namensvarianten, notfalls ohne Rücksicht auf Groß-/Kleinschreibung |
| „Kein verfolgter Wert mit dem Symbol ." | Meldung nennt das erwartete Feld, verweist auf `werteliste` |
| Dublettenschlüssel hing an der Feldreihenfolge | Felder werden sortiert |
| lange Läufe endeten mit Serverfehler | eigene Frist von zwölf Minuten, Teilergebnis statt Ausnahme |

Nach den ersten drei Korrekturen: **fünf von acht**, `nachrichten` in 35 Sekunden
statt sechs vergeblicher Runden.

### Der eigentliche Befund über die Modelle

| Modell | Verhalten |
| --- | --- |
| `nemotron3:33b` | langsam (70–250 s), Urteile **korrekt** |
| `qwen3:8b` mit Denken | korrekt, aber **unzuverlässig** — mal ruft es das Werkzeug, mal beschreibt es nur, was es täte |
| `qwen3:8b` ohne Denken | 17 s, inhaltlich **falsch** |
| `qwen3-vl:4b` | schnell und **gefährlich** |
| `granite3.2-vision` | beherrscht keine Werkzeugaufrufe |

Voreinstellung: `nemotron3:33b`. Wer Tempo braucht, mietet Rechenleistung —
nicht ein kleineres Modell.

## 10. Aufräumen zum Schluss

- `deep_mid_nd` liegt unter `ml/models/verworfen/` samt Messprotokoll.
  `DeepForecastService` durchsucht `ml/models` nach `deep_*.json`; ein zweites
  Modell für dasselbe Band hätte bei jedem Start eine Warnung erzeugt.
  Gelöscht wird es nicht — ein Negativergebnis ist ein Ergebnis, und ohne die
  Datei ist die Messung nicht nachvollziehbar.
- **SSH.NET 2024.2.0 → 2026.0.0.** Die alte Fassung trägt eine als hoch
  eingestufte Schwachstelle (GHSA-q939-rpr3-3284), und 2025.1.0 trägt sie
  weiterhin. Die neue Fassung annotiert ihre Schnittstelle: `SshHost` und
  `SshUser` dürfen nicht null sein. Statt die Warnung wegzudrücken prüft
  `Oeffne` das jetzt selbst und nennt im Fehlerfall den Endpunkt und das
  fehlende Feld.
- Vier ungelesene `ILogger`-Parameter aus den neuen Diensten entfernt.

**Übersetzung ohne Warnung, ohne Fehler.**

## Was offen bleibt

- [ ] **Die Säulen Kapitalfluss, Knowledge und Semantik liefern keinen
      numerischen Prognosebeitrag.** Ihre Gewichte wirken deshalb nur auf die
      Tagesübersicht, nicht auf die Prognose. Der Weg dorthin steht in
      [BRUECKE-TEXT-ZU-PROGNOSE.md](BRUECKE-TEXT-ZU-PROGNOSE.md).
- [ ] **Historische Nachrichten (GDELT).** Ohne Text mit Zeitstempel ab 2015
      gibt es für die Semantik-Säule keinen Sperrbereich und damit keine
      Validierung.
- [ ] **Artikel zu Werten zuordnen.** Ein Artikel über Ripple ist nicht mit
      XRP-USD verknüpft; die Ähnlichkeitssuche über den Firmennamen ist ein
      schwacher Ersatz, wie die SCCO-Fundstelle gezeigt hat.
