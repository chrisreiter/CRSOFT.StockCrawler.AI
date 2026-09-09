# Die Brücke von Text zu Zahl

**CRSOFT.StockCrawler**, Entwurf vom 22.08.2026

Wie eingebettetes Wissen, Handelsstrategien und Nachrichten — auch solche über
Weltereignisse — mit den numerischen Prognosen zusammenkommen.

---

## Warum der naheliegende Weg nicht funktioniert

Der Reflex lautet: Man gibt einem Sprachmodell die Nachrichten des Tages und
fragt, wohin der Kurs geht. Das scheitert an vier Stellen zugleich, und keine
davon lässt sich durch ein größeres Modell beheben.

**Es lässt sich nicht messen.** Eine Antwort in Prosa hat kein
Fehlerverhältnis. Ohne Zahl gegen den Stillstand gibt es keinen Rückhalt-Test,
und ohne Rückhalt-Test hat diese Säule nach den Regeln des Projekts Gewicht
null.

**Das Modell kennt die Zukunft.** Jedes Sprachmodell wurde auf Text trainiert,
der bis in die jüngste Vergangenheit reicht. Fragt man es nach März 2020, weiß
es, was im April geschah. Ein Rückblick darauf misst das Gedächtnis des Modells,
nicht seine Prognosefähigkeit — der teuerste Selbstbetrug, den dieses Projekt
sich leisten könnte.

**Nachrichten sind gleichzeitig, nicht vorlaufend.** Diese Sitzung hat dreimal
dasselbe gemessen: Modenanalyse 53 % gemeinsame Bewegung bei 2,8 Bars
Phasenstreuung; Querschnittsmodell R² 0,355 gleichzeitig gegen 0,0039 einen Tag
voraus; Kurvendiskussion Faktor 9,6 bei Median-Abstand null. Nachrichten
*berichten* über Bewegungen. Der Kurs ist meist schneller als die Meldung.

**Ein Buch ist kein Beleg.** Dass ein Autor eine Strategie empfiehlt, sagt
nichts darüber, ob sie funktioniert. Die Ähnlichkeitssuche findet Stellen, die
zu einer Frage *passen* — nicht Stellen, die *recht haben*.

Der Entwurf muss also alle vier Punkte beantworten, nicht umgehen.

---

## Der Grundgedanke: drei Kanäle, drei verschiedene Aufgaben

Text ist nicht eine Sache. Ein Lehrbuch, eine Zinsentscheidung und ein
Erdbebenbericht haben nichts gemeinsam außer der Form. Sie in einen Topf zu
werfen und daraus eine Zahl zu ziehen, ist der Fehler — jeder von ihnen trägt
etwas anderes bei, und zwar an einer anderen Stelle der Rechnung.

```
                       ┌──────────────────────────────┐
  Bücher, Strategien → │ 1. Hypothesengenerator       │ → Regeln, die geprüft
                       │    Text wird NIE zur Zahl    │    werden müssen
                       └──────────────────────────────┘
                                                              ↓
                       ┌──────────────────────────────┐   ┌────────────────┐
  Nachrichten,      →  │ 2. Zustandsvektor            │ → │ Deep-Learning- │
  Weltereignisse       │    Text wird zu 16 Zahlen    │   │ Modell         │
                       └──────────────────────────────┘   └────────────────┘
                                                              ↓
                       ┌──────────────────────────────┐
  dieselben         →  │ 3. Analogieabfrage           │ → Verteilung dessen,
  Nachrichten          │    Text bleibt Text          │    was damals folgte
                       └──────────────────────────────┘
```

---

## Kanal 1 — Wissen als Hypothesengenerator

**Der Text tritt nie in die Prognose ein.** Er liefert Kandidaten; das
Zahlwerk entscheidet.

Ein Fachbuch enthält Sätze der Form „nach drei Tagen mit überdurchschnittlichem
Volumen und fallendem Kurs setzt häufig eine Gegenbewegung ein". Das ist keine
Prognose, sondern eine **prüfbare Behauptung** mit einer Bedingung und einer
erwarteten Folge.

Der Ablauf:

1. Aus den eingebetteten Abschnitten werden Passagen gezogen, die eine
   Bedingung-Folge-Struktur haben.
2. Ein lokales Sprachmodell — hier reicht `nemotron3:33b`, das ohnehin
   vorliegt — übersetzt sie in eine **formale Regel** gegen die vorhandenen
   Merkmale: `vol_z > 1.5 AND r3 < 0 ⇒ r5 > 0`.
3. Die Regel läuft durch `FeatureControl.Compare` — einzeln, gegen Momentum
   kontrolliert, überlappungsfrei ausgewertet.
4. Was den Test besteht, wird ein Merkmal. Was ihn nicht besteht, bleibt ein
   Zitat.

