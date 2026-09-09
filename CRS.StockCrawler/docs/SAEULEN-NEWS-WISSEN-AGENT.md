# Säulen „News & Medien", „Wissen" und der Agent

**CRSOFT.StockCrawler**, Planung, Stand 21.08.2026

Drei aufeinander aufbauende Ausbaustufen. Die ersten beiden sammeln und machen
durchsuchbar; die dritte setzt darauf auf und ist ohne sie sinnlos.

## Werkzeugwahl — und wo Qdrant diesmal richtig ist

Beim Formvergleich der Kurven war die Antwort auf die Qdrant-Frage nein: Ein
Modenvektor hat fünf bis zwanzig Zahlen, und unterhalb von etwa hundert
Dimensionen bringt ein HNSW-Index nichts gegenüber einem Bereichsindex.

**Hier ist es umgekehrt.** Ein Textabschnitt wird zu einem Vektor mit 1024
Dimensionen, und es werden Millionen davon. Genau dafür ist Qdrant gebaut, und
es bringt zwei Dinge mit, die hier nicht verzichtbar sind:

- **Hybride Suche** aus dichten und dünnbesetzten Vektoren. Rein semantische
  Suche verfehlt Eigennamen und Tickersymbole — „NVDA" ist semantisch nah an
  „AMD", und das ist beim Nachrichtenabgleich genau falsch.
- **Filter auf Nutzdaten.** Ohne einen harten Filter auf den
  Veröffentlichungszeitpunkt ist jede Rückrechnung wertlos: Die Suche fände
  Nachrichten, die zum bewerteten Zeitpunkt noch nicht existierten. Das ist
  dieselbe Falle wie beim Formvergleich, nur teurer, weil sie überzeugendere
  Ergebnisse liefert.

### Einbettungsmodell

**BGE-M3** ist die richtige Wahl und in Ollama als `bge-m3` verfügbar.

| Eigenschaft | Warum sie hier zählt |
| --- | --- |
| mehrsprachig | Deutsche Nachrichten neben englischen Meldungen und Berichten, in einem Vektorraum |
| 8192 Token Kontext | Ganze Meldungen und Buchabschnitte am Stück, ohne sie vorher zu zerhacken |
| dicht + dünn + Mehrvektor in einem Durchgang | Liefert genau die hybride Suche, die Qdrant braucht — ohne ein zweites Modell |
| 1024 Dimensionen | Vertretbarer Speicherbedarf: eine Million Abschnitte ≈ 4 GB |

Alternativen wie `nomic-embed-text` oder `mxbai-embed-large` sind schneller und
kleiner, aber einsprachig und ohne dünnbesetzten Anteil. Für einen deutschen
Nachrichtenbestand ist das der falsche Tausch.

## Säule „News & Medien"

**Raster für Quellen.** Je Zeile: URL oder Kanal, Art (RSS, Webseite, X/Twitter,
Reddit, Telegram), Abrufintervall, Sprache, Themenmarkierung, aktiv ja/nein.
Dazu je Quelle die Kennzahlen des letzten Laufs — abgerufen, neu, Fehler.

**Ablauf.**

1. **Abholen** nach Zeitplan, je Quelle mit eigenem Intervall. Der vorhandene
   `CronScheduler` trägt das bereits.
2. **Entdoppeln** über einen Prüfwert des Textes. Nachrichten werden von
   Agenturen mehrfach ausgespielt; ohne diesen Schritt gewichtet die spätere
   Auswertung dieselbe Meldung zehnfach.
3. **Zerlegen** in Abschnitte von 300 bis 500 Token mit Überlappung.
4. **Einbetten** mit BGE-M3, **ablegen** in Qdrant mit Nutzdaten:
   `published_at`, `source`, `language`, `tickers[]`, `sector`, `url`.
5. **Werte zuordnen** über Symbol- und Firmennamenserkennung. Bewusst
   regelbasiert und nicht über ein Sprachmodell: Bei Zehntausenden Meldungen
   täglich zählt Durchsatz, und Tickererkennung ist ein gelöstes Problem.

**Die Prüfung, ohne die die Säule kein Gewicht bekommt.** Dieselbe wie überall:
Nachrichtenlage bis Zeitpunkt t, gemessen gegen die marktbereinigte Bewegung
danach, mit Rückhaltezeitraum. Wenn Nachrichtenstimmung nichts über die
Fortsetzung sagt, bekommt die Säule Gewicht null — so wie es der Formvergleich
bekommen hat.

**Aufwand:** Speicher unkritisch. Eine Million Abschnitte sind rund 4 GB für die
Vektoren und wenige GB für die Texte. Da die Datenbank bereits bei 2,8 GB liegt
und C: noch 99 GB frei hat, gehört Qdrant dennoch auf **D:** (652 GB frei) —
dort wächst es ohne Rücksicht.

## Säule „Wissen"

Gleiche Technik, anderer Zweck und anderer Umgang mit der Zeit.

**Raster für Quellen:** PDF, EPUB, Webseite, mit Titel, Autor, Jahr,
Themengebiet und Gewicht. Fachliteratur ändert sich nicht täglich; ein Abruf
beim Hinzufügen genügt.

