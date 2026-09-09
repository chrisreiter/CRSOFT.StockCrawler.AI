# CRSOFT.StockCrawler

Sammelt Kurse von Aktien, Fonds/ETFs und Kryptowährungen in ein **einheitliches
Datenmodell**, zeigt sie in einer Weboberfläche übereinander oder nebeneinander,
untersucht die **Wechselwirkungen** zwischen ihnen und stellt daraus
**selbstlernende Prognosen** über mehrere Horizonte.

---

## Schnellstart

```bash
# 1. Datenbank anlegen und Schema einspielen
#    -ExecutionPolicy Bypass ist nötig, weil Skriptausführung
#    auf diesem Rechner standardmäßig gesperrt ist.
powershell -ExecutionPolicy Bypass -File infra/apply-sql.ps1

# 2. API starten
cd src/Ingest.Api
dotnet run

# 3. Oberfläche öffnen
#    http://localhost:5011
```

Beim allerersten Start ist die Datenbank leer. Ein kompletter Erstlauf —
Universum ermitteln, Historie holen, Analyse rechnen, erste Prognosen —
geht über einen Aufruf:

```bash
curl -X POST "http://localhost:5011/api/ingest/bootstrap?months=24"
```

Das dauert je nach Umfang rund fünf bis zehn Minuten. Danach läuft der
Scheduler von allein weiter.

---

## Provider — was evaluiert wurde und warum die Wahl so fiel

| Anbieter | Aktien | ETF | Krypto | 1h | Key nötig | Bewertung |
|---|:--:|:--:|:--:|:--:|:--:|---|
| **Yahoo Finance** | ✓ | ✓ | ✓ | ✓ | nein | **Standard.** Ein Endpoint, ein Schema für alle drei Klassen. 1h bis 730 Tage zurück, Tagesbars beliebig weit. Inoffizielle Schnittstelle. |
| **Twelve Data** | ✓ | ✓ | ✓ | ✓ | ja | **Empfohlen für den Dauerbetrieb.** Sauber dokumentiert und lizenziert, `/time_series` deckt alle Klassen ab. Frei: 800 Aufrufe/Tag, 8/Minute. Implementiert, aktiviert sich sobald ein Schlüssel hinterlegt ist. |
| **CoinGecko** | – | – | ✓ | – | nein | Liefert als einzige Quelle eine belastbare **Marktkapitalisierungs-Rangliste** für Krypto. Wird genau dafür benutzt, nicht für Kurse. |
| EOD Historical Data | ✓ | ✓ | – | ✓ | ja | Solide, aber Krypto fehlt und der Testzugang beschränkt sich auf Demo-Ticker. Nicht weiterverfolgt. |
| Alpha Vantage | ✓ | ✓ | ✓ | ✓ | ja | Freistufe auf 25 Aufrufe/Tag geschrumpft — für 300 Werte unbrauchbar. |
| Stooq | ✓ | ✓ | – | – | nein | Inzwischen hinter einer JavaScript-Prüfung, maschinell nicht mehr abrufbar. Ausgeschieden. |
| CryptoCompare / CoinDesk Data | – | – | ✓ | ✓ | ja | Freistufe im Mai 2026 eingestellt. Ausgeschieden. |

**Die Entscheidung:** Yahoo als Standard, weil das System damit **ohne
Registrierung sofort arbeitet** und alle drei Anlageklassen aus einer Quelle mit
identischem Schema kommen. Twelve Data ist vollständig implementiert und
übernimmt automatisch, sobald ein Schlüssel hinterlegt ist — für den
Dauerbetrieb ist das die belastbarere Wahl, weil die Schnittstelle offiziell
unterstützt wird.

Umschalten geht ohne Codeänderung:

```json
"Sources": { "TwelveData": { "ApiKey": "dein-schlüssel" } }
```

Unter `/api/health/providers` steht, wer gerade einsatzbereit ist.

---

## Datenmodell

Der Kern ist **eine** Tabelle für alles:

