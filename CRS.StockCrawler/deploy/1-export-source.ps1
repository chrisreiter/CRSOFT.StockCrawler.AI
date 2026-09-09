<#
  1-export-source.ps1  —  Erzeugt das Deploy-Bundle fuer CRSOFT.StockCrawler.
  Laeuft auf DIESER (Quell-)Maschine. Sammelt: SQL-Backup, Qdrant-Snapshots,
  veroeffentlichte App, Modelle/Config. Ergebnis: deploy\bundle\  (auf den
  Zielserver kopieren und dort 2-install-target.ps1 ausfuehren).

  Beispiel:
    powershell -ExecutionPolicy Bypass -File .\1-export-source.ps1
#>
[CmdletBinding()]
param(
  [string]$SqlServer   = ".\SQLSERVER",
  [string]$SqlUser     = "stockcrawler",
  [string]$SqlPassword = "StockCrawler!",
  [string]$Database    = "stockcrawler",
  [string]$QdrantUrl   = "http://localhost:6333",
  [string[]]$Collections = @("crs_wissen","crs_semantik"),
  [string]$OutDir      = "",

  # Raeumt das Zielverzeichnis auch dann aus, wenn dort Fremdes liegt.
  [switch]$Ueberschreiben
)

$ErrorActionPreference = "Stop"
$repo   = Split-Path -Parent $PSScriptRoot          # deploy\ -> Repo-Wurzel
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot "bundle" }
$sqlcmd = "C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\sqlcmd.exe"

function Info($m){ Write-Host "[export] $m" -ForegroundColor Cyan }
function Ok($m){ Write-Host "[  ok  ] $m" -ForegroundColor Green }
function Warn($m){ Write-Host "[ warn ] $m" -ForegroundColor Yellow }

Info "Repo:   $repo"
Info "Ziel:   $OutDir"

# frisches Bundle
#
#   Das Skript RAEUMT sein Zielverzeichnis AUS. Das ist richtig -- ein Bundle
#   aus alten und neuen Teilen waere schlimmer als keines. Es hat aber einmal
#   Dateien mitgenommen, die jemand von Hand dazugelegt hatte: LIESMICH.md,
#   doku\ und skripte\ waren nach dem naechsten Lauf weg, und weder Repo noch
#   Papierkorb hatten sie.
#
#   Deshalb wird vorher gezaehlt, was das Skript NICHT wieder anlegt, und der
#   Lauf bricht ab, statt es stillschweigend zu loeschen. Wer es trotzdem will,
#   sagt -Ueberschreiben.
$bekannt = @("sql","qdrant","app","config","ml","qdrant-engine",
             "2-install-target.ps1","README.md","LIESMICH.md","manifest.txt")

if (Test-Path $OutDir) {
  $fremd = Get-ChildItem $OutDir -Force | Where-Object { $bekannt -notcontains $_.Name }

  if ($fremd -and -not $Ueberschreiben) {
    Warn "Im Zielverzeichnis liegt, was dieses Skript nicht wieder anlegt:"
    $fremd | ForEach-Object { Warn ("   " + $_.Name) }
    throw ("Abgebrochen, damit nichts verlorengeht. Erst sichern -- oder mit " +
           "-Ueberschreiben erneut starten.")
  }

  Remove-Item $OutDir -Recurse -Force
}

$dirs = @("sql","qdrant","app","config","ml")
$dirs | ForEach-Object { New-Item -ItemType Directory -Force -Path (Join-Path $OutDir $_) | Out-Null }

