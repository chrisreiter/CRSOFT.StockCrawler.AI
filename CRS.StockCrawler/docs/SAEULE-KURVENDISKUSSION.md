# Kurvendiskussion

**CRSOFT.StockCrawler**, Teil der Säule „Mathematische Analyse", Stand 22.08.2026

Jede Kursreihe wird geglättet und dann diskutiert wie eine Funktion.

## Das Werkzeug

Geglättet und abgeleitet wird über eine **lokale Polynomanpassung** nach
Savitzky und Golay: Um jeden Punkt legt ein Polynom zweiten Grades die
Nachbarschaft aus, und Wert, Steigung und Krümmung sind dessen Koeffizienten.

```
p(x) = a₀ + a₁x + a₂x²   →   f = a₀,  f' = a₁,  f'' = 2a₂
```

**Warum nicht gleitendes Mittel und Differenzen.** Das macht zwei Fehler
zugleich. Ein gleitendes Mittel verschmiert genau an den Wenden am stärksten —
dort also, wo die Kurve am meisten zu sagen hat. Und jede Differenz verdoppelt
den Rauschanteil; die zweite Ableitung vervierfacht ihn. Bei Kursdaten bleibt
davon nichts Verwertbares.

Weil die Stützstellen gleichabständig sind, hängt die Anpassung nur von der
Fensterform ab. Die Lösung der kleinsten Quadrate lässt sich deshalb einmal als
drei Faltungskerne ausrechnen — das Verfahren ist trotz Anpassung an jedem Punkt
schnell.

**Auf Log-Kursen.** Die Steigung in Dollar hängt vom Kursniveau ab: Zehn Dollar
am Tag sind bei einem Kurs von 20 eine Explosion und bei 4.000 nichts. Auf dem
Logarithmus ist die Steigung eine Rendite je Bar und damit über alle Werte
hinweg dieselbe Größe.

## Zentriert oder kausal

| | zentriert | kausal |
| --- | --- | --- |
| Fenster | 2m+1 Punkte um die Stelle | m+1 Punkte davor |
| Verschiebung | keine | Ableitung am rechten Rand |
| Für die Beschreibung der Vergangenheit | **richtig** | ungenauer |
| Für eine Prognose | **verboten** | erlaubt |

Ein zentriert geglätteter Wert kennt die Zukunft und verrät sie weiter. Für die
Frage „wann lag der Hochpunkt" ist das richtig; für jedes Merkmal, das in ein
Modell fließt, ist es die tödliche Form von Zukunftswissen. Beide Formen sind
umgesetzt, die Wahl steht in der Oberfläche und wird bei jedem Lauf mit
abgelegt.

## Was gefunden wird

| Art | Bedingung | Stufe bemisst sich an |
| --- | --- | --- |
| Hochpunkt | f' wechselt + → − | \|f''\| — die Schärfe der Spitze |
| Tiefpunkt | f' wechselt − → + | \|f''\| |
| Wendepunkt | f'' wechselt Vorzeichen | \|f'\| — Tempo des Trends |
| Sattelpunkt | f' und f'' nahe null, vorher Bewegung | vorherige Steigung |
| Steigungsausbruch | \|f'\| über Schwelle | \|f'\| |
| Krümmungsausbruch | \|f''\| über Schwelle | \|f''\| |
| Sprung | Rohwert weit von der Anpassung | Abstand |

Nullstellen werden über den **Vorzeichenwechsel** gefunden, nicht über „nahe
null". Ein Schwellenwert auf \|f'\| fände in ruhigen Phasen hunderte Stellen und
in bewegten keine.

## Die Stufe misst nicht in Prozent

Eine Steigung von 2 % je Tag ist bei einem Versorger außergewöhnlich und bei
einem jungen Kryptowert Alltag. Bewertet wird gegen die übliche Streuung
**dieser** Reihe in einem Fenster, das **vor** der Stelle endet. Als Maßstab
dient der Median der Beträge, nicht die Standardabweichung: Ein einzelner großer
Ausschlag hebt die Standardabweichung so weit an, dass er sich selbst klein
rechnet.

