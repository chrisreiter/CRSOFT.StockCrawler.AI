<#  Spielt alle infra/pgsql/*.sql in Reihenfolge in eine PostgreSQL-Datenbank
    ein. Gegenstueck zu apply-sql.ps1 fuer SQL Server; idempotent.

    Voraussetzung: Rolle und Datenbank existieren (als Superuser einmalig):
      CREATE ROLE stockcrawler LOGIN PASSWORD 'StockCrawler!';
      CREATE DATABASE stockcrawler OWNER stockcrawler;

    Braucht psql. Der Windows-Installer legt es unter
    C:\Program Files\PostgreSQL\<Version>\bin ab, aber nicht in den PATH.    #>
param(
  [string]$Host     = "localhost",
  [int]   $Port     = 5432,
  [string]$Database = "stockcrawler",
  [string]$User     = "stockcrawler",
  [string]$Password = "StockCrawler!"
)
$ErrorActionPreference = "Stop"

$psql = Get-Command psql -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $psql) {
  $psql = Get-ChildItem "C:\Program Files\PostgreSQL\*\bin\psql.exe" -ErrorAction SilentlyContinue |
          Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $psql) { throw "psql nicht gefunden. PostgreSQL installieren oder psql in den PATH nehmen." }

$env:PGPASSWORD       = $Password
$env:PGCLIENTENCODING = "UTF8"

$files = Get-ChildItem -Path (Join-Path $PSScriptRoot "pgsql") -Filter "*.sql" | Sort-Object Name
foreach ($f in $files) {
  Write-Host "==> $($f.Name)"
  # ON_ERROR_STOP: ein Fehler bricht ab, statt dass die Haelfte still durchlaeuft.
  & $psql -h $Host -p $Port -U $User -d $Database -v ON_ERROR_STOP=1 -q -f $f.FullName
  if ($LASTEXITCODE -ne 0) { throw "$($f.Name) fehlgeschlagen (psql exit $LASTEXITCODE)" }
}

# Suchpfad als Vorgabe der Datenbank, damit auch unqualifizierte Namen (und
# psql-Sitzungen) im Schema dbo landen. Die Anwendung setzt ihn zusaetzlich
# selbst auf der Verbindung.
& $psql -h $Host -p $Port -U $User -d $Database -v ON_ERROR_STOP=1 -q `
  -c "ALTER DATABASE $Database SET search_path = dbo, public;"

$n = & $psql -h $Host -p $Port -U $User -d $Database -t -A `
  -c "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'dbo';"
Write-Host "Fertig. $($n.Trim()) Tabellen im Schema dbo." -ForegroundColor Green