# ---------------------------------------------------------------- 1) SQL --
Info "SQL-Backup von '$Database' ..."
$defPath = (& $sqlcmd -S $SqlServer -U $SqlUser -P $SqlPassword -C -h -1 -W -Q `
  "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000));").Trim()
if (-not $defPath) { throw "Konnte Default-Backup-Pfad nicht ermitteln." }
$srvBak = Join-Path $defPath "$Database.deploy.bak"

$backupSql = "BACKUP DATABASE [$Database] TO DISK = N'$srvBak' WITH INIT, FORMAT, COMPRESSION, STATS = 10;"
& $sqlcmd -S $SqlServer -U $SqlUser -P $SqlPassword -C -b -Q $backupSql
if ($LASTEXITCODE -ne 0) {
  Warn "COMPRESSION nicht unterstuetzt (evtl. Express) -> erneut ohne Kompression."
  $backupSql = "BACKUP DATABASE [$Database] TO DISK = N'$srvBak' WITH INIT, FORMAT, STATS = 10;"
  & $sqlcmd -S $SqlServer -U $SqlUser -P $SqlPassword -C -b -Q $backupSql
  if ($LASTEXITCODE -ne 0) { throw "SQL-Backup fehlgeschlagen." }
}
Move-Item $srvBak (Join-Path $OutDir "sql\$Database.bak") -Force
Ok ("SQL-Backup: {0:N1} MB" -f ((Get-Item (Join-Path $OutDir "sql\$Database.bak")).Length/1MB))

# ------------------------------------------------------------ 2) Qdrant --
foreach ($c in $Collections) {
  Info "Qdrant-Snapshot '$c' ..."
  $snap = Invoke-RestMethod -Method Post -Uri "$QdrantUrl/collections/$c/snapshots" -TimeoutSec 600
  $name = $snap.result.name
  if (-not $name) { throw "Snapshot fuer '$c' fehlgeschlagen." }
  $target = Join-Path $OutDir "qdrant\$c.snapshot"
  Invoke-WebRequest -Uri "$QdrantUrl/collections/$c/snapshots/$name" -OutFile $target -TimeoutSec 1200
  # Serverseitigen Snapshot wieder loeschen (Platz sparen)
  try { Invoke-RestMethod -Method Delete -Uri "$QdrantUrl/collections/$c/snapshots/$name" -TimeoutSec 60 | Out-Null } catch {}
  Ok ("Snapshot {0}: {1:N1} MB" -f $c, ((Get-Item $target).Length/1MB))
}

# --------------------------------------------------------------- 3) App --
Info "dotnet publish Ingest.Api (Release) ..."
$proj = Join-Path $repo "src\Ingest.Api\Ingest.Api.csproj"
dotnet publish $proj -c Release -o (Join-Path $OutDir "app") --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen." }
Ok "App veroeffentlicht."

# ---------------------------------------------------- 4) Config + Modelle --
Info "Config + Modelle kopieren ..."
# Voreingestellter GPU-Endpunkt (vast als Default) + Produktions-Settings
Copy-Item (Join-Path $PSScriptRoot "config\ollama-endpunkte.default.json") (Join-Path $OutDir "config\ollama-endpunkte.json") -Force
Copy-Item (Join-Path $PSScriptRoot "config\appsettings.Production.json")    (Join-Path $OutDir "config\appsettings.Production.json") -Force
# Quellenlisten der App
foreach ($f in "quellen.json","welt.json") {
  $src = Join-Path $repo "infra\$f"
  if (Test-Path $src) { Copy-Item $src (Join-Path $OutDir "config\$f") -Force }
}
# ONNX-Modelle (klein)
$mlSrc = Join-Path $repo "ml\models"
if (Test-Path $mlSrc) { Copy-Item $mlSrc (Join-Path $OutDir "ml\models") -Recurse -Force }

# Qdrant-Engine mitliefern (versionsgleich zu den Snapshots, kein Download am Ziel noetig)
$qexeSrc = $null
$qproc = Get-Process qdrant -ErrorAction SilentlyContinue | Select-Object -First 1
if ($qproc) { $qexeSrc = $qproc.Path }
elseif (Test-Path "D:\qdrant\qdrant.exe") { $qexeSrc = "D:\qdrant\qdrant.exe" }
if ($qexeSrc -and (Test-Path $qexeSrc)) {
  New-Item -ItemType Directory -Force -Path (Join-Path $OutDir "qdrant-engine") | Out-Null
  Copy-Item $qexeSrc (Join-Path $OutDir "qdrant-engine\qdrant.exe") -Force
  $qcfg = Join-Path (Split-Path $qexeSrc) "config.yaml"
  if (Test-Path $qcfg) { Copy-Item $qcfg (Join-Path $OutDir "qdrant-engine\config.yaml") -Force }
  Ok ("Qdrant-Engine gebundelt: {0:N0} MB" -f ((Get-Item (Join-Path $OutDir 'qdrant-engine\qdrant.exe')).Length/1MB))
} else {
  Warn "qdrant.exe nicht gefunden -> Engine nicht gebundelt (Ziel braucht dann Internet)."
}

# Migrationen mitgeben.
#
#   Das Voll-Backup enthaelt das Schema bereits -- fuer eine Neuinstallation
#   braucht es die Einzelskripte also nicht. Wer aber eine BESTEHENDE
#   Installation nachzieht, braucht genau sie, und ohne sie muesste er die
#   Datenbank ueberschreiben. Ausserdem ist es die einzige Stelle, an der
#   sichtbar wird, auf welchem Stand das Schema im Bundle ist.
$sqlSrc = Join-Path $repo "infra\sql"
if (Test-Path $sqlSrc) {
  Copy-Item (Join-Path $sqlSrc "*.sql") (Join-Path $OutDir "sql") -Force
  $n = (Get-ChildItem (Join-Path $OutDir "sql") -Filter *.sql).Count
  Ok "Migrationen mitgegeben: $n"
}

# Install-Script und Dokumentation ins Bundle
Copy-Item (Join-Path $PSScriptRoot "2-install-target.ps1") (Join-Path $OutDir "2-install-target.ps1") -Force
foreach ($doc in "README.md","LIESMICH.md") {
  $src = Join-Path $PSScriptRoot $doc
  if (Test-Path $src) { Copy-Item $src (Join-Path $OutDir $doc) -Force }
}

# ------------------------------------------------------------- Manifest --
$manifest = @()
$manifest += "CRSOFT.StockCrawler Deploy-Bundle"
$manifest += "erstellt: (Zeitpunkt siehe Dateidatum)"
$manifest += "Quelle-DB: $Database @ $SqlServer"
$manifest += "Qdrant-Collections: " + ($Collections -join ", ")
$manifest += ""
Get-ChildItem $OutDir -Recurse -File | ForEach-Object {
  $manifest += ("{0,10:N1} MB  {1}" -f ($_.Length/1MB), $_.FullName.Substring($OutDir.Length+1))
}
$total = (Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum
$manifest += ""
$manifest += ("GESAMT: {0:N1} MB" -f ($total/1MB))
$manifest | Set-Content (Join-Path $OutDir "manifest.txt") -Encoding UTF8

Ok ("Bundle fertig: {0}  ({1:N1} MB)" -f $OutDir, ($total/1MB))
Info "Naechster Schritt: Ordner auf den Zielserver kopieren, dort als Admin:"
Info "  powershell -ExecutionPolicy Bypass -File .\2-install-target.ps1"
