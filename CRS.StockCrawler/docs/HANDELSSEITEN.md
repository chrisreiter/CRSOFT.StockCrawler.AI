# Kreuzungen, Tausch und Day Trading

**CRSOFT.StockCrawler**, Stand 23.08.2026

Drei Ansichten, die dieselbe Frage aus drei Richtungen stellen: *Lässt sich mit
dem, was gemessen wurde, überhaupt handeln?* Die Antwort ist überwiegend nein —
und der Ertrag dieser Seiten liegt darin, dass sie das belegen statt es zu
verschweigen.

## Die gemeinsame Grundlage: eine Kreuzung ist ein Paar

Eine Kreuzung sagt **nicht**, dass ein Wert steigt. Sie sagt, dass er einen
anderen überholt hat. Gerechnet wird deshalb immer als Paar:

```
Paargewinn = Rendite der oberen Seite − Rendite der unteren Seite
```

Nur so ist die Zahl vom allgemeinen Marktgang unabhängig. In einer Woche, in
der alles um fünf Prozent steigt, hat ein Paar nichts geleistet, wenn beide
Seiten fünf Prozent gestiegen sind.

## Die Kostenschwelle

`Handelskosten` in `Ingest.Core/Analysis`. Ein Tausch hat **zwei Beine** —
verkaufen und kaufen —, also fallen Gebühr und Schlupf doppelt an:

| Posten | je Seite |
| --- | ---: |
| Gebühr des Nehmers | 0,10 % |
| Schlupf | 0,05 % |
| **Rundlauf** | **0,30 %** |

Diese Zahl ist keine Nebenrechnung, sondern das Nadelöhr. Sie kam aus der
Strategieübersicht, die als Quelle in der Wissenssäule liegt
(*Marktneutrale Strategien im Kryptomarkt*), und ihr Kernsatz lautet: **Der
Vorsprung ist kompetitiv, nicht physikalisch.**

### Der Erwartungswert, nicht die Trefferquote

```
EV = (2p − 1) · E|r| − Rundlauf
```

Bei einer Trefferquote p gewinnt man in p Fällen die typische Bewegung und
verliert sie in (1 − p) Fällen; der Nettoanteil ist (2p − 1). Bei p = 52,4 %
ist dieser Faktor **0,048** — der Bruttovorsprung ist also nur ein Zwanzigstel
der Bewegung.

**Das war ein echter Fehler im ersten Entwurf.** Er verlangte „Trefferquote
über 0,523 UND Bewegung über den Kosten" und wies damit den Stundenhorizont
als tragfähig aus:

| | |
| --- | ---: |
| Trefferquote | 52,4 % |
| mittlere Bewegung | 0,47 % |
| Bruttovorsprung | 0,023 % |
| Kosten | 0,30 % |
| **Erwartungswert** | **−0,277 %** |

Beide Bedingungen erfüllt, und trotzdem ein Verlustgeschäft. Umgestellt auf
`Handelskosten.Erwartungswert`.

Die Umkehrung ist die ernüchterndste Zahl der ganzen Anwendung —
`NoetigeTrefferquote`:

| Horizont | mittlere Bewegung | nötige Trefferquote |
| --- | ---: | ---: |
| 1 Stunde | 0,47 % | **81,8 %** |
| 4 Stunden | 1,05 % | **64,3 %** |
| 1 Stunde, Krypto | 0,33 % | **95,4 %** |

Nicht 51, nicht 55.

---

## Ansicht 1: Kreuzungs-Rangliste (unter „Analyse")

`GET /api/analysis/crossings/chancen`

Je Paar zählt die **jüngste** Kreuzung im Fenster. Ein Paar, das dreimal hin
und her gekippt ist, hat kein dreifaches Signal — es hat gar keines, und die
letzte Lage ist alles, was zählt.

### Die Bewährungsspalte

