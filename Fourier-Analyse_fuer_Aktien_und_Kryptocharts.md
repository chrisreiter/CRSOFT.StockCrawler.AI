# Fourier-Transformation und Fourier-Analyse bei Aktien- und Kryptocharts

> **Hinweis:** Die folgenden Ausführungen dienen der mathematischen und technischen Analyse. Sie sind keine Anlageberatung und keine Garantie für profitable Handelssignale.

## Grundidee

Die Fourier-Transformation zerlegt einen zeitlichen Verlauf in einzelne Schwingungen beziehungsweise Frequenzen. Statt nur zu fragen:

> Wie bewegt sich Bitcoin oder eine Aktie im Zeitverlauf?

fragt man:

> Welche periodischen Komponenten stecken in diesen Bewegungen – Stunden, Tage, Wochen oder Monate?

Für eine diskrete Zeitreihe \(x_n\) mit \(N\) Messpunkten lautet die diskrete Fourier-Transformation (DFT):

\[
X_k=\sum_{n=0}^{N-1}x_n\,e^{-i2\pi kn/N}
\]

Dabei beschreibt:

- \(|X_k|\): die Stärke beziehungsweise Amplitude der Frequenz,
- \(\arg(X_k)\): die Phase, also die zeitliche Lage der Schwingung,
- \(f_k=k/N\): die Frequenz in Zyklen pro Abtastintervall,
- \(T_k=N/k\): die Periodendauer in Kerzen beziehungsweise Messpunkten.

Bei Tageskerzen und einem deutlichen Peak für \(T=28\) könnte beispielsweise ein ungefähr 28 Handelstage langer Zyklus in den untersuchten Daten vorhanden sein. Das ist zunächst nur ein statistischer Hinweis und kein Handelssignal.

## Anwendung auf Aktien- und Kryptodaten

### 1. Nicht einfach den Rohpreis transformieren

Aktien- und Kryptopreise enthalten meistens starke Trends. Diese konzentrieren viel Energie in sehr niedrigen Frequenzen und können dadurch scheinbare Zyklen erzeugen.

Besser geeignet sind logarithmische Renditen:

\[
r_t=\ln\left(\frac{P_t}{P_{t-1}}\right)
\]

Alternativ kann die logarithmierte Preisreihe von einem geschätzten Trend bereinigt werden:

\[
x_t=\ln(P_t)-\operatorname{Trend}_t
\]

Als Trendmodell kommen beispielsweise infrage:

- ein linearer Trend,
- ein gleitender Durchschnitt,
- ein exponentieller gleitender Durchschnitt,
- ein Savitzky-Golay-Filter.

Welche Vorverarbeitung richtig ist, hängt von der Fragestellung ab. Log-Renditen eignen sich eher für die Untersuchung kurzfristiger Preisänderungen. Trendbereinigte Log-Preise können sinnvoll sein, wenn nach mittelfristigen Zyklen gesucht wird.

### 2. Gleichmäßige Zeitabstände sicherstellen

Die klassische Fourier-Transformation setzt gleichmäßig verteilte Messpunkte voraus.

- Bei Kryptowährungen ist das relativ einfach, da rund um die Uhr gehandelt wird.
- Bei Aktien müssen Wochenenden, Feiertage und Handelsunterbrechungen berücksichtigt werden.
- Bei Intraday-Daten sollte immer dieselbe Kerzenlänge verwendet werden.

Bei einer Aktie bedeutet eine Periode von 20 Tageskerzen normalerweise 20 Handelstage und nicht 20 Kalendertage.

### 3. Fensterfunktion anwenden

Ein untersuchtes Datenfenster enthält selten eine exakt abgeschlossene Schwingung. An seinen Rändern entstehen dadurch Sprünge. Im Frequenzspektrum verteilt sich die Energie einer Frequenz dann auf benachbarte Frequenzen. Dieses Problem heißt **Spectral Leakage**.