```
price_bar(asset_id, interval_code, ts_utc, open, high, low, close, adj_close, volume, provider)
          └─ PK: (asset_id, interval_code, ts_utc)
```

Eine Apple-Aktie, ein S&P-500-ETF und Bitcoin liegen in derselben Struktur, in
1h- und 1d-Auflösung. Jede Auswertung liest nur hier — Sonderfälle je
Anlageklasse gibt es oberhalb der Ingest-Schicht nicht mehr.

`asset` beschreibt das Instrument (Klasse, Symbol, Marktkapitalisierung, Rang,
Provider) und trägt das Flag `is_tracked`. Nur verfolgte Werte werden abgerufen,
analysiert und prognostiziert; die übrigen bleiben als Auswahlvorrat stehen.

### Zeitstempel werden gerastert

Yahoo stempelt Tagesbars von US-Aktien auf den Handelsbeginn (13:30 UTC),
Krypto dagegen auf Mitternacht; Stundenbars laufen bei Aktien auf `:30`, bei
Krypto auf `:00`. Ohne Korrektur hätten zwei Reihen **null** gemeinsame
Zeitpunkte, und jede Korrelation wäre leer. `BarNormalizer` rundet deshalb beim
Ingest ab: Tagesbars auf 00:00 des Handelstages, Stundenbars auf den
Stundenbeginn.

### Datenqualität: unmögliche Sprünge

Die Zuordnung eines CoinGecko-Coins auf einen Yahoo-Ticker über das Schema
`SYMBOL-USD` trifft bei kleineren Token gelegentlich ein **ganz anderes
Papier**. Das fällt nicht durch fehlende Daten auf, sondern durch Kursreihen,
die um Größenordnungen springen. Konkret gefunden:

| Symbol | Name | schlimmster Tagessprung |
|---|---|---:|
| `GRAM-USD` | Gram (prev. Toncoin) | Faktor **1254** |
| `JUP-USD` | Jupiter | Faktor 687, 39-mal auffällig |
| `LIT-USD` | Lighter | Faktor 18 |

Der Effekt war erheblich: der mittlere Prognosefehler über 30 Tage lag bei
Krypto dadurch bei **333 %**, während Aktien und ETFs bei 4–12 % lagen.

Zwei Vorkehrungen dagegen:

1. **`BarQualityFilter`** verwirft beim Ingest Bars, die gegenüber der
   Vorgängerbar um mehr als Faktor 10 (täglich) beziehungsweise 5 (stündlich)
   springen. Ein Sprung, der auf dem neuen Niveau *bleibt*, wird als echte
   Neubewertung durchgelassen — sonst bliebe ein Wert nach einer
   Token-Umstellung dauerhaft ohne Kurse.
2. **`POST /api/assets/quarantine`** durchsucht den vorhandenen Bestand,
   löscht die Daten betroffener Werte und nimmt sie aus der Verfolgung.
   Mit `apply=false` nur als Vorschau.

Eine gepflegte Ausnahmeliste wäre die naheliegende Alternative gewesen, würde
dem Problem aber immer hinterherlaufen. Geprüft wird deshalb die Plausibilität
der Reihe selbst.

### Weitere Tabellen

| Tabelle | Inhalt |
|---|---|
| `pair_stat` | Korrelation und bester Vorlauf je Wertepaar |
| `crossing` | Zeitpunkte, an denen zwei normalisierte Kurven die Plätze tauschten |
| `forecast` / `forecast_component` | abgegebene Prognosen samt Beitrag jedes Teilmodells |
| `forecast_score` | nachträgliche Bewertung gegen den tatsächlichen Kurs |
| `model_weight` | **die gelernten Gewichte** je Wert × Horizont × Teilmodell |
| `ingest_run` | Protokoll aller Läufe mit Kennzahlen |

---

## Universum — „die größten 100"

Vollständig über Live-Abfragen, keine gepflegte Symbolliste:

- **Aktien** — mehrere Yahoo-Screener zusammengeführt, sortiert nach
  `marketCap`
