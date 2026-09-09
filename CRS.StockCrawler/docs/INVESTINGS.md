# Investings — das virtuelle Depot

Kursansicht → **Investings**, neben *Linie*.

Man trägt ein, mit wieviel man heute in einen Wert ginge, und sieht fortan, was
daraus geworden wäre. Der Betrag lässt sich jederzeit tagesaktuell anpassen —
dadurch werden aus einer einzelnen Wette Zu- und Verkäufe, also eine Simulation.

---

## Die eine Entscheidung, aus der alles folgt

> Gespeichert wird ein **Buchungsjournal**, keine Momentaufnahme.

`holding` hält je Wert eine Zeile mit dem aktuellen Kapital — richtig für die
Tausch-Seite, die fragt „womit stecke ich wo drin". Hier ist die Frage eine
andere: *wie hat sich das entwickelt, seit ich es eingesetzt habe.*

Wer den Betrag in einer einzigen Zeile überschreibt, löscht bei jeder Anpassung
genau die Geschichte, die er sehen will. Der Verlauf liesse sich danach nicht
mehr rechnen — nicht ungenau, sondern **gar nicht**.

Eine Buchung ist deshalb ein Ereignis: *an diesem Tag, zu diesem Kurs, dieser
Betrag.* Der Bestand an Anteilen ist ihre Summe, der heutige Wert Anteile mal
heutiger Kurs. Bauart eines Kontoauszugs, und aus demselben Grund.

---

## Das Feld trägt den Soll-Stand, nicht die Veränderung

Wer 1.000 stehen hat und **1.500** einträgt, legt 500 nach; wer **0** einträgt,
löst auf. Das Feld ist mit dem heutigen Stand vorbelegt — bestätigen ohne
Änderung bucht nichts.

Ein Feld „wieviel dazu" verlangte vom Nutzer die Subtraktion, die die Anwendung
selbst machen kann, und jeder versehentliche zweite Klick wäre eine echte zweite
Buchung.

**Keine Rückdatierung.** Gebucht wird zum jüngsten bekannten Tagesschluss,
dessen Zeitstempel mit in der Zeile steht. Wer zu einem vergangenen Datum buchen
dürfte, betriebe keine Simulation mehr, sondern eine Rückrechnung mit bekanntem
Ausgang.

---

## Die Spalten

| Spalte | was drinsteht |
| --- | --- |
| **Kurs** | jüngster Tagesschluss, darunter sein Zeitstempel |
| **Nulllinie** | noch investierter Betrag je Anteil — der Kurs, ab dem die Position im Plus ist |
| **Einsatz** | eingezahlt minus entnommen |
| **Stand heute** | Anteile mal Kurs |
| **Gewinn** | Stand minus Einsatz, dazu bezogen auf das jemals Eingezahlte |

### Warum „Nulllinie" und nicht „Einstand"

Die Spalte hiess zuerst so, und es war falsch. Nach einer Entnahme fällt die
Zahl, weil weniger Geld drinsteckt — nicht, weil jemand billiger gekauft hätte:
Bei NVDA sprang sie von **200,85 auf 191,34**, nachdem 3.391 entnommen waren. Zu
191,34 hat nie ein Kauf stattgefunden.

Ein Einstandskurs ändert sich beim Verkauf **gar nicht**. Diese Zahl schon, und
sie soll es auch — sie beantwortet „ab welchem Kurs bin ich im Plus".

### Zwei Nenner, und der Unterschied zählt

*Einsatz* ist netto und die Zahl, gegen die der heutige Stand zu halten ist.
*Eingezahlt* ist brutto und der ehrliche Nenner für einen Prozentsatz: Wer 1.000
einsetzt, 500 entnimmt und heute bei 600 steht, hat nicht 20 % verloren, sondern
10 % gewonnen.

Der Prozentsatz ist **keine Zeitrendite**. Wer nachlegt, verschiebt den Nenner,
ohne dass sich die Leistung des Wertes geändert hätte.

---

## Der Verlauf

**Stundenauflösung für die jüngsten drei Tage**, Tagesauflösung davor. Zuerst
lief die Kurve nur auf Tagesschlüssen — und ein Depot, das heute eröffnet
wurde, hatte damit genau **einen** Punkt und zeichnete keine Linie. Das
widersprach dem, was daneben stand: Der Wert bewegt sich mit jeder Stundenbar.

Die feinen Zeitpunkte kommen aus den Kursen selbst, nicht aus einer erzeugten
Stundenfolge — eine Stunde ohne Bar wäre sonst ein Punkt, der die
Fortschreibung wiederholt und die Kurve waagerecht ausfranst. Der letzte Punkt
ist immer *jetzt*, sonst endete die Kurve auf der letzten vollen Stunde,
während die Tabelle daneben den aktuellen Stand zeigt.

> **Die Reihe beginnt beim ersten Vorgang, nicht um Mitternacht.** Mit dem
> Datum statt dem Zeitstempel begann sie Stunden vor der ersten Einzahlung, ihr
> erster Punkt stand bei null, und die Kurve sprang senkrecht nach oben — sie
> behauptete einen Verlust, den es nie gab. Genau der Fehler, den ich beim
> Zeichnen der Depotlinien vermieden und hier wieder eingebaut hatte.

Zwei Linien je Währung: **Vermögen** durchgezogen, **eingesetzt** gepunktet. Ihr
Abstand ist der Gewinn. Eine einzelne Vermögenslinie sagt nichts, solange man
nicht weiss, wieviel hineingegangen ist — dieselbe Lehre wie beim aufsummierten
Vorsprung in der Rückschau.

