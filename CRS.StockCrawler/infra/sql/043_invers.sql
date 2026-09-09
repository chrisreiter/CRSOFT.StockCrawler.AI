/*  043 -- Die vierte Strategie: `invers`.

    WAS SIE TUT. Genau das Gegenteil von `aktiv`. Wo `aktiv` die Werte mit der
    hoechsten Erwartung haelt, haelt `invers` die mit der NIEDRIGSTEN -- also
    genau jene, von denen das Modell einen Rueckgang erwartet.

    WARUM SIE TROTZ DES BEKANNTEN BEFUNDS SINNVOLL IST. Die Seite
    „Umkehrschluss" haelt fest: Ein Signal umzudrehen nuetzt nur, wenn es
    ZUVERLAESSIG FALSCH ist. Eine Trefferquote von 0,505 umgedreht ergibt
    0,495, und die Gebuehren bleiben dieselben. Als Geldanlage ist das also
    keine Hoffnung, sondern eine leicht teurere Variante des Wuerfelns.

    Ihr Wert liegt woanders: Sie ist die GEGENKONTROLLE, die bisher fehlte.

      halten  misst gegen den Markt -- aber sie handelt nicht und traegt
              deshalb ein voellig anderes Kostenprofil.

      invers  handelt genauso oft wie `aktiv`, zahlt dieselben Gebuehren, ist
              demselben Marktgang ausgesetzt -- und benutzt dasselbe Signal
              mit umgekehrtem Vorzeichen.

    Damit ist `aktiv` minus `invers` der Informationsgehalt des Signals,
    bereinigt um Marktgang UND Kosten. Laufen beide gleich, ist das Signal
    Rauschen -- und das laesst sich mit `halten` allein nicht zeigen.

    WAS SIE NICHT IST. Kein Leerverkauf. Dieses System kann nicht leerverkaufen
    (siehe „Ein Paargewinn setzt ZWEI Positionen voraus"), also KAUFT `invers`
    die schlechtbewerteten Werte. Erwartet das Modell zu Recht einen Rueckgang,
    verliert sie; irrt es, gewinnt sie. Das ist der Test, nicht die Wette.     */

SET NOCOUNT ON;
GO

/*  Konten fuer das neue Depot, in beiden Waehrungen. Ohne sie scheiterte die
    erste Buchung an einem fehlenden Konto -- und zwar mitten im Lauf.        */
MERGE dbo.invest_konto WITH (HOLDLOCK) AS t
USING (SELECT d.depot, w.waehrung
         FROM (VALUES ('invers')) AS d(depot)
        CROSS JOIN (VALUES ('EUR'), ('USD')) AS w(waehrung)) AS s
   ON t.depot = s.depot AND t.waehrung = s.waehrung
WHEN NOT MATCHED THEN INSERT (depot, waehrung) VALUES (s.depot, s.waehrung);
GO

/*  Die Einstellungszeile. Sie erbt die Voreinstellungen der Tabelle --
    Startkapital 1000 USD, Takt 1T, aktiv = 0.

    AUSGESCHALTET angelegt, wie die drei anderen es waren. Eine Strategie, die
    von selbst zu handeln beginnt, weil jemand eine Migration eingespielt hat,
    waere eine unangenehme Ueberraschung auf einem Konto.                     */
/*  Die Parameter werden von `aktiv` UEBERNOMMEN, nicht auf die Voreinstellung
    der Tabelle gesetzt.

    Eine Gegenkontrolle muss dieselben Parameter tragen wie das, was sie
    kontrolliert. Haelt `aktiv` sechs Werte und `invers` acht, unterscheiden
    sich die beiden Depots in ZWEI Dingen -- im Vorzeichen des Signals und in
    der Streuung -- und der Unterschied laesst sich keinem von beiden mehr
    zuordnen. Genau derselbe Fehler wie eine gebuehrenfreie Grundlinie.       */
MERGE dbo.autopilot_einstellung WITH (HOLDLOCK) AS t
USING (SELECT 'invers' AS depot, a.werte, a.max_anteil, a.hysterese,
              a.startkapital, a.waehrung, a.takt
         FROM dbo.autopilot_einstellung a WHERE a.depot = 'aktiv') AS s
   ON t.depot = s.depot
WHEN NOT MATCHED THEN
  INSERT (depot, werte, max_anteil, hysterese, startkapital, waehrung, takt)
  VALUES (s.depot, s.werte, s.max_anteil, s.hysterese,
          s.startkapital, s.waehrung, s.takt);
GO

/*  Die Ruecksetz-Prozedur kennt das neue Depot noch nicht.

    Ihre Liste ist eine zweite Stelle, an der die Strategienamen stehen -- die
    Sorte Liste, die man beim Hinzufuegen vergisst. Hier faellt es wenigstens
    laut auf: Der Aufruf endete mit „Nur streng, aktiv oder halten lassen sich
    zuruecksetzen." und nicht stillschweigend.                                */
CREATE OR ALTER PROCEDURE dbo.reset_autopilot_depot
    @depot NVARCHAR(16)
AS
BEGIN
  SET NOCOUNT ON;

  IF @depot NOT IN ('streng', 'aktiv', 'halten', 'invers')
  BEGIN
    RAISERROR('Nur streng, aktiv, halten oder invers lassen sich zurücksetzen.', 16, 1);
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

/*  Auch die Leitstrategie kennt die vierte noch nicht -- dieselbe vergessene
    Liste, ein zweites Mal.

    `invers` darf zaehlen duerfen. Sie ALS Leitstrategie zu fahren waere eine
    seltsame Wahl, aber das ist die Entscheidung des Betreibers und nicht die
    einer Prozedur: Eine Strategie, die man anlegen, einschalten und laufen
    lassen kann, deren Ergebnis aber nie im Gesamtvermoegen erscheinen darf,
    waere auf halbem Weg stehengeblieben.                                     */
CREATE OR ALTER PROCEDURE dbo.set_leitstrategie
    @depot NVARCHAR(16)
AS
BEGIN
  SET NOCOUNT ON;

  IF @depot NOT IN ('streng', 'aktiv', 'halten', 'invers')
  BEGIN
    RAISERROR('Nur streng, aktiv, halten oder invers können zum Vermögen zählen.', 16, 1);
    RETURN;
  END

  UPDATE dbo.autopilot_einstellung
     SET zaehlt = CASE WHEN depot = @depot THEN 1 ELSE 0 END,
         updated_utc = SYSUTCDATETIME();

  SELECT depot, zaehlt FROM dbo.autopilot_einstellung ORDER BY depot;
END
GO
