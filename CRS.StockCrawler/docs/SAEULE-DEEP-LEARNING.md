# Säule „Deep Learning"

**CRSOFT.StockCrawler**, Stand 22.08.2026

Ein globales Sequenzmodell über alle Werte, in Python trainiert und in .NET über
ONNX ausgeführt.

## Warum drei Modelle statt einem

Der erste Durchgang lief über alle neun Horizonte zugleich und ist daran
gescheitert. Der Fehler war nicht das Netz, sondern der Maßstab der Zielgrößen:

| Horizont | Streuung | relativ |
| ---: | ---: | ---: |
| 1 Tag | 0,0319 | 1,0× |
| 5 Tage | 0,0659 | 2,1× |
| 20 Tage | 0,1304 | 4,1× |
| 60 Tage | 0,2270 | 7,1× |
| 250 Tage | 0,4448 | **13,9×** |

Mit einer gemeinsamen Normierung stammt fast der gesamte Verlust aus den langen
Horizonten. Ein Prozent Fehler auf 250 Tage wiegt dann so viel wie ein Prozent
auf einen Tag — obwohl das eine im Rauschen liegt und das andere eine große
Bewegung wäre. Die kurzen Horizonte wurden praktisch nicht trainiert.

Das Ergebnis war eindeutig:

```
            Trainingsverlust   Abstimmung Fehlerverhältnis   Richtung
Epoche 1        -0,6352                1,0550                50,99 %
Epoche 2        -0,9489                1,1341                51,30 %
Epoche 3        -1,0652                1,1485                51,36 %
Epoche 4        -1,1357                1,1957                51,10 %
```

Trainingsverlust fällt, Abstimmungsfehler steigt — Überanpassung, und der Fehler
lag von der ersten Epoche an über eins.

**Der Maßstab ist aber nur die halbe Begründung.** Kurzfristig überwiegen
Rückkehr zum Mittel und Mikrostruktur, langfristig Trend und Faktorbindung.
Inhaltlich sind das zwei verschiedene Aufgaben, und ein Modell für beide
schließt einen Kompromiss, den keine von beiden braucht.

## Die Grenzen der drei Bereiche

Nicht rund gewählt, sondern dort gesetzt, wo diese Sitzung Struktur **gemessen**
hat:

| Bereich | Horizonte | Warum diese Grenze |
| --- | --- | --- |
| **kurz** | 1, 2, 3, 5 | Unter einer Woche. Mikrostruktur und kurzfristige Rückkehr. Unsere Übertragungsmessung fand hier bei 3 Tagen 58 % und bei 5 Tagen 42 % — also nichts Belastbares. |
| **mittel** | 10, 20, 60 | Zwei Wochen bis ein Quartal. **Hier liegt alles, was gehalten hat**: die Übertragung bei 10 Tagen (66 % gegen 38 % im Nulltest) und die Vorlaufordnung im Band 15–60 Tage (Rangkorrelation +0,65 über zwei getrennte Dreizehnjahreszeiträume). |
| **lang** | 120, 250 | Halbes bis ganzes Jahr. Hier kehrte sich die Vorlaufordnung out-of-sample **um** (ρ = −0,36). Dazu kommt ein hartes Datenproblem: Bei 250 Tagen Horizont und 6.000 Tagen Historie gibt es je Wert nur 24 überlappungsfreie Beobachtungen. |

Die Erwartung ist damit klar geordnet: Das mittlere Modell ist das, in das sich
Aufwand lohnt. Das lange ist das aussichtsloseste, und zwar aus Datengründen,
nicht aus Modellgründen.

## Aufbau

```
Merkmale     29, kausal gebaut, aus dem vorhandenen Export
Netz         kausales Faltungsnetz mit wachsender Schrittweite
             (kein Transformer -- ohne CUDA-Karte waeren das Tage statt Stunden)
Einbettung   je Wert und je Horizont
Ausgabe      Mittelwert UND Log-Varianz, trainiert ueber Gauss-Log-Likelihood
Zeitsplit    Training bis 2020-09 | Abstimmung bis 2023-07 | Sperre bis heute
Inferenz     ONNX in .NET ueber Microsoft.ML.OnnxRuntime
```