**Das Sprachmodell übersetzt, es urteilt nicht.** Es darf falsch übersetzen —
dann fällt die Regel im Test durch und kostet nichts. Es darf nicht entscheiden,
ob eine Regel gilt; das entscheidet der Sperrbereich.

Damit ist auch die dritte Falle entschärft: Selbst wenn das Modell die Zukunft
kennt, kann es nur *Hypothesen* einbringen. Geprüft werden sie auf Daten, mit
demselben Zeitsplit wie alles andere.

**Nebenertrag:** Jede Regel trägt ihre Quelle mit — Buch, Seite, Abschnitt. Zu
jedem Merkmal, das die Prognose beeinflusst, lässt sich sagen, woher es kommt
und ob es sich bewährt hat. Das ist mehr Nachvollziehbarkeit, als die
Deep-Learning-Säule je bieten kann.

---

## Kanal 2 — Nachrichten als Zustandsvektor

Hier wird Text tatsächlich zur Zahl, aber unter strengen Bedingungen.

### Die Aufbereitung

Je Tag *t* und Anlagewelt entsteht ein Vektor:

```
E(t) = Mittel der Einbettungen aller Abschnitte mit Zeitstempel in (t−1, t]
```

**Nur Text, der vor dem Handelsschluss vorlag.** Der Zeitstempel eines
Abschnitts ist deshalb Pflicht, nicht Zierde — `occurred_utc` in
`knowledge_chunk`. Ein Abschnitt ohne Zeitstempel darf nie in einen
Zustandsvektor eingehen. Ohne diese Regel fließt Zukunftswissen ein, und der
Rückblick sieht großartig aus.

### Die Verdichtung

1024 Dimensionen sind als Merkmal unbrauchbar: Bei 4.000 Handelstagen und
1.024 Eingängen je Tag lernt jedes Modell den Kalender auswendig. Verdichtet
wird auf **16 Hauptkomponenten**, bestimmt **ausschließlich auf dem
Trainingszeitraum**.

Auch das ist eine Falle mit Ansage: Eine Hauptkomponentenzerlegung über den
gesamten Zeitraum kennt die Richtungen, in die sich der Raum später entwickelt.
Sie muss auf dem Trainingsteil gebildet und auf Abstimmung und Sperrbereich nur
noch angewendet werden.

### Die drei Größen, die daraus entstehen

| Größe | Formel | Was sie behauptet |
| --- | --- | --- |
| **Lage** | `PCA₁₆(E(t))` | wovon gerade die Rede ist |
| **Neuheit** | `1 − cos(E(t), Ē(t−30…t−1))` | wie ungewöhnlich der Tag ist |
| **Tonlage** | Projektion auf eine Achse aus Gegensatzpaaren | Richtung der Stimmung |

Die **Neuheit** ist dabei die aussichtsreichste und zugleich die
bescheidenste. Sie sagt nichts über die Richtung, sondern über die
**Schwankungsbreite** — und genau das ist der Zusammenhang, für den die
Literatur belastbare Belege hat. Ein Tag, dessen Nachrichtenlage nichts mit den
letzten dreißig zu tun hat, ist ein Tag mit großer Bewegung. In welche Richtung,
steht damit nicht fest.

Für die Deep-Learning-Säule ist das trotzdem wertvoll: Das Modell sagt Mittelwert
**und Log-Varianz** vorher. Eine bessere Varianzschätzung verbessert die
Aussagekraft jeder einzelnen Vorhersage, auch wenn der Mittelwert unberührt
bleibt.

### Die Prüfung

Die drei Größen wandern als zusätzliche Kanäle in `FeatureSet`. Gemessen wird
wie bei allem anderen: Fehlerverhältnis und Trefferquote im Sperrbereich, mit
und ohne diese Kanäle. Verbessert sich nichts, fliegen sie raus.

---

## Kanal 3 — Analogieabfrage

Der Kanal, der ohne Training auskommt und deshalb sofort etwas liefert.

**Die Frage:** „Die Nachrichtenlage von heute — welchen dreißig Tagen der
Vergangenheit ähnelt sie am meisten, und was geschah in den fünf Tagen danach?"

```
1. E(heute) bilden
2. In Qdrant die k=30 ähnlichsten Tage suchen — nur Tage vor heute
3. Zu jedem gefundenen Tag d: die tatsächliche Folgerendite r(d, d+h) nachschlagen
4. Ausgeben: Verteilung dieser 30 Renditen, nicht ihren Mittelwert
```

**Warum Verteilung und nicht Mittelwert.** Der Mittelwert von dreißig
Renditen sieht aus wie eine Prognose und ist keine. Die Verteilung zeigt, ob die
dreißig ähnlichen Tage sich einig waren — bei einer Streuung, die so groß ist wie
die unbedingte, hat die Ähnlichkeit nichts beigetragen, und das sieht man sofort.

