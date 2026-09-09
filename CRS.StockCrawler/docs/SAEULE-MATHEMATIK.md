# Säule 2 — Mathematische Analyse

**CRSOFT.StockCrawler**, Stand 21.08.2026

Signalverarbeitung auf der einzelnen Kursreihe. Grundlage ist
`Fourier-Analyse_fuer_Aktien_und_Kryptocharts.md`; die dort genannten Fallen
sind nicht nur zitiert, sondern jede einzeln nachgestellt und abgestellt worden.

## Die fünf Verfahren

| Verfahren | Frage | Prognose? |
| --- | --- | --- |
| **Spektrum** | Welche Periodenlängen stecken in der Reihe? | nein |
| **Zyklenstabilität** | Hält der gefundene Zyklus über die Zeit? | nein, dämpft |
| **Momentanzyklus** | Wo im Zyklus stehen wir jetzt? | ja |
| **Singuläre Spektralanalyse** | Welche Bausteine trägt die Reihe? | ja |
| **Marktverhalten** | Läuft es weiter oder kehrt es um? | nein, gewichtet um |

Dass drei von fünf keine Prognose liefern, ist der Kern des Entwurfs. Die
Vorlage sagt es deutlich: Als alleinige Kursprognose ist Fourier-Analyse zu
schwach. Ihr Wert liegt im Beurteilen.

### Spektrum

Welch-Schätzung über überlappende Teilfenster, Hann-Fenster, Nullauffüllung um
den Faktor vier. Gerechnet wird auf zwei Grundlagen — Log-Renditen und einem
Bandpass der Log-Preise —, weil beide verschiedene Fragen beantworten und
häufig verschiedene Antworten geben.

### Zyklenstabilität

Dieselbe Rechnung auf rollierenden Abschnitten. Zählt, wie oft dieselbe Periode
wiedergefunden wird, und verlangt dabei, dass die Spitze aus ihrer Umgebung
herausragt. Das Ergebnis dämpft sämtliche Zyklusbeiträge der Säule.

### Momentanzyklus

Kausaler Hilbert-Diskriminator nach Ehlers: Phase und Zykluslänge aus einem
Homodyn-Verfahren, gebildet ausschließlich aus zurückliegenden Werten. In der
Fortschreibung läuft die Phase weiter, die Amplitude wird exponentiell gedämpft.

### Singuläre Spektralanalyse

Bahnmatrix, Eigenzerlegung nach Jacobi, Rekonstruktion über Diagonalmittelung,
Fortschreibung über die lineare Rekursion aus den führenden Komponenten. Das
einzige Verfahren der Säule mit einer eigenständigen Prognose über längere
Horizonte.

### Marktverhalten

Trendbereinigte Fluktuationsanalyse (Hurst-Exponent). Liefert selbst keine
Prognose, sondern verschiebt die Vorabgewichte der **ersten** Säule: In einer
trendfolgenden Phase hat das Momentum-Modell recht, in einer rückkehrenden das
Gegenteil. Bislang findet die Hedge-Gewichtung das erst heraus, indem sie sich
irrt — dieser Hinweis erspart ihr den Umweg.

## Fünf Fallen, jede davon aufgetreten

Diese Liste ist nicht theoretisch. Jeder Punkt war eine falsche Zahl, die
überzeugend aussah.

**1. Frequenzraster.** Bei Teilfenstern von 128 Bars liegen oberhalb von Periode
40 nur noch die Stützstellen 42,7, 64 und 128. Ein echter 50-Bar-Zyklus wurde
verlässlich als 42,7 gemeldet — so stabil, dass die Verwechslung wie ein Befund
aussah. *Abhilfe:* Nullauffüllung um Faktor vier.

**2. Es gibt immer eine stärkste Frequenz.** Reines Rauschen kam auf eine
Zyklenstabilität von 0,92. Jedes Fenster fand eine dominante Periode, und sie
landete oft genug in derselben Gegend. *Abhilfe:* Ein Fenster zählt nur als
Treffer, wenn seine Spitze aus der Umgebung herausragt.

