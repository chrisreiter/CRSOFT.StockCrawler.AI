# Die Erhaltungsannahme, dreifach gemessen

**CRSOFT.StockCrawler**, Stand 22.08.2026

Die Annahme lautete: Bleibt das Gesamtvolumen des Marktes konstant, dann muss in
der heutigen Bewegung aller Kurse genau die morgige Bewegung eines einzelnen
stecken — man müsse nur lange genug darauf trainieren.

Die erste Hälfte davon stimmt. Die zweite nicht.

## Was gemessen wurde

Drei voneinander unabhängige Verfahren, jedes auf Zeiträumen ausgewertet, die
die Anpassung nie gesehen hat.

| Verfahren | Datenbasis | gleichzeitig | einen Tag voraus |
| --- | --- | ---: | ---: |
| Ridge-Regression | 98 Aktien, 5.958 Tage | R² **0,355** (bis 0,641) | R² **0,0039** |
| Netz, alle Werte | 278 Werte, 577 Tage | Fehlerverhältnis **0,915**, Richtung **66,5 %** | 1,025 / 49,5 % |
| Netz, nur Aktien | 159 Werte, 4.010 Tage | — | 1,047 / 51,1 % |

Faktor 91 zwischen gleichzeitig und einen Tag voraus.

## Warum das kein Widerspruch ist

Eine Erhaltungsgröße verknüpft Größen **zum selben Zeitpunkt**. Dass die Summe
heute feststeht, legt fest, wie sich die heutige Bewegung auf die Einzelwerte
verteilt — und genau das misst die linke Spalte, sehr deutlich. Über die
morgige Verteilung sagt sie nichts.

Die Modenanalyse hatte dasselbe von der anderen Seite gezeigt: Ein gemeinsamer
Modus erklärt 53 % der Bewegung bei einer Phasenstreuung von 2,8 Bars. Die Werte
bewegen sich *gemeinsam*, nicht *nacheinander*. Wo kein Vorlauf ist, ist nichts
vorherzusagen.

## Die Kontrolle, die das absichert

Der naheliegende Einwand gegen ein Negativergebnis ist immer derselbe: falsch
gerechnet. Deshalb wurde dieselbe Architektur, auf denselben Daten, mit
demselben Zeitsplit auf die **gleichzeitige** Bewegung angesetzt — der Zielwert
aus seiner eigenen Eingabe entfernt, sonst wäre die Aufgabe eine
Nachschlagetabelle.

```
    Fehlerverhältnis 0,9149   Richtung 66,486 %   trägt
```

Kette, Architektur, Merkmale und Daten sind also in Ordnung. Es scheitert allein
der Schritt um einen Tag.

## Handelskalender, wieder

Beim ersten Zuschnitt kamen bei 90 % geforderter Beteiligung nur **577 Tage ab
2022-08** zustande — nicht wegen der Wochenenden, sondern weil das Universum
über die Jahre gewachsen ist. Getrennt nach Anlageklasse reicht der Aktienteil
bis 2010 zurück: 4.010 gemeinsame Handelstage, 159 Werte, 2,5 Mio. Beispiele.
Auch dort kein Vorsprung.

Aufruf:

```bash
python ml/train_cross.py --csv D:/_data/stockcrawler/features_1d.csv \
    --klasse stocks --min-days 4000 --coverage 0.98 --horizons 1,2,3,5
python ml/train_cross.py ... --contemporaneous   # die Kontrolle
```
