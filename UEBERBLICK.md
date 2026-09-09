# CRSOFT.StockCrawler — was dieses Projekt kann

Ein offenes Labor für Marktanalyse und Prognose. Es sammelt Kurse, Nachrichten
und Fachliteratur in **ein** Datenmodell, untersucht die Wechselwirkungen
darin, erzeugt daraus selbstlernende Prognosen — und **misst schonungslos
nach, was davon trägt.**

Der letzte Halbsatz ist der eigentliche Unterschied. Alles hier ist gegen einen
Sperrbereich geprüft, und wo ein Verfahren nichts leistet, steht das in der
Oberfläche. Auf Deutsch und auf Englisch.

---

## Die Kernpunkte

**Kurse.** 641 verfolgte Werte — Aktien, Fonds/ETFs, Kryptowährungen, Indizes,
Devisen — aus mehreren Anbietern in einem einheitlichen Modell. **3,94 Millionen
Bars**, Tages- und Stundenauflösung, bis 2001 zurück.

**Nachrichten, sprachunabhängig.** 35 Feeds von Reuters, WSJ, FT, CNBC über
Handelsblatt, NZZ und Der Standard bis Nikkei, SCMP und Economic Times, dazu
Fed, EZB, Bank of England, BIZ und SEC. **11.644 Artikel** eingelesen und
eingebettet. Das Einbettungsmodell (`bge-m3`) ist mehrsprachig — eine deutsche
Frage findet eine japanische Meldung, ohne dass irgendwo übersetzt wird.

**Fachliteratur.** Bücher und Arbeiten werden in überlappende Abschnitte zerlegt
und mit Fundstelle abgelegt: **42.219 Abschnitte**. Jeder Treffer trägt Quelle,
Seite und Textanker — ein Treffer ohne Fundstelle wäre eine Behauptung.

**Prognosen.** **978.837 Live-Prognosen** über sieben Horizonte von 24 Stunden
bis einem Jahr, davon **303.465 bereits gegen den eingetroffenen Kurs
ausgewertet**. Aus jeder Auswertung werden die Gewichte der Teilmodelle
nachgezogen.

**Ein Reasoning-Modell als Erklärer, nicht als Orakel.** `nemotron3:33b`
beantwortet Fragen zu Prognosen, Kurvenereignissen, Verknüpfungen und dem
gesammelten Wissen — **und darf keine Zahl erfinden.** Jede Zahl stammt aus
einem Werkzeugaufruf, und die Aufrufe stehen mit Name, Argumenten und Ergebnis
unter jeder Antwort. Es antwortet in der Sprache der Frage.

**Und die unbequeme Zahl.** Über **9.035 entdoppelte Live-Prognosen** auf 24
Stunden liegt die Richtungstrefferquote bei **0,5109**. Nötig wären bei 1,55 %
Tagesbewegung und 0,3 % Rundlauf **0,598**, damit ein Geschäft die Kosten deckt.
Das steht so in der Anwendung, und es ist der Grund, warum es hier keine
Kaufempfehlungen gibt.

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

**Kein Verfahren dieser Anwendung schlägt im Sperrbereich die blosse Drift** —
die mittlere Rendite des Trainingszeitraums, eine einzige Zahl. Das lange
Deep-Modell kam bei 250 Tagen auf Fehlerverhältnis 0,9604 und 64,3 % Richtung;
die Drift auf 0,9139 und 75,1 %. Eine einzige Zahl schlug alle neun
Modellausgaben, auf beiden Massen.

Drei unabhängige Befunde stützen dieselbe Aussage: Der gemeinsame Marktmodus
erklärt 53 % der Bewegung bei 2,8 Bars Phasenstreuung. Der Querschnitt aller
Kurse erklärt die Bewegung eines einzelnen am **selben** Tag mit R² 0,355 —
einen Tag voraus bleiben davon **0,0039**. Und die Kurvendiskussion findet
Ereignispaare mit Faktor 9 über der Erwartung, bei einem Medianabstand von
**null** Tagen.

**Kurse bewegen sich gemeinsam, nicht nacheinander. Wo kein Vorlauf ist, ist
nichts vorherzusagen.**

Diese Befunde stehen nicht im Kleingedruckten, sondern in der Oberfläche, an der
Stelle, an der jemand sonst eine Zahl für bare Münze nähme.

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
Messanlage samt ihrer Negativbefunde.** Wer im Finanzbereich Verfahren prüft,
kämpft üblicherweise gegen Backtests, die zu gut aussehen, und gegen
Veröffentlichungen, die nur zeigen, was funktioniert hat. Hier ist beides
sichtbar — die Verfahren, die nichts tragen, samt der Rechnung, die es zeigt.

Die dankbarsten Beiträge sind deshalb nicht neue Ideen, sondern
**Widerlegungen.** Wenn eine der gemessenen Aussagen nicht hält, ist das der
wertvollste Pull Request, den dieses Projekt bekommen kann.

[CONTRIBUTING.md](CONTRIBUTING.md) sagt, wie.