Die Unsicherheit ist keine Zierde. Ohne sie kann die Säule nicht schweigen, wenn
sie nichts weiß — sie behauptete dann in jeder Lage gleich viel. In der
Oberfläche werden Zeilen abgeblendet, in denen das Modell sein eigenes
Vorzeichen für nicht tragfähig hält.

## Die zweite Latte: die blosse Drift

Alle drei Modelle sind trainiert, und das lange meldete zunächst den ersten
Erfolg dieses Projekts:

```
h=250   Fehlerverhältnis 0,9604   Richtung 64,29 %   trägt
```

**Es war keiner.** Über ein Jahr steigen Aktien im Mittel. Ein Modell, das nur
„aufwärts" sagt, schlägt den Stillstand zwangsläufig — ohne etwas gelernt zu
haben außer der Drift.

Die Gegenprobe: die mittlere Rendite des **Trainingszeitraums**, eine einzige
Zahl, angewandt auf den Sperrbereich.

| Horizont | Modell FV | Drift FV | Modell Richtung | Drift Richtung |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 1,0050 | **0,9994** | 49,8 % | **52,0 %** |
| 2 | 1,0035 | **0,9988** | 50,6 % | **53,2 %** |
| 3 | 1,0033 | **0,9982** | 50,8 % | **53,9 %** |
| 5 | 1,0019 | **0,9975** | 52,0 % | **54,5 %** |
| 10 | 1,0508 | **0,9960** | 52,2 % | **55,4 %** |
| 20 | 1,0528 | **0,9927** | 52,2 % | **57,4 %** |
| 60 | 1,0086 | **0,9767** | 57,7 % | **63,2 %** |
| 120 | 1,0060 | **0,9645** | 55,6 % | **66,4 %** |
| 250 | 0,9604 | **0,9139** | 64,3 % | **75,1 %** |

**Eine einzige Zahl schlägt alle neun Modellausgaben, auf beiden Maßen.**

Bei 250 Tagen waren 75,1 % der Zielwerte im Sperrbereich positiv. Die
Trefferquote des Modells von 64,3 % lag also **unter** der eines Würfels, der
immer „aufwärts" sagt.

Die Latte ist deshalb dauerhaft eingebaut: `train_deep.py` rechnet sie mit,
`DeepModelInfo.Carries()` verlangt beide, und die Oberfläche stellt sie
nebeneinander. Ohne diesen Vergleich hätte die Säule irgendwann „trägt"
angezeigt für gelernte Drift — und das ist genau die Art Anzeige, gegen die
dieses Projekt gebaut ist.

## Der Versuch, die Drift zu entfernen

Wenn die Drift den größten Teil der Zielgröße ausmacht, verbraucht das Netz
seine Kapazität darauf, sie nachzubilden — und hat nichts mehr übrig für das,
was darüber hinausgeht. Der naheliegende Schluss: sie vorher herausnehmen.

```bash
python ml/train_deep.py --name deep_mid_nd --horizons 10,20,60 --seq 96 --detrend
```

Die Drift wird **ausschließlich auf dem Trainingsbereich** bestimmt und beim
Auswerten wieder aufgeschlagen. Beides ist notwendig: Zählte der Sperrbereich
mit, kennte das Netz beim Lernen bereits die mittlere Rendite der Zukunft;
schlüge man sie nicht wieder auf, wäre ein Fehlerverhältnis von 0,96 auf einer
entdrifteten Größe etwas ganz anderes als eines auf der rohen und mit den
bisherigen Zahlen nicht vergleichbar.

Gemessen wurden für das mittlere Band:

```
Drift aus der Zielgroesse genommen: 10=+0.00284, 20=+0.00584, 60=+0.01977
```

Der Sinn ist nicht, dass das Netz dadurch besser würde — sondern dass es
**nicht mehr scheinbar gewinnen kann, indem es „aufwärts" sagt.** Was danach
noch an Vorsprung bleibt, ist echter Vorsprung.

### Das Ergebnis

Ein kleiner, durchgängiger Gewinn in der ersten Epoche:

