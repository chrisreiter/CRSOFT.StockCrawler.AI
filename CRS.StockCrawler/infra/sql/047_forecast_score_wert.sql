/*  047 -- Wert und Horizont an der Bewertung selbst.

    WARUM. `forecast_score` trug bisher nur `forecast_id`. Jede Frage der Art
    „wie gut trifft dieser Wert auf diesem Horizont" musste deshalb ueber den
    Verbund mit `dbo.forecast` gehen -- und ein Verbund ist genau die Stelle,
    an der das EventMesh-DataCell-Backend am 27.09.2026 gemessen einbrach:

      Einzelzugriff auf forecast_score ueber den Primaerschluessel     1,8 ms
      ganze Tabelle zaehlen (483.588 Zeilen)                         916 ms
      der Verbund, eingeschraenkt auf EINEN Wert (997 Treffer)        14,1 s
      dieselben 2.124 Kennungen als IN-Liste                        138,6 s

    Der Node filtert die linke Seite korrekt und materialisiert die rechte
    vollstaendig, bevor er verbindet: 2.124 mal 483.588 Paare fuer 997
    Treffer. Das ist dort gemeldet und wird dort behoben.

    ABER DAS IST NICHT DER GANZE PUNKT. Auch ein schneller Verbund waere hier
    der Umweg. Die Bewertung einer Prognose gehoert unbestreitbar zu genau
    einem Wert und genau einem Horizont -- das steht bei der Entstehung fest
    und aendert sich nie. Es hier zu fuehren ist keine Denormalisierung aus
    Not, sondern die Tatsache an ihrem Ort. Danach ist die Treffsicherheit
    eine gewoehnliche GROUP-BY-Abfrage ueber EINE Tabelle mit Index, und der
    Rueckblick liest seine Bewertungen ohne jeden Verbund.

    Die Spalten bleiben NULLABLE. Ein NOT NULL waere ehrlicher, verlangte aber
    das Nachtragen von 484.000 Zeilen VOR dem Einspielen -- und ein
    Migrationsskript, das an vorhandenen Daten scheitert, ist schlimmer als
    eine Spalte, die eine Weile Luecken hat. Nachgetragen wird beim Start
    (`ForecastRepository.NachtragenAsync`), und zwar messbar: Der Lauf sagt,
    wie viele Zeilen er gefuellt hat.

    Idempotent. Gegenstueck: infra/pgsql/016_forecast_score_wert.sql          */

IF NOT EXISTS (SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID('dbo.forecast_score')
                  AND name = 'asset_id')
  ALTER TABLE dbo.forecast_score ADD asset_id INT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID('dbo.forecast_score')
                  AND name = 'horizon_hours')
  ALTER TABLE dbo.forecast_score ADD horizon_hours INT NULL;
GO

/*  Der Index traegt die beiden gelesenen Masse gleich mit: Die
    Treffsicherheit braucht ausser Wert und Horizont nur `abs_pct_error` und
    `direction_correct`, und die stehen damit im Index selbst. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID('dbo.forecast_score')
                  AND name = 'IX_forecast_score_wert')
  CREATE INDEX IX_forecast_score_wert
      ON dbo.forecast_score (asset_id, horizon_hours)
     INCLUDE (abs_pct_error, direction_correct);
GO