- **Fonds/ETFs** — Screener `top_etfs_us`, sortiert nach `netAssets`
- **Krypto** — CoinGecko `market_cap_rank`; Stablecoins werden ausgefiltert,
  weil eine konstante Reihe bei 1 USD für Korrelationen wertlos ist

Was tatsächlich verfolgt wird, ist im Reiter **Auswahl** frei einstellbar.

---

## Analyse

**Korrelation** auf Log-Renditen, nicht auf Kursen — Kurse sind zu
trendbehaftet, zwei beliebige steigende Papiere korrelieren sonst scheinbar
perfekt.

**Vorlauf (Lead/Lag)** über Kreuzkorrelation bis ±12 Bars. Ein positiver Lag
bedeutet: A bewegt sich zuerst, B zieht später nach. Gesucht wird nur, wo
überhaupt ein Gleichlauf von mindestens 0,15 besteht — sonst ist der „beste
Lag" reines Rauschen.

**Ausrichtung erfolgt paarweise.** Eine gemeinsame Zeitachse über alle 300
Werte existiert nicht: ein einziger Feiertag an einer Börse würde den Tag für
sämtliche Paare unbrauchbar machen. Jedes Paar wird auf seiner eigenen
Schnittmenge verglichen.

**Kreuzungen** auf zwei auf 100 normalisierten Kurven — der Moment, in dem die
relative Stärke kippt.

Größenordnung: 300 Werte ergeben rund 35.000 Paare; ein kompletter Lauf braucht
etwa **6 Sekunden**.

---

## Prognose und die Lernschleife

Fünf Teilmodelle liefern je einen erwarteten Log-Return:

| Modell | Idee |
|---|---|
| `naive` | Kurs bleibt, wo er ist — die Messlatte, die es zu schlagen gilt |
| `drift` | historische mittlere Rendite fortgeschrieben |
| `momentum` | EWMA des Trends, mit dem Horizont gedämpft |
| `meanrev` | Rückkehr zum gleitenden Mittel, je nach Abstand |
| `leadlag` | überträgt die Bewegung der Frühindikatoren über deren Beta |

Kombiniert werden sie **gewichtet je Wert und Horizont**. Horizonte bis 72
Stunden rechnen auf Stundenbars, darüber auf Tagesbars.

### Wie gelernt wird

Jede Prognose wird gespeichert. Ist ihr Zielzeitpunkt erreicht, holt
`ScoringService` den tatsächlichen Kurs und bewertet **jedes Teilmodell
einzeln**. Die Gewichte werden nach dem **Hedge-Verfahren** angepasst:

```
w_i  ←  w_i · exp(−η · Verlust_i)
```

ein Verfahren mit bekannter Regret-Schranke — die Gewichte laufen nachweislich
gegen das beste Teilmodell, ohne dass vorher feststehen muss, welches das ist.
Ein Mindestgewicht bleibt erhalten, damit ein Modell zurückkommen kann, wenn
sich das Marktregime dreht.

### Der Verlust bestraft auch die Richtung

Das ist der Punkt, an dem die erste Fassung danebenlag. Optimiert man allein
den Betragsfehler, gewinnt zwangsläufig `naive` — „keine Änderung" ist
rechnerisch der beste Schätzer. Als Aussage ist das wertlos, weil nie eine
Richtung genannt wird.

Gemessen an einem Wert über 240 Prognosen:

| Verlustfunktion | Gewicht `naive` | Richtung korrekt | Ø Betragsfehler |
|---|---:|---:|---:|
| nur Betrag | 42,0 % | 51,7 % | 3,63 % |
| **Betrag + Richtungsstrafe** | **1,4 %** | **60,4 %** | 3,65 % |

Die Richtungsstrafe kostet praktisch keine Genauigkeit im Betrag und hebt die
Trefferquote deutlich. Sie ist deshalb aktiv (`directionPenalty`, Standard 1,2).

### Backtest statt Warten

Frisch aufgesetzt hätte das System keinerlei gelernte Gewichte und man müsste
wochenlang auf fällig werdende Prognosen warten. Der Walk-Forward-Backtest
schließt diese Lücke:

