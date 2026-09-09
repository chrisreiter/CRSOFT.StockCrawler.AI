# CRSOFT.StockCrawler — was dieses Projekt kann

**Aktien, ETFs, Kryptowährungen und Indizes von 17 Börsenplätzen —
mathematisch analysiert.
Börsennachrichten aus aller Welt und die Fachliteratur dazu — sprachunabhängig
als Vektoren.
Und beides zusammen, Zahlen wie Text, gelesen von NVIDIA Nemotron.**

Auf einem Rechner. Ohne Cloud. Offen und kostenlos.

In Zahlen: 641 Werte · 3,9 Millionen Kursbars · 397.011 ausgewertete Wertpaare ·
35 Nachrichtenquellen · 42.219 Abschnitte Fachliteratur · 978.837 Prognosen.

Die ganze Kette, in einem System:

```
   Kurse          17 Börsen, live, stündlich, zurück bis 2001
      ↓
   Mathematik     Korrelationen · Kreuzungen · Vorlauf · Kurvendiskussion
      ↓           Spektralanalyse · Kapitalfluss · Zeitzonen
   Nachrichten    35 Quellen weltweit, in JEDER Sprache
      ↓
   Vektorraum     sprachunabhängig — und darin zugleich die Fachliteratur:
      ↓           arXiv, Dissertationen, Klassiker
   Reasoning      NVIDIA Nemotron, das jede Zahl belegen muss
      ↓
   Täglich        Journal · Auffälligkeiten · Trends · Prognosen
```

Das ist der Aufbau, für den Anbieter fünfstellige Jahreslizenzen verlangen.

**Und dann der Teil, den keiner von denen mitliefert: die Messung, was davon
trägt.** Alles hier ist gegen einen Sperrbereich geprüft, und wo ein Verfahren
nichts leistet, steht das in der Oberfläche — nicht im Kleingedruckten.

---

## Die Kette im Einzelnen

### 1 · Märkte — 641 Werte von 17 Börsenplätzen

| | |
| ---: | --- |
| **405** | Aktien |
| **109** | Kryptowährungen |
| **107** | Fonds und ETFs |
| **20** | Indizes und Devisen |

Xetra, Paris, London, Zürich, Tokio, Amsterdam, Wien, Mailand, Madrid,
Stockholm, Hongkong und die US-Börsen. **3.941.331 Bars**, Tages- und
Stundenauflösung, bis 2001 zurück. Stündlich kommen neue dazu.

Dass verschiedene Börsen verschiedene Handelskalender haben, ist dabei kein
Detail, sondern die häufigste Falle: Wer über das rohe Zeitraster rechnet,
misst den Kalender statt den Markt — und zwar überzeugend genug, um es zu
glauben. Drei Auswertungen dieses Projekts sind genau daran gescheitert, bevor
es behoben war.

### 2 · Mathematische Auswertung

- **397.011 Wertpaare** auf Korrelation, besten Lag und Richtung ausgewertet
- **Kreuzungen** — wann zwei normalisierte Kurven die Plätze getauscht haben,
  mit Rangliste nach Paargewinn und Bewährung aus **getrennten Zeiträumen**
- **Kurvendiskussion** über den gesamten Bestand: Hoch-, Tief-, Wende- und
  Sattelpunkte über lokale Polynomanpassung, dazu Steigungs- und
  Krümmungsausbrüche
- **Frühindikatoren** und **Zeitzonen-Vorlauf** — Tokio, Europa, New York, mit
  sauberer Trennung von echtem Vorlauf und blosser Überlappung
- **Spektralanalyse**: Periodenlängen, Zyklenstabilität, Momentanzyklus, SSA
- **Kapitalfluss**: Umverteilung gegen Zu- und Abfluss, Rotationsverdacht
- **Bot-Herde** — was die öffentlich bekannten Auslöser tatsächlich
  hinterlassen, über tausende Ereignisse gemessen

### 3 · Nachrichten — stündlich, in jeder Sprache

**35 Quellen aus elf Regionen**: Reuters, WSJ, FT, CNBC, MarketWatch,
Handelsblatt, NZZ, Der Standard, Nikkei, South China Morning Post, Economic
Times, CoinDesk, Cointelegraph — dazu Fed, EZB, Bank of England, BIZ und SEC,
und UN News für Weltereignisse. **11.644 Artikel** eingelesen.