**Der entscheidende Unterschied zur Nachrichtensäule:** Wissen ist nicht an
einen Zeitpunkt gebunden und darf deshalb **nicht** über `published_at`
gefiltert werden — sonst dürfte eine Rückrechnung von 2010 kein Lehrbuch von
2015 benutzen, obwohl dessen Inhalt zeitlos ist. Getrennte Sammlungen in Qdrant,
getrennte Filterregeln.

**PDF-Verarbeitung:** Der Textauszug ist der heikle Teil. Fachbücher enthalten
Formeln, Tabellen und mehrspaltigen Satz. Lokal vorhanden und dafür geeignet ist
`PaddleOCR-VL` — es ist genau für Belegverarbeitung gebaut und beherrscht
Layouterkennung. Für Bücher mit Textebene reicht ein gewöhnlicher Auszug; nur
Scans brauchen den Umweg über OCR.

## Der Agent

Das wichtigste Merkmal — und dasjenige, bei dem der häufigste Entwurfsfehler
gemacht wird.

**Der Agent darf nichts wissen. Er muss fragen können.**

Ein Modell, dem man Kurse und Nachrichten in die Gewichte trainiert, gibt
veraltete Auskünfte mit voller Überzeugung. Der Kurs von gestern gehört nicht in
ein Modell, er gehört in eine Abfrage. Der Agent bekommt deshalb **Werkzeuge**,
und die gibt es bereits — jede Säule hat ihre Schnittstelle:

```
/api/series          Kursverläufe und Prognosen
/api/spectral/modes  gemeinsame Marktschwingungen
/api/spectral/leaders Vorläufer und Nachzügler
/api/transmission    Übertragungen zwischen Werten
/api/flow            Kapitalfluss, Zuordnung, Ereignisse
/api/forecast        Prognosen und ihre Treffsicherheit
Qdrant               Nachrichten und Fachwissen
```

**Modellwahl.** Verlässliches Werkzeugaufrufen ist die Anforderung, nicht
Sprachgewandtheit. Lokal vorhanden und geeignet:

| Modell | Größe | Einschätzung |
| --- | --- | --- |
| `nemotron3:33b` | 27,6 GB | vorhanden, 30B-Klasse — die untere Grenze für verlässliches Werkzeugaufrufen über mehrere Schritte |
| `qwen3-vl:8b` | 6,1 GB | zu klein für mehrstufiges Werkzeugaufrufen, brauchbar für Zusammenfassungen |

Empfehlung: mit `nemotron3:33b` beginnen und die Werkzeugaufrufe **messen** —
Anteil wohlgeformter Aufrufe, Anteil sinnvoll gewählter Werkzeuge. Fällt das
durch, ist ein größeres Modell nötig, und dann wird die Frage nach einem Dienst
statt lokaler Ausführung unvermeidlich.

## LoRA — die Bewertung

**Für Wissen: nein, und zwar aus einem sachlichen Grund.** Feinabstimmung
verändert Verhalten, nicht Faktenbestand. Was sie an Fakten mitnimmt, nimmt sie
unzuverlässig mit und ohne Quellenangabe — das Modell behauptet dann etwas,
ohne sagen zu können, woher. Bei Kursen und Nachrichten kommt hinzu, dass sich
der Bestand täglich ändert: Man müsste täglich nachtrainieren und hätte am Ende
ein Modell, das gestrige Kurse für gegenwärtig hält.

**Wofür LoRA hier tatsächlich taugt** — eng, aber echt:

- **Hausformat.** Wie eine Antwort aussehen soll: welche Zahlen genannt werden,
  wann Unsicherheit auszuweisen ist, dass eine Prognose ohne ihre Trefferquote
  nicht ausgeliefert wird.
- **Werkzeuggebrauch.** Auf unsere konkreten Schnittstellen abgestimmt, mit
  ihren Parametern und ihren Eigenheiten. Dafür braucht es einige hundert
  Beispielverläufe — die entstehen im Betrieb von selbst.
- **Fachsprache.** Dass „Mode", „Vorlauf", „Umschichtung" hier eine bestimmte
  Bedeutung haben und nicht die umgangssprachliche.

Reihenfolge: erst Abruf, dann messen, und **nur wenn** die Messung zeigt, dass
das Modell an Format oder Werkzeuggebrauch scheitert, LoRA. Nicht umgekehrt.

## Reihenfolge des Baus

1. Qdrant auf D:, BGE-M3 über Ollama, Einbettungsdienst, eine Sammlung.
2. Quellenraster und Abruf für RSS und Webseiten — die einfachsten Quellen
   zuerst; X und soziale Netzwerke brauchen Zugangsschlüssel und haben eigene
   Nutzungsbedingungen.
3. Zuordnung Meldung → Wert, und die Rückrechnung: Sagt Nachrichtenlage etwas
   über die marktbereinigte Bewegung danach? **Erst diese Zahl entscheidet über
   das Gewicht der Säule.**
4. Wissenssammlung mit PDF-Auszug.
5. Agent mit Werkzeugen, Chatoberfläche, Messung des Werkzeuggebrauchs.
6. LoRA nur, wenn Schritt 5 es nachweislich nötig macht.
