# Grundschwingungen — der Katalog der Zyklen

Ein Lauf über alle verfolgten Werte, der jeden Kursverlauf per
Fourier-Zerlegung auf seine stabilen Grundperioden untersucht, gleiche
Muster zu einem Katalog zusammenfasst und ausweist, welche Werte ein Muster
teilen — mit Phasenversatz. Eigener Reiter *Grundschwingungen*, Endpunkte
unter `/api/grundschwingungen/`, Werkzeug `grundschwingungen` des
Reasoning-Agenten, Beitrag zur Prognose als Teil der Säule *math*.

Erstlauf 15.09.2026: **546 Werte, 22 Epochen, 26.511 Spektralspitzen,
5.308 Akkorde, 335 Klassen, 6 Paare, davon 1 beständig.** Dauer 198 s.

---

## Die Kette

| Schritt | Was | Wo |
| --- | --- | --- |
| Epoche | 1024 Tagesbars, die an einem **Jahresanker** enden (heute, vor einem Jahr, …) | `GrundschwingungService.LaufeAsync` |
| Spektrum | Bandpass auf Log-Kursen, Welch-Spektrum, Spitzen gegen den örtlichen Untergrund, Stabilität über Teilfenster | `FrequencyPatterns.Extract` |
| Phase | Projektion der Epoche auf Sinus/Kosinus **jeder** gefundenen Periode; Lage am Epochenende in Grad | `FrequencyPatterns.Projektion` |
| Akkord | die ein bis drei prominentesten Perioden einer Epoche; Nebenstimmen nur ab 60 % der Prominenz der stärksten | `Grundschwingungen.Akkorde` |
| Klasse | Akkorde gleicher Stimmenzahl, deren Perioden alle innerhalb von 12 % beieinanderliegen; ab drei Akkorden | `Grundschwingungen.Katalog` |
| Harmonik | *rein* · *Oberton* (Nebenstimmen ganzzahlige Teiler des Grundtons, ±8 %) · *Schwebung* (zwei nahe, nicht harmonische) · *Dreiklang* | `Grundschwingungen.Harmonik` |
| Skizze | Summe der Kosinusschwingungen der Klasse über zwei Grundperioden, Phase null, auf ±1 normiert | `Grundschwingungen.Skizze` |
| Paare | zwei Werte in derselben Klasse in ≥ 3 gemeinsamen Epochen; Versatz aus der Phasendifferenz der Grundtöne, in Bars | `Grundschwingungen.Paare` |
| Ablage | `freq_run`, `freq_class`, `freq_akkord`, `freq_match`, `freq_pattern` (rohe Spitzen) | Migration 044 / pgsql 013 |

**Beständig** heisst: Versatz über die Epochen derselbe (|Mittel| > 2 σ)
**und** ungleich null (≥ 1 Bar). Nur das wäre ein Vorlauf. Ein Versatz um
null ist Gleichzeitigkeit; einer, der springt, war Zufall in hübscher Form.

---

## Was der erste Lauf zeigt

**Der Katalog ist kurzperiodisch.** Die zehn grössten Klassen haben Grundtöne
zwischen 7 und 13 Bars; Klasse #1 (9,4 + 7,3 + 5,7 Bars, Dreiklang) fasst
198 der 546 Werte. Lange Perioden (93, 114 Bars) kommen vor, aber in kleinen
Klassen. Ein Teil davon ist die Flanke des Bandpasses bei fünf Bars — die
Auswertung beginnt dort, wo der Filter noch nicht ganz durchlässt.

**Die Paare bestätigen das Verfahren, nicht den Vorlauf.** Von sechs Paaren
mit drei gemeinsamen Epochen sind drei `SPY / ^GSPC / VV` untereinander —
derselbe Index dreimal — mit Versatz 0,0 ± 0,1 Bars. Das ist die Kontrolle:
Was dasselbe ist, wird als gleichzeitig erkannt. Ein einziges Paar gilt als
beständig (CSCO / CMCSA, +2,5 ± 0,4 Bars über drei Epochen) — ein Kandidat,
kein Befund; bei 335 Klassen und 546 Werten ist ein Treffer dieser Art auch
durch Zufall zu erwarten.

