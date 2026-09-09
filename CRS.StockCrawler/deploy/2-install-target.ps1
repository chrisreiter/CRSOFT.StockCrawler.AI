<#
  2-install-target.ps1  -  EIN Script, das CRSOFT.StockCrawler auf dem Zielserver
  komplett in Betrieb nimmt: IIS + ASP.NET-Core-Hosting-Bundle, Qdrant als Dienst,
  SQL-Restore, Datenwiederherstellung, App-Deployment, IIS-Site stock.crsoft.at.

  Voraussetzung: als ADMIN ausfuehren; das Bundle (Ausgabe von 1-export-source.ps1)
  liegt neben diesem Script. SQL Server muss auf dem Server installiert sein.

  Beispiel:
    powershell -ExecutionPolicy Bypass -File .\2-install-target.ps1 -SqlServer ".\SQL2022" -SqlUser "sa" -SqlPassword "Cire1234!"
#>
[CmdletBinding()]
param(
  [string]$BundleDir      = $PSScriptRoot,
  [string]$SiteHost       = "stock.crsoft.at",
  [string]$InstallRoot    = "C:\inetpub\stock.crsoft.at",
  [string]$SqlServer      = ".\SQLSERVER",
  [string]$SqlUser        = "stockcrawler",
  [string]$SqlPassword    = "StockCrawler!",
  [string]$Database       = "stockcrawler",
  [string]$QdrantHome     = "C:\qdrant",
  [string]$ReasoningModel = "nemotron3:33b",
  [switch]$AddHostsEntry
)

$ErrorActionPreference = "Stop"

