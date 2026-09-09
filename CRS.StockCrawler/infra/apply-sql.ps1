<#  Spielt alle infra/sql/*.sql in Reihenfolge ein. GO-Batches werden
    aufgetrennt, da SqlClient sie nicht selbst versteht.                  #>
param(
  [string]$Server = ".\SQLSERVER",
  [string]$User   = "stockcrawler",
  [string]$Password = "StockCrawler!"
)
$ErrorActionPreference = "Stop"
$files = Get-ChildItem -Path (Join-Path $PSScriptRoot "sql") -Filter "*.sql" | Sort-Object Name
foreach ($f in $files) {
  Write-Host "==> $($f.Name)"
  $sql = Get-Content $f.FullName -Raw
  # Startet die Datei mit CREATE DATABASE, muss sie gegen master laufen.
  $db = if ($sql -match 'CREATE DATABASE') { "master" } else { "stockcrawler" }
  $cs = "Server=$Server;Database=$db;User Id=$User;Password=$Password;TrustServerCertificate=true;Connect Timeout=15"
  $conn = New-Object System.Data.SqlClient.SqlConnection($cs)
  $conn.Open()
  $batches = [System.Text.RegularExpressions.Regex]::Split($sql, '(?im)^\s*GO\s*$')
  $i = 0
  foreach ($b in $batches) {
    if ([string]::IsNullOrWhiteSpace($b)) { continue }
    $i++
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $b
    $cmd.CommandTimeout = 120
    try { [void]$cmd.ExecuteNonQuery() }
    catch { Write-Host "    Batch $i FEHLER: $($_.Exception.Message.Split([char]10)[0])" -ForegroundColor Red; throw }
  }
  $conn.Close()
  Write-Host "    $i Batches ok"
}
Write-Host "Fertig." -ForegroundColor Green