**Sprachunabhängig.** Das Einbettungsmodell `bge-m3` ist mehrsprachig: Eine
deutsche Frage findet eine japanische Meldung, ohne dass irgendwo übersetzt
wird. Es gibt keinen Übersetzungsschritt, weil keiner nötig ist — die Vektoren
liegen im selben Raum.

*Noch nicht dabei:* soziale Kanäle. Die Feed-Schicht nimmt jede RSS- oder
Atom-Quelle, die Erweiterung ist also klein — sie steht unter *Was geplant ist*.

### 4 · Fachliteratur im selben Vektorraum

**55 Quellen, 42.219 Abschnitte**, jeder mit Quelle, Seite und Textanker:

| | |
| ---: | --- |
| **41** | arXiv-Arbeiten zu Limit Order Books, statistischer Arbitrage, Marktwirkung, Ausführungsstrategien, Deep Learning im Handel |
| **12** | gemeinfreie Klassiker — Lefèvre, Selden, Harper, Clews, Crump, Gibson, Lawson, Rice, Brandenburg, Butler, Francis |
| **1** | Dissertation zu Marktmikrostruktur und algorithmischem Handel |
| **1** | BIZ-Arbeitspapier zum Hochfrequenzhandel |

**Nachrichten und Literatur liegen im selben Raum.** Eine Frage nach
Verlustaversion findet die Stelle im Lehrbuch *und* die Meldung von heute
Morgen — ohne dass jemand die Verbindung von Hand angelegt hätte.

### 5 · KI-Reasoning über NVIDIA Nemotron

`nemotron3:33b` beantwortet Fragen zu Prognosen, Kurvenereignissen,
Verknüpfungen und dem gesammelten Wissen — **und darf dabei keine Zahl
erfinden.** Jede Zahl stammt aus einem Werkzeugaufruf, und die Aufrufe stehen
mit Name, Argumenten und Ergebnis unter jeder Antwort. Wer misstraut, sieht
nach.

Er antwortet in der Sprache der Frage. Und er ist ausdrücklich **Erklärer, nicht
Orakel**: Er liest die vorhandenen Prognosen ab, er erzeugt keine. Deshalb hat
diese Säule kein Gewicht am Ergebnis.

### 6 · Täglich: Journal, Trends, Prognosen

- **978.837 Live-Prognosen** über sieben Horizonte von 24 Stunden bis einem
  Jahr, davon **303.465 bereits gegen den eingetroffenen Kurs ausgewertet**
- **Marktjournal** — was sich bewegt hat, was darüber berichtet wurde, was die
  Messung dazu hergibt, als Markdown zum Weiterverwenden
- **Tagesübersicht** mit den auffälligen Stellen, gewichtet nach Auffälligkeit
  **mal** gemessenem Verdienst ihrer Säule

**Das Journal wird ohne Sprachmodell geschrieben.** Jede Zahl darin stammt aus
einer Abfrage; der Satz drumherum ist Vorlage, nicht Erzeugnis. Der Grund steht
in der Messung: Ein kleineres Modell las hier ein Fehlerverhältnis von 1,0034
als „nahezu perfekt" und behauptete das Gegenteil der Werkzeugausgabe. Was in
ein Blog geht, muss nachrechenbar bleiben.

---

## Und die Messung dazu

Jede Prognose wird gegen den Kurs ausgewertet, der tatsächlich eingetroffen ist
— nicht in einer Rückrechnung, sondern gegen Kurse, die kein Modell dieser
Anwendung je gesehen hat. Trefferquote, Fehlerverhältnis und Erwartungswert
nach Kosten stehen je Wert und je Horizont **in der Oberfläche**, neben der
Prognose, auf die sie sich beziehen.

Das ist der Grund, warum es hier keine Kaufempfehlungen gibt: Wer die gemessene
Güte einer Prognose direkt danebenstehen hat, braucht keine Empfehlung — er
trifft seine eigene Entscheidung, und zwar auf einer Grundlage, die er
nachrechnen kann.

---

## Die acht Säulen

