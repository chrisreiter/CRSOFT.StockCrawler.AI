# Weltbestand und der Zeitzonen-Vorlauf

**CRSOFT.StockCrawler**, Stand 23.08.2026

Zwei zusammenhängende Arbeiten. Die erste räumt eine stille Einschränkung aus,
die zweite beantwortet die Frage, die dadurch überhaupt erst stellbar wurde.

---

## Der Bestand war ausschließlich amerikanisch

Aufgefallen ist es an einer einfachen Frage: *Warum sehe ich Rheinmetall nicht?*

Die Antwort stand in einer Zeile SQL:

```sql
SELECT COUNT(*) FROM dbo.asset WHERE symbol LIKE '%.%';   -- 0
```

**Kein einziges Symbol mit Punkt** — also kein einziges nicht-amerikanisches
Papier. Von 338 Werten trugen 100 das Land „US", der Rest keines (Krypto und
ETFs).

### Warum

`YahooScreenerUniverseProvider` benutzt sechs vordefinierte Screener:
`most_actives`, `undervalued_large_caps`, `growth_technology_stocks`,
`day_gainers`, `day_losers`, `aggressive_small_caps`. Sie kennen **keinen
Regionsparameter**. Was sie liefern, ist der US-Markt.

Und der Weg, den die README versprach — „europäische Papiere lassen sich über
die Auswahl ergänzen" — **gab es nicht**: weder Endpunkt noch Bedienelement.

### Was gebaut wurde

**`POST /api/assets/aufnehmen`** mit `{ "symbol": "RHM.DE" }`. Schlägt zuerst
bei Yahoo nach und legt erst dann an — ein Symbol, das der Kursanbieter nicht
kennt, erzeugt sonst einen Wert ohne Bars, und davon führt der Bestand schon
genug mit. Kennt Yahoo es nicht, nennt die Fehlermeldung die Börsenkürzel:
`.DE` Xetra, `.VI` Wien, `.SW` Schweiz, `.L` London, `.T` Tokio.

**`POST /api/assets/welt`** liest `infra/welt.json` — eine kuratierte Liste von
rund 280 Symbolen aus dreizehn Regionen. Als Datei neben der Anwendung, nicht
im Code: Wer eine Börse ergänzt, soll das ohne Übersetzen tun können. Dieselbe
Entscheidung wie bei `quellen.json`.

Ergebnis:

| | |
| ---: | --- |
| 269 | angelegt |
| 2 | Symbole unbekannt (IIA.VI, ROG.SW) — **gemeldet, nicht verschluckt** |
| 16 | Länder |
| 12 | Währungen: EUR 144, CHF 24, GBp 23, JPY 22, SEK/HKD/CAD/AUD je 11 |

Danach 593 verfolgte Werte, 2.875.163 Kursbars in 5:35 nachgeladen.

### Zwei Fallen dabei

**`upsert_asset` kennt die Spalte `is_tracked` nicht.** `IsTracked = true` am
Modell zu setzen bleibt wirkungslos. Beim ersten Weltlauf waren deshalb 269
Werte angelegt und **keiner verfolgt** — sie standen in der Datenbank und
tauchten nirgends auf. Das Einschalten ist ein eigener Aufruf.

**Fremdwährungen sind harmlos, aber nicht überall.** Für Korrelationen,
Kreuzungen und Paargewinne ist die Währung unerheblich — sie rechnen mit
Renditen. Wer Kurse addiert oder Kapital verteilt, muss umrechnen. Die Antwort
der Aufnahme sagt das ausdrücklich, wenn die Währung nicht USD ist. Nebenbei:
London notiert in **Pence** (`GBp`), nicht in Pfund.

---

## Der Zeitzonen-Vorlauf

Die Frage kam vom Nutzer und war die erste in diesem Projekt, die der
Datenbestand vorher gar nicht beantworten konnte:

> *Ergaben sich vielleicht deshalb keine Prognosetendenzen, weil immer nur
> innerhalb der USA verglichen wurde?*

### Warum die Vermutung trägt

Bisher lautete der Befund: gleichzeitig R² 0,355, morgen 0,0039 — Faktor 91.
Aber „gleichzeitig" bedeutete bei lauter US-Werten **dieselbe Sitzung**. Da
*kann* es keinen Vorlauf geben, weil es keine Reihenfolge gibt.

| Börse | Schluss UTC |
| --- | ---: |
| Tokio | 06:00 |
| Xetra, Wien, Zürich | 16:00 |
| New York | 20:00 |

Tokios Bar vom Tag *d* liegt **vierzehn Stunden vor** New Yorks Bar vom Tag *d*.

### Die Falle, und sie ist ernst

