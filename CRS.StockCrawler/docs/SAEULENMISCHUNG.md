# Die Säulenmischung

Wie aus mehreren Säulen **eine** Prognose wird — und warum die meisten davon
nichts beitragen, obwohl sie eingebaut sind.

---

## Die Regel

> Der Regler bestimmt, **wie viel** von etwas Brauchbarem einfliesst.
> Ob etwas brauchbar ist, entscheidet die **Messung**.

Gewichtet wird mit `Regler × gemessener Verdienst`. Der Verdienst steht in
`dbo.pillar_skill` und kommt aus einem Vergleich gegen eingetroffene Kurse. Eine
Säule mit Verdienst null bewegt nichts — auch bei Regler auf hundert.

Das ist keine Vorsicht, sondern die Lehre aus dem ersten Entwurf der
Säulengewichte: Sie waren eine Einstellung ohne Gegenprobe, und eine Säule ohne
jeden Nachweis ging mit demselben Anteil ein wie die einzige mit gemessener
Trefferquote.

---

## Zwei Arten von Beitrag

| Art | wer | wie sie eingeht |
| --- | --- | --- |
| **Grundlage** | `learning`, `math` | schätzt die Rendite selbst; gewichtet gemittelt |
| **Aufschlag** | `knowledge`, `semantic` | sagt, was **zusätzlich** zum üblichen Gang zu erwarten ist; kommt obendrauf |

### Warum die Trennung nötig war

Der erste Entwurf mittelte alles gewichtet. Bei **FCX** schätzte Säule 1
−15,7 % über einen Monat, die Wissenssäule +0,25 % aus vier gemessenen Mustern —
und weil deren Verdienst höher war, bekam die Zahl von einem Viertelprozent
**72 % Gewicht** und zog die Prognose auf −4,1 %.

Ein Musterüberschuss ist aber keine Kursprognose. Er sagt: *zusätzlich zum
üblichen Gang sind 0,25 % zu erwarten.* Das gehört aufaddiert, nicht
gegengerechnet.

Nach der Umstellung: höchste Abweichung zwischen Säule 1 und Mischung
**0,669 %** statt 15,94 %. Der Aufschlag ist ausserdem auf den Betrag der
Grundlage gedeckelt — ein Muster darf eine Schätzung nuancieren, nicht ersetzen.

---

## Die Säulen einzeln

### `learning` — Kurslernen · Grundlage

Das Ensemble aus fünf Teilmodellen. **Verdienst:** die gemessene
Richtungstrefferquote der bereits bewerteten Prognosen dieses Wertes und
Horizonts, Nullpunkt 0,523. Unter zwanzig bewerteten Fällen gilt ein
vorsichtiger Zwischenwert von 0,25.

> Zuvor kam der Verdienst aus der *Zuversicht* des Ensembles, mit 0,25 als
> Rückfall. Das ist fast immer der Fall gewesen und machte Säule 1
> grundsätzlich schwach — jede andere Säule mit bescheidenem Vorsprung bekam
> die Hälfte des Gewichts. **Ein Rückfallwert ist keine Messung.**

### `math` — Spektralanalyse · Grundlage

Singuläre Spektralanalyse: Aus den führenden Komponenten wird eine lineare
Rekursion abgeleitet und fortgeschrieben. Von den Verfahren der zweiten Säule
ist sie die einzige, die nicht nur beschreibt, sondern fortschreibt.

**Verdienst:** Rückhalteprüfung über 40 Bars — der letzte Abschnitt wird
zurückgehalten, die Zerlegung auf dem Rest gerechnet, die Fortschreibung gegen
die Wirklichkeit gehalten. Wer die Latte „besser als Stillstand" reisst, bekommt
null.

**Zwei Riegel:**

1. **Nicht weiter behaupten, als geprüft wurde.** Der Rückhalt misst 40 Bars;
   jenseits davon gibt es keinen Beitrag. In der ersten Fassung schrieb die SSA
   für ENA-USD **90 Tage** fort — +33,4 %, gestützt auf einen Vorsprung über
   vierzig Tage. Die Mischung zog die Prognose um 42 Prozentpunkte.
2. **Kein Davonlaufen.** Fortschreibungen jenseits des Dreifachen der eigenen
   üblichen Schwankung werden verworfen. Eine davongelaufene Rekursion sieht
   sonst wie eine mutige Prognose aus.

Sichtbare Folge: Über 3 Monate, 6 Monate und 1 Jahr trägt **ausser Säule 1
nichts** bei.

### `knowledge` — Wissen · Aufschlag

Die Muster, über die die Literatur schreibt — RSI-Schwellen, Bollinger-Bänder,
Goldenes Kreuz, 52-Wochen-Hoch. Von allem, was in den Texten steht, sind sie das
Einzige, was am Kurs geprüft ist: `bot_trigger_stat` hält je Auslöser den
Median-Ertrag nach 1, 5 und 20 Tagen **gegen eine Grundlinie**, über tausende
Ereignisse.

**Der Beitrag ist der Überschuss über die Grundlinie**, nicht der rohe Ertrag —
der enthält den Marktgang, und den hätte man auch ohne Muster bekommen.