**Die messbare Kennzahl** ist deshalb nicht die mittlere Rendite, sondern:

```
Nutzen = 1 − Streuung(bedingt auf ähnliche Tage) / Streuung(unbedingt)
```

Liegt sie bei null, ist die Analogie wertlos. Das ist eine Zahl, die man in den
Sperrbereich schicken kann.

---

## Das Datenproblem — und seine Lösung

Alle drei Kanäle scheitern an einem einzigen Punkt, wenn man ihn nicht löst:

> **Wir haben keine historischen Nachrichten.**

Was heute eingesammelt wird, beginnt heute. Ein Rückhalt-Test über einen
Sperrbereich braucht aber Text, der zu den Kursen von 2015 gehört. Ohne ihn
lässt sich kein Kanal validieren, und eine Säule ohne Validierung bekommt nach
den Regeln dieses Projekts Gewicht null — zu Recht.

### Die Antwort: GDELT

Das **GDELT-Projekt** ist genau dafür gemacht und kostet nichts:

| | |
| --- | --- |
| Umfang | weltweite Nachrichten, alle 15 Minuten |
| Zeitraum | Version 2 ab Februar 2015, Version 1 ab 1979 |
| Inhalt | Ereignisse mit Ort, Akteuren, Thema und **Tonlage** |
| Zugang | Tagesdateien als CSV, offen herunterladbar |
| Volumen | rund 250 GB für zehn Jahre roh — verdichtet auf Tagesvektoren wenige hundert MB |

Damit gibt es Text **mit Zeitstempel** von 2015 bis heute — 2.800 Handelstage,
genug für Training, Abstimmung und einen Sperrbereich von mehreren Jahren.

GDELT bringt außerdem mit, was die Frage nach den Weltereignissen beantwortet:
Konflikte, Naturkatastrophen, politische Umbrüche sind darin als eigene
Ereignisklassen erfasst, nicht nur als Wirtschaftsmeldungen.

**Der Zuschnitt auf die Anlagewelt** geschieht über die Themenkennungen: ein
Weltvektor aus allem, ein Wirtschaftsvektor aus den entsprechenden Klassen, und
je Sektor einer. Das ergibt die Auflösung, die nötig ist, um eine
Halbleiter-Meldung von einer Energie-Meldung zu unterscheiden — ohne die
Einzelmeldung einem Einzelwert zuordnen zu müssen, was bei historischen Daten
ohnehin unzuverlässig wäre.

---

## Der Weg dorthin

| Stufe | Was entsteht | Prüfbar an |
| --- | --- | --- |
| **1** | GDELT-Tagesdateien ab 2015, verdichtet zu Tagesvektoren | Vollständigkeit, Lücken |
| **2** | Kanal 3, Analogieabfrage | `1 − σ_bedingt/σ_unbedingt` im Sperrbereich |
| **3** | Neuheit als Merkmal | Vorhersage der Schwankungsbreite |
| **4** | Lage und Tonlage als Merkmale im Deep-Modell | Fehlerverhältnis mit/ohne |
| **5** | Kanal 1, Regeln aus Büchern | `FeatureControl.Compare` je Regel |

Stufe 2 und 3 sind die aussichtsreichsten und zugleich die billigsten. Stufe 4
ist die teuerste und nach allem, was diese Sitzung gemessen hat, die
unwahrscheinlichste — dieselbe Gleichzeitigkeit, die den Querschnitt scheitern
ließ, dürfte auch hier zuschlagen.

**Stufe 5 ist die einzige, die etwas liefert, das die anderen Säulen nicht
können: eine Begründung.**

---

## Was ehrlicherweise zu erwarten ist

Nach allem, was in dieser Sitzung gemessen wurde, wäre folgende Erwartung
begründet:

- **Neuheit sagt Schwankungsbreite vorher.** Wahrscheinlich. Der Zusammenhang
  ist gut belegt und braucht keine Richtung.
- **Tonlage sagt Richtung vorher.** Unwahrscheinlich, jedenfalls über Nacht.
  Der Kurs ist schneller als die Meldung.
- **Analogie verengt die Verteilung.** Möglich, aber vermutlich schwach.
- **Regeln aus Büchern bestehen den Test.** Einzelne ja, die meisten nein — so
  wie es dem Buchgewinn-Merkmal erging, das sich als Momentum unter neuem Namen
  herausstellte.

Das ist kein Grund, es nicht zu bauen. Es ist der Grund, es **so** zu bauen: mit
einer Messung an jedem Kanal, damit man am Ende weiß, welcher trägt — statt eine
Säule zu haben, die überzeugend klingt und nichts beiträgt.