Ohne sie zeigt eine Rangliste nach Gewinn nur, was schon gelaufen ist; wer oben
einsteigt, kauft nach der Bewegung. Die Bewährung misst die **früheren**
Kreuzungen desselben Paares über `haltedauer` **gemeinsame** Bars — und
ausschließlich Kreuzungen **vor** dem Anzeigefenster, sonst benotete sich jede
Zeile selbst.

Ein Paar gilt als bewährt bei **drei** Bedingungen:

1. mindestens 8 frühere Kreuzungen
2. Trefferquote ≥ 55 %
3. mittlerer Ertrag **über den Kosten**

Die dritte fehlte zuerst. Gemessen am Bestand:

| | |
| ---: | --- |
| 154 | Paare mit genug Vorgeschichte |
| 54 | davon Trefferquote ≥ 55 % |
| **42** | davon auch nach Kosten positiv |

**Zwölf Paare bestehen die Trefferquote und verlieren Geld.** ETHFI-USD gegen
TLH trifft in 59 % der Fälle und bringt im Mittel −1,17 %. Das ist die schiefe
Verteilung, wörtlich: viele kleine Gewinne, seltene große Verluste.

### Zwei Sperren, ohne die die Ansicht wertlos ist

**Die Sprungprüfung.** Ein Ein-Bar-Sprung von mehr als 49 % bei einer Aktie
(146 % bei Krypto) ist kein Kurs, sondern ein Split oder ein Datenfehler.
Belegt an MNST: Yahoo liefert um den 2:1-Split vom 10.08.2026 eine **gemischte**
Reihe — 97,65 → 48,19 → 93,55 → 90,36 → 45,53, mit `adj_close = close`
durchgehend. Ein erneuter vollständiger Abruf ändert daran nichts; der Fehler
sitzt beim Anbieter. Ohne die Prüfung waren zwanzig der fünfundzwanzig besten
„Gewinne" nichts als „irgendetwas gegen MNST".

Ausgelassene Paare werden **mit Grund angezeigt**, nicht stillschweigend
entfernt — sonst verbirgt der Filter zugleich, dass eine Kursreihe kaputt ist.

**Die Grenze je Symbol.** MNT-USD verlor binnen eines Monats die Hälfte. Damit
standen **24 der 25** besten Zeilen auf „irgendetwas gegen MNT-USD". Jede
einzelne rechnerisch richtig — als Übersicht war es eine Zeile,
vierundzwanzigmal geschrieben. Voreinstellung: höchstens drei Zeilen je Wert,
gezählt auf **beiden** Seiten.

---

## Ansicht 2: Tausch

`GET /api/bestand`, `POST /api/bestand`, `GET /api/bestand/tausch`

Der Nutzer trägt ein, mit wieviel Kapital er in welchem Wert steckt. Gesucht
wird je Position die Gegenseite einer Kreuzung, bei der **genau dieser Wert die
untere Seite** geworden ist.

Entscheidend in der Abfrage: erst die jüngste Kreuzung je Paar nehmen, **dann**
prüfen, ob der gehaltene Wert unten liegt. Andersherum bekäme man ein
Verkaufssignal, das eine spätere Gegenkreuzung längst aufgehoben hat.

Der Bestand liegt in `dbo.holding`, nicht in `app_state`: Der
Oberflächenzustand wird nach 180 Tagen weggeräumt, und wer Positionen einträgt,
hat Daten eingegeben, keine Ansicht eingestellt.

Die Spalte **„wäre geworden"** schreibt das eingesetzte Kapital mit dem
Paargewinn fort. Sie beantwortet *was wäre gewesen*, nicht *was wird sein* —
und steht dort, weil ein Prozentsatz allein bei kleinen Beträgen zu
Fehlschlüssen verleitet: 40 Prozent auf 200 Euro sind achtzig Euro.

---

## Ansicht 3: Day Trading

`GET /api/daytrading/heute`