Alle Tagesbars sind über `BarNormalizer` auf **Stunde 0** gerastert. Tokio und
New York sitzen auf demselben Rasterpunkt. Wer daraus eine „gleichzeitige"
Korrelation macht, misst in Wahrheit vierzehn Stunden Vorsprung — und wer
daraus ein Merkmal baut, hat **Lookahead in Reinform**. Es käme ein
spektakuläres Ergebnis heraus, das nichts wert ist.

Die Reihenfolge kommt deshalb aus der **Börse** und nicht aus dem Zeitstempel.

### Was gemessen wird

Drei Renditen je Region und Tag, gleich gewichtet über ihre Mitglieder:

| | |
| --- | --- |
| `r_cc` | log(Schluss ÷ Vortagesschluss) — der ganze Tag |
| `r_co` | log(Eröffnung ÷ Vortagesschluss) — **der Sprung über Nacht** |
| `r_oc` | log(Schluss ÷ Eröffnung) — **der handelbare Teil** |

Gleich gewichtet und nicht nach Marktkapitalisierung: Sonst misst man in
Amerika im Wesentlichen die sieben größten Technologiewerte.

### Das Ergebnis, zehn Jahre

| früher | später | Vorsprung | **Überlappung** | Tage | naiv | Sprung | **handelbar** | Gegenprobe |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Asien | Amerika | 12 h | **0 h** | 2.559 | 0,263 | **0,396** | **−0,010** | 0,465 |
| Asien | Europa | 8 h | 1 h | 2.566 | 0,433 | **0,598** | 0,041 | 0,328 |
| Europa | Amerika | 4 h | 3 h | 2.543 | 0,622 | **0,703** | 0,197 | 0,165 |

**Der Vorlauf ist echt und groß — und er landet vollständig im
Eröffnungssprung.** Asien gegen Amerikas Eröffnung: 0,396. Europa gegen
Amerikas Eröffnung: 0,703.

**Danach bleibt nichts.** Die einzige Strecke ohne gemeinsame Handelszeit —
Asien nach Amerika — lässt **−0,010** übrig. Die Schwelle, ab der sich das von
null unterscheiden liesse, liegt bei 0,039. Was im Eröffnungssprung steckt, war
zum Schluss des Vortages noch nicht da; man hätte es nicht kaufen können.

### Die Überlappung, ohne die es ein Fehlschluss wäre

Die Zeile *Europa → Amerika* mit 0,197 handelbarer Korrelation sah zunächst wie
ein Fund aus. Sie ist keiner: **Xetra schließt 16:30 UTC, die NYSE öffnet
13:30.** Drei Stunden handeln beide zugleich. Europas Tagesrendite enthält
damit Zeit, die *innerhalb* des amerikanischen Fensters liegt — das ist
Gleichzeitigkeit und kein Vorlauf.

Die Spalte `stunden_ueberlappung` steht deshalb in der Tabelle, und die Ansicht
kennzeichnet solche Zeilen als **nicht als Vorlauf lesbar**. Weggelassen werden
sie nicht: Es ist die auffälligste Zahl der Tabelle, und sie zu verstecken hiesse,
dem Leser die Möglichkeit zu nehmen, den Einwand selbst zu prüfen.

### Die Gegenprobe

Ohne sie wäre der Rest nichts wert. Umgekehrt gemessen — Amerika am Vortag
gegen Asien heute — kommt **0,465** heraus, die stärkste Zahl der Tabelle.
Amerika führt Asien über Nacht, und das ist unstrittig. **Die Rechnung
funktioniert also; der Markt ist an dieser Stelle leer.**

---

## Was die Antwort auf die ursprüngliche Frage ist

Die Vermutung war richtig im Mechanismus und falsch in der Hoffnung.

Es gibt einen echten, großen Vorlauf zwischen den Zeitzonen — er existierte im
alten Bestand nur deshalb nicht, weil alle Werte im selben Fenster handelten.
Nur ist er **bis zum Eröffnungskurs vollständig eingepreist**. Der
Übernachtsprung ist die Stelle, an der die Information ankommt, und dort ist
sie für jemanden, der zum Schluss des Vortages kaufen müsste, nicht mehr zu
holen.

Das reiht sich in die übrigen Befunde ein, und zwar mit derselben Signatur:

| Messung | gleichzeitig | prognostisch |
| --- | ---: | ---: |
| Querschnitt aller Kurse (R²) | 0,355 | 0,0039 |
| Nachrichtentonalität gegen SPY | 0,137 | 0,042 |
| Zeitzonen, Asien → Amerika | 0,396 (Sprung) | −0,010 |

Dreimal dasselbe: **Der Zusammenhang ist da, und er ist bereits eingepreist,
bevor man handeln könnte.**
