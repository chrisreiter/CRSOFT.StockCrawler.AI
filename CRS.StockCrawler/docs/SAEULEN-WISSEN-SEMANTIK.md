# Säulen „Knowledge" und „Semantik"

**CRSOFT.StockCrawler**, Stand 22.08.2026

Beide holen Text herein, zerlegen ihn, betten ihn ein und machen ihn
durchsuchbar. Nur die Quelle unterscheidet sich: hochgeladene Fachliteratur
hier, beobachtete Adressen dort.

## Aufbau

```
Datei / Adresse
   ↓  PdfPig (mit Geometriefilter) bzw. HTTP + HTML-Reinigung
Text ohne Kopf- und Fußzeilen
   ↓  Zerlegung an Absatzgrenzen, 1.400 Zeichen, 200 Überlappung
Abschnitte  →  SQL (knowledge_chunk)                ← der Volltext
   ↓  bge-m3 über Ollama, 1024 Dimensionen, auf Länge 1 normiert
Vektoren    →  Qdrant (crs_wissen / crs_semantik)   ← nur die Nachbarschaft
```

**Warum der Text in SQL bleibt.** Qdrant ist eine Vektordatenbank, kein
Dokumentenspeicher. Den Volltext dort abzulegen hieße, zwei Systeme zur Wahrheit
über denselben Text zu machen — und beim ersten Neuaufbau der Sammlung wäre er
weg.

**Warum bge-m3.** Mehrsprachig. Die Quellen sind teils deutsch, teils englisch;
ein rein englisches Modell bettet deutsche Abschnitte in eine Ecke des Raums, in
der sie sich nur noch untereinander ähneln.

**Warum eine gemeinsame Umsetzung.** Zwei getrennte hießen zwei Zerlegungen,
zwei Einbettungswege und beim nächsten Modellwechsel zwei Änderungen — mit der
Aussicht, dass eine davon vergessen wird.

## Drei Fallen, die beim ersten Lauf zuschlugen

### Der Löschfilter traf nie

Die Nutzlast legte `source_id` als **Zeichenkette** ab; Qdrants Filter
vergleicht gegen eine **Zahl**. „1" ist nicht 1, also traf `DeleteBySource` nie
etwas. Beim erneuten Einbetten blieben die alten Vektoren stehen, während die
zugehörigen Abschnitte in SQL gelöscht wurden. Die Suche fand daraufhin Punkte
ohne Text und lieferte für drei von vier Fragen **gar nichts**.

Still und schlimm: Kein Fehler, keine Meldung, nur leere Antworten.

### Fußzeilen ließen sich nicht über den Text entfernen

Der Versuch, Zeilen zu streichen, die auf vielen Seiten wortgleich vorkommen,
scheiterte doppelt. PdfPigs `page.Text` liefert die ganze Seite als **eine**
Zeichenkette ohne Zeilenumbrüche — es gibt keine Zeilen zu vergleichen. Und eine
Fußzeile trägt meist die Seitenzahl mit, ist also nie wortgleich:

```
10. Oktober 2024 Susanne Pichler 137
10. Oktober 2024 Susanne Pichler 138
```

Über die **Geometrie** ist es eindeutig: Was im obersten und untersten
Zwanzigstel der Seite steht, ist Kolumnentitel oder Fußzeile. Das gilt für jedes
Dokument, ohne dass man es auf eines einstellen müsste. Die Wortkästen liefert
`page.GetWords()`; aus ihren Grundlinien lassen sich zugleich die Zeilen
zurückgewinnen, die `page.Text` nicht hat.

Dass Fußnoten dabei zum Teil mit verschwinden, ist kein Verlust: *„Vgl.
Kahneman/Tversky (1979), S. 263ff."* trägt keine Aussage, die man suchen könnte,
verschiebt aber jeden Vektor, in dem sie steht.

### Das Literaturverzeichnis war der beste Treffer

Auf „Risikomanagement und Positionsgröße" antwortete das System mit:

> *„Charness, G./Gneezy, U. (2012): Strong Evidence for Gender Differences in
> Risk Taking, Journal of Economic Behavior & Organization…"*

Der Eintrag ist voller langer Wörter und hat wenige Ziffern — er kommt durch
jeden naheliegenden Filter. Sein Kennzeichen ist ein anderes: die dichte Folge
von **Jahreszahlen in Klammern**, eine je Eintrag. Mehr als zwei je 400 Zeichen
kommt in Fließtext nicht vor.

