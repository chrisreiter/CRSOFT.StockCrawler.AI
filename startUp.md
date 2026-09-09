# startUp — die Anwendung auf einem neuen Rechner zum Laufen bringen

Diese Datei ist ein **Ablaufplan für Claude Code**, nicht bloss eine Anleitung
zum Nachlesen. Ein Beitragender startet sie mit einem Befehl:

```
claude "/startup"
```

Ohne Claude Code lässt sie sich genauso von Hand abarbeiten — jeder Schritt
steht als Befehl da.

---

## Anweisung an Claude Code

Arbeite die Abschnitte **der Reihe nach** ab. Halte dich an vier Regeln:

1. **Prüfen, bevor du installierst.** Jeder Abschnitt beginnt mit einer Prüfung.
   Ist das Werkzeug schon da, überspringe die Installation und sage es.
2. **Vor jedem grossen Download fragen.** Alles über 2 GB wird nicht ohne
   Rückfrage geholt — `nemotron3:33b` allein sind 27,6 GB.
3. **Nach jedem Schritt nachweisen, dass er gewirkt hat.** Die Prüfbefehle
   stehen dabei. Ein Schritt ohne Nachweis gilt als nicht erledigt.
4. **Bei einem Fehlschlag anhalten und berichten**, statt den nächsten Schritt
   zu versuchen. Was hier schiefgeht, geht meist an einer Voraussetzung schief,
   und die folgenden Schritte verdecken sie nur.

Am Ende gibst du eine Übersicht: was läuft, was fehlt, was davon optional ist.

---

## 0 · Was der Rechner mitbringen muss

| | mindestens | wofür |
| --- | --- | --- |
| Plattenplatz | **10 GB**, mit Reasoning-Modell **45 GB** | Datenbank, Vektoren, Modelle |
| Arbeitsspeicher | 8 GB, für `nemotron3:33b` **32 GB** | das Modell liegt im RAM, wenn keine GPU da ist |
| Betriebssystem | Windows, Linux oder macOS | die Anwendung selbst ist plattformunabhängig |

**Windows ist der bequemere Weg**, weil die Hilfsskripte PowerShell sind und
SQL Server dort nativ läuft. Unter Linux und macOS gehen SQL Server und Qdrant
über Docker; das steht bei den jeweiligen Schritten.

Prüfen:

```powershell
[math]::Round((Get-PSDrive C).Free/1GB,1)          # Windows
```
```bash
df -h .   &&   free -g 2>/dev/null || vm_stat      # Linux / macOS
```

---

## 1 · .NET 8 SDK

Die Projekte stehen auf `net8.0`. Ein neueres SDK (10.x) baut sie problemlos —
es braucht **kein** SDK 8, solange eines ab 8 vorhanden ist.

```bash
dotnet --version
```

Fehlt es: <https://dotnet.microsoft.com/download> — oder
`winget install Microsoft.DotNet.SDK.8` · `brew install --cask dotnet-sdk` ·
`sudo apt install dotnet-sdk-8.0`.

**Nachweis:** `dotnet --list-sdks` zeigt eine Fassung ab 8.

---

## 2 · SQL Server

Geprüft mit **SQL Server 2025**; ab 2019 sollte alles gehen. Express reicht
nicht immer — die Datenbank wächst über die 10-GB-Grenze von Express hinaus,
sobald ein grösserer Bestand verfolgt wird. Für die Entwicklung ist
**Developer Edition** die richtige Wahl (kostenlos, voller Funktionsumfang).

```bash
sqlcmd -S ".\SQLSERVER" -Q "SELECT @@VERSION" -C
```

Fehlt er:

- **Windows:** SQL Server Developer Edition installieren, Instanzname
  `SQLSERVER`, gemischte Authentifizierung einschalten.
- **Linux / macOS / überall:**
  ```bash
  docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=<starkes-Kennwort>" \
             -p 1433:1433 --name crs-sql -d mcr.microsoft.com/mssql/server:2022-latest
  ```
  Der Server heisst dann `localhost,1433` statt `.\SQLSERVER`.

### Nur die Anmeldung anlegen — die Datenbank kommt von selbst