```bash
curl -X POST "http://localhost:5011/api/forecast/backtest?steps=40&stride=3"
```

Er stellt Prognosen zu Zeitpunkten in der Vergangenheit — die Modelle sehen
dabei ausschließlich die Bars davor — wertet sie sofort gegen den eingetretenen
Kurs aus und wendet dieselbe Lernregel an wie im Livebetrieb.

> **Einschränkung, bewusst in Kauf genommen:** Die Auswahl der Frühindikatoren
> stammt aus der Analyse über den gesamten Zeitraum. Das Signal selbst wird nur
> aus Vergangenheitsdaten gebildet, die *Auswahl* kennt aber den ganzen
> Zeitraum. Für `leadlag` ist das Backtest-Ergebnis daher eher optimistisch; die
> übrigen vier Teilmodelle sind davon nicht betroffen.

### Vollständiger Walk-Forward

```bash
curl -X POST "http://localhost:5011/api/learning/walkforward?buckets=40&pairRefreshEvery=60"
```

Läuft **Bar für Bar** durch die gesamte Historie — nicht als Stichprobe wie
`/backtest`, sondern lückenlos von der ersten bis zur letzten Bar. Drei
Durchläufe hintereinander: Tag → Stunde → Tag, mit über die Durchläufe hinweg
mitgeführten Gewichten.

Zwei Eigenschaften sind dabei entscheidend:

**Die Bewertung ist aufgeschoben.** Eine Prognose wird erst dann ausgewertet,
wenn ihr Zielzeitpunkt im Durchlauf tatsächlich erreicht ist; bis dahin wartet
sie in einer Warteschlange. Würde direkt nach der Prognose gelernt, kennten die
Gewichte an Tag n+1 bereits das Ergebnis von Tag n+30.

**Die Vorlaufstruktur wird mitgeführt.** Alle `pairRefreshEvery` Schritte
werden Korrelationen und Lead-Lag-Beziehungen neu bestimmt — ausschließlich aus
den bis dahin bekannten Daten. Sonst wüsste `leadlag` von Zusammenhängen, die
sich erst später ergeben haben.

Im Tagesdurchlauf entfallen die Horizonte unter 24 Stunden: sie sind kürzer als
eine Bar und dort nicht auflösbar.

Ergebnis eines vollen Laufs (275 Werte, **6,0 Mio. Prognose-Lern-Zyklen**,
Laufzeit **3 Minuten**):

| Durchlauf | Prognosen | Ø Fehler | Richtung korrekt |
|---|---:|---:|---:|
| 1 — Tag | 506.881 | 5,20 % | 52,38 % |
| 2 — Stunde | 4.982.649 | 5,09 % | 52,25 % |
| 3 — Tag | 506.881 | 5,20 % | 52,43 % |

#### Was der Lauf zeigt — und was nicht

**Die Modellauswahl lernt nachweislich.** Von Gleichverteilung startend
verschieben sich die Gewichte deutlich und in die richtige Richtung:

| Modell | erstes Zeitfenster | letztes Zeitfenster |
|---|---:|---:|
| `drift` | 26,2 % | **48,3 %** |
| `meanrev` | 32,4 % | 32,3 % |
| `momentum` | 19,7 % | 12,3 % |
| `naive` | 11,2 % | 4,3 % |
| `leadlag` | 10,5 % | 2,9 % |

**Die Trefferquote steigt im Tagesdurchlauf sichtbar** — je nach Horizont um
4,5 bis 6,3 Prozentpunkte vom ersten zum letzten Drittel, von rund 49 % auf
rund 54 %.

**Aber dieser Anstieg ist überwiegend ein Zeitraum-Effekt, kein Lerneffekt.**
Das zeigt der dritte Durchlauf: Er startet mit vollständig trainierten
Gewichten und reproduziert trotzdem dieselbe Kurve — inklusive der Schwäche im
ersten Drittel (+4,45 statt +4,51 Prozentpunkte). Wäre der Anstieg gelerntes
Wissen, müsste der dritte Durchlauf von Anfang an besser liegen. Der frühe
Zeitraum war schlicht schwerer zu prognostizieren als der späte.