**Der Verdienst hängt an der Richtungstrefferquote, nicht am Überschuss.** Ein
Auslöser mit grossem Überschuss und Trefferquote 0,40 beschreibt seltene
Ausschläge, nicht Vorhersagbarkeit — genau das Profil, das in jeder Rückrechnung
glänzt und im Betrieb verliert. Gemessen: Bei Aktien hat *jeder* der sieben
Auslöser einen positiven Zwanzigtagesüberschuss, auch die bärischen, und deren
Richtungstrefferquote liegt bei 0,40 bis 0,43.

> **Die Erkennung benutzt dieselbe SQL-Bedingung wie die Messung.**
> `get_active_triggers` spiegelt 027 wörtlich. Eine zweite Umsetzung in C# liefe
> unweigerlich auseinander — ein anderer RSI, ein anderes Fenster —, und der
> Verdienst gehörte zu einem anderen Signal als dem gemeldeten.

Dubletten je (Wert, Auslöser) werden entfernt: Ein Bollinger-Ausbruch feuert
gern an zwei Tagen hintereinander und ginge sonst doppelt ein.

Zwischen 1, 5 und 20 Tagen wird linear interpoliert; **darüber hinaus nicht
fortgeschrieben.** Aus einem Zwanzigtageseffekt einen Jahreseffekt zu rechnen
hiesse, dreissig Mal zu behaupten, was einmal gemessen wurde.

### `semantic` — Nachrichten · Aufschlag

Aus den Artikeln der letzten Tage wird je Wert eine Stimmung zwischen −1 und +1
gewonnen: Wörterbuch deutsch/englisch, Verneinung über drei Wörter zurück,
Halbwertszeit ein Tag, Zuordnung über einen Wortindex statt über 600 × 20.000
Textsuchen.

**Und dann steht sie still.** Zwischen „die Nachrichten sind positiv" und „der
Kurs steigt um x Prozent" liegt ein Faktor, den niemand raten darf. Er kommt aus
der Kalibrierung — der Stimmung eines Tages gegen die *danach* eingetroffene
Rendite.

Ergebnis am 25.08.2026:

| Horizont | Paare | Korrelation | Zufallsschwelle | Verdienst |
| --- | ---: | ---: | ---: | ---: |
| 1 Tag | 1.499 | −0,0178 | 0,0517 | **0** |
| 1 Woche | 687 | −0,0587 | 0,0763 | **0** |
| 2 Wochen | 512 | −0,0536 | 0,0884 | **0** |
| 1 Monat | 325 | −0,0835 | 0,1109 | **0** |

Alle unter der Schwelle, **alle negativ** — die Stimmung läuft der Folgerendite
eher entgegen, als ihr voraus. Deckt sich mit der GDELT-Messung: 0,0419
prognostisch gegen eine Schwelle von 0,077, gleichzeitig dagegen 0,1365.
Nachrichten hängen mit heute zusammen, nicht mit morgen.

Die Säule ist damit **eingebaut, sichtbar und ohne Wirkung**. Das ist kein
Fehlschlag, sondern der Zweck der Kalibrierung: Sie steht bereit, falls sich das
ändert, und erfindet bis dahin nichts.

### `flow`, `deep`

`deep` liefert eine Zahl, hat aber gemessenen Verdienst null — kein Band schlägt
die Drift. `flow` erzeugt keine Renditezahl; Kapitalfluss beschreibt, wohin Geld
wandert, nicht wohin ein Kurs geht.

---

## Was gespeichert wird

| Spalte | Inhalt |
| --- | --- |
| `predicted_close` | **Säule 1 allein.** Daran hängt die Rückkopplung |
| `combined_close` | die Mischung über alle Säulen |
| `pillar_mix` | wer mit welchem Anteil und warum — als JSON |

**Warum getrennt.** `ScoringService` verschiebt nach `predicted_close` die
Gewichte der fünf Teilmodelle. Stünde dort die gemischte Zahl, lernte Säule 1
aus einem Fehler, den sie nicht gemacht hat — die Komponenten erklärten die
Vorhersage nicht mehr.

Beide werden getrennt bewertet: `forecast_score` und
`forecast_score_combined`. Erst dadurch lässt sich die eigentliche Frage
beantworten — **bringt das Mischen überhaupt etwas?** Ohne die zweite Bewertung
wäre das eine Glaubensfrage.

---

## Bedienung

**Prognose → „Woraus die Prognose entsteht"**

| Knopf | zeigt |
| --- | --- |
| *Zeigen* | die Mischung einer konkreten Prognose, Säule für Säule |
| *Rückhalt aller Säulen* | was in `pillar_skill` steht |
| *Bringt das Mischen etwas?* | Säule 1 gegen Mischung auf denselben Prognosen |

Über die Schnittstelle: `POST /api/saeulen/kalibrieren`,
`GET /api/saeulen/skill`, `GET /api/saeulen/mischung/{symbol}`,
`GET /api/guete/mischvergleich`.

---

## Kosten

Der Prognoselauf über 606 Werte und sieben Horizonte dauert **rund 37 Sekunden**
statt 13,6 — die Spektralanalyse macht den grössten Teil davon aus. Die
Beiträge der übrigen Säulen werden **einmal je Lauf für alle Werte** gesammelt;
je Wert einzeln wären es mehrere tausend Fahrten zur Datenbank.