**3. Kursspektren sind rot.** Die Prominenz wurde zunächst gegen den Median des
gesamten Bandes gemessen — das setzt ein flaches Spektrum voraus. Kursspektren
fallen aber monoton mit der Frequenz. NVDA, Bitcoin und BNB meldeten daraufhin
alle drei dieselbe dominante Periode mit dreistelliger Prominenz: exakt ein
Drittel der Teilfensterlänge. Drei verschiedene Märkte, identischer Befund — das
war die Farbe des Rauschens. *Abhilfe:* Prominenz gegen den **örtlichen**
Untergrund, gemessen in einem Ring um die Stelle. Ein gleichmäßig abfallendes
Spektrum ergibt damit überall ein Verhältnis nahe eins, also die richtige
Antwort: kein Zyklus.

**4. Die FFT hat eine Naht.** Das analytische Signal über Fourier zu bilden
funktioniert nicht: Die Transformation setzt das Fenster periodisch fort, und
der Sprung an der Nahtstelle verdirbt den rechten Rand — also die Gegenwart. Bei
einem sauberen Sinus lag die Länge richtig, die Güte der Phase aber bei null.
*Abhilfe:* kausale Filter statt Transformation. Nebeneffekt: Zukunftswissen kann
nicht mehr einsickern.

**5. Erklärte Streuung ist keine Prognosegüte.** Auf Log-Kursen erklärt die erste
SSA-Komponente über 95 Prozent — sie fängt den Trend ein. Diese Zahl sagt nichts
darüber, ob sich die Struktur fortschreiben lässt, führte aber dazu, dass die
Zerlegung stets mit vollem Gewicht in die Zusammenführung ging. *Abhilfe:*
Rückhalteprüfung.

## Die Rückhalteprüfung

Das Vertrauen jedes prognostizierenden Verfahrens kommt aus einer Messung, nicht
aus einer Selbstauskunft: Der letzte Abschnitt wird zurückgehalten, das
Verfahren rechnet auf dem Rest, und seine Fortschreibung wird gegen die
Wirklichkeit gehalten — gemessen daran, ob sie besser war als die Annahme, der
Kurs bleibe stehen.

Wer diese Latte reißt, bekommt null und trägt nichts bei.

**Das Ergebnis auf echten Daten, 30 Bars Rückhalt, fünf Jahre Historie:**

| Wert | Zerlegung | Momentanzyklus | Beitrag |
| --- | --- | --- | --- |
| NVDA | 0 % | 0 % | keiner |
| AAPL | 0 % | 0 % | keiner |
| SOL-USD | 0 % | 0 % | keiner |
| BTC-USD | 29 % besser | 0 % | −18,4 % über 30 Bars |

In drei von vier Fällen schlägt **kein** Verfahren dieser Säule die Annahme,
der Kurs bleibe stehen. Die Säule schweigt dann und trägt nichts bei.

Das ist kein Mangel der Umsetzung, sondern das erwartete Ergebnis und die
Bestätigung, dass die Prüfung greift. Eine Säule, die immer etwas zu sagen hat,
wäre der verdächtigere Befund.

## Was noch nicht verdrahtet ist

Der Beitrag fließt in die **Anzeige**, nicht in die gespeicherten Prognosen. Die
Trefferquote der ersten Säule ist über zehntausende ausgewertete Prognosen
gemessen; diese Zahl durch einen ungeprüften Beitrag zu verwässern, wäre der
falsche Weg herum. Die Reihenfolge ist: erst ein Nachweis über die volle
Historie im Walk-Forward, dann die Verdrahtung.

## Wo es liegt

```
src/Ingest.Core/Analysis/Spectral/
  Fft.cs             Radix-2, an Ort und Stelle
  Spectrum.cs        Welch, Hann, Bandpass, örtlicher Untergrund
  CycleStability.cs  rollierende Prüfung
  HilbertCycle.cs    kausaler Homodyn-Diskriminator
  Ssa.cs             Zerlegung, Jacobi, lineare Rekursion
  Regime.cs          Fluktuationsanalyse, Vorabgewichte
  CrossSpectrum.cs   Kohärenz und Phasenvorlauf

src/Ingest.Api/Endpoints/SpectralEndpoints.cs
tests/Ingest.Core.Tests/SpectralTests.cs      20 Prüfungen
```

Abhängigkeitsfrei — passend zur bestehenden Linie im Projekt. Für die
singuläre Spektralanalyse wird nur die Eigenzerlegung einer kleinen
symmetrischen Matrix gebraucht, und dafür ist das Jacobi-Verfahren das
numerisch gutmütigste.