**Ein zweiter Tagesdurchlauf bringt fast nichts** (52,38 % → 52,43 %). Das ist
kein Fehler, sondern eine Eigenschaft des Hedge-Verfahrens: es konvergiert
schnell und ist gegenüber dem Startzustand unempfindlich. Weitere Epochen sind
verschwendete Rechenzeit.

**Der Stundendurchlauf zeigt keinen Trend** (zwischen −3,1 und +0,7
Prozentpunkte). Konsistent damit, dass er mit den bereits im Tagesdurchlauf
trainierten Gewichten startet — die Gewichte je Wert und Horizont sind
gemeinsam, unabhängig von der Auflösung.

> **Korrektur zu einer früheren Angabe:** Der Stichproben-Backtest
> (`/api/forecast/backtest`) meldete 57,7 % Richtungstreffer. Diese Zahl ist
> **zu optimistisch**. Er bewertet jede Prognose sofort und lernt daraus, bevor
> der nächste Prüfpunkt an die Reihe kommt — bei einem Abstand von 3 Bars und
> Horizonten bis 30 Bars fließt damit Wissen aus der Zukunft in die Gewichte
> ein. Zusätzlich deckt er nur den jüngsten Zeitraum ab. Belastbar ist die Zahl
> aus dem vollständigen Walk-Forward: **rund 52,3 %**.

### Ergebnis des Stichproben-Backtests (zum Vergleich)

297 Werte, 63.651 Prognosen, jede gegen den tatsächlichen Kurs geprüft
(Laufzeit 6 Minuten). **Die Zahlen sind aus dem oben genannten Grund zu
optimistisch** und stehen hier nur als Vergleich:

| Horizont | n | Ø Betragsfehler | Richtung korrekt |
|---|---:|---:|---:|
| 1 h | 10.640 | 0,39 % | 52,0 % |
| 4 h | 10.640 | 0,94 % | 52,7 % |
| 1 Tag | 10.640 | 2,58 % | 59,1 % |
| 3 Tage | 10.624 | 4,09 % | **65,9 %** |
| 1 Woche | 10.563 | 4,24 % | 55,3 % |
| 30 Tage | 10.544 | 8,47 % | 61,5 % |
| **gesamt** | **63.651** | **3,44 %** | **57,7 %** |

Und so verteilten sich die Gewichte am Ende:

| Modell | Ø Fehler | Richtung | Gewicht |
|---|---:|---:|---:|
| `drift` | 0,0388 | 50,7 % | 33,7 % |
| `momentum` | 0,0360 | 49,7 % | 33,3 % |
| `meanrev` | 0,0408 | 52,5 % | 27,8 % |
| `leadlag` | 0,0362 | 6,5 % | 2,9 % |
| `naive` | **0,0352** | 2,1 % | 2,3 % |

Bemerkenswert daran: `naive` hat den **kleinsten** Betragsfehler und trotzdem
das geringste Gewicht — genau das leistet die Richtungsstrafe.

Über die Stundenhorizonte liegt die Trefferquote bei rund 52 % und damit kaum
über dem Münzwurf. Das ist kein Mangel der Umsetzung, sondern die Realität
kurzfristiger Kursbewegungen. Verwertbar wird das Signal erst ab einem Tag.

Die Gewichte werden **je Wert und Horizont** gelernt, nicht global. Für NVDA
auf 24 Stunden ergab der Lauf etwa: `meanrev` 76 %, `momentum` 20 %, der Rest
nahe null — weil `meanrev` für genau diesen Wert und diesen Horizont die beste
Trefferquote hatte.

---

## Zeitplan

| Takt | Was passiert |
|---|---|
| stündlich (`:08`) | Stundenbars nachladen → fällige Prognosen bewerten (**hier wird gelernt**) → neue Prognosen |
| täglich (`02:20 UTC`) | Universum auffrischen → Tagesbars → Analyse neu rechnen → bewerten |