**Die Datenbank legt `010_unified_schema.sql` selbst an**, und zwar idempotent:

```sql
IF DB_ID('stockcrawler') IS NULL
  EXEC('CREATE DATABASE stockcrawler');
```

`apply-sql.ps1` verbindet für genau diese Datei gegen `master` und für alle
übrigen gegen `stockcrawler`. Von Hand anzulegen ist also **nur die
Serveranmeldung** — und die Berechtigung, eine Datenbank zu erzeugen:

```sql
CREATE LOGIN [stockcrawler] WITH PASSWORD = N'StockCrawler!';
ALTER SERVER ROLE dbcreator ADD MEMBER [stockcrawler];
```

Mehr braucht es nicht. Wer die Datenbank erzeugt, ist ihr **Eigentümer** und
damit darin `dbo` — ein eigenes `CREATE USER` entfällt.

> **Claude: für eine lokale Entwicklungsinstanz genügt die Vorgabe
> `stockcrawler` / `StockCrawler!` aus den Skripten** — sie ist genau dafür da.
>
> **Für alles, was aus dem Netz erreichbar ist, erfindest du ein Kennwort und
> fragst den Nutzer, ob er es so will.** Dieses Repository ist öffentlich; die
> Vorgabe steht damit für jeden lesbar da und taugt nur so weit, wie die
> Datenbank ohnehin niemandem offensteht.

**Wenn `dbcreator` nicht vergeben werden darf** — auf einem verwalteten Server
der Normalfall —, legt ein Administrator die Datenbank vorher an und mappt den
Benutzer hinein:

```sql
CREATE DATABASE stockcrawler;
GO
USE stockcrawler;
CREATE USER [stockcrawler] FOR LOGIN [stockcrawler];
ALTER ROLE db_owner ADD MEMBER [stockcrawler];
GO
```

Das `IF DB_ID(...) IS NULL` in der Migration sieht die vorhandene Datenbank und
überspringt das Anlegen.

**Nachweis:**
`sqlcmd -S <server> -U stockcrawler -P <kennwort> -d master -Q "SELECT 1" -C`
— die Datenbank selbst gibt es an dieser Stelle noch nicht.

---

## 3 · Schema einspielen

```powershell
cd CRS.StockCrawler
powershell -File infra/apply-sql.ps1 -Server <server> -User stockcrawler -Password <kennwort>
```

Das Skript spielt `infra/sql/*.sql` in Reihenfolge ein und ist **idempotent** —
ein zweiter Lauf schadet nicht.

**Nachweis:** Die Ausgabe endet bei `043_invers.sql`, und

```sql
SELECT COUNT(*) FROM sys.tables;     -- deutlich über 40
```

---

### Braucht die Datenbank ein Seeding? Nein.

**Eine leere Datenbank genügt.** Was an Konfiguration nötig ist, legen die
Migrationen selbst an — sie sind nicht nur Schema:

| | |
| --- | --- |
| `012` | die gespeicherten Prozeduren |
| `026` · `027` | Kreuzungsumkehr und die sieben Bot-Auslöser mit ihren Definitionen |
| `028` | die Vorgabegewichte der acht Säulen |
| `029` | die Zeitzonenzuordnung der Börsenplätze |
| `037` | die Verrechnungskonten je Depot und Währung |
| `038` · `039` · `043` | die vier Autopilot-Strategien samt Budget und Takt |

**Nicht** in den Migrationen stehen die eigentlichen Daten: Werte, Kurse,
Nachrichten, Fachliteratur. Die holt die Anwendung selbst — siehe Schritt 9.

Das ist Absicht. Ein Seeding mit Kursdaten wäre ein Datenabzug im Repository:
Er wäre am Tag der Veröffentlichung veraltet, würde das Repository um
Größenordnungen aufblähen und für die Nachrichten- und Literaturabschnitte
fremde Texte weiterverbreiten. Die Anwendung holt sich stattdessen alles aus
den Quellen, und zwar in dem Umfang, den der Betreiber wählt.

---

## 4 · Zugangsdaten eintragen

`src/Ingest.Api/appsettings.json`, Feld `ConnectionStrings:Sql`. Die
API-Schlüssel für TwelveData und CoinGecko sind **optional**: Ohne sie holt die
Anwendung ihre Kurse von Yahoo, was für die Entwicklung genügt.

