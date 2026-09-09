# Langfrist, Bot-Herde und der Umkehrschluss

**CRSOFT.StockCrawler**, Stand 23.08.2026

Drei Ansichten, die nicht prognostizieren, sondern **nachrechnen** — und deren
Wert darin liegt, dass sie an mehreren Stellen ein sauberes Nein liefern.

---

## Langfrist

`GET /api/langfrist/uebersicht?jahre=5&minTage=400&limit=40`

### Warum ausgerechnet diese Seite trägt

Der zentrale Befund des Projekts lautet, dass die blosse Drift — die mittlere
Rendite eines Zeitraums — jedes Prognosemodell schlägt. Auf kurze Sicht ist das
ein Negativbefund. Auf lange Sicht **ist die Drift die Rendite.** Diese Seite
misst also genau die Größe, die sich als einzige als tragfähig erwiesen hat.

### Die drei Fallen, und wie sie behandelt sind

**Erstens die Auswahl.** Das verfolgte Universum ist die *heutige* Rangliste
nach Marktkapitalisierung. Jeder Wert darin hat überlebt; was unterging, steht
gar nicht auf der Liste. Der Hinweis steht **oben** auf der Seite und nennt eine
Zahl: Der mittlere verfolgte Wert kam auf 10,5 Prozent im Jahr, VOO auf 11,4.
Dass der Median *darunter* liegt, spricht in diesem Fenster ausnahmsweise gegen
eine starke Verzerrung — der Mittelwert von 17,2 Prozent dagegen ist von den
Kryptowerten getrieben.

**Zweitens die Anpassung.** Wer aus 283 Werten die beste Kombination sucht,
findet immer eine, die im betrachteten Zeitraum glänzt. Die Auswahl trifft
deshalb nur die ersten 60 Prozent der Reihe; gemessen wird auf den letzten 40.
Nur Zeilen mit dem Vermerk **Sperrbereich** sagen etwas.

**Drittens die gemeinsame Zeitachse.** Der erste Entwurf schnitt die Achsen
aller 283 Reihen — und behielt von fünf Jahren **48 Handelstage**. Ein einziger
junger Kryptowert schneidet die Achse für alle ab. Richtig ist: Kennzahlen eines
*einzelnen* Wertes brauchen keine gemeinsame Achse. Nur ein Korb braucht sie,
und dort umfasst sie genau seine Mitglieder — eine kleine, bewusst gewählte
Menge, also genau der Fall, für den ein Schnitt gedacht ist.

### Was die Zahlen sagen

Fünf Jahre, Sperrbereich ab 22.08.2024:

| Kombination | p. a. | tiefster Einbruch | Rendite/Einbruch | engste Bindung |
| --- | ---: | ---: | ---: | ---: |
| Breiter Markt (SPY) | 16,8 % | 19,0 % | 0,88 | — |
| Aktien + Anleihen (SPY, IEF) | 6,7 % | 9,8 % | 0,68 | 0,05 |
| 60/40 | 8,8 % | 11,4 % | 0,78 | 1,00 |
| **Drei Richtungen (SPY, IEF, GLD)** | **15,5 %** | **9,6 %** | **1,62** | 0,17 |
| Gesucht, 5 Werte | 35,5 % | 12,1 % | 2,93 | 0,99 |

Die aussagekräftigste Zeile ist **Drei Richtungen**: praktisch dieselbe Rendite
wie der breite Markt bei halb so tiefem Einbruch. Das ist der ganze Ertrag der
Streuung, gemessen und nicht behauptet.

Die gesuchten Körbe sehen besser aus und sind es nicht im selben Maß: Ihre
engste Bindung liegt bei 0,99 — XAUT-USD und PAXG-USD sind beide goldgedeckt.

### Die ehrlichste Spalte: die Korrelationsdrift

| Korb | engste Bindung bei Auswahl | im Sperrbereich | Drift |
| --- | ---: | ---: | ---: |
| Gesucht, 3 Werte | 0,881 | **0,989** | +0,108 |
| Drei Richtungen | 0,437 | **0,171** | −0,266 |

XAUT-USD gegen PAXG-USD korrelierte in der Auswahlhälfte mit 0,838 und im
Sperrbereich mit 0,984. **Die Streuung, auf die sich die Auswahl stützte, gab es
zum Messzeitpunkt nicht mehr.** Das ist kein Rechenfehler, sondern die bekannte
Schwäche jeder korrelationsbasierten Streuung: Korrelationen laufen gegen eins,
wenn es darauf ankommt.

