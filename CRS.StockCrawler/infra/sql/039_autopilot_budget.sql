/*  Eigenes Startbudget je Strategie, und die Möglichkeit, sie zurückzusetzen.

    ------------------------------------------------------------------------

    WARUM DAS BUDGET IN DIE EINSTELLUNGEN GEHÖRT UND NICHT AUF DAS KONTO.
    Der Kontostand ist ein Ist-Wert -- die Summe aller Bewegungen, die sich mit
    jedem Kauf ändert. Das Budget ist ein Soll-Wert: der Betrag, auf den ein
    Zurücksetzen zurückführt. Beides in dieselbe Spalte zu legen hiesse, nach
    dem ersten Geschäft nicht mehr sagen zu können, womit die Strategie
    angetreten ist -- und genau das ist die Zahl, gegen die ihr Ergebnis zu
    halten ist.

    1.000 USD als Vorgabe. Der Betrag ist klein genug, dass niemand ihn für
    eine Empfehlung hält, und gross genug, dass acht Positionen daraus keine
    Bruchteile von Cents werden.

    WARUM USD UND NICHT EUR. Von 594 verfolgten Werten notieren 331 in USD und
    145 in EUR; die gesamte Kryptoseite ohnehin. Ein Depot in EUR könnte einen
    Grossteil des Bestands nur über eine Umrechnung halten, die dieses System
    für Einzelpositionen bewusst nicht vornimmt.

    WAS EIN ZURÜCKSETZEN MITNIMMT. Buchungen, Kassenbewegungen, Läufe und
    Beschlüsse dieses Depots -- und zwar in ALLEN Währungen, nicht nur in der
    eingestellten. Bliebe ein alter EUR-Stand neben einem frischen USD-Budget
    stehen, zeigte das Gesamtvermögen Geld, das zu keiner Strategie mehr
    gehört. Die Läufe fliegen mit, weil sie sonst Geschäfte belegen, deren
    Buchungen es nicht mehr gibt.                                            */
USE stockcrawler;
GO

IF COL_LENGTH('dbo.autopilot_einstellung', 'startkapital') IS NULL
  ALTER TABLE dbo.autopilot_einstellung
    ADD startkapital DECIMAL(18,2) NOT NULL CONSTRAINT DF_aes_start DEFAULT(1000);
GO

/*  Die Vorgabe für die Währung von EUR auf USD umstellen. Ein DEFAULT lässt
    sich nicht ändern, nur ersetzen -- der alte Zwang muss also weichen.      */
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_aes_waehr')
BEGIN
  ALTER TABLE dbo.autopilot_einstellung DROP CONSTRAINT DF_aes_waehr;
  ALTER TABLE dbo.autopilot_einstellung
    ADD CONSTRAINT DF_aes_waehr DEFAULT('USD') FOR waehrung;
END
GO

/*  Bestehende Zeilen mitziehen. Das ist hier gefahrlos: Zum Zeitpunkt dieser
    Migration hat noch keine Strategie ausserhalb der Erprobung gehandelt, und
    ein Zurücksetzen räumt ohnehin auf.                                      */
UPDATE dbo.autopilot_einstellung
   SET waehrung = 'USD', startkapital = 1000, updated_utc = SYSUTCDATETIME()
 WHERE waehrung <> 'USD' OR startkapital IS NULL;
GO

/*  Eine Strategie vollständig auf Anfang stellen.

    In EINER Transaktion, und in dieser Reihenfolge: erst die Beschlüsse (sie
    hängen am Lauf), dann die Läufe, dann die Kassenbewegungen (sie hängen an
    der Buchung), dann die Buchungen. Andersherum stünden Fremdschlüssel im
    Weg -- und ein halb aufgeräumtes Depot wäre schlimmer als ein volles.    */
CREATE OR ALTER PROCEDURE dbo.reset_autopilot_depot
    @depot NVARCHAR(16)
AS
BEGIN
  SET NOCOUNT ON;

  IF @depot NOT IN ('streng', 'aktiv', 'halten')
  BEGIN
    RAISERROR('Nur streng, aktiv oder halten lassen sich zurücksetzen.', 16, 1);
    RETURN;
  END

  DECLARE @waehrung NVARCHAR(8), @betrag DECIMAL(18,2);

  SELECT @waehrung = waehrung, @betrag = startkapital
    FROM dbo.autopilot_einstellung WHERE depot = @depot;

  BEGIN TRAN;

    DELETE e
      FROM dbo.autopilot_entscheidung e
      JOIN dbo.autopilot_lauf l ON l.lauf_id = e.lauf_id
     WHERE l.depot = @depot;

    DELETE FROM dbo.autopilot_lauf        WHERE depot = @depot;

    -- ALLE Währungen, nicht nur die eingestellte.
    DELETE FROM dbo.invest_kontobewegung  WHERE depot = @depot;
    DELETE FROM dbo.invest_buchung        WHERE depot = @depot;

    /*  Das Budget als gewöhnliche Einzahlung -- damit ist der Startbetrag im
        Kassenjournal sichtbar und der Kontostand bleibt das, was er überall
        sonst ist: die Summe seiner Bewegungen.                              */
    IF @betrag > 0
      INSERT INTO dbo.invest_kontobewegung (depot, waehrung, betrag, grund, notiz)
      VALUES (@depot, @waehrung, @betrag, 'einzahlung', 'Startbudget');

  COMMIT;

  SELECT @depot AS depot, @waehrung AS waehrung, @betrag AS startkapital;
END
GO
