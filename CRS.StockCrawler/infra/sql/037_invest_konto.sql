/*  Kontostand je Währung, Gebühren, und die Verbindung zu den Positionen.

    ------------------------------------------------------------------------

    WARUM AUCH DAS KONTO EIN JOURNAL IST. Derselbe Grund wie bei den
    Positionen: Ein gespeicherter Kontostand beantwortet „wieviel habe ich
    jetzt", aber nicht „wie kam es dazu". Ohne die Bewegungen liesse sich der
    Kassenverlauf nicht rechnen -- und ohne den ist eine Vermögenskurve
    unvollständig, denn nach einem Verkauf steckt das Geld nicht mehr im Kurs,
    sondern im Konto. Eine Kurve, die den Verkauf als Absturz zeigt, ist
    falsch.

    Der Stand ist deshalb die SUMME der Bewegungen. „Stand setzen" bucht die
    Differenz -- dieselbe Regel wie beim Feld der Position: Das Eingabefeld
    trägt das Soll, gebucht wird die Veränderung.

    DIE GEBÜHR IST IMMER EIN ABFLUSS. Beim Kauf verlässt der Betrag PLUS die
    Gebühr das Konto, beim Verkauf kommt der Betrag MINUS die Gebühr zurück.
    In einer Zeile: `konto -= betrag + gebuehr`, wobei `betrag` sein Vorzeichen
    trägt und `gebuehr` nie negativ ist. Zwei Fälle, eine Formel; getrennte
    Zweige für Kauf und Verkauf wären zwei Stellen, an denen ein Vorzeichen
    falsch stehen kann.

    WARUM DIE GEBÜHR AUF DER BUCHUNG STEHT UND NICHT NUR ALS SATZ. Der Satz
    ändert sich; die bezahlte Gebühr nicht. Stünde nur der Prozentsatz da,
    verschöbe eine spätere Änderung rückwirkend jede bereits bezahlte Gebühr --
    dieselbe Überlegung wie bei den gespeicherten Anteilen.

    ZWEI BEINE. Der Satz gilt je Vorgang, nicht je Rundlauf: Wer kauft und
    später verkauft, zahlt ihn zweimal. Das entspricht der Rechnung der
    Handelsseiten, wo `Handelskosten` den Rundlauf mit 0,3 % ansetzt, also
    0,15 % je Bein.                                                          */
USE stockcrawler;
GO

/*  Was tatsächlich bezahlt wurde -- je Buchung, nicht je Satz.              */
IF COL_LENGTH('dbo.invest_buchung', 'gebuehr') IS NULL
  ALTER TABLE dbo.invest_buchung
    ADD gebuehr DECIMAL(18,2) NOT NULL CONSTRAINT DF_ib_geb DEFAULT(0);
GO

/*  Einstellungen je Währung. Der Stand steht hier NICHT -- er ist die Summe
    der Bewegungen.                                                          */
IF OBJECT_ID('dbo.invest_konto') IS NULL
CREATE TABLE dbo.invest_konto (
  waehrung     NVARCHAR(8)   NOT NULL,
  gebuehr_pct  DECIMAL(6,4)  NOT NULL CONSTRAINT DF_ik_geb DEFAULT(0.15),
  updated_utc  DATETIME2(0)  NOT NULL CONSTRAINT DF_ik_upd DEFAULT(SYSUTCDATETIME()),
  CONSTRAINT PK_invest_konto PRIMARY KEY (waehrung),

  /*  Ein Satz über zehn Prozent je Vorgang ist kein Gebührensatz, sondern ein
      Tippfehler. Die Grenze steht hier und nicht nur in der Oberfläche, denn
      eine falsche Zahl an dieser Stelle verfälscht jede spätere Auswertung.  */
  CONSTRAINT CK_ik_geb CHECK (gebuehr_pct >= 0 AND gebuehr_pct <= 10)
);
GO

/*  Jede Bewegung auf dem Konto.

    `grund` unterscheidet, was von aussen kam, von dem, was nur zwischen Konto
    und Position hin- und hergeht. Ohne diese Trennung liesse sich „was habe
    ich eingezahlt" nicht von „was habe ich umgeschichtet" unterscheiden -- und
    genau das ist der Nenner, gegen den der Gewinn zu halten ist.            */
IF OBJECT_ID('dbo.invest_kontobewegung') IS NULL
CREATE TABLE dbo.invest_kontobewegung (
  bewegung_id  INT            IDENTITY(1,1) NOT NULL,
  waehrung     NVARCHAR(8)    NOT NULL,
  am_utc       DATETIME2(0)   NOT NULL CONSTRAINT DF_ikb_am DEFAULT(SYSUTCDATETIME()),
  betrag       DECIMAL(18,2)  NOT NULL,

  /*  einzahlung | auszahlung | kauf | verkauf | gebuehr                     */
  grund        NVARCHAR(20)   NOT NULL,

  /*  Die Buchung, zu der die Bewegung gehört. NULL bei Ein- und Auszahlung.

      Wer eine Simulation verwirft, verliert AUCH diese Bewegungen -- und das
      ist nicht Bequemlichkeit, sondern Notwendigkeit: Bliebe die Kassenspur
      stehen, während die Position verschwindet, fehlte dem Konto der
      eingesetzte Betrag für immer, ohne dass etwas dafür da wäre. Das Geld
      wäre stillschweigend vernichtet.

      Kein Fremdschlüssel mit ON DELETE CASCADE: Die Reihenfolge steht im
      Dienst, in einer Transaktion, und sie ist dort zu sehen. Eine
      Löschweitergabe in der Tabellendefinition wirkt an einer Stelle, an der
      niemand nachsieht, wenn er den Löschpfad liest.                        */
  buchung_id   INT            NULL,
  notiz        NVARCHAR(400)  NULL,

  CONSTRAINT PK_invest_kontobewegung PRIMARY KEY (bewegung_id),
  CONSTRAINT CK_ikb_betrag CHECK (betrag <> 0)
);
GO

IF IndexProperty(OBJECT_ID('dbo.invest_kontobewegung'), 'IX_ikb_waehr_am', 'IndexID') IS NULL
  CREATE INDEX IX_ikb_waehr_am ON dbo.invest_kontobewegung(waehrung, am_utc)
    INCLUDE (betrag, grund);
GO

/*  Die beiden Währungen anlegen, damit die Oberfläche etwas zum Einstellen
    hat. Voreinstellung 0,15 % je Vorgang -- die Hälfte des Rundlaufs von
    0,3 %, den `Handelskosten` ansetzt.                                      */
MERGE dbo.invest_konto WITH (HOLDLOCK) AS t
USING (VALUES ('EUR'), ('USD')) AS s(waehrung) ON t.waehrung = s.waehrung
WHEN NOT MATCHED THEN INSERT (waehrung) VALUES (s.waehrung);
GO
