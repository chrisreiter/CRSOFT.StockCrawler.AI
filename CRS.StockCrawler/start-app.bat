@echo off
setlocal EnableExtensions
chcp 65001 >nul
title CRSOFT.StockCrawler starten

rem ============================================================================
rem  CRSOFT.StockCrawler -- alles starten, was die Anwendung braucht.
rem
rem  Reihenfolge und warum:
rem    1. Datenbank pruefen (SQL Server ODER PostgreSQL, je nach appsettings)
rem    2. Qdrant     -- Vektorspeicher fuer Wissen und Semantik. Laeuft NICHT
rem                     als Dienst; ohne diesen Schritt bleiben Wissenssuche,
rem                     Tagesjournal und Day Trading leer, ohne Fehlermeldung.
rem    3. Ollama     -- bge-m3 (Laufzeitbedarf) und nemotron3 (Reasoning).
rem    4. Die App    -- Bau, dann Start in eigenem Fenster; danach der Browser.
rem
rem  Jeder Schritt prueft zuerst, ob das Ziel schon laeuft, und startet nur,
rem  was fehlt. Zweimal ausfuehren schadet nicht.
rem
rem  Pfade sind Vorgaben fuer diesen Rechner und lassen sich per Umgebungs-
rem  variable uebersteuern: CRS_QDRANT_DIR, CRS_QDRANT_EXE, CRS_OLLAMA_APP.
rem  Die Kopie auf dem Desktop ruft nur diese Datei auf -- geaendert wird HIER.
rem
rem  find mit vollem Pfad: In einer Git-Bash-Umgebung stehen die
rem  Unix-Werkzeuge gleichen Namens vorn im PATH und verstehen die Aufrufe
rem  nicht; timeout verweigert umgeleitete Eingabe, daher ping als Pause.
rem  Keine runden Klammern in echo-Zeilen INNERHALB von Bloecken --
rem  cmd liest sie als Blockende.
rem ============================================================================

set "REPO=%~dp0"
if "%REPO:~-1%"=="\" set "REPO=%REPO:~0,-1%"
set "API=%REPO%\src\Ingest.Api"
set "FIND=%SystemRoot%\System32\find.exe"

if not defined CRS_QDRANT_DIR set "CRS_QDRANT_DIR=D:\qdrant"
if not defined CRS_QDRANT_EXE set "CRS_QDRANT_EXE=%CRS_QDRANT_DIR%\qdrant.exe"
if not defined CRS_OLLAMA_APP set "CRS_OLLAMA_APP=%LOCALAPPDATA%\Programs\Ollama\ollama app.exe"

echo.
echo  CRSOFT.StockCrawler  --  %REPO%
echo  ---------------------------------------------------------------------

rem ---------------------------------------------------------------- 1. Datenbank
echo.
echo  [1/4] Datenbank
sc query "MSSQL$SQLSERVER" 2>nul | "%FIND%" "RUNNING" >nul && echo        SQL Server .\SQLSERVER: laeuft
sc query "MSSQL$SQLSERVER" 2>nul | "%FIND%" "STOPPED" >nul && echo        SQL Server .\SQLSERVER: GESTOPPT -- Dienst MSSQL$SQLSERVER starten, falls er gebraucht wird
set "PGSVC="
sc query state= all > "%TEMP%\crs-dienste.txt" 2>nul
for /f "tokens=2" %%s in ('findstr /i /c:"SERVICE_NAME: postgresql" "%TEMP%\crs-dienste.txt"') do set "PGSVC=%%s"
if defined PGSVC (
  sc query "%PGSVC%" | "%FIND%" "RUNNING" >nul && echo        PostgreSQL-Dienst %PGSVC%: laeuft
  sc query "%PGSVC%" | "%FIND%" "STOPPED" >nul && echo        PostgreSQL-Dienst %PGSVC%: GESTOPPT -- Dienst starten, falls er gebraucht wird
) else (
  echo        PostgreSQL: kein Dienst gefunden
)
findstr /c:"\"Postgres\":" "%API%\appsettings.json" | findstr /v /c:"//" >nul && echo        appsettings.json: Postgres aktiv
findstr /c:"\"Sql\":" "%API%\appsettings.json" | findstr /v /c:"//" >nul && echo        appsettings.json: SQL Server aktiv

