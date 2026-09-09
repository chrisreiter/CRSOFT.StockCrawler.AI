# CRSOFT.StockCrawler — das Auslieferungsbündel

Was in `D:\setup\stockcrawler` liegt, wie es entsteht und was am Zielserver
davon zu tun ist.

> **Diese Datei ist neu geschrieben.** Die vorherige Fassung ging beim
> Neubau am 31.08.2026 verloren: Das Export-Skript räumt sein Zielverzeichnis
> aus, und sie war von Hand dazugelegt worden — es kannte sie nicht. Das ist
> jetzt behoben (siehe *Der Schutz vor dem Ausräumen*), und diese Datei liegt
> unter `deploy/`, wird also mit ausgeliefert und mitversioniert.

---

## Was drin ist

| | | |
| --- | ---: | --- |
| `sql\stockcrawler.bak` | ~1,3 GB | Voll-Backup, komprimiert. 599.922 Seiten |
| `sql\*.sql` | 35 Dateien | die Migrationen 010–043, für das Nachziehen einer bestehenden Installation |
| `qdrant\crs_wissen.snapshot` | ~689 MB | Fachliteratur, eingebettet |
| `qdrant\crs_semantik.snapshot` | ~522 MB | Nachrichten, eingebettet |
| `app\` | | `dotnet publish -c Release`, einschliesslich `app\loc\` mit den Sprachdateien |
| `config\` | | `appsettings.Production.json`, ein **voreingestellter** Ollama-Endpunkt, `quellen.json`, `welt.json` |
| `ml\models\` | | die ONNX-Modelle |
| `qdrant-engine\` | ~80 MB | `qdrant.exe` in der Fassung, die zu den Snapshots passt |
| `2-install-target.ps1` | | das Installationsskript für den Zielserver |

Gesamt rund **2,8 GB**.

---

## Bündel erzeugen

Auf der Quellmaschine, aus `CRS.StockCrawler\deploy`:

```
powershell -ExecutionPolicy Bypass -File .\1-export-source.ps1 -OutDir D:\setup\stockcrawler
```

Voraussetzungen: SQL Server erreichbar, **Qdrant läuft** (die Snapshots
entstehen über dessen HTTP-Schnittstelle), `dotnet` im Pfad. Dauer im
gemessenen Lauf: Backup 7 s, Snapshots je 2–4 Minuten, Publish unter 1 Minute.

### Der Schutz vor dem Ausräumen

Das Skript **löscht sein Zielverzeichnis**, bevor es neu schreibt. Ein Bündel
aus alten und neuen Teilen wäre schlimmer als keines — eine `Ingest.Api.dll`
von heute neben einer Sprachdatei von vorgestern ergibt Fehler, die niemand
zuordnen kann.

Es zählt jetzt aber vorher, was dort liegt und **von ihm nicht wieder angelegt
wird**, und bricht ab, statt es stillschweigend mitzunehmen:

```
[ warn ] Im Zielverzeichnis liegt, was dieses Skript nicht wieder anlegt:
[ warn ]    doku
[ warn ]    skripte
Abgebrochen, damit nichts verlorengeht.
```

Wer es trotzdem will: `-Ueberschreiben`.

---

## Installation am Zielserver

Als Administrator, im kopierten Ordner:

```
powershell -ExecutionPolicy Bypass -File .\2-install-target.ps1
```

Das Skript aktiviert die IIS-Rolle, installiert bei Bedarf das **ASP.NET Core 8
Hosting Bundle**, richtet Qdrant samt Autostart ein, spielt Datenbank und
Vektorsammlungen zurück, legt die Anwendung nach `C:\inetpub\stock.crsoft.at`,
erzeugt die IIS-Site mit App-Pool „No Managed Code“ und prüft am Ende
`/api/health`.

Parameter (alle optional):

```
-SiteHost stock.crsoft.at   -InstallRoot C:\inetpub\stock.crsoft.at
-SqlServer .\SQLSERVER      -SqlUser stockcrawler   -SqlPassword StockCrawler!
-QdrantHome C:\qdrant       -ReasoningModel nemotron3:33b
-AddHostsEntry              # 127.0.0.1 stock.crsoft.at für den lokalen Test
```

---

## Nach der Installation — von Hand

**Der erste Verwalter.** Die frische Anwendung hat keinen Benutzer. „Wer sich
zuerst meldet, wird Verwalter“ wäre auf einem öffentlichen Server genau das
Loch, das die Anmeldung schliessen soll. Stattdessen schreibt der Start ein
**Einrichtungswort ins Protokoll** — wer es lesen kann, hat ohnehin Zugriff auf
den Server. Damit über `POST /api/auth/einrichten` den ersten Verwalter
anlegen. Das Wort gilt nur für diesen Programmlauf.

**Zwei Rollen.** Verwalter dürfen alles; Nutzer sehen denselben Inhalt, aber
nur lesend. Durchgesetzt wird das an der **HTTP-Methode**, nicht an einer Liste
von Seiten: GET, HEAD und OPTIONS sind erlaubt, alles andere nicht — die Regel
gilt damit auch für Endpunkte, die es heute noch nicht gibt.

**Der Ollama-Endpunkt.** `config\ollama-endpunkte.json` ist ein *Vorschlag* und
zeigt auf die zuletzt gemietete Instanz. Nach jedem Fortsetzen vergibt vast.ai
**Adresse und SSH-Port neu**; die Übernahme muss dann erneut laufen, sonst
zeigt der Endpunkt ins Leere. Den privaten SSH-Schlüssel nach
`…\stock.crsoft.at\secrets\id_ed25519` legen — er gehört nicht ins Bündel.

**Was zur Laufzeit wirklich gebraucht wird**, ist nur `bge-m3` (1,2 GB): für
das stündliche Einbetten der Feeds und für jede Wissenssuche. Ohne es bleiben
Day Trading und Tagesjournal **inhaltsleer** — nicht kaputt, was die Diagnose
erschwert. `nemotron3:33b` (27,6 GB) ist optional und nur für die
Reasoning-Seite; die Prognosemodelle laufen als ONNX im Prozess auf der CPU.

**HTTPS und DNS.** Zertifikat für den Hostnamen binden, DNS auf den Server
zeigen lassen. Ohne HSTS genügt ein Aufruf über `http`, um das Sitzungscookie
im Klartext zu verlieren.

---

## Sprachen

Die Oberfläche ist umschaltbar; der Umschalter steht oben rechts. Die
Sprachdateien liegen im Bündel unter `app\loc\`:

```
loc.res.de.xml   2.272 Einträge   Deutsch (der Katalog)
loc.res.en.xml   2.272 Einträge   Englisch
loc.res.it.xml       14 Einträge   Italienisch, absichtlich unvollständige Probe
```

**Eine weitere Sprache ist eine Datei und sonst nichts:**
`loc.res.<kürzel>.xml` dazulegen, Anwendung neu starten. Es gibt keine Liste
im Quelltext, in die man sie zusätzlich eintragen müsste.

Gesucht wird das Verzeichnis über `Ablage.Sprachen`: erst `CRS_SPRACHEN`, dann
der Entwicklungspfad, dann `loc` neben der Anwendung. Am Zielserver greift die
letzte Stufe. Einzelheiten in `docs/SPRACHEN.md`.

---

## Das Datenbankkennwort

`stockcrawler` / `StockCrawler!` steht im Klartext in `appsettings.json`, im
Installationsskript und in dieser Datei. Das ist eine **Entwicklungs­vorgabe für
eine lokale Instanz**, kein Betriebsgeheimnis — das Repository ist öffentlich,
also ist dieses Kennwort öffentlich.

**Auf einem erreichbaren Server gehört es geändert.** `2-install-target.ps1`
nimmt `-SqlUser` und `-SqlPassword` als Parameter; wer sie nicht angibt, bekommt
die veröffentlichte Vorgabe. Das ist für einen Entwicklungsrechner bequem und
für einen Produktivserver falsch.

Von Hand aus dem Paket zu entfernen trägt nicht — die Quelle ist
`src/Ingest.Api/appsettings.json`, und jedes `dotnet publish` kopiert sie
zurück. Wer es dauerhaft anders will, verlegt die Verbindungszeichenfolge in
User Secrets oder eine Umgebungsvariable; das steht als offener Punkt in
CLAUDE.md.

Der vast.ai-Schlüssel liegt **nicht** hier, sondern in der Umgebungsvariablen
`CRS_VASTAI_KEY`.

---

## Was das Bündel nicht kann

Es enthält den **vollständigen Datenbestand** — Kurse, Prognosen, Vektoren.
Wer es weitergibt, gibt alles weiter. Für eine leere Installation wäre statt
des Backups das Schema aus `sql\*.sql` einzuspielen und mit
`POST /api/ingest/bootstrap?months=24` zu befüllen.
