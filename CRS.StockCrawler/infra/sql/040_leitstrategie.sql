/*  Nur EINE Strategie zählt zum Gesamtvermögen.

    ------------------------------------------------------------------------

    DER FEHLER, DEN DAS BEHEBT. Das Band ganz oben summierte alle vier Depots
    und meldete 3.422,79 EUR, wo 2.000 USD richtig gewesen wären: einmal das
    manuelle Depot und einmal der Autopilot.

    `streng`, `aktiv` und `halten` sind aber keine drei Geldtöpfe. Sie sind
    drei Antworten auf dieselbe Frage -- „was wäre aus denselben 1.000
    geworden, wenn ich so oder so vorgegangen wäre". Sie zu addieren zählt
    dasselbe Geld dreimal. Dieselbe Art Fehler wie die stündlich wiederholten
    Prognosen auf denselben Zielbar: formal eine Summe, inhaltlich eine
    Scheinmenge.

    WARUM NICHT DAS BUDGET DRITTELN. Naheliegend wäre, den 1.000 auf drei
    Strategien à 333 aufzuteilen -- die Summe stimmte dann auch. Es wäre
    trotzdem falsch: Die drei blieben Gegenrechnungen, nur kleinere, und man
    addierte weiterhin dasselbe Geld dreimal. Ausserdem verlöre der Vergleich
    an Aussagekraft, weil sechs Positionen aus 333 näher an die Rundung
    geraten als aus 1.000.

    Also: Jede Strategie behält ihr volles Budget, damit sie unter gleichen
    Bedingungen antritt. Genau eine trägt `zaehlt = 1` und geht ins
    Gesamtvermögen ein; die anderen beiden erscheinen weiter in der Aufstellung
    und im Vergleich, aber nicht in der Summe.

    `zaehlt` ist ein Auswahlknopf, kein Häkchen: Es ist immer genau eine
    Strategie. Zwei wären wieder eine Doppelzählung, keine gar keine Anzeige.  */
USE stockcrawler;
GO

IF COL_LENGTH('dbo.autopilot_einstellung', 'zaehlt') IS NULL
  ALTER TABLE dbo.autopilot_einstellung
    ADD zaehlt BIT NOT NULL CONSTRAINT DF_aes_zaehlt DEFAULT(0);
GO

/*  `aktiv` als Voreinstellung: Sie ist die Strategie, die das System
    tatsächlich fahren würde. `streng` handelt nach heutiger Datenlage nie und
    stünde als Gesamtvermögen für ein Depot, das nichts tut; `halten` ist die
    Grundlinie und misst die anderen, statt selbst das Ergebnis zu sein.      */
IF NOT EXISTS (SELECT 1 FROM dbo.autopilot_einstellung WHERE zaehlt = 1)
  UPDATE dbo.autopilot_einstellung
     SET zaehlt = CASE WHEN depot = 'aktiv' THEN 1 ELSE 0 END,
         updated_utc = SYSUTCDATETIME();
GO

/*  Genau eine. Die Umschaltung gehört in eine Prozedur und nicht in zwei
    Aufrufe des Dienstes: Zwischen „alte löschen" und „neue setzen" darf es
    keinen Zustand geben, in dem gar keine oder zwei zählen -- das Band würde
    in diesem Moment eine falsche Summe zeigen.                              */
CREATE OR ALTER PROCEDURE dbo.set_leitstrategie
    @depot NVARCHAR(16)
AS
BEGIN
  SET NOCOUNT ON;

  IF @depot NOT IN ('streng', 'aktiv', 'halten')
  BEGIN
    RAISERROR('Nur streng, aktiv oder halten können zum Vermögen zählen.', 16, 1);
    RETURN;
  END

  UPDATE dbo.autopilot_einstellung
     SET zaehlt = CASE WHEN depot = @depot THEN 1 ELSE 0 END,
         updated_utc = SYSUTCDATETIME();

  SELECT depot, zaehlt FROM dbo.autopilot_einstellung ORDER BY depot;
END
GO