Die Seite beantwortet **nicht** „was soll ich heute kaufen", sondern die Frage
davor: *Trägt der Markt heute überhaupt einen Handel innerhalb des Tages —
nach Gebühren?* Diese Frage hat eine messbare Antwort. Die andere hat in
diesem System keine.

### Die entscheidende Kennzahl

Der Anteil der Stundenbars, deren Betragsbewegung den Rundlauf übersteigt:

| Klasse | Ø Bewegung | Median | **über Kosten** |
| --- | ---: | ---: | ---: |
| Aktien | 0,60 % | 0,32 % | **52 %** |
| Fonds und ETFs | 0,22 % | 0,10 % | **19 %** |
| Krypto | 0,33 % | 0,16 % | **34 %** |

Bei ETFs sind vier von fünf Stunden verloren, bevor die Richtungsfrage
überhaupt gestellt ist. Man kann eine Bewegung nicht handeln, die kleiner ist
als ihre Kosten.

### Warum nicht die Deep-Modelle

Deren Horizonte zählen in **Tagesbars** — das kürzeste Band sagt einen bis fünf
Tage voraus. Sie hier als Stundenmaß auszugeben wäre ein Faktor 24 daneben. Was
auf Stundenbasis wirklich gemessen wurde, sind die bewerteten Prognosen des
Ensembles mit `horizon_hours` 1 und 4 — 11.538 beziehungsweise 12.338 Stück.

### Handelsfenster aus den Daten, nicht aus einem Kalender

Ob eine Klasse gerade handelt, wird an der jüngsten Stundenbar abgelesen. Ein
hinterlegter Börsenkalender wäre eine zweite Wahrheit, die still veralten kann;
die jüngste Bar veraltet nicht.

### Die Fachliteratur

Sechs Fundstellen aus der Wissenssäule, gesucht nach dem, was die Zahlen oben
behaupten. Eine Stelle, die widerspricht, wäre wertvoller als eine, die
bestätigt — es widersprach keine. Aus *Trading Skills*: In einem
TSE-Datensatz über fünfzehn Jahre verloren rund 75 Prozent der Day-Trader Geld;
in einem brasilianischen Futures-Datensatz erzielten 0,5 Prozent Nettogewinne
über dem Mindestlohn.

---

## Was in die Wissenssäule kam

- **Marktneutrale Strategien im Kryptomarkt** — die Landkarte samt
  Kostenrechnung, aus der die Kostenschwelle stammt
- **39 arXiv-Arbeiten**, 1.262 Abschnitte: Paarhandel, statistische Arbitrage,
  Transaktionskosten, Intraday-Vorhersagbarkeit, Krypto-Mikrostruktur

Ausgewählt wurde nach Nähe zu dem, was diese Anwendung tut. Reine
Strommarkt-Arbeiten blieben draußen — eine Quelle, die nie eine Frage
beantwortet, kostet nur Einbettungszeit.

Der Bestand der Wissenssäule wuchs damit von 6.625 auf **7.894 Vektoren**.

## Was das Journal daraus macht

Ein neuer Abschnitt **„Wo umgeschichtet wurde"**: die Kreuzungen der letzten
vierzehn Tage, die dem doppelten Test standhalten, samt Kostenzeile und der
Zahl derer, die die Trefferquote bestehen und trotzdem scheitern.

## Vorgemerkt

- [ ] **Der Rundlauf ist fest verdrahtet.** Wer Rückvergütung als Steller
      bekommt oder auf einer teureren Börse handelt, braucht andere Zahlen.
      Gehört in die Oberfläche.
- [ ] **Die Bewährung mittelt über den ganzen Zeitraum.** Was vor zwei Jahren
      trug, muss heute nicht tragen; ein gleitendes Fenster wäre ehrlicher.
- [ ] **MNST ist weiterhin kaputt** und wird es bleiben, solange der Anbieter
      eine gemischte Reihe liefert. Eine lokale Reparatur über die
      Split-Ereignisse aus `events.splits` wäre möglich — sie braucht ein
      eigenes Feld für den Split-Zeitpunkt.