rem ------------------------------------------------------------------- 2. Qdrant
echo.
echo  [2/4] Qdrant, Port 6333
curl -s -m 3 -o nul http://localhost:6333/collections && (
  echo        laeuft bereits
) || (
  if exist "%CRS_QDRANT_EXE%" (
    echo        starte %CRS_QDRANT_EXE%
    powershell -NoProfile -Command "Start-Process -FilePath '%CRS_QDRANT_EXE%' -ArgumentList '--config-path','%CRS_QDRANT_DIR%\config.yaml' -WorkingDirectory '%CRS_QDRANT_DIR%' -WindowStyle Hidden"
    call :warte http://localhost:6333/collections 30 "Qdrant"
  ) else (
    echo        NICHT GEFUNDEN: %CRS_QDRANT_EXE% -- Wissen und Semantik bleiben leer
  )
)

rem ------------------------------------------------------------------- 3. Ollama
echo.
echo  [3/4] Ollama, Port 11434
curl -s -m 3 -o nul http://localhost:11434/api/version && (
  echo        laeuft bereits
) || (
  if exist "%CRS_OLLAMA_APP%" (
    echo        starte Ollama
    start "" "%CRS_OLLAMA_APP%"
    call :warte http://localhost:11434/api/version 60 "Ollama"
  ) else (
    where ollama >nul 2>&1 && (
      echo        starte ollama serve
      start "Ollama" /min ollama serve
      call :warte http://localhost:11434/api/version 60 "Ollama"
    ) || echo        NICHT GEFUNDEN -- Reasoning, Wissen und Semantik ohne Modell
  )
)
curl -s -m 5 http://localhost:11434/api/tags 2>nul | "%FIND%" "bge-m3" >nul && (echo        bge-m3: vorhanden) || echo        bge-m3: FEHLT -- ollama pull bge-m3
curl -s -m 5 http://localhost:11434/api/tags 2>nul | "%FIND%" "nemotron3" >nul && (echo        nemotron3: vorhanden) || echo        nemotron3: fehlt -- optional, nur Reasoning

rem ------------------------------------------------------------------- 4. Die App
echo.
echo  [4/4] Anwendung, Port 5011
curl -s -m 3 -o nul http://localhost:5011/api/health && (
  echo        laeuft bereits
) || (
  echo        baue ...
  dotnet build "%API%\Ingest.Api.csproj" -nologo -v q
  if errorlevel 1 (
    echo.
    echo        BAU FEHLGESCHLAGEN -- siehe Meldungen oben.
    pause
    exit /b 1
  )
  echo        starte in eigenem Fenster ...
  start "CRSOFT.StockCrawler" /D "%API%" cmd /k "set ASPNETCORE_ENVIRONMENT=Development&& dotnet run --no-build --launch-profile http"
  call :warte http://localhost:5011/api/health 90 "Anwendung"
)

echo.
echo  Fertig: http://localhost:5011/
start "" http://localhost:5011/
ping -n 6 127.0.0.1 >nul
exit /b 0

rem ----------------------------------------------------------------------------
rem  :warte URL SEKUNDEN NAME -- pollt, bis die URL antwortet oder die Zeit um ist.
:warte
set "_url=%~1"
set /a "_rest=%~2"
:warte_schleife
curl -s -m 2 -o nul "%_url%" && (echo        %~3: bereit & exit /b 0)
if %_rest% leq 0 (echo        %~3: antwortet nach %~2 s noch nicht -- weiter ohne zu warten & exit /b 1)
set /a "_rest-=2"
ping -n 3 127.0.0.1 >nul
goto warte_schleife