Die Grenze bei der Auswahl liegt deshalb auf der **höchsten** Paarkorrelation
(0,90) und nicht auf der mittleren — die mittlere blieb bei 0,39 unauffällig,
weil ein dritter Wert sie herunterzog.

---

## Bot-Herde

`GET /api/herde/ausloeser`, `POST /api/herde/ausloeser/rechnen?jahre=5`

### Die Frage

Hunderte automatischer Handelssysteme folgen denselben, öffentlich bekannten
Auslösern. Wenn viele zugleich handeln, muss das eine Spur hinterlassen. Das ist
prüfbar.

Geprüft werden sieben Auslöser: goldenes Kreuz und Todeskreuz (SMA 50 gegen
200), RSI unter 30 und über 70, Bollinger-Ausbruch nach oben und unten, neues
52-Wochen-Hoch. Gemessen wird der **Median** der Rendite über 1, 5 und 20
Handelstage gegen den Median über *alle* Tage derselben Werte im selben
Zeitraum.

### Warum Median

Der erste Entwurf mittelte und lieferte für Krypto „RSI unter 30 → **+45,0 %**
am Folgetag" bei einem Volumenfaktor von **15.591**. Beides beschreibt das Ende
sterbender Kleinstwerte, nicht den Markt: Ein Kurs von 0,0000 erzeugt beim
kleinsten Sprung dreistellige Prozentzahlen. Mit dem Median: +0,005 Prozent und
Faktor 0,75.

### Das Ergebnis, Aktien, fünf Jahre

| Auslöser | Fälle | Δ 20 Tage | Richtung 20 | Volumen |
| --- | ---: | ---: | ---: | ---: |
| **Bollinger-Ausbruch nach unten** | 6.117 | **+1,006 %** | **0,40** | 1,30× |
| **RSI unter 30** | 3.714 | **+0,677 %** | 0,57 | 1,04× |
| **Bollinger-Ausbruch nach oben** | 8.265 | **+0,354 %** | 0,56 | 1,17× |
| Todeskreuz | 388 | +0,264 % | **0,43** | 0,94× |
| Neues 52-Wochen-Hoch | 8.810 | +0,168 % | 0,57 | 1,03× |
| Goldenes Kreuz | 373 | +0,130 % | 0,56 | 0,88× |
| RSI über 70 | 4.953 | +0,084 % | **0,43** | 0,94× |

**Zwei Befunde.**

*Die Spur existiert.* Das Volumen am Auslösetag liegt beim Bollinger-Ausbruch
nach unten 30 Prozent über dem üblichen, nach oben 17 Prozent. Bei den
Kreuzungen der gleitenden Mittel dagegen **unter** dem üblichen (0,88× / 0,94×)
— dieser Auslöser bewegt niemanden.

*Die Verkaufssignale zeigen in die falsche Richtung.* Jeder der sieben Auslöser
hat einen positiven Zwanzigtagesüberschuss — auch die bärischen. Deren
Richtungstrefferquote liegt bei 0,40 bis 0,43, also **unter** dem Münzwurf. Die
Herde verkauft in den Einbruch, schießt über, und es dreht zurück.

*Und die Einschränkung.* Von sieben Auslösern schlagen **drei** den Rundlauf von
0,3 Prozent. Die übrigen sind messbar und nicht handelbar. Dazu: Die Fenster
überlappen — die Fallzahl ist keine Fallzahl, und ein t-Wert stünde um rund die
Wurzel der Fensterlänge zu hoch. Deshalb steht bei jeder Zeile die Anzahl und
kein t-Wert.

---

## Der Umkehrschluss

`GET /api/herde/umkehr`, `POST /api/herde/umkehr/rechnen`

### Die Frage

Wenn die meisten Prognosen nichts taugen — lässt sich daraus invers etwas
ableiten? Der Gedanke ist naheliegend und die Antwort messbar.

**Ein Signal umzudrehen nützt nur, wenn es zuverlässig falsch ist.** Eine
Trefferquote von 49,8 Prozent umgedreht ergibt 50,2 Prozent, und die Kosten
bleiben dieselben. Gesucht sind also Paare mit einer Trefferquote *signifikant*
unter der Hälfte.

### Das Ergebnis

26.130 Paare mit mindestens acht Kreuzungen, Horizont 28 Kalendertage:

| | |
| ---: | --- |
| 0,5096 | mittlere Trefferquote |
| 291 | Paare mit z < −1,96 |
| 87 | Paare mit z > +1,96 |
| **653** | **allein durch Zufall erwartet, je Seite** |

**Nein.** Es gibt *weniger* auffällig falsche Paare, als der Zufall bei so
vielen Prüfungen hervorbringt. Ohne die Korrektur für Mehrfachvergleiche hätte
man 291 „Funde" gemeldet und wäre auf sie hereingefallen.