### Das Ergebnis nach allen drei Korrekturen

Dieselbe Datei, dieselben Fragen:

| Frage | vorher | nachher |
| --- | --- | --- |
| Verlustaversion | Fußnotenblock, 0,694 | Nutzenfunktion S. 26, 0,687 |
| Risikomanagement | Literaturverzeichnis S. 167 | Risiko-Ertrags-Verhältnis S. 127 |
| Herdenverhalten | Fußnotenapparat S. 96 | Fließtext S. 149 |
| Handelserfolg | Fußnoten S. 154 | Erfolgsfaktoren S. 12 |

Die Ähnlichkeitswerte änderten sich kaum — **die Zahl war nie das Problem.** Sie
sagte die ganze Zeit dasselbe: „passt mittelmäßig." Nur passte vorher der
Fußnotenapparat mittelmäßig und nachher der Inhalt.

## Der Zeitplan

Die Säule „Semantik" hängt am **Stundenlauf**, nicht am Tageslauf: Meldungen
sind schnell alt, und eine Zinsentscheidung erst am nächsten Morgen einzulesen
wäre für eine Prognose wertlos. Geholt wird nur, was nach dem eigenen Abstand
der Quelle fällig ist — im Stundenlauf steht also der schnellste **mögliche**
Takt, nicht der tatsächliche.

Fehler bleiben in der Säule. Eine nicht erreichbare Adresse oder ein nicht
gestartetes Qdrant darf den Stundenlauf nicht mitreißen — an dem hängen auch
Kursabruf und Prognose.

Unverändertes wird nicht erneut eingebettet: Zu jeder Quelle steht die Prüfsumme
ihres Inhalts. Ohne das würde jede Seite bei jedem Lauf neu eingebettet, für ein
Ergebnis, das schon dasteht.

## Was diese Säulen nicht können

Sie finden Stellen, die zu einer Frage **passen** — nicht Stellen, die **recht
haben**. Ein Buch, das eine Strategie empfiehlt, ist kein Beleg dafür, dass sie
funktioniert.

Wie aus diesem Material trotzdem ein messbarer Beitrag zur Prognose werden kann,
steht in [BRUECKE-TEXT-ZU-PROGNOSE.md](BRUECKE-TEXT-ZU-PROGNOSE.md).

## Aufrufe

```
GET    /api/knowledge/health
GET    /api/knowledge/sources?saeule=knowledge|semantic
POST   /api/knowledge/upload?saeule=knowledge        (Formular, PDF/TXT/MD/CSV)
POST   /api/knowledge/web?saeule=semantic            {url, title, pollMinutes}
POST   /api/knowledge/index/{id}?neu=true
POST   /api/knowledge/refresh?saeule=semantic
GET    /api/knowledge/search?frage=…&saeule=…&limit=8
DELETE /api/knowledge/{id}
```

`neu=true` gibt es, weil sich nicht nur die Quelle ändern kann, sondern auch das
Verfahren: Nach einer geänderten Zerlegung oder einem anderen Einbettungsmodell
ist der alte Bestand veraltet, obwohl der Text derselbe ist.

## Stand

- [x] Ablage, Zerlegung, Einbettung, Suche für beide Säulen
- [x] PDF mit Geometriefilter, HTML-Reinigung
- [x] Qdrant-Sammlungen `crs_wissen` und `crs_semantik`
- [x] Rasteransicht mit Hochladen, Einbetten, Stilllegen, Löschen
- [x] Semantik im Stundenlauf
- [x] Erprobt: *Trading Skills* (188 Abschnitte), FOMC-Mitteilung (10 Abschnitte)

### Vorgemerkt

- [ ] **Zeitliche Kennung je Abschnitt.** `occurred_utc` steht im Schema, wird
      aber noch nicht gefüllt. Ohne sie lässt sich eine Aussage nicht in Bezug
      zu einer Kursbewegung setzen — und ohne diesen Bezug ist kein einziger
      Kanal der Text-zu-Zahl-Brücke prüfbar. **Das ist die wichtigste offene
      Arbeit dieser Säulen.**
- [ ] **Historische Nachrichten (GDELT).** Ohne Text mit Zeitstempel ab 2015
      gibt es keinen Sperrbereich und damit keine Validierung.
- [ ] **Feeds statt Einzelseiten.** RSS und Atom liefern Zeitstempel frei Haus
      und lösen damit zugleich den Punkt oben.