Jede stellt eine andere Frage an dieselben Daten. Jede lässt sich zuschalten
und gewichten — **aber keine bewegt etwas ohne gemessenen Rückhalt.** Gewichtet
wird mit *Regler × Verdienst*, und der Verdienst kommt aus dem Sperrbereich,
nicht aus der Einstellung.

| | |
| --- | --- |
| **Kurslernen** | fünf Teilmodelle, deren Gewichte laufend aus dem eigenen Fehler nachgezogen werden. Die einzige Säule, die derzeit in die gespeicherten Prognosen einfliesst |
| **Mathematische Analyse** | Spektrum, Zyklenstabilität, Momentanzyklus, SSA, Hurst — Signalverarbeitung auf der einzelnen Reihe |
| **Kapitalfluss** | Umverteilung gegen Zu- und Abfluss, Rotationsverdacht, Gruppen mit erhaltener Summe |
| **Deep Learning** | drei ONNX-Modelle über getrennte Horizontbänder, im Prozess auf der CPU |
| **Wissen** | eingebettete Fachliteratur, gemessene Bot-Muster als Beitrag |
| **Semantik** | Nachrichtenstimmung, kalibriert gegen die danach eingetroffene Rendite |
| **Reasoning** | erklärt, rechnet nicht mit |
| **Kurvendiskussion** | Hoch-, Tief-, Wende- und Sattelpunkte über lokale Polynomanpassung |

---

## Was die Anwendung im Einzelnen kann

### Analyse

- **Korrelationen und Frühindikatoren** über 397.011 ausgewertete Paare, mit
  bestem Lag und Richtung
- **Kreuzungen** — wann zwei normalisierte Kurven die Plätze getauscht haben,
  mit Rangliste nach Paargewinn und **Bewährung aus getrennten Zeiträumen**
- **Kurvendiskussion** über den gesamten Bestand, kausal oder zentriert
  geglättet, mit Verknüpfung gleichzeitiger Ereignisse
- **Weltbestand und Zeitzonen** — Tokio, Europa, New York, mit sauberer
  Trennung von Vorlauf und blosser Überlappung
- **Bot-Herde** — was die öffentlich bekannten Auslöser (Gleitende Mittel, RSI,
  Bollinger) tatsächlich hinterlassen, gemessen über tausende Ereignisse
- **Umkehrschluss** — taugt ein Signal, wenn man es umdreht?

### Prognose und Güte

- sieben Horizonte, je Wert und Zieltag **eine** Beobachtung (entdoppelt)
- **Lernkurve**: Wird es besser? Median des Fehlerverhältnisses je Zieltag
- **Mischvergleich**: Bringt das Zusammenführen der Säulen überhaupt etwas?
- Rückblick als **aufsummierter Vorsprung** gegenüber dem Stillstand

### Handel und Depot

- **Day Trading** — nicht „was kaufen", sondern: Trägt der Markt heute überhaupt
  einen Handel innerhalb des Tages, nachdem die Gebühren abgezogen sind?
- **Tausch** — Umschichtungsvorschläge auf den eigenen Bestand bezogen
- **Langfrist** — Renditen, tiefste Einbrüche, Körbe, mit sichtbarer
  Überlebensverzerrung
- **Virtuelles Depot** mit Verrechnungskonto in EUR und USD, Gebühr je Vorgang,
  Buchungsjournal und Vermögensverlauf
- **Autopilot mit vier Strategien**: `streng` (handelt nur mit Nachweis),
  `aktiv` (folgt der Modellerwartung), `halten` (Grundlinie) und `invers` (die
  Gegenkontrolle — hält die schwächstbewerteten Werte, damit `aktiv` − `invers`
  den Informationsgehalt des Signals misst)
- **Neuzugänge** — Vorankündigungen aus dem Nasdaq-IPO-Kalender und Binances
  Listing-Katalog, als vollständige Kohorte einschliesslich der Fehlschläge

### Betrieb

- Zeitplan: stündlich Kurse, Prognosen, Bewertung; täglich Universum, Analyse,
  Neuzugänge, Autopilot
- **Zwei Rollen**, durchgesetzt an der HTTP-Methode statt an einer Liste von
  Seiten — die Regel gilt damit auch für Endpunkte, die es noch nicht gibt