Erst bewerten, dann prognostizieren — so fließen die frisch gelernten Gewichte
sofort in die nächste Prognose ein. Einstellbar über `Ingest:HourlyCronUtc` und
`Ingest:DailyCronUtc`.

---

## Oberfläche

| Reiter | Zweck |
|---|---|
| **Kurse** | bis zu 12 Werte auswählen, **übereinander** oder **nebeneinander**, 1h/1d, Zeitraum in Monaten, wahlweise auf 100 normalisiert |
| **Auswahl** | die Gesamtliste aller gefundenen Instrumente; hier wird bestimmt, was verfolgt wird |
| **Analyse** | Frühindikatoren, Korrelationen, Kreuzungen |
| **Prognose** | aktuelle Prognosen je Horizont mit bisheriger Trefferquote, plus die gelernten Modellgewichte |
| **System** | Bestand, Provider-Status, Protokoll der Läufe |

Die Normalisierung auf 100 ist das, was ein Übereinanderlegen überhaupt
sinnvoll macht: eine 300-Dollar-Aktie und ein 70.000-Dollar-Bitcoin sind sonst
nicht in einem Diagramm vergleichbar.

---

## Wichtigste Endpunkte

```
GET  /api/assets/?cls=Stock&search=AAPL&tracked=true
POST /api/assets/track                      { assetIds: [1,2], tracked: true }
POST /api/assets/universe/refresh

POST /api/ingest/bootstrap?months=24        kompletter Erstlauf
POST /api/ingest/backfill?months=24&interval=1d
POST /api/ingest/update?interval=1h

GET  /api/series/?ids=1,2,3&interval=1d&months=12&rebase=true
GET  /api/series/{assetId}?interval=1d&months=12

POST /api/analysis/recompute?interval=1d&windowBars=500
GET  /api/analysis/leaders/{assetId}?interval=1h
GET  /api/analysis/correlations/{assetId}
GET  /api/analysis/crossings?interval=1d&days=30

POST /api/forecast/run
POST /api/forecast/score
POST /api/forecast/backtest?steps=40&stride=3

POST /api/learning/walkforward?buckets=40&pairRefreshEvery=60
GET  /api/learning/epochs
GET  /api/learning/curve/{epochId}
GET  /api/forecast/{assetId}
GET  /api/forecast/accuracy
GET  /api/forecast/weights/{assetId}/{horizonHours}

GET  /api/health/stats
GET  /api/health/providers
GET  /api/health/runs?last=50
```

---

## Aufbau

```
src/
  Ingest.Core/            Modelle, Schnittstellen, Analyse- und Prognosemathematik
    Analysis/             Statistik, Ausrichtung, Kreuzungen, Modelle, Ensemble
  Ingest.Infrastructure/
    Providers/            Yahoo, TwelveData, CoinGecko, Yahoo-Screener
    Repositories/         Dapper, SqlBulkCopy für Massenschreibvorgänge
    Services/             Universum, Ingest, Analyse, Prognose, Scoring, Backtest
  Ingest.Api/             Minimal-API, Scheduler, wwwroot (Oberfläche)
infra/sql/                Schema und Prozeduren
```

Die Prognose- und Analysemathematik liegt bewusst in `Ingest.Core` — ohne
Abhängigkeit zu Datenbank oder HTTP und damit testbar.

---

## Grenzen — was dieses System nicht leistet

- **Keine Anlageberatung.** Die Prognosen sind eine statistische Fortschreibung
  historischer Muster. Über längere Horizonte liegt die Richtungstrefferquote
  nahe am Münzwurf, und das ist kein Fehler, sondern die Natur der Sache.
- **Die Trefferquote braucht Zeit.** Aussagekräftig wird sie erst nach einigen
  hundert ausgewerteten Prognosen je Horizont. Vorher ist der Backtest der
  einzige belastbare Anhaltspunkt.
- **Yahoo ist inoffiziell.** Die Schnittstelle kann sich jederzeit ändern. Für
  den Dauerbetrieb einen Twelve-Data-Schlüssel hinterlegen.