Dass die Skala trägt, zeigt der erste Volllauf: Auf Platz eins stehen **PULS**
und **FLOT** im März 2020 — Ultrakurz-Anleihen-ETFs in der Liquiditätskrise, bei
einer Tagesbewegung von nur −0,29 %. Für diese Reihen ist das ein Erdbeben, und
die Stufe sagt es.

## Der erste Volllauf

```
295 Werte · 98.099 Stellen · 135.481 Verknüpfungen · 64 Sekunden

Wendepunkt         37.690  (38 %, Ø Stufe 32)
Tiefpunkt          14.407  (15 %, Ø Stufe 35)
Hochpunkt          13.171  (13 %, Ø Stufe 33)
Krümmungsausbruch  12.399  (13 %, Ø Stufe 53)
Sprung              9.303  ( 9 %, Ø Stufe 67)
Steigungsausbruch   8.676  ( 9 %, Ø Stufe 55)
Sattelpunkt         2.453  ( 3 %, Ø Stufe 58)
```

Die Mischung ist die erste Plausibilitätsprüfung: Sind fast alle Funde von einer
Art, stimmt die Schwelle nicht.

## Die Verknüpfung — und ihre Grenze

Gezählt wird, wie oft Ereignisse zweier Werte dicht beieinanderliegen, **gegen
die Erwartung bei Unabhängigkeit**:

```
erwartet ≈ n_A · n_B · (2·Fenster+1) / gemeinsame Handelstage
```

Ohne diese Normierung gewännen immer die Werte mit den meisten Ereignissen. Und
der Nenner sind die Zeitpunkte, an denen **beide** gehandelt haben — sonst sähe
jedes Aktien-Krypto-Paar auffällig aus, weil die Aktie an einem Drittel der Tage
gar nicht handeln konnte.

Der erste Lauf über sechs Kryptowerte:

| Paar | Arten | Faktor | Treffer | Abstand | danach |
| --- | --- | ---: | ---: | ---: | ---: |
| ETH → BNB | Steigungsausbruch | 9,57× | 17 | +2,0 | 71 % |
| BTC → ETH | Steigungsausbruch | 9,00× | 19 | +1,0 | 53 % |
| BTC → ETH | Krümmungsausbruch | 8,19× | 18 | +1,0 | 67 % |
| BTC → ETH | Hochpunkt | 6,37× | 15 | −1,0 | 33 % |

**Ein hoher Faktor bei einem Abstand um null ist kein Vorlauf.** Er heißt, die
beiden bewegen sich gemeinsam — eine Aussage über Struktur, nicht über
Vorhersagbarkeit. Dasselbe Bild wie bei der Modenanalyse und beim
Querschnittsmodell: gemeinsam, nicht nacheinander.

Erst ein Abstand deutlich über null bei einem Anteil klar über 0,5 wäre ein
Vorlauf — und auch der müsste sich in einem getrennten Zeitraum wiederholen.

## Aufrufe

```
POST /api/curve/scan?halbfenster=10&kausal=false&minZ=2.5&minStufe=20
GET  /api/curve/events?seite=1&groesse=50&art=wendepunkt&sortierung=stufe
GET  /api/curve/links?limit=100
GET  /api/curve/stats
GET  /api/curve/runs
```

## Vorgemerkt

- [ ] **Kausaler Lauf als Prognosemerkmal.** Der Volllauf ist zentriert
      geglättet und damit nur zur Beschreibung tauglich. Ein zweiter,
      kausaler Lauf wäre die Grundlage für einen Beitrag zur Prognose.
- [ ] **Die Verknüpfung über getrennte Zeiträume prüfen.** Bislang steht sie
      auf dem ganzen Zeitraum. Ein Vorlauf, der sich nicht in einer zweiten,
      unabhängigen Hälfte wiederholt, ist keiner.
