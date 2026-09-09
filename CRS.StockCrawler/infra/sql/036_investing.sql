/*  Virtuelles Depot: was aus einem heute eingesetzten Betrag geworden wäre.

    ------------------------------------------------------------------------

    WARUM EIN JOURNAL UND KEINE MOMENTAUFNAHME. `holding` hält je Wert eine
    Zeile mit dem aktuellen Kapital -- richtig für die Tausch-Seite, die fragt
    „womit stecke ich wo drin". Hier ist die Frage eine andere: „wie hat sich
    das entwickelt, seit ich es eingesetzt habe, und was wäre geworden, wenn
    ich zwischendurch nachgelegt hätte". Wer den Betrag in einer einzigen
    Zeile überschreibt, löscht bei jeder Anpassung genau die Geschichte, die
    er sehen will. Der Verlauf liesse sich danach nicht mehr rechnen -- nicht
    „ungenau", sondern gar nicht.

    Eine Buchung ist deshalb ein Ereignis: an diesem Tag, zu diesem Kurs,
    dieser Betrag. Der Bestand an Anteilen ist ihre Summe, der heutige Wert
    Anteile mal heutiger Kurs. Dieselbe Bauart wie ein Kontoauszug, und aus
    demselben Grund.

    ANTEILE WERDEN GESPEICHERT, NICHT GERECHNET. Sie ergäben sich aus
    `betrag / kurs`. Gespeichert stehen sie trotzdem, denn sie sind die
    eigentliche Tatsache: Der Betrag ist Vergangenheit, der Kurs ändert sich,
    die Anteile bleiben. Eine Änderung an der Rundung dürfte einen bereits
    gebuchten Bestand nicht rückwirkend verschieben.

    VORZEICHEN. `betrag` und `anteile` sind vorzeichenbehaftet: positiv
    einsetzen, negativ entnehmen. Zwei Spalten für Kauf und Verkauf wären
    dieselbe Information mit doppelt so vielen Stellen, an denen ein
    Vorzeichen falsch stehen kann.

    KEINE RÜCKDATIERUNG. Gebucht wird zum jüngsten bekannten Kurs, und dessen
    Zeitstempel steht mit in der Zeile. Wer zu einem vergangenen Datum buchen
    dürfte, betriebe keine Simulation mehr, sondern eine Rückrechnung mit
    bekanntem Ausgang -- die Disziplin, die dieses Projekt an jeder anderen
    Stelle einhält, gilt auch hier.

    WÄHRUNG. Der Kursanstieg eines Wertes vermehrt den eingesetzten Betrag in
    jeder Zähleinheit gleichermassen; die Bewegung des WECHSELKURSES bildet
    dieses System nicht ab, weil es keine Devisenreihen führt. Summiert wird
    deshalb je Währung getrennt. Ein Gesamtwert über EUR- und USD-Positionen
    hinweg wäre eine Zahl, die niemand nachrechnen kann.                     */
USE stockcrawler;
GO

IF OBJECT_ID('dbo.invest_buchung') IS NULL
CREATE TABLE dbo.invest_buchung (
  buchung_id   INT            IDENTITY(1,1) NOT NULL,
  asset_id     INT            NOT NULL,

  /*  Der Zeitpunkt der Buchung -- wann der Nutzer sie ausgelöst hat.        */
  am_utc       DATETIME2(0)   NOT NULL CONSTRAINT DF_ib_am DEFAULT(SYSUTCDATETIME()),

  /*  Der Kurs, zu dem gebucht wurde, samt dem Zeitstempel SEINER Bar. Beides
      gehört in die Zeile: Am Wochenende ist der jüngste Aktienkurs zwei Tage
      alt, und wer das später nicht sieht, hält den Einstand für tagesaktuell. */
  kurs         DECIMAL(28,10) NOT NULL,
  kurs_utc     DATETIME2(0)   NULL,

  betrag       DECIMAL(18,2)  NOT NULL,
  anteile      DECIMAL(28,10) NOT NULL,
  waehrung     NVARCHAR(8)    NOT NULL CONSTRAINT DF_ib_waehr DEFAULT('EUR'),
  notiz        NVARCHAR(400)  NULL,

  CONSTRAINT PK_invest_buchung PRIMARY KEY (buchung_id),
  CONSTRAINT FK_ib_asset FOREIGN KEY (asset_id) REFERENCES dbo.asset(asset_id),
  CONSTRAINT CK_ib_kurs CHECK (kurs > 0),

  /*  Eine Buchung über null Euro ist keine Buchung. Sie entstünde beim
      Bestätigen eines unveränderten Feldes und füllte das Journal mit
      Zeilen, die nichts sagen.                                             */
  CONSTRAINT CK_ib_betrag CHECK (betrag <> 0)
);
GO

/*  Der Verlauf liest je Wert alle Buchungen in zeitlicher Ordnung; die
    Positionsübersicht summiert je Wert. Beides bedient dieser Index.        */
IF IndexProperty(OBJECT_ID('dbo.invest_buchung'), 'IX_ib_asset_am', 'IndexID') IS NULL
  CREATE INDEX IX_ib_asset_am ON dbo.invest_buchung(asset_id, am_utc)
    INCLUDE (betrag, anteile, kurs, waehrung);
GO