**Die Fortschreibung trägt fast nichts.** Gemessen über 496 Werte mit genug
Historie: 124 haben überhaupt Rückhalt gegenüber dem Stillstand (Median
0,041, Höchstwert 0,364), 372 keinen. Drei von vier Werten steuern also
nichts zur Prognose bei — dasselbe Bild wie bei der SSA-Fortschreibung.

Das deckt sich mit dem zentralen Befund dieses Projekts: **Kurse bewegen
sich gemeinsam, nicht nacheinander.** Der Katalog ist gebaut, ihn zu prüfen,
nicht zu umgehen — der Versatz wird gemessen und mit seiner Streuung
ausgewiesen.

---

## Wie es in Prognose und Reasoning einfliesst

**Prognose.** `ForecastService` legt je Wert einmal
`GrundschwingungenPrognose.Rechne` auf die Tagesschlüsse: Akkord des
jüngsten 1024-Bar-Fensters, fortgeschrieben; Rückhalt aus einem Fenster,
das 40 Bars vor dem Ende aufhört, gegen die tatsächlichen 40 Bars, Fehler
gegen den Stillstand. Der Beitrag geht als zweite **Grundlage** der Säule
`math` in die Mischung — Gewicht × gemessener Verdienst, wie alles andere.
Ohne Rückhalt null; über 40 Bars hinaus kein Beitrag; Beträge über dem
Dreifachen der üblichen Schwankung verworfen. Die Begründung in
`pillar_mix` nennt die Perioden und den Rückhalt.

**Reasoning.** Werkzeug `grundschwingungen(symbol)`: Perioden, relative
Amplituden, Phasen, Harmonik, Katalogklasse, Partner mit Versatz und
Urteil — und **vorneweg der Rückhalt**. Ist er null, sagt das Werkzeug
wörtlich, dass die Perioden die Vergangenheit beschreiben und keine
Handelsgrundlage sind. Ein Agent, der Perioden weitergibt, ohne das zu
sagen, wäre die gefährlichste Komponente dieser Seite.

---

## Fallen, gemessen

**Epochen müssen auf einem Kalenderraster liegen.** Der erste Lauf zählte
die Epochen je Wert von seinem eigenen ersten Bar aus. Keine Epoche zweier
Werte endete am selben Tag, „dieselbe Klasse in derselben Epoche" traf
praktisch nie zu: 311 Klassen, **zwei** Paare. Mit Jahresankern: 6 Paare,
darunter die drei Index-Kontrollen bei Versatz null. Preis: Das Fenster ist
in Bars gleich (1024), in Kalenderzeit nicht — bei Aktien rund vier Jahre,
bei Krypto knapp drei.

**Die Phase muss je Spitze gerechnet werden.** Die Hilbert-Phase des
Momentanzyklus wurde verworfen, sobald dessen Periode nicht zur Spitze
passte: 51 von 435 Mustern behielten eine Phase. Ohne Phase kein Versatz,
ohne Versatz nur „gleiche Periode" — das Uninteressante. Die Projektion auf
Sinus und Kosinus der gefundenen Periode gilt für jede Spitze.

**Nebenstimmen brauchen eine Schwelle.** Die Spitzenliste gibt immer drei
her; ohne die 60-%-Schwelle hatte jeder Akkord drei Stimmen, und der Katalog
zerfiel in Dreiklänge, die sich in der dritten, schwachen Stimme
unterschieden — 127 Akkorde, neun Klassen, die 36 davon fassten.

**Die Amplituden sind Log-Einheiten.** Der Bandpass läuft auf Log-Kursen.
Die Auslenkungsdifferenz ist damit unmittelbar die Log-Rendite; der erste
Entwurf addierte sie auf den Kurs und bekam +0,000 %.

**`Extract` verlangte acht Bars mehr als die Epoche.** Ein Fenster von genau
1024 Bars lieferte still nichts. Die Reserve hatte keinen Grund und ist weg.

**Deutsches Anführungszeichen in einem Literal.** `„Jetzt rechnen"` mit
geradem Schlusszeichen — zum vierten Mal in diesem Projekt (CLAUDE.md).