# BundleDir robust ermitteln: $PSScriptRoot ist je nach Aufrufart leer.
if ([string]::IsNullOrWhiteSpace($BundleDir)) {
  if ($PSScriptRoot) { $BundleDir = $PSScriptRoot }
  elseif ($PSCommandPath) { $BundleDir = Split-Path -Parent $PSCommandPath }
  elseif ($MyInvocation.MyCommand.Path) { $BundleDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
  else { $BundleDir = (Get-Location).Path }
}
Write-Host "Bundle-Ordner: $BundleDir" -ForegroundColor DarkGray

function Step($m){ Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Ok($m){ Write-Host "[ ok ] $m" -ForegroundColor Green }
function Warn($m){ Write-Host "[warn] $m" -ForegroundColor Yellow }
function Die($m){ Write-Host "[FEHLER] $m" -ForegroundColor Red; exit 1 }

# Admin?
$id = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $id.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Die "Bitte als Administrator ausfuehren."
}
$sqlcmd = "C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\sqlcmd.exe"
if (-not (Test-Path $sqlcmd)) { $sqlcmd = "sqlcmd" }

# Hilfsfunktion: eine skalare Abfrage ausfuehren, Ergebnis als String
function SqlScalar($query){
  $out = & $sqlcmd -S $SqlServer -U $SqlUser -P $SqlPassword -C -h -1 -W -Q $query
  if ($LASTEXITCODE -ne 0) { throw "sqlcmd-Fehler bei: $query" }
  return ($out | Select-Object -First 1).Trim()
}
function SqlExec($query){
  $global:SqlOut = (& $sqlcmd -S $SqlServer -U $SqlUser -P $SqlPassword -C -b -Q $query 2>&1 | Out-String)
  return $LASTEXITCODE
}

# ================================================== 1) IIS-Features ==
Step "IIS-Features sicherstellen"
$feat = @("IIS-WebServerRole","IIS-WebServer","IIS-ASPNET45","IIS-NetFxExtensibility45",
          "IIS-ManagementConsole","IIS-HttpCompressionStatic")
foreach ($f in $feat) {
  $s = Get-WindowsOptionalFeature -Online -FeatureName $f -ErrorAction SilentlyContinue
  if ($s -and $s.State -ne "Enabled") {
    Enable-WindowsOptionalFeature -Online -FeatureName $f -All -NoRestart | Out-Null
    Ok "aktiviert: $f"
  }
}
Import-Module WebAdministration -ErrorAction SilentlyContinue
Ok "IIS bereit."

# ========================================= 2) ASP.NET Core 8 Hosting ==
Step "ASP.NET Core 8 Hosting Bundle"
$hasAsp = (& dotnet --list-runtimes 2>$null) -match "Microsoft.AspNetCore.App 8\."
if ($hasAsp) { Ok "Hosting Bundle bereits vorhanden." }
else {
  $bundledInst = Join-Path $BundleDir "prereq\dotnet-hosting-win.exe"
  $inst = Join-Path $env:TEMP "dotnet-hosting-8-win.exe"
  try {
    if (Test-Path $bundledInst) {
      Ok "Hosting Bundle aus dem Bundle (prereq) - kein Download noetig."
      $inst = $bundledInst
    } else {
      Warn "keine gebundelte Datei -> lade ASP.NET Core 8 Hosting Bundle ..."
      Invoke-WebRequest -Uri "https://aka.ms/dotnet/8.0/dotnet-hosting-win.exe" -OutFile $inst -UseBasicParsing
    }
    Start-Process $inst -ArgumentList "/quiet","/norestart" -Wait
    cmd /c "net stop was /y" 2>$null | Out-Null
    cmd /c "net start w3svc" 2>$null | Out-Null
    Ok "Hosting Bundle installiert."
  } catch {
    Warn "Hosting Bundle nicht installierbar. Manuell: https://dotnet.microsoft.com/download/dotnet/8.0"
    Die "Datei nach prereq\dotnet-hosting-win.exe legen oder manuell installieren, dann erneut starten."
  }
}

# =============================================== 3) Qdrant ==
Step "Qdrant einrichten"
$qexe = Join-Path $QdrantHome "qdrant.exe"
New-Item -ItemType Directory -Force -Path $QdrantHome | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $QdrantHome "storage") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $QdrantHome "snapshots") | Out-Null
if (-not (Test-Path $qexe)) {
  $bundledEngine = Join-Path $BundleDir "qdrant-engine"
  if (Test-Path (Join-Path $bundledEngine "qdrant.exe")) {
    Copy-Item (Join-Path $bundledEngine "qdrant.exe") $qexe -Force
    $cfgSrc = Join-Path $bundledEngine "config.yaml"
    if (Test-Path $cfgSrc) { Copy-Item $cfgSrc (Join-Path $QdrantHome "config.yaml") -Force }
    Ok "Qdrant-Engine aus dem Bundle uebernommen (versionsgleich zu den Snapshots)."
  } else {
    try {
      Warn "keine gebundelte Engine -> lade Qdrant ..."
      $zip = Join-Path $env:TEMP "qdrant-win.zip"
      Invoke-WebRequest -UseBasicParsing -OutFile $zip -Uri "https://github.com/qdrant/qdrant/releases/latest/download/qdrant-x86_64-pc-windows-msvc.zip"
      Expand-Archive $zip -DestinationPath $QdrantHome -Force
      Ok "Qdrant entpackt."
    } catch {
      Die "Qdrant fehlt und Download scheiterte. qdrant.exe nach $QdrantHome legen, dann erneut starten."
    }
  }
}
$taskName = "Qdrant"
if (-not (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) {
  $act = New-ScheduledTaskAction -Execute $qexe -WorkingDirectory $QdrantHome
  $trg = New-ScheduledTaskTrigger -AtStartup
  $pr  = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
  $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
  Register-ScheduledTask -TaskName $taskName -Action $act -Trigger $trg -Principal $pr -Settings $set | Out-Null
  Ok "Autostart-Aufgabe angelegt."
}
if (-not (Get-Process qdrant -ErrorAction SilentlyContinue)) {
  Start-Process $qexe -WorkingDirectory $QdrantHome -WindowStyle Hidden
}
$qUrl = "http://localhost:6333"
$ready = $false
for ($i=0; $i -lt 30; $i++) {
  try { Invoke-RestMethod "$qUrl/readyz" -TimeoutSec 3 | Out-Null; $ready=$true; break } catch { Start-Sleep 1 }
}
if ($ready) { Ok "Qdrant laeuft ($qUrl)." } else { Warn "Qdrant nicht bereit - Restore kann fehlschlagen." }

# =============================================== 4) SQL herstellen ==
Step "SQL-Datenbank '$Database' herstellen"
$bacpac = Get-ChildItem (Join-Path $BundleDir "sql") -Filter *.bacpac -ErrorAction SilentlyContinue | Select-Object -First 1
$bak    = Join-Path $BundleDir "sql\$Database.bak"

if ($bacpac) {
  # --- BACPAC-Import: versions-portabel (z.B. Quelle SQL 2025 -> Ziel SQL 2022) ---
  Ok ("BACPAC gefunden: {0} - versions-portabler Import" -f $bacpac.Name)
  $sp = "C:\tools\sqlpackage\sqlpackage.exe"
  if (-not (Test-Path $sp)) {
    $bundledSp = Join-Path $BundleDir "sqlpackage\sqlpackage.exe"
    if (Test-Path $bundledSp) { $sp = $bundledSp }
    else {
      try {
        Warn "SqlPackage laden (self-contained) ..."
        New-Item -ItemType Directory -Force -Path "C:\tools\sqlpackage" | Out-Null
        $z = Join-Path $env:TEMP "sqlpackage-win.zip"
        Invoke-WebRequest -Uri "https://aka.ms/sqlpackage-windows" -OutFile $z -UseBasicParsing
        Expand-Archive $z -DestinationPath "C:\tools\sqlpackage" -Force
        $sp = "C:\tools\sqlpackage\sqlpackage.exe"
      } catch { Die "SqlPackage fehlt und Download scheiterte. Zip von https://aka.ms/sqlpackage-windows nach C:\tools\sqlpackage\ entpacken, dann erneut starten." }
    }
  }
  # Import verlangt eine NICHT existierende Ziel-DB -> ggf. vorher droppen
  SqlExec "IF DB_ID('$Database') IS NOT NULL BEGIN ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database]; END" | Out-Null
  & $sp /Action:Import /tsn:"$SqlServer" /tdn:"$Database" /tu:"$SqlUser" /tp:"$SqlPassword" /TargetTrustServerCertificate:True /sf:"$($bacpac.FullName)"
  if ($LASTEXITCODE -ne 0) { Die "BACPAC-Import fehlgeschlagen." }
  Ok "BACPAC importiert."
}
elseif (Test-Path $bak) {
  # --- klassischer .bak-Restore (nur wenn Quell-Version <= Ziel-Version) ---
  $restorePath = SqlScalar "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000));"
  $srvBak = Join-Path $restorePath "$Database.restore.bak"
  Copy-Item $bak $srvBak -Force
  $dataDir = SqlScalar "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000));"
  $logDir  = SqlScalar "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000));"
  $fileList = & $sqlcmd -S $SqlServer -U $SqlUser -P $SqlPassword -C -h -1 -W -s "|" -Q "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$srvBak';"
  $move = @()
  foreach ($line in $fileList) {
    $cols = $line -split '\|'
    if ($cols.Count -ge 3) {
      $logical = $cols[0].Trim()
      $type = $cols[2].Trim()
      if ($type -eq "D") { $move += ("MOVE N'{0}' TO N'{1}'" -f $logical, (Join-Path $dataDir "$Database.mdf")) }
      elseif ($type -eq "L") { $move += ("MOVE N'{0}' TO N'{1}'" -f $logical, (Join-Path $logDir ($Database + "_log.ldf"))) }
    }
  }
  if ($move.Count -eq 0) {
    Write-Host ($fileList | Out-String) -ForegroundColor DarkYellow
    Die "MOVE-Klausel leer - Restore abgebrochen."
  }
  $moveClause = ($move -join ", ")
  $restoreSql = "IF DB_ID('$Database') IS NOT NULL BEGIN ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; END; RESTORE DATABASE [$Database] FROM DISK = N'$srvBak' WITH REPLACE, $moveClause, STATS = 10; ALTER DATABASE [$Database] SET MULTI_USER;"
  if ((SqlExec $restoreSql) -ne 0) {
    Write-Host "----- SQL-Ausgabe -----" -ForegroundColor Red
    Write-Host $SqlOut -ForegroundColor Red
    Die "SQL-Restore fehlgeschlagen (Meldung oben)."
  }
  Remove-Item $srvBak -Force -ErrorAction SilentlyContinue
}
else { Die "Weder .bacpac noch .bak in sql\ gefunden." }

# App-Login 'stockcrawler' sicherstellen (die App verbindet sich laut appsettings als stockcrawler) + db_owner
$loginSql = "IF SUSER_ID(N'stockcrawler') IS NULL CREATE LOGIN [stockcrawler] WITH PASSWORD = N'StockCrawler!', CHECK_POLICY = OFF; USE [$Database]; IF USER_ID(N'stockcrawler') IS NULL CREATE USER [stockcrawler] FOR LOGIN [stockcrawler]; ALTER ROLE db_owner ADD MEMBER [stockcrawler];"
SqlExec $loginSql | Out-Null
Ok "Datenbank bereit."

# =============================================== 5) Qdrant-Restore ==
Step "Qdrant-Collections wiederherstellen"
$snaps = Get-ChildItem (Join-Path $BundleDir "qdrant") -Filter *.snapshot -ErrorAction SilentlyContinue
foreach ($sn in $snaps) {
  $col = $sn.BaseName
  $dest = Join-Path $QdrantHome ("snapshots\" + $sn.Name)
  Copy-Item $sn.FullName $dest -Force
  $loc = "file:///" + ($dest -replace '\\','/')
  try {
    $body = @{ location = $loc } | ConvertTo-Json
    Invoke-RestMethod -Method Put -Uri "$qUrl/collections/$col/snapshots/recover" -TimeoutSec 1200 -ContentType "application/json" -Body $body | Out-Null
    $cnt = (Invoke-RestMethod "$qUrl/collections/$col" -TimeoutSec 30).result.points_count
    Ok "Collection '$col' wiederhergestellt ($cnt Punkte)."
  } catch {
    Warn ("Restore '{0}' fehlgeschlagen: {1}" -f $col, $_.Exception.Message)
  }
}

# =============================================== 6) App-Deployment ==
Step "App nach $InstallRoot deployen"
New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
Copy-Item (Join-Path $BundleDir "app\*") $InstallRoot -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $InstallRoot "infra") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $InstallRoot "logs")  | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $InstallRoot "secrets") | Out-Null
Copy-Item (Join-Path $BundleDir "config\appsettings.Production.json") $InstallRoot -Force
# Connection-String auf die tatsaechliche Ziel-Instanz + App-Login stockcrawler setzen
$appcfg = Join-Path $InstallRoot "appsettings.Production.json"
try {
  $j = Get-Content $appcfg -Raw | ConvertFrom-Json
  $conn = "Server=$SqlServer;Database=$Database;User Id=stockcrawler;Password=StockCrawler!;TrustServerCertificate=true"
  if (-not $j.ConnectionStrings) { $j | Add-Member -NotePropertyName ConnectionStrings -NotePropertyValue ([pscustomobject]@{}) }
  if ($j.ConnectionStrings.PSObject.Properties['Sql']) { $j.ConnectionStrings.Sql = $conn }
  else { $j.ConnectionStrings | Add-Member -NotePropertyName Sql -NotePropertyValue $conn }
  ($j | ConvertTo-Json -Depth 12) | Set-Content $appcfg -Encoding UTF8
  Ok "Connection-String gesetzt: Server=$SqlServer (App-Login stockcrawler)"
} catch { Warn "Connection-String-Patch fehlgeschlagen: $($_.Exception.Message)" }
foreach ($f in @("quellen.json","welt.json","ollama-endpunkte.json")) {
  $src = Join-Path $BundleDir "config\$f"
  if (Test-Path $src) { Copy-Item $src (Join-Path $InstallRoot "infra\$f") -Force }
}
if (Test-Path (Join-Path $BundleDir "ml\models")) {
  New-Item -ItemType Directory -Force -Path (Join-Path $InstallRoot "ml") | Out-Null
  Copy-Item (Join-Path $BundleDir "ml\models") (Join-Path $InstallRoot "ml\models") -Recurse -Force
}
Ok "Dateien kopiert."