> **Claude: trage die Verbindungszeichenfolge mit dem in Schritt 2 vergebenen
> Kennwort ein.** Weise den Nutzer darauf hin, dass diese Datei in der
> Versionsverwaltung liegt — sein Kennwort landet also in seinem nächsten
> Commit, wenn er nicht aufpasst.

---

## 5 · Ollama und die Modelle

### Ollama installieren

```powershell
winget install Ollama.Ollama                    # Windows
```
```bash
brew install ollama                              # macOS
curl -fsSL https://ollama.com/install.sh | sh    # Linux
```

Geprüft mit **0.33.2**. Unter Windows und macOS startet der Dienst mit dem
System; unter Linux richtet das Installationsskript einen systemd-Dienst ein
(`systemctl enable --now ollama`).

**Nachweis:** `curl -s http://localhost:11434/api/version`

### `bge-m3` — Laufzeitbedarf, nicht Zubehör

```bash
ollama pull bge-m3                               # 1,2 GB
```

Es wird an drei Stellen gebraucht, und zwei davon laufen von selbst:

| | wo | wann |
| --- | --- | --- |
| Feeds einbetten | Zeitplan | **stündlich** |
| Frage einbetten | jede Wissenssuche | **bei jedem Aufruf** |
| Quellen aufnehmen | Upload, Webquellen | bei Bedarf |

Die zweite Zeile ist die wichtige: An der Suche hängen auch *Day Trading*, das
*Tagesjournal* und das `wissen`-Werkzeug des Agenten. **Ohne `bge-m3` bleiben
diese Seiten inhaltsleer — nicht kaputt**, was die Fehlersuche erschwert.

Auf der CPU ist das billig; Einbetten kostet einen Bruchteil von Texterzeugung.
Eine GPU braucht es dafür nicht.

### `nemotron3:33b` — optional, nur für die Reasoning-Seite

```bash
ollama pull nemotron3:33b                        # 27,6 GB
```

> **Claude: hole dieses Modell nicht ungefragt.** Nenne die Zahlen — 27,6 GB
> Download, ohne GPU rund 32 GB Arbeitsspeicher, auf der CPU ein bis zwei
> Minuten je Antwortrunde bei bis zu sechs Runden je Frage — und lass den
> Nutzer entscheiden. Die Anwendung läuft ohne es vollständig; nur der Reiter
> *Säulen → Reasoning* bleibt still.

**Kein kleineres Modell als Ersatz.** Gemessen an denselben acht Fragen:

| | |
| --- | --- |
| `nemotron3:33b` | langsam (70–250 s), Urteile korrekt wiedergegeben |
| `qwen3:8b` | mit Denken korrekt, ruft aber unzuverlässig Werkzeuge auf |
| `qwen3-vl:4b` | schnell und **gefährlich** — las ein Fehlerverhältnis von 1,0034 als „nahezu perfekt", das Gegenteil der Werkzeugausgabe |
| `granite3.2-vision` | beherrscht keine Werkzeugaufrufe |

Wer Tempo braucht, mietet Rechenleistung, statt das Modell zu verkleinern.

### Für den Dauerbetrieb: das Modell resident halten

Ollama entlädt ein Modell nach fünf Minuten Untätigkeit. Bei 27,6 GB heisst
das, dass die erste Frage nach einer Pause das Laden mitbezahlt.

```bash
curl http://localhost:11434/api/generate   -d '{"model":"nemotron3:33b","keep_alive":-1}'
```

`keep_alive: -1` hält es bis zum Neustart. Das belegt den Arbeitsspeicher
dauerhaft — auf einem Entwicklungsrechner meist unerwünscht, auf einem Server
für die Reasoning-Seite genau richtig.

### Auf einer gemieteten GPU

Die Anwendung kann Ollama über einen **SSH-Tunnel** ansprechen; einzurichten
unter *Säulen → Reasoning → Wo Ollama läuft*. Drei gemessene Stolpersteine:

- Im Abbild `vastai/ollama` horcht ein Caddy auf **21434**, nicht auf 11434.
  Wer nur 11434 sucht, findet einen toten Port.