**Fortgeschrieben, nicht geschnitten.** Ein Depot aus Aktie und Krypto hat
samstags nur für die Krypto einen frischen Kurs. Die Regel dieses Projekts —
*über mehrere Werte nur auf gemeinsamen Handelszeitpunkten* — gilt hier
ausdrücklich **nicht**: Sie schützt Messungen von Zusammenhängen davor, den
Kalender statt den Markt zu messen. Dies ist keine Messung, sondern eine
Addition. Die Aktie ist am Samstag nicht wertlos, sie wird nur nicht gehandelt.
Wer hier schnitte, bekäme ein Depot, das an jedem Wochenende auf den
Kryptoanteil zusammenfiele.

> Was aus dieser Reihe **nicht** werden darf, ist eine Statistik über
> Tagesrenditen: Die fortgeschriebenen Tage sind keine Beobachtungen, und eine
> Schwankungsbreite darüber wäre systematisch zu klein.

---

## Währung

Betrag in **EUR oder USD**, je Position eine; ein Wechsel mitten im Lauf wird
abgewiesen, weil er Beträge in zwei Zähleinheiten addierte.

Summiert wird **je Währung getrennt**. Der Kursanstieg eines Wertes vermehrt den
Einsatz in jeder Zähleinheit gleichermassen — die Bewegung des **Wechselkurses**
bildet dieses System nicht ab, weil es keine Devisenreihen führt. Ein Gesamtwert
über EUR- und USD-Positionen wäre eine Zahl, die niemand nachrechnen kann.

---

## Was die Seite bewusst nicht rechnet

Keine Gebühren, kein Schlupf, keine Steuer. Sie beantwortet *wie hat sich der
Kurs auf meinen Einsatz ausgewirkt* und nicht *was hätte ich verdient* — für die
zweite Frage gibt es `Handelskosten`, und sie gehört zu den Handelsseiten. Eine
halbe Kostenrechnung wäre schlechter als keine, weil sie nach einer
vollständigen aussieht.

---

## Welche Werte erscheinen

Die **gewählten** Werte — auch ohne Position, denn in eine Zeile, die es nicht
gibt, trägt man nichts ein — **und alle mit Buchungen**, auch wenn sie nicht
gewählt sind. Eine Position, die aus der Übersicht fällt, weil jemand die
Auswahl geändert hat, sähe aus wie verlorenes Geld.

Ein Wert ohne Buchung zeigt in Stand und Gewinn **„–"**, nicht „0,00". Kein
Bestand ist nicht dasselbe wie ein Bestand im Wert von null. Eine aufgelöste
Position dagegen steht berechtigt bei null: Dort sind die Anteile weg, aber der
Ertrag der Rundreise steht noch im Gewinn.

---

## Das Journal

**Zwei Journale:** eines für das manuelle Depot, **eines für den Autopiloten**
— dort stehen die Vorgänge aller drei Strategien in einer Liste, jede Zeile mit
ihrer Strategie. Der Nutzer sieht den Autopiloten als eine Sache; drei Fenster
nacheinander zu öffnen wäre eine Trennung, die nur im Datenmodell existiert.

Jedes öffnet ein Fenster mit **allen** Vorgängen — Ein- und Auszahlungen wie
Käufe und Verkäufe, jüngste zuerst.

| Spalte | was drinsteht |
| --- | --- |
| Betrag | was in den Kurs ging |
| Gebühr | was der Vorgang an Reibung gekostet hat |
| Kasse | was das Konto gekostet hat — Betrag **plus** Gebühr |
| Konto danach | der Stand **nach** diesem Vorgang |

**Warum alles zusammen.** Es gab bereits ein Journal je Wert und eines je
Kassenwährung. Beide beantworten Teilfragen. Wer prüfen will, ob der Kontostand
stimmt, muss den ganzen Weg sehen — eingezahlt, gekauft, Gebühr, verkauft — in
der Reihenfolge, in der es passiert ist.

**Der Kontostand steht in jeder Zeile.** Ohne ihn ist ein Journal eine Liste von
Ereignissen; mit ihm eine Rechnung, die man nachrechnen kann. Von unten gelesen:

```
einzahlung  +1.000,00  →  1.000,00
kauf MNST     −166,66  →    833,34
kauf GLW      −166,66  →    666,68
…
kauf BX       −166,66  →      0,04
```

Der laufende Stand wird **vorwärts** gerechnet und danach umgedreht.
Rückwärts wäre derselbe Betrag mit umgekehrtem Vorzeichen — und genau dort
schleicht sich ein Vorzeichenfehler ein, den niemand sieht, weil das Ergebnis
plausibel bleibt.

**Der Kontostand gilt je Strategie, und die Summenzeile auch.** Die erste
Fassung addierte die drei zu „Konto 1.000,08" — die Summe dreier
Gegenrechnungen auf dasselbe Geld, also genau die Doppelzählung, vor der der
Text zwei Zeilen darunter warnt. Jetzt eine Summenzeile je Strategie.

Ein natives `<dialog>`: Es bringt Esc, den Hintergrundklick und die Fokusfalle
von sich aus mit. Ein selbstgebautes Overlay müsste das alles nachbilden, und
die Tastaturbedienung wäre das Erste, was dabei vergessen wird.

---

## Schnittstelle

| | |
| --- | --- |
| `GET /api/invest/?werte=1,2,3` | Positionen und Summen je Währung |
| `POST /api/invest/` | `{symbol, betrag, waehrung}` — `betrag` ist der Soll-Stand |
| `GET /api/invest/verlauf` | eine Reihe je Währung |
| `GET /api/invest/journal` | alle Vorgänge eines Depots mit laufendem Kontostand |
| `GET /api/invest/buchungen/{assetId}` | das Journal eines Wertes |
| `DELETE /api/invest/{assetId}` | verwirft die Simulation dieses Wertes |

Tabelle `dbo.invest_buchung`, Migration `036_investing.sql`.
