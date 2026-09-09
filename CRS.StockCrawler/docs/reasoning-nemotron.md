# Reasoning mit nemotron3:33b — wie es funktioniert und die täglichen Prompts

Diese Datei beschreibt, **wie der Analyse-Assistent von CRSOFT.StockCrawler denkt**, warum er
zuverlässig auf unseren eigenen Daten arbeitet (statt frei zu fabulieren) und **welche Prompts man
jeden Tag absetzen sollte**, um den größten Nutzen herauszuholen.

Quelle der Wahrheit ist der Code: `src/Ingest.Infrastructure/Services/ReasoningService.cs`.

---

## 1. Das Wichtigste in einem Satz

Der Assistent ist **kein nackter Chat mit einem Sprachmodell**, sondern ein
**werkzeug-aufrufender Agent** (Function-Calling): nemotron entscheidet, *welche* unserer Daten es
braucht, ruft dafür ein Werkzeug auf, bekommt das echte Ergebnis aus **SQL / Qdrant / den
Deep-Learning-Modellen** zurück und formuliert die Antwort **ausschließlich daraus**.

> Jede Zahl in der Antwort stammt aus einem Werkzeugaufruf — erfundene Zahlen sind per Systemregel
> verboten, und jeder Aufruf wird zur Nachprüfung mitgeliefert.

---

## 2. Der Ablauf einer Frage