- **Mehrsprachige Oberfläche**: 2.272 Texte in Deutsch und Englisch. Eine
  weitere Sprache ist eine XML-Datei und sonst nichts
- **Tagesjournal** als Markdown — **ohne Sprachmodell geschrieben.** Jede Zahl
  stammt aus einer Abfrage, der Satz darum ist Vorlage. Was in ein Blog geht,
  muss nachrechenbar bleiben

---

## Was geplant ist

- **Testabdeckung für `Ingest.Core/Analysis`** — die Mathematik ist bewusst
  abhängigkeitsfrei und gut testbar, hat aber kaum Tests. Der dankbarste
  Einstieg für einen neuen Beitragenden
- **Kopfstau in der Bewertungswarteschlange auflösen** — dauerhaft nicht
  bewertbare Prognosen blockieren die bewertbaren dahinter
- **Werte ohne Kursdaten aussortieren**, statt sie mitzuführen
- **Zugangsdaten in User Secrets** statt in `appsettings.json`
- **Soziale Kanäle als Quelle** — die Feed-Schicht nimmt jede RSS- oder
  Atom-Quelle; heute sind es 35 Nachrichten-, Notenbank- und Aufsichtsquellen
  und **kein** sozialer Kanal. Die Erweiterung ist klein, die Frage nach der
  Signalqualität die eigentliche Arbeit
- **SEC EDGAR anbinden** — Registrierungen liefern Wochen früher Signal als der
  IPO-Kalender
- **Leerverkäufe im virtuellen Depot** — erst damit wäre `invers` ein echtes
  Spiegelbild statt eines zweiten Kaufkorbs
- **Zahlen- und Datumsformate mitschalten** — die Oberfläche ist übersetzt, die
  Formate sind es nicht

---

## Was es nicht ist

**Kein Handelssystem und keine Anlageberatung.** Die Anwendung sagt an keiner
Stelle, was zu kaufen wäre. Sie beantwortet die Frage davor: Was lässt sich
überhaupt messen, und trägt es die Kosten?

**Keine Prognose ohne ihre gemessene Güte.** Jede Zahl, die diese Anwendung
anzeigt, hat eine Messung hinter sich, und die Messung steht daneben — nicht im
Kleingedruckten, sondern an der Stelle, an der jemand sonst eine Zahl für bare
Münze nähme. Was ein Verfahren im Sperrbereich trägt und was nicht, entscheidet
über sein Gewicht am Ergebnis: **Regler mal gemessener Verdienst.** Eine Säule
ohne Nachweis bewegt nichts, auch beim Regler auf hundert.

Die vollständigen Messreihen — je Säule, je Horizont, samt der Verfahren, die
nichts getragen haben — stehen in den technischen Unterlagen unter
`CRS.StockCrawler/docs/` und in der Anwendung selbst.

---

## Technik

.NET 8, Minimal API, Dapper, SQL Server, uPlot. ONNX Runtime im Prozess für die
Deep-Modelle. Ollama für Einbettungen (`bge-m3`) und Reasoning
(`nemotron3:33b`). Qdrant als Vektorspeicher. Keine Cloud nötig — alles läuft
lokal.

Einrichtung mit einem Befehl: [`startUp.md`](startUp.md) bzw. `/startup` in
Claude Code.

Lizenz: **AGPL-3.0** mit kommerzieller Ausnahme — siehe
[LICENSING.md](LICENSING.md).

---

## Warum mitmachen

Weil hier etwas Seltenes offen liegt: **eine vollständig instrumentierte
Messanlage.** Wer im Finanzbereich Verfahren prüft, kämpft üblicherweise gegen
Backtests, die zu gut aussehen, und gegen Veröffentlichungen, die nur zeigen,
was funktioniert hat. Hier ist die ganze Rechnung sichtbar — jedes Verfahren
mit der Messung, die über sein Gewicht entscheidet.

Die dankbarsten Beiträge sind deshalb nicht neue Ideen, sondern
**Widerlegungen.** Wenn eine der gemessenen Aussagen nicht hält, ist das der
wertvollste Pull Request, den dieses Projekt bekommen kann.

[CONTRIBUTING.md](CONTRIBUTING.md) sagt, wie.
