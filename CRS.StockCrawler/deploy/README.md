# CRSOFT.StockCrawler — Deployment auf einen neuen Server (IIS)

Bringt die App **mit dem kompletten aktuellen Datenbestand** (SQL + Qdrant) auf einen Windows-Server
und lässt sie unter **IIS** als `stock.crsoft.at` laufen. Der GPU-Endpunkt (vast.ai) ist als
konfigurierbarer **Default** vorbelegt.

## Ablauf in 3 Schritten

### 1. Bundle erzeugen (auf DIESER Quell-Maschine)
```
cd deploy
powershell -ExecutionPolicy Bypass -File .\1-export-source.ps1
```
Erzeugt `deploy\bundle\` mit:
- `sql\stockcrawler.bak` — Voll-Backup der SQL-DB (~8 GB, komprimiert wenn möglich)
- `qdrant\crs_wissen.snapshot`, `qdrant\crs_semantik.snapshot` — Vektor-Collections
- `app\` — veröffentlichte App (`dotnet publish -c Release`)
- `config\` — `appsettings.Production.json`, `ollama-endpunkte.json` (vast als Default), `quellen.json`, `welt.json`
- `ml\models\` — ONNX-Modelle
- `2-install-target.ps1`, `manifest.txt`

### 2. Bundle auf den Zielserver kopieren
Den ganzen Ordner `deploy\bundle\` auf den Server bringen (Kopie, USB, Netzwerk).
**Voraussetzung am Ziel:** Windows Server mit IIS-Rolle und **SQL Server** installiert.

### 3. EIN Script am Zielserver (als Administrator)
```
powershell -ExecutionPolicy Bypass -File .\2-install-target.ps1
```
Das Script macht alles:
1. IIS-Features aktivieren
2. **ASP.NET Core 8 Hosting Bundle** installieren (falls fehlt)
3. **Qdrant** einrichten (Download, Autostart-Aufgabe, Start)
4. **SQL-Restore** der DB `stockcrawler` + Login `stockcrawler`
5. **Qdrant-Restore** der beiden Collections aus den Snapshots
6. App nach `C:\inetpub\stock.crsoft.at` deployen + Config/Env
7. **IIS-Site `stock.crsoft.at`** (App-Pool „No Managed Code") + Rechte
8. Starten + Health-Check

Parameter (optional):
```
-SiteHost stock.crsoft.at  -InstallRoot C:\inetpub\stock.crsoft.at
-SqlServer .\SQLSERVER  -SqlUser stockcrawler  -SqlPassword StockCrawler!
-QdrantHome C:\qdrant  -ReasoningModel nemotron3:33b
-AddHostsEntry     # 127.0.0.1 stock.crsoft.at fuer lokalen Test
```

## Nach der Installation — manuell
- **SSH-Key** für den vast-Tunnel nach `…\stock.crsoft.at\secrets\id_ed25519` legen (Secret, nicht im Bundle).
- **Neue vast-Instanz**: in der App unter *Reasoning → gemietete GPU* SSH-Host/Port aktualisieren
  (Default zeigt auf die aktuelle Instanz `180.189.55.43:16054`).
- **API-Keys** (TwelveData/CoinGecko) in `appsettings.Production.json`, falls laufende Aktualisierung gewünscht.
- **HTTPS**: Zertifikat für `stock.crsoft.at` binden (z. B. win-acme / Let's Encrypt).
- **DNS**: `stock.crsoft.at` auf den Server zeigen lassen.

## Reasoning / GPU
Siehe `docs/reasoning-nemotron.md`. Kurz: am besten eine **Ollama-Instanz mit `nemotron3:33b` auf der
gemieteten GPU** verwenden und das Modell resident halten (`keep_alive: -1`). Die App spricht den
Endpunkt über einen SSH-Tunnel an; die Werkzeug-Abfragen (SQL/Qdrant) laufen lokal auf dem Server.