1. **Nachrichtenliste bauen:** System-Prompt (die Regeln) + die letzten 8 Gesprächszüge + deine Frage.
2. **Senden an Ollama:** `POST {endpunkt}/api/chat` mit
   - `model = nemotron3:33b` (Default, umschaltbar über `CRS_REASONING_MODEL`)
   - `think = true` (nemotron/qwen „denken" vor der Antwort — Faktor ~8 langsamer, aber deutlich genauer)
   - `temperature = 0.2`
   - `tools = [8 Werkzeug-Schemata]`
3. **Werkzeug-Runde:** Antwortet das Modell mit `tool_calls`, führt der Server die Werkzeuge **gegen
   unsere Daten** aus und gibt die Ergebnisse als `role:"tool"`-Nachrichten zurück.
4. **Wiederholen** bis zu **6 Runden**, dann formuliert das Modell die Endantwort.
5. **Ausliefern:** Antworttext **plus** alle `ToolTrace`s (Werkzeugname, Argumente, Ergebnis) — damit
   nachvollziehbar ist, worauf die Antwort beruht.

**Der Endpunkt** kommt vom `IOllamaEndpointService`: entweder „Dieser Rechner" (lokales Ollama) oder
eine **gemietete GPU** über SSH-Tunnel (z. B. die vast.ai-RTX-5090). Der Agent merkt davon nichts —
er redet immer mit `…/api/chat`.

### Schutzmechanismen im Code
- **12-Minuten-Frist** je Anfrage: läuft sie ab, kommt ein Teilergebnis mit den bisherigen
  Werkzeugausgaben statt eines 500-Fehlers.
- **Dubletten-Sperre:** ruft das Modell dasselbe Werkzeug mit denselben (feld-sortierten) Argumenten
  erneut auf, bekommt es das gespeicherte Ergebnis zurück statt es neu zu rechnen.
- **Toleranter Argument-Leser:** `wert`/`ticker`/`name` werden wie `symbol` gelesen, Groß-/Kleinschreibung
  egal, erfundene Zusatzfelder werden ignoriert.

---

## 3. Die 8 Werkzeuge = unser Datenbestand

| Werkzeug | Wofür | Quelle |
|---|---|---|
| `kurs` | Letzter Kurs + Bewegung (1/5/20 Tage) | SQL-Kursbars |
| `prognose` | Vorhersage aller Bänder **inkl. gemessener Güte** | Deep-Learning-Modelle |
| `modellzustand` | Welche Modelle geladen sind und was sie taugen | Modell-Register |
| `kurvenereignisse` | Hoch-/Tief-/Wende-/Sattelpunkte, Ausbrüche, Sprünge | Kurvendiskussion (~98k) |
| `verknuepfungen` | Werte mit auffällig gleichzeitigen Ereignissen | Kreuz-Asset-Analyse |
| `wissen` | **zeitlose** Fachliteratur (Bücher, Theorie) | **Qdrant `crs_wissen`** |
| `nachrichten` | **aktuelle** Meldungen (Reuters, WSJ, CNBC, CoinDesk, Notenbanken …) | **Qdrant `crs_semantik`** |
| `werteliste` | Welche Werte verfolgt werden | SQL |

Die Werkzeuge laufen **server-seitig** gegen lokales SQL + Qdrant. Die GPU macht nur das
Sprach-Reasoning — unsere Daten verlassen den Server nicht.

---

## 4. Die eingebaute Skepsis (warum die Antworten belastbar sind)

Der System-Prompt zwingt nemotron zu ehrlichen Aussagen. Die wichtigsten, nicht verhandelbaren Regeln:

1. **Keine erfundenen Zahlen** — nur aus Werkzeugen.
2. **Zwei Latten, nicht eine:** Eine Prognose muss sowohl den **Stillstand** („Kurs bleibt stehen")
   als auch die **blosse Drift** (mittlere Trainingsrendite) schlagen. Fehlerverhältnis ≥ 1 heißt:
   **keine Handelsgrundlage** — und das muss die Antwort sagen.
3. **Gleichzeitigkeit ≠ Vorlauf:** Ein hoher Zusammenhang bei Zeitabstand ~0 erlaubt **keine** Vorhersage.
4. **Fundstelle ≠ Beweis:** Ein Treffer aus der Wissenssammlung ist eine passende Textstelle, kein Beleg —
   Buch und Seite werden genannt.
5. **Keine Anlageempfehlung**, nur Beschreibung des Gemessenen.

Deshalb ist der **Default bewusst nemotron3:33b**: langsam (70–250 s je Runde auf CPU, deutlich
schneller auf der gemieteten GPU), aber es ruft Werkzeuge zuverlässig auf und gibt Urteile korrekt
wieder. Kleinere Modelle (`qwen3-vl:4b`, `granite3.2`) sind schneller, aber gefährlich bzw. können keine
Werkzeuge — dann entstünde genau der wertlose „nackte Prompt". **Modell nur mit Bedacht umstellen.**

> ### ⭐ Empfehlung: eine dedizierte Ollama-Instanz mit nemotron3:33b
>
> Für den besten und verlässlichsten Betrieb sollte **nemotron3:33b auf einer eigenen Ollama-Instanz mit
> GPU** laufen — am besten die **gemietete GPU** (vast.ai RTX 5090), nicht die lokale CPU:
>
> - **Werkzeug-Zuverlässigkeit:** nemotron3:33b ist das einzige der getesteten Modelle, das die 8
>   Werkzeuge stabil aufruft. Damit stehen und fallen die eingebundenen Erkenntnisse.
> - **Tempo:** auf der 5090 sinkt eine Runde von Minuten (CPU) auf Sekunden — bei bis zu 6 Runden je
>   Frage entscheidet das über brauchbar vs. unzumutbar. Gemessen: dieselbe Frage 524 s (CPU) gegen 83 s.
> - **Modell vorziehen und resident halten:** einmal `ollama pull nemotron3:33b`, dann mit
>   `keep_alive: -1` im VRAM halten, damit nicht jede erste Frage 30–60 s Ladezeit kostet.
> - **Ein Endpunkt, ein Modell:** die Instanz soll genau **nemotron3:33b** bereitstellen und als aktiver
>   Endpunkt gewählt sein — dann trifft jede Reasoning-Runde die GPU, nicht versehentlich ein lokales
>   Ollama (häufige Falle: lokales Ollama belegt Port 11434, der Tunnel muss auf einen freien Port).
>
> Kurz: **eine GPU-Ollama-Instanz mit nemotron3:33b ist die empfohlene Betriebsart.** Lokales Ollama auf
> CPU ist nur die Notlösung, wenn keine GPU verfügbar ist.

---

## 5. Tägliche Prompt-Routine

Diese Prompts sind so formuliert, dass nemotron **das richtige Werkzeug** wählt (die Werkzeugwahl war
laut Code die häufigste Fehlerquelle) und die **Güte-Angaben** erzwingt. Faustregeln:

- **Ein Wert pro Frage** (nicht „NVDA und BTC und ETH" in einem Satz).
- **Exakte Symbole** verwenden: `NVDA`, `BTC-USD`, `AAPL` … (im Zweifel zuerst `werteliste`).
- **Nach Güte fragen**, nicht nur nach der Richtung.

### A. Morgens — Systemcheck (immer zuerst)
1. `Welche Modelle sind geladen, und welche schlagen im Sperrbereich sowohl den Stillstand als auch die blosse Drift? Nenne für jedes Band Fehlerverhältnis und Trefferquote.`
2. `Gib mir die Liste der verfolgten Werte gruppiert nach Anlageklasse.`

### B. Prognosen der Kernwerte (je Wert ein Prompt)
3. `Was sagt das Modell aktuell zu NVDA? Nenne für jedes Band das Fehlerverhältnis, beide Latten (Stillstand und blosse Drift) und ob die Prognose trägt.`
4. `Was sagt das Modell zu BTC-USD, und ist die Aussage belastbar oder schlägt sie die Drift nicht?`
5. `Prognose für AAPL — und wenn das Modell die blosse Drift nicht schlägt, sag es deutlich.`

*(Zeile 3–5 für die eigene Watchlist wiederholen — pro Wert eine Frage.)*

### C. Auffälligkeiten & Struktur
6. `Welche auffälligen Kursereignisse gab es zuletzt bei NVDA — stärkste zuerst, mit Datum, Typ und Stufe?`
7. `Welche Werte bewegen sich auffällig gleichzeitig? Sag klar, wo es nur Gleichzeitigkeit ist und wo ein möglicher Vorlauf.`
8. `Gibt es Werte mit einem echten Vorlauf gegenüber BTC-USD, oder ist alles gleichzeitig?`

### D. Aktuelle Nachrichten (Werkzeug `nachrichten`)
9. `Was wird in den letzten Tagen über Bitcoin geschrieben? Fasse die Meldungen zusammen und nenne die Quellen.`
10. `Gibt es aktuelle Nachrichten zur Zinsentscheidung der Notenbank?`
11. `Was ist zuletzt zu NVDA berichtet worden?`

### E. Fachwissen bei Bedarf (Werkzeug `wissen`)
12. `Was sagt die Fachliteratur zu Verlustaversion bei Handelsentscheidungen? Nenne Buch und Seite.`
13. `Erkläre mit Belegstellen aus der Literatur, was Marktmikrostruktur für kurzfristige Prognosen bedeutet.`

### F. Tagesabschluss / Sanity
14. `Fasse zusammen: Welche meiner Kernwerte haben heute eine Prognose, die BEIDE Latten schlägt — und welche nicht?`

> Tipp: Prompt 1, 6, 7 und 9 decken den größten Teil ab. Wer wenig Zeit hat, sendet nur diese vier.

---

## 6. Umschalten & Konfiguration

- **GPU-Endpunkt:** auf der Reasoning-Seite unter „gemietete GPU" (Name / Adresse / SSH-Host / Port /
  Schlüssel → „Eintragen"). Standardmäßig ist der aktuelle vast.ai-Zugang voreingestellt; für eine neue
  Instanz nur SSH-Host und Port aktualisieren.
- **Modell:** Umgebungsvariable `CRS_REASONING_MODEL` (Default `nemotron3:33b`). Nur auf ein
  werkzeugfähiges Modell wechseln.
- **Denkmodus:** intern `Think` (Default an) — aus nur, wenn schnelle, ungefähre Antworten reichen.
- **nemotron auf der GPU-Instanz bereitstellen und resident halten** (auf der gemieteten Maschine, hinter
  dem Tunnel):
  ```
  ollama pull nemotron3:33b
  # vorwaermen + im VRAM halten, damit keine 30-60 s Ladezeit anfallen:
  curl 127.0.0.1:11434/api/chat -d '{"model":"nemotron3:33b","messages":[{"role":"user","content":"bereit"}],"keep_alive":-1}'
  ```
  Prüfen, dass genau dieses Modell erreichbar ist: die Reasoning-Seite listet die Modelle des aktiven
  Endpunkts; `nemotron3:33b` muss dabei sein.

---

*Diese Beschreibung entspricht dem Stand von `ReasoningService.cs`. Ändert sich dort die Werkzeug- oder
Regelliste, diese Datei nachziehen.*