Step "Umgebungsvariablen setzen"
[Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT","Production","Machine")
[Environment]::SetEnvironmentVariable("CRS_QDRANT_URL",$qUrl,"Machine")
[Environment]::SetEnvironmentVariable("CRS_REASONING_MODEL",$ReasoningModel,"Machine")
[Environment]::SetEnvironmentVariable("CRS_OLLAMA_ENDPUNKTE",(Join-Path $InstallRoot "infra\ollama-endpunkte.json"),"Machine")
[Environment]::SetEnvironmentVariable("CRS_QUELLEN",(Join-Path $InstallRoot "infra\quellen.json"),"Machine")
[Environment]::SetEnvironmentVariable("CRS_WELT",(Join-Path $InstallRoot "infra\welt.json"),"Machine")
[Environment]::SetEnvironmentVariable("CRS_MODELS_DIR",(Join-Path $InstallRoot "ml\models"),"Machine")
Ok "Env gesetzt."

# =============================================== 7) IIS-Site ==
Step "IIS-Site '$SiteHost' anlegen"
$pool = $SiteHost
if (Get-Website -Name $SiteHost -ErrorAction SilentlyContinue) {
  Warn "Site existiert bereits."
} else {
  if (-not (Test-Path "IIS:\AppPools\$pool")) { New-WebAppPool -Name $pool | Out-Null }
  Set-ItemProperty "IIS:\AppPools\$pool" -Name managedRuntimeVersion -Value ""
  Set-ItemProperty "IIS:\AppPools\$pool" -Name startMode -Value "AlwaysRunning"
  New-Website -Name $SiteHost -PhysicalPath $InstallRoot -ApplicationPool $pool -HostHeader $SiteHost -Port 80 | Out-Null
  Ok "Site + AppPool angelegt."
}

Step "Berechtigungen setzen"
$idn = "IIS AppPool\$pool"
function Grant($path,$rights){
  $acl = Get-Acl $path
  $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($idn,$rights,"ContainerInherit,ObjectInherit","None","Allow")
  $acl.SetAccessRule($rule)
  Set-Acl $path $acl
}
Grant $InstallRoot "ReadAndExecute"
Grant (Join-Path $InstallRoot "infra")   "Modify"
Grant (Join-Path $InstallRoot "logs")    "Modify"
Grant (Join-Path $InstallRoot "secrets") "ReadAndExecute"
Ok "ACLs gesetzt."

if ($AddHostsEntry) {
  $hosts = "$env:windir\System32\drivers\etc\hosts"
  if (-not (Select-String -Path $hosts -Pattern "\s$SiteHost$" -Quiet)) {
    Add-Content $hosts "`n127.0.0.1`t$SiteHost"
    Ok "hosts-Eintrag hinzugefuegt."
  }
}

# =============================================== 8) Start + Check ==
Step "Starten"
Start-WebAppPool -Name $pool -ErrorAction SilentlyContinue
Start-Website -Name $SiteHost -ErrorAction SilentlyContinue
Start-Sleep 3
try {
  $r = Invoke-WebRequest -Uri "http://$SiteHost/" -UseBasicParsing -TimeoutSec 20
  Ok "Site antwortet: HTTP $($r.StatusCode)"
} catch {
  Warn "Erststart noch nicht erreichbar. Logs: $InstallRoot\logs"
}

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host " CRSOFT.StockCrawler installiert." -ForegroundColor Green
Write-Host "  URL       : http://$SiteHost/" -ForegroundColor Green
Write-Host "  App       : $InstallRoot" -ForegroundColor Green
Write-Host "  SQL       : $Database @ $SqlServer" -ForegroundColor Green
Write-Host "  Qdrant    : $qUrl (Home $QdrantHome)" -ForegroundColor Green
Write-Host "  Reasoning : $ReasoningModel" -ForegroundColor Green
Write-Host "------------------------------------------------------------" -ForegroundColor Yellow
Write-Host " Manuell noch: SSH-Key nach $InstallRoot\secrets\id_ed25519," -ForegroundColor Yellow
Write-Host " DNS auf diesen Server, HTTPS-Zertifikat binden." -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Green