### Die Ausnahme, und warum sie keine ist

Die zwanzig extremsten sind ausnahmslos vom selben Typ:

| Paar | n | Trefferquote | z | invertiert nach Kosten |
| --- | ---: | ---: | ---: | ---: |
| XAUT-USD / IAU | 23 | 0,000 | −4,80 | +0,40 % |
| PAXG-USD / GLD | 34 | 0,118 | −4,46 | +0,34 % |
| GRAM-USD / USDG-USD | 59 | 0,254 | −3,78 | +11,05 % |
| LTC-USD / USDG-USD | 41 | 0,220 | −3,59 | +11,64 % |

Goldgedeckte Marke gegen Gold-ETF; irgendetwas gegen eine an den Dollar
gebundene Marke. **Wo beide Seiten dasselbe abbilden, ist das Zurückschwingen zu
erwarten** — das heißt Paarhandel und nicht „das Kreuzungssignal ist falsch
herum". Die Ansicht kennzeichnet solche Paare als *nahe verwandt*, statt sie
wegzulassen: Sie sind der interessanteste Teil des Ergebnisses.

---

## Was nicht gebaut wurde, und warum

### Ein viertes Modell mit semantischem Wissen

Geprüft und **begründet verworfen**. Die Semantik-Säule deckt **115 von 1.827
Handelstagen** ab — 6,3 Prozent —, und davon fast alles aus den letzten zehn
Tagen: 382 Artikel am 22.08., drei am 15.08. Ein Merkmal, das nur für die
jüngste Vergangenheit existiert, lernt das Datum und nicht die Nachricht.

Was es bräuchte, wäre eine Quelle mit echter Historie. GDELT liefert
Tagesreihen ab 2015; geholt wurden 945 Punkte für 2024 bis 2026 (die
Schnittstelle drosselt hart — zwischen zwei Abfragen gehört mehr als eine
Minute Pause). Damit ließ sich die eigentliche Frage beantworten, **bevor**
jemand ein Modell darauf baut.

Gemessen wurde die tägliche Änderung der Nachrichtentonalität zum Suchbegriff
*stock market OR equities* gegen die Rendite desselben Tages und die des
Folgetages:

| Reihe | Wert | gleichzeitig | prognostisch |
| --- | --- | ---: | ---: |
| Tonalität | SPY, n = 648 | **0,1365** | 0,0419 |
| Tonalität | BTC-USD, n = 944 | −0,0100 | 0,0080 |
| Meldungsmenge | SPY, n = 648 | −0,0167 | −0,0220 |
| Meldungsmenge | BTC-USD, n = 944 | 0,0380 | −0,0507 |

Bei 648 Beobachtungen liegt die Schwelle, ab der sich eine Korrelation von null
unterscheiden lässt, bei rund 0,077. **Genau ein Wert in dieser Tabelle
überschreitet sie: die gleichzeitige Tonalität gegen SPY.** Alle vier
prognostischen Werte liegen darunter, und die Meldungsmenge hat auch
gleichzeitig nichts zu sagen — es ist der Ton, nicht die Zahl der Meldungen.

Das ist dieselbe Signatur wie beim Querschnitt (R² 0,355 gleichzeitig gegen
0,0039 morgen, Faktor 91) und bei den Kurvenereignissen (Lift 9,0 bei einem
Medianabstand von +1 Bar). Nachrichten hängen mit dem zusammen, was heute
passiert — und bis die Nachricht gemessen ist, hat die Bewegung
stattgefunden.

**Damit ist das vierte Modell nicht nur mangels Daten verworfen, sondern
mangels Signal.** Das ist die stärkere Aussage: Sie gilt auch dann noch, wenn
die eigene Semantik-Säule in einem Jahr genug Historie hat.

### Eine Seite für Neuzugänge

`first_seen_utc` steht bei allen 338 Werten auf dem Tag, an dem die Datenbank
gefüllt wurde — es ist kein Erstnotiz-Datum. Schwerer wiegt: Das Universum ist
die Rangliste nach Marktkapitalisierung, ein Wert taucht also erst auf,
*nachdem* er gestiegen ist. Eine Statistik über „Neuzugänge" misst damit
Gewinner mit bereits gelaufenem Anstieg.

Was es bräuchte: ein eigenes Feld für die Erstnotiz, Listing-Ankündigungen als
Feed-Art in der Semantik-Säule, und die **vollständige** Kohorte einschließlich
der Fehlschläge. Ohne die Fehlschläge ist jede Basisrate optimistisch verzerrt.