- **Rund 20 der 300 gefundenen Werte liefern keine Kurse** — meist junge
  Krypto-Token, die bei CoinGecko gelistet sind, aber unter dem Schema
  `SYMBOL-USD` bei Yahoo nicht existieren. Sie stehen im Universum, bleiben
  aber ohne Bars.
- **Nur US-Handelszeiten.** Aktien und ETFs stammen aus US-Screenern.
  Europäische Papiere lassen sich über die Auswahl ergänzen, wenn das Symbol
  bei Yahoo bekannt ist (z. B. `SAP.DE`).

## Die Dokumentation

Jede Säule und jede Ansicht hat ein eigenes Blatt. Sie beschreiben nicht nur,
was gebaut wurde, sondern **was gemessen wurde und was dabei schiefging** —
letzteres ist meist der nützlichere Teil.

| Blatt | Inhalt |
| --- | --- |
| [SAEULE-MATHEMATIK](docs/SAEULE-MATHEMATIK.md) | Spektren, Moden, Merkmalskontrolle |
| [SAEULE-KURVENDISKUSSION](docs/SAEULE-KURVENDISKUSSION.md) | Savitzky-Golay, Extrema, Verknüpfungen |
| [SAEULE-DEEP-LEARNING](docs/SAEULE-DEEP-LEARNING.md) | Die drei Bänder, die Driftlatte, das Entdriften |
| [ERHALTUNG-QUERSCHNITT](docs/ERHALTUNG-QUERSCHNITT.md) | Warum Gleichzeitiges kein Vorlauf ist |
| [SAEULEN-WISSEN-SEMANTIK](docs/SAEULEN-WISSEN-SEMANTIK.md) | Einbettung, Qdrant, Fundstellen |
| [SAEULE-REASONING](docs/SAEULE-REASONING.md) | Der Gesprächsagent und seine Werkzeuge |
| [QUELLEN-UND-FEEDS](docs/QUELLEN-UND-FEEDS.md) | Woher die Nachrichten und die Literatur kommen |
| [HANDELSSEITEN](docs/HANDELSSEITEN.md) | Kreuzungs-Rangliste, Tausch, Day Trading, Kostenmodell |
| [RATGEBERSEITEN](docs/RATGEBERSEITEN.md) | Langfrist, Bot-Herde, Umkehrschluss |
| [WELTBESTAND-UND-ZEITZONEN](docs/WELTBESTAND-UND-ZEITZONEN.md) | Warum der Bestand nur US war, und was der Zeitzonen-Vorlauf hergibt |
| [NACHTLAUF-22-08-2026](docs/NACHTLAUF-22-08-2026.md) | Ein Protokoll, was eine Nacht Prüfen zutage fördert |

## Sicherheitshinweis

`appsettings.json` enthält die Zugangsdaten zur Datenbank im Klartext und liegt
im Repository. Für alles über den lokalen Entwicklungsstand hinaus gehört das in
User Secrets oder eine Umgebungsvariable:

```bash
dotnet user-secrets set "ConnectionStrings:Sql" "Server=…;User Id=…;Password=…"
```

---

## Lizenz

**AGPL-3.0** — benutzen, ändern, weitergeben und betreiben ist frei, auch
geschäftlich. Wer die Software weitergibt **oder als Netzdienst anbietet**, muss
den Quelltext seiner Fassung unter derselben Lizenz offenlegen (§ 13 der AGPL
schliesst die Lücke, die die gewöhnliche GPL bei Webanwendungen lässt).

Wer eine Fassung **ohne** diese Offenlegungspflicht braucht — etwa für ein
geschlossenes Produkt —, wendet sich wegen einer kommerziellen Lizenz an CRSOFT.

Volltext in [LICENSE](../LICENSE), die Begründung und die Grenzen des Modells in
[LICENSING.md](../LICENSING.md), die Rechteeinräumung für Beiträge in
[CONTRIBUTING.md](../CONTRIBUTING.md).