Deshalb wird die Zeitreihe häufig mit einer Fensterfunktion multipliziert, beispielsweise mit einem Hann-Fenster:

\[
x'_n=x_n\,w_n
\]

Anschließend wird die FFT auf \(x'_n\) angewendet. Das verringert Randeffekte, verbreitert allerdings auch Frequenzspitzen etwas.

### 4. Frequenzspektrum untersuchen

Aus den Fourier-Koeffizienten kann ein Leistungsspektrum beziehungsweise Periodogramm berechnet werden:

\[
S_k=|X_k|^2
\]

Peaks im Spektrum können dominante Perioden anzeigen:

| Gefundene Periode | Mögliche Interpretation |
|---:|---|
| 7 Tage | möglicher Wochenrhythmus im Kryptohandel |
| 20–22 Handelstage | ungefähr ein Börsenmonat |
| 60–65 Handelstage | ungefähr ein Quartal |
| wenige Kerzen | häufig Marktrauschen oder Mikrostruktur des Handels |

Eine Frequenzspitze beweist jedoch weder einen stabilen Zyklus noch dessen wirtschaftliche Handelbarkeit.

## Sinnvolle Einsatzgebiete

### Zyklische Muster erkennen

Mit einem Periodogramm lässt sich untersuchen, ob Renditen, Handelsvolumen oder Volatilität wiederkehrende Frequenzen enthalten. Bei Kursrenditen sind dominante Zyklen häufig schwach und instabil. Bei Volumen, Volatilität oder intraday bedingten Marktzeiten können periodische Strukturen deutlicher sein.

### Rauschen und langfristige Bewegungen trennen

Man kann bestimmte Fourier-Koeffizienten entfernen und die verbleibenden Komponenten mit der inversen Fourier-Transformation rekonstruieren:

\[
\hat{x}_t=\operatorname{IFFT}(\widetilde{X}_k)
\]

Beispiele:

- hohe Frequenzen entfernen: geglätteten Verlauf erzeugen,
- tiefe Frequenzen entfernen: kurzfristige Schwankungen isolieren,
- ein Frequenzband auswählen: einen vermuteten Marktzyklus sichtbar machen.

Das ist zunächst nur eine Filterung historischer Daten und noch keine Prognose.

### Zwei Märkte oder Datenreihen vergleichen

Mit Kreuzspektrum und Kohärenz kann untersucht werden:

- ob Bitcoin und Ethereum in bestimmten Frequenzbereichen ähnlich schwingen,
- ob Volumen und Preisbewegungen gemeinsame Frequenzen besitzen,
- ob eine Aktie und ihr Referenzindex auf bestimmten Zeitskalen zusammenhängen.

Die Phasendifferenz kann anzeigen, ob eine Reihe der anderen innerhalb einer bestimmten Frequenz vorausläuft oder folgt. Daraus folgt jedoch keine Kausalität. Außerdem muss geprüft werden, ob dieser Zusammenhang in späteren Zeiträumen stabil bleibt.

### Merkmale für Machine Learning erzeugen

Fourier-Ergebnisse können als zusätzliche Eingangsmerkmale verwendet werden:

- Stärke ausgewählter Frequenzbänder,
- dominante Periodendauer,
- Verhältnis niedriger zu hoher Frequenzen,
- spektrale Entropie,
- Phase ausgewählter Frequenzen,
- zeitliche Veränderung dominanter Frequenzen.

Diese Nutzung als Feature Engineering ist häufig sinnvoller, als historische Sinuswellen direkt in die Zukunft zu verlängern.

## Das zentrale Problem: Finanzmärkte sind nicht stationär

Eine gewöhnliche FFT über das gesamte Datenfenster behandelt die Frequenzen so, als wären sie während des vollständigen Zeitraums vorhanden. Finanzmärkte verändern ihr Verhalten jedoch laufend:

- Bullen- und Bärenmärkte,
- Nachrichten und Liquiditätsschocks,
- wechselnde Volatilität,
- regulatorische Ereignisse,
- unterschiedliche Marktteilnehmer und Handelsalgorithmen.

Ein scheinbarer 30-Tage-Zyklus kann daher einige Monate sichtbar sein und anschließend verschwinden.

Für Finanzdaten sind deshalb häufig zeitlokale oder robustere Verfahren geeigneter:

- **Short-Time Fourier Transform (STFT):** FFT auf aufeinanderfolgenden, meist überlappenden Fenstern,
- **Spektrogramm:** visualisiert, zu welchem Zeitpunkt welche Frequenzen stark waren,
- **Wavelet-Transformation:** lokalisiert Strukturen gleichzeitig in Zeit und Frequenz und erlaubt unterschiedliche zeitliche Auflösungen,
- **Welch-Methode:** mittelt die Spektren mehrerer überlappender Teilfenster und liefert meist eine stabilere Spektralschätzung als ein einzelnes Periodogramm.

## Praktischer Analyseablauf

Für tägliche Bitcoin-Daten könnte ein sinnvoller Ablauf folgendermaßen aussehen:

1. 512 oder 1.024 aufeinanderfolgende Schlusskurse laden.
2. Datenfehler, fehlende Werte und ungleichmäßige Zeitabstände prüfen.
3. Log-Renditen berechnen oder logarithmierte Preise trendbereinigen.
4. Mittelwert entfernen und eine Hann-Fensterfunktion anwenden.
5. FFT, Periodogramm oder Welch-Spektrum berechnen.
6. Nur wirtschaftlich relevante Perioden, beispielsweise 5 bis 120 Tage, betrachten.
7. Das Verfahren auf mehreren rollierenden Fenstern wiederholen.
8. Prüfen, ob Peaks über verschiedene Fenster und Parameter hinweg stabil bleiben.
9. Eine daraus abgeleitete Strategie inklusive Gebühren, Spread und Slippage backtesten.
10. Parameter ausschließlich auf Trainingsdaten bestimmen und anschließend auf unangetasteten Out-of-Sample-Daten testen.

Ein Peak, der nur im Gesamtdatensatz erscheint, in rollierenden Fenstern aber ständig verschwindet, ist wahrscheinlich keine belastbare Marktstruktur.

## Beispielhafter Python-Code

```python
import numpy as np
from scipy import signal

# prices: gleichmäßig abgetastete Schlusskurse als NumPy-Array
log_returns = np.diff(np.log(prices))

# Welch-Schätzung; fs=1 bedeutet eine Messung pro Handelstag bzw. Tag
frequencies, power = signal.welch(
    log_returns,
    fs=1.0,
    window="hann",
    nperseg=min(256, len(log_returns)),
    noverlap=min(128, len(log_returns) // 2),
    detrend="constant",
    scaling="density",
)

# Frequenz 0 hat keine endliche Periodendauer
valid = frequencies > 0
periods = 1.0 / frequencies[valid]
spectral_power = power[valid]

# Beispiel: stärkste Frequenz zwischen 5 und 120 Tagen
band = (periods >= 5) & (periods <= 120)
candidate_period = periods[band][np.argmax(spectral_power[band])]

print(f"Stärkste gefundene Periode: {candidate_period:.1f} Tage")
```

Der Code sucht nur die stärkste historische Spektralkomponente im gewählten Bereich. Vor einer Interpretation müssen Stabilität, statistische Signifikanz und Out-of-Sample-Verhalten untersucht werden.

## Das gefährliche Missverständnis

Man kann die stärksten Frequenzen auswählen, daraus per inverser FFT einen sehr glatten historischen Kurs rekonstruieren und die Sinuskurven anschließend in die Zukunft verlängern. Das Ergebnis sieht häufig erstaunlich überzeugend aus.

Die Methode unterstellt jedoch, dass Frequenz, Amplitude und Phase unverändert bleiben. Genau diese Annahme ist bei Finanzmärkten oft nicht erfüllt. Zusätzlich drohen:

- Overfitting,
- Look-ahead-Bias,
- Scheinkorrelationen,
- instabile Frequenzspitzen,
- künstliche Zyklen durch die gewählte Fensterlänge,
- Daten-Snooping durch wiederholtes Ausprobieren,
- scheinbar profitable Ergebnisse ohne Gebühren und Slippage.

Besonders kritisch ist eine Rekonstruktion, bei der die dominanten Frequenzen aus dem vollständigen Zeitraum bestimmt wurden und dieselben Daten anschließend als angeblicher Backtest dienen.

## Fazit

Fourier-Analyse eignet sich bei Aktien und Kryptowährungen vor allem für:

- Zyklenerkennung,
- Filterung und Glättung,
- Untersuchung von Volumen und Volatilität,
- Vergleich verschiedener Märkte und Zeitskalen,
- Feature Engineering für statistische oder maschinelle Lernverfahren,
- Erkennung zeitlich wechselnder Marktregime.

Als alleinige Kursprognose oder Kauf- und Verkaufsstrategie ist sie normalerweise zu schwach. Am wertvollsten ist sie als Analysebaustein in Kombination mit rollierenden Fenstern, Spektrogrammen oder Wavelets, Volatilitätsmodellen sowie sauberem Out-of-Sample-Backtesting.

## Externe Referenzen

1. SciPy: [Discrete Fourier Transform – `scipy.fft`](https://docs.scipy.org/doc/scipy/tutorial/fft.html)
2. SciPy: [`scipy.signal.periodogram` – Schätzung der spektralen Leistungsdichte](https://docs.scipy.org/doc/scipy/reference/generated/scipy.signal.periodogram.html)
3. SciPy: [`scipy.signal.welch` – Spektralschätzung mit überlappenden Segmenten](https://docs.scipy.org/doc/scipy/reference/generated/scipy.signal.welch.html)
4. SciPy: [Signal Processing Tutorial – Periodogramm und Welch-Methode](https://docs.scipy.org/doc/scipy/tutorial/signal.html)
5. SciPy: [`scipy.signal.ShortTimeFFT` – Short-Time Fourier Transform](https://docs.scipy.org/doc/scipy/reference/generated/scipy.signal.ShortTimeFFT.html)
6. SciPy: [`scipy.signal.spectrogram` – zeitabhängige Darstellung des Frequenzspektrums](https://docs.scipy.org/doc/scipy/reference/generated/scipy.signal.spectrogram.html)
7. SciPy: [`scipy.signal.csd` – Kreuzspektraldichte zweier Signale](https://docs.scipy.org/doc/scipy/reference/generated/scipy.signal.csd.html)
8. SciPy: [`scipy.signal.coherence` – Kohärenz zweier Signale](https://docs.scipy.org/doc/scipy/reference/generated/scipy.signal.coherence.html)
9. Carnegie Mellon University, KiltHub: [Examining Applications of Fourier Transforms to Financial Data and Covariance Estimation](https://kilthub.cmu.edu/articles/thesis/Examining_Applications_of_Fourier_Transforms_to_Financial_Data_and_Covariance_Estimation/12824255)
10. SSRN: [Analysis of Financial Time-Series Using Fourier and Wavelet Methods](https://papers.ssrn.com/sol3/papers.cfm?abstract_id=1289420)
11. UCL Discovery: [Analysis of High-Frequency Financial Data over Different Timescales](https://discovery.ucl.ac.uk/id/eprint/1530091/)
12. Xiong und Wen: [Non-Stationary Time Series Forecasting Based on Fourier Analysis and Cross Attention Mechanism](https://arxiv.org/abs/2505.06917)

---

Stand: August 2026