- Ohne Tunnel verlangt dieser Caddy **Basic**-Anmeldung: ins Feld *Schlüssel*
  gehört `vastai:<OPEN_BUTTON_TOKEN>`. Ein Bearer-Token wird mit 401 abgewiesen
  — und ein 401 liest sich wie „Modell nicht da".
- Nach jedem Fortsetzen einer angehaltenen Instanz vergibt vast.ai **Adresse
  und SSH-Port neu**. Die Übernahme muss erneut laufen.

**Eine gemietete Karte ändert keine einzige Zahl dieser Anwendung** — sie macht
die Antwort schneller, nicht besser. Die Prognosemodelle laufen als ONNX im
eigenen Prozess auf der CPU.

---

## 6 · Qdrant

Der Vektorspeicher für Wissens- und Semantiksäule. Ohne ihn startet die
Anwendung, aber die Suche findet nichts.

```bash
docker run -p 6333:6333 -p 6334:6334            -v qdrant_storage:/qdrant/storage            --restart unless-stopped --name crs-qdrant -d qdrant/qdrant
```

Ohne Docker: Binärdatei von <https://github.com/qdrant/qdrant/releases>
entpacken und starten; unter Windows empfiehlt sich eine geplante Aufgabe beim
Systemstart.

**Nachweis:** `curl -s http://localhost:6333/collections` antwortet mit JSON.

Die beiden Sammlungen `crs_wissen` und `crs_semantik` legt die Anwendung beim
ersten Einbetten selbst an.

---

## 7 · Bauen und starten

```bash
cd CRS.StockCrawler
dotnet build
cd src/Ingest.Api && dotnet run
```

Die Anwendung hört auf <http://localhost:5011>.

**Nachweis:** `curl -s -o /dev/null -w "%{http_code}" http://localhost:5011/api/health`
antwortet mit `200`.

---

## 8 · Der erste Verwalter

Die frische Anwendung hat **keinen Benutzer**. „Wer sich zuerst meldet, wird
Verwalter“ wäre auf einem erreichbaren Server genau das Loch, das die Anmeldung
schliessen soll. Stattdessen schreibt der Start ein **Einrichtungswort ins
Protokoll** — wer es lesen kann, hat ohnehin Zugriff auf den Rechner.

> **Claude: lies das Einrichtungswort aus der Startausgabe** (Zeile
> `EINRICHTUNGSWORT:`) und lege damit den ersten Verwalter an. Das Wort gilt nur
> für diesen Programmlauf.

```bash
curl -X POST "http://localhost:5011/api/auth/einrichten" \
     -H "Content-Type: application/json" \
     -d '{"token":"<Einrichtungswort>","login":"<anmeldename>","kennwort":"<mind. 12 Zeichen>"}'
```

---

## 9 · Daten holen

Ohne Kurse ist die Oberfläche leer. Der erste Lauf dauert je nach Umfang
Minuten bis eine gute Stunde.

```bash
curl -X POST "http://localhost:5011/api/ingest/bootstrap?months=24"
```

Danach übernimmt der Zeitplan: stündlich neue Bars und Prognosen, täglich
zusätzlich Universum, Analyse, Neuzugänge und Autopilot.

### Nachrichten und Fachliteratur — denselben Wissensstand holen

Beide werden **nicht** mit `bootstrap` geholt. Das Repository enthält nur die
Liste; die Texte holt sich Ihre Instanz an der Quelle. Damit bekommt jeder
denselben Stand, ohne dass fremde Werke weitergereicht werden.

**Nachrichten** — *Säulen → Semantik → Startliste eintragen*, dann *Feeds
abholen*: 35 Feeds von Reuters, WSJ, FT, CNBC, MarketWatch über Handelsblatt,
NZZ und Der Standard bis Nikkei, SCMP und Economic Times, dazu Fed, EZB, Bank
of England, BIZ und SEC.

**Fachliteratur** — *Säulen → Knowledge → Freie Quellen eintragen*, dann *Alle
einbetten*: **55 Quellen**, das ist der vollständige Bestand, mit dem dieses
Projekt arbeitet.