| Horizont | roh, Epoche 1 | entdriftet, Epoche 1 |
| ---: | ---: | ---: |
| 10 | 1,0454 | **1,0354** |
| 20 | 1,0632 | **1,0417** |
| 60 | 1,0935 | **1,0757** |

Danach überanpasst es: Abstimmung 1,0509 → 1,1068 → 1,1856 → 1,1685. Der frühe
Abbruch hält Epoche 1.

Im Sperrbereich:

| Horizont | Modell | Richtung | blosse Drift | Richtung |
| ---: | ---: | ---: | ---: | ---: |
| 10 | 1,0567 | 52,2 % | **0,9960** | **55,5 %** |
| 20 | 1,0531 | 52,1 % | **0,9927** | **57,5 %** |
| 60 | 1,0058 | 58,9 % | **0,9757** | **63,4 %** |

**Die Drift schlägt es weiterhin in jedem Horizont, auf beiden Maßen.** Bei 60
Tagen kommt das entdriftete Netz auf 58,9 Prozent Richtung; eine einzige Zahl
kommt auf 63,4.

Das war der aussichtsreichste Eingriff, den die Messungen hergaben — und er
reicht nicht. Der Befund hat sich damit verschoben: Vorher lautete er, die
Modelle hätten die Drift gelernt. Jetzt lautet er, dass **über die Drift hinaus
in diesen Merkmalen nichts Lernbares steckt** — jedenfalls nicht für ein
Faltungsnetz dieser Größe auf diesen 29 Merkmalen.

Wo es weitergehen könnte, sagen die anderen Messungen dieser Sitzung: Der
Querschnitt erklärt die **gleichzeitige** Bewegung mit einem Bestimmtheitsmaß
von 0,355 und die morgige mit 0,0039. Solange kein Merkmal einen echten Vorlauf
trägt, ändert kein Netz daran etwas.

## Stand

- [x] Merkmalsexport: 1.155.853 Zeilen, 278 Werte, 9 Horizonte
- [x] Trainingsprogramm mit Zeitsplit, Normierung je Horizont, frühem Abbruch
- [x] ONNX-Export und Modellbeschreibung
- [x] .NET-Inferenz (`DeepForecastService`) und Endpunkte `/api/deep/*`
- [x] Reiter in der Oberfläche, zeigt die Sperrbereichszahlen neben jeder Vorhersage
- [x] **Kurzes Modell** (1, 2, 3, 5 Tage) — läuft, trägt nicht
- [x] **Mittleres Modell** (10, 20, 60 Tage) — läuft, trägt nicht
- [x] **Langes Modell** (120, 250 Tage) — läuft, hat die Drift gelernt
- [x] **Alle drei gleichzeitig geladen**, Band aus den Horizonten abgeleitet
- [x] **Driftlatte** in Training, Dienst, Endpunkten und Oberfläche
- [x] **Gewicht je Band**, das zusammen das Gewicht der Säule ergibt
- [x] **Rückblick** — Prognose gegen Wirklichkeit im Chart, mit aufsummiertem
      Vorsprung

### Vorgemerkt
- [ ] **Stufe 2: Analysewerte als zusätzliche Merkmale.** Moden, Zyklenstabilität
      und Vorlaufordnung sind derzeit **nicht kausal** gerechnet — sie bestimmen
      ihre Eigenvektoren über das gesamte Fenster. So eingespeist verrieten sie
      dem Modell die Zukunft. Sie müssten an jedem historischen Zeitpunkt neu
      und nur aus der Vergangenheit gerechnet werden.
- [ ] **Stundendaten.** Bislang nur Tagesbasis. Für die kurzen Horizonte ist die
      Stundenauflösung die natürlichere, aber sie reicht nur ein Jahr zurück.

## Die Latte

Das Modell muss zwei Dinge schlagen, sonst bekommt die Säule Gewicht null:

1. **Die Annahme, es ändere sich nichts** — Fehlerverhältnis unter 1.
2. **Die erste Säule** — Trefferquote über 52,3 %.

Beides wird im **Sperrbereich** gemessen, den das Training nie gesehen hat, und
beides steht in der Oberfläche neben jeder einzelnen Vorhersage. Ein Verfahren,
das sich nicht erklären kann, muss sich umso deutlicher messen lassen.