| | |
| ---: | --- |
| **41** | arXiv-Arbeiten zu Limit Order Books, statistischer Arbitrage, Marktwirkung, Ausführungsstrategien, Deep Learning im Handel |
| **12** | gemeinfreie Klassiker aus dem Project Gutenberg — Clews, Lawson, Rice, Lefèvre, Francis, Crump, Gibson, Selden, Harper, Butler, Brandenburg |
| **1** | BIZ-Arbeitspapier *Quantifying the High-Frequency Trading Arms Race* |
| **1** | offen zugängliche Dissertation zu Marktmikrostruktur (Uni Luxemburg) |

Die Liste steht in `infra/quellen.json` mit **Rechtslage je Eintrag**. Das
Einbetten dauert: Der grösste Posten (*Fifty Years in Wall Street*) ergibt
allein 2.074 Abschnitte, der Gesamtbestand rund 42.000.

**Was nicht mitgeliefert werden kann.** Zwei Dokumente stecken im Bestand des
Autors, aber nicht im Repository:

- **„Trading Skills"** (Susanne Pichler, Masterarbeit, JKU Linz, Oktober 2024,
  189 Seiten) — fremdes Urheberrecht. Was die Arbeit enthält und warum sie
  aufgenommen wurde, steht in `docs/QUELLE-TRADING-SKILLS.md`; wer sie
  einbetten will, besorgt sie sich selbst über die JKU.
- **„Marktneutrale Strategien im Kryptomarkt"** — eigener Text und deshalb
  **im Repository enthalten**: `docs/wissen/marktneutrale-krypto-strategien.md`.
  Über *Dateien hochladen* einlesen.

Ohne diese beiden Schritte läuft alles andere; nur *Säulen → Wissen*,
*Semantik*, *Day Trading* und das *Tagesjournal* bleiben inhaltsleer.

---

## 10 · Was Sie danach sehen sollten

| Reiter | sollte gehen |
| --- | --- |
| Kurse | Linien, sobald Bars da sind |
| Auswahl | die verfolgten Werte |
| Prognose | nach dem ersten Prognoselauf |
| Säulen → Wissen | nur mit Qdrant **und** `bge-m3` |
| Säulen → Reasoning | nur mit `nemotron3:33b` |
| Neuzugänge | sofort — die Quellen hängen an nichts |

Die Sprache stellen Sie oben rechts um; Deutsch und Englisch liegen bei.

---

## Wenn etwas nicht geht

**Die Oberfläche ist leer, aber `/api/health` antwortet.** Es sind noch keine
Kurse da — Schritt 9.

**Day Trading und Tagesjournal bleiben inhaltsleer.** `bge-m3` fehlt. Das ist
der häufigste Fall und sieht nicht nach einem Fehler aus, weil nichts abstürzt.

**„Ollama antwortet nicht oder bge-m3 fehlt".** Diese Meldung nennt die zwei
naheliegenden Ursachen und verschweigt eine dritte: Ein früher gewählter, jetzt
toter Endpunkt bleibt ausgewählt. Nachsehen unter *Säulen → Reasoning → Wo
Ollama läuft*.

**`apply-sql.ps1` bricht bei einer Datei ab.** Die Skripte bauen aufeinander
auf; die Reihenfolge ist die Dateinummer. Ein einzelnes Skript nachzuziehen
geht mit `sqlcmd -f 65001` — **ohne** `-f 65001` landen die Umlaute als
`RSA Ã¼ber 70` in der Datenbank, und zwar in Spalten, die später in der
Oberfläche stehen.

**Der Aufbau der Wissenssäule dauert.** Ein Fachbuch einzubetten kostet
Minuten. Das ist kein Hänger.

---

## Und wenn Sie mitarbeiten wollen

[CONTRIBUTING.md](CONTRIBUTING.md) — Hausstil und Rechteeinräumung.
[LICENSING.md](LICENSING.md) — AGPL-3.0 und die kommerzielle Ausnahme.
`CRS.StockCrawler/CLAUDE.md` — rund hundert Regeln, jede aus einem Fehlschlag.
**Lesen Sie die zuerst.** Sie ist lang und spart Ihnen die Fallen ein zweites
Mal zu finden.
