/*  Autopilot: mehrere Depots nebeneinander, und das Protokoll seiner Beschlüsse.

    ------------------------------------------------------------------------

    EINE SPALTE STATT EINES ZWEITEN APPARATS. Der Autopilot braucht Buchungen,
    ein Verrechnungskonto, Gebühren, ein Kassenjournal und einen
    Vermögensverlauf -- also genau das, was für das manuelle Depot schon steht
    und geprüft ist. Ein eigener Satz Tabellen daneben hiesse, dieselbe Logik
    ein zweites Mal zu schreiben; sie liefe unweigerlich auseinander, und
    spätestens beim ersten Fehler in der Gebührenrechnung stünden zwei
    Fassungen da, von denen eine repariert wird.

    Deshalb bekommt jede Zeile ein `depot`. Vier Werte:

      manuell  was der Nutzer einträgt
      streng   handelt nur, wenn (2p-1)*E|r| den Rundlauf schlägt
      aktiv    hält immer die bestbewerteten N, schichtet um
      halten   kauft die Startauswahl einmal gleichgewichtet und rührt sich nie

    `halten` ist die Grundlinie. Sie zahlt DIESELBEN Gebühren wie die anderen --
    eine kostenfreie Grundlinie wäre unschlagbar und damit als Vergleich
    wertlos.

    WARUM AUCH DIE ABGELEHNTEN WERTE ABGELEGT WERDEN. Ein Autopilot, der nur
    seine Geschäfte protokolliert, lässt sich nicht prüfen: Man sieht, was er
    getan hat, und nie, was er erwogen und verworfen hat. Beim strengen Depot
    ist das Ausbleiben von Geschäften sogar das eigentliche Ergebnis -- gemessen
    liegt die Richtungstrefferquote auf einen Tag bei 45,0 % (n=1.650), nötig
    wären 59,7 %. Ohne eine Zeile je abgelehntem Wert sähe ein Depot, das nichts
    tut, genauso aus wie eines, das nicht läuft.                              */
USE stockcrawler;
GO

-- ------------------------------------------------------------ Depotspalte --

IF COL_LENGTH('dbo.invest_buchung', 'depot') IS NULL
  ALTER TABLE dbo.invest_buchung
    ADD depot NVARCHAR(16) NOT NULL CONSTRAINT DF_ib_depot DEFAULT('manuell');
GO

IF COL_LENGTH('dbo.invest_kontobewegung', 'depot') IS NULL
  ALTER TABLE dbo.invest_kontobewegung
    ADD depot NVARCHAR(16) NOT NULL CONSTRAINT DF_ikb_depot DEFAULT('manuell');
GO

/*  Das Konto gibt es künftig je Depot UND Währung. Der Schlüssel muss deshalb
    beide Spalten umfassen; ohne den Umbau könnte das strenge Depot kein
    eigenes EUR-Konto haben.                                                 */
IF COL_LENGTH('dbo.invest_konto', 'depot') IS NULL
BEGIN
  ALTER TABLE dbo.invest_konto
    ADD depot NVARCHAR(16) NOT NULL CONSTRAINT DF_ik_depot DEFAULT('manuell');
END
GO

IF EXISTS (SELECT 1 FROM sys.key_constraints
            WHERE name = 'PK_invest_konto' AND parent_object_id = OBJECT_ID('dbo.invest_konto'))
   AND NOT EXISTS (SELECT 1 FROM sys.index_columns ic
                     JOIN sys.columns c ON c.object_id = ic.object_id
                                       AND c.column_id = ic.column_id
                    WHERE ic.object_id = OBJECT_ID('dbo.invest_konto')
                      AND c.name = 'depot'
                      AND ic.index_id = (SELECT unique_index_id FROM sys.key_constraints
                                          WHERE name = 'PK_invest_konto'))
BEGIN
  ALTER TABLE dbo.invest_konto DROP CONSTRAINT PK_invest_konto;
  ALTER TABLE dbo.invest_konto ADD CONSTRAINT PK_invest_konto PRIMARY KEY (depot, waehrung);
END
GO

/*  Je Depot und Währung ein Konto. Voreinstellung 0,15 % je Vorgang -- die
    Hälfte des Rundlaufs von 0,3 %, den `Handelskosten` ansetzt.             */
MERGE dbo.invest_konto WITH (HOLDLOCK) AS t
USING (SELECT d.depot, w.waehrung
         FROM (VALUES ('manuell'), ('streng'), ('aktiv'), ('halten')) AS d(depot)
        CROSS JOIN (VALUES ('EUR'), ('USD')) AS w(waehrung)) AS s
   ON t.depot = s.depot AND t.waehrung = s.waehrung
WHEN NOT MATCHED THEN INSERT (depot, waehrung) VALUES (s.depot, s.waehrung);
GO

/*  Die Übersicht filtert immer nach Depot; ohne die Spalte vorn im Index
    liefe jede Abfrage über den ganzen Bestand aller vier Depots.            */
IF IndexProperty(OBJECT_ID('dbo.invest_buchung'), 'IX_ib_depot_asset', 'IndexID') IS NULL
  CREATE INDEX IX_ib_depot_asset ON dbo.invest_buchung(depot, asset_id, am_utc)
    INCLUDE (betrag, anteile, kurs, gebuehr, waehrung);
GO

IF IndexProperty(OBJECT_ID('dbo.invest_kontobewegung'), 'IX_ikb_depot_waehr', 'IndexID') IS NULL
  CREATE INDEX IX_ikb_depot_waehr ON dbo.invest_kontobewegung(depot, waehrung, am_utc)
    INCLUDE (betrag, grund);
GO

-- ------------------------------------------------------------------ Läufe --

IF OBJECT_ID('dbo.autopilot_lauf') IS NULL
CREATE TABLE dbo.autopilot_lauf (
  lauf_id       INT            IDENTITY(1,1) NOT NULL,
  gestartet_utc DATETIME2(0)   NOT NULL CONSTRAINT DF_al_start DEFAULT(SYSUTCDATETIME()),
  beendet_utc   DATETIME2(0)   NULL,
  depot         NVARCHAR(16)   NOT NULL,
  geprueft      INT            NOT NULL CONSTRAINT DF_al_gepr DEFAULT(0),
  geschaefte    INT            NOT NULL CONSTRAINT DF_al_gesch DEFAULT(0),
  gebuehren     DECIMAL(18,2)  NOT NULL CONSTRAINT DF_al_geb DEFAULT(0),

  /*  Was am Ende dastand -- damit sich der Verlauf des Depots auch dann noch
      nachvollziehen lässt, wenn später jemand Buchungen entfernt.           */
  vermoegen     DECIMAL(18,2)  NULL,

  /*  Ob dieser Lauf handeln DURFTE. Ein Lauf ohne Geschäfte hat zwei
      grundverschiedene Ursachen -- nichts war lohnend, oder der Takt war noch
      nicht abgelaufen -- und ohne diese Spalte sähen beide gleich aus.      */
  handelstag    BIT            NOT NULL CONSTRAINT DF_al_ht DEFAULT(1),
  notiz         NVARCHAR(1000) NULL,
  CONSTRAINT PK_autopilot_lauf PRIMARY KEY (lauf_id)
);
GO

-- ------------------------------------------------------------ Beschlüsse --

IF OBJECT_ID('dbo.autopilot_entscheidung') IS NULL
CREATE TABLE dbo.autopilot_entscheidung (
  entscheidung_id INT           IDENTITY(1,1) NOT NULL,
  lauf_id         INT           NOT NULL,
  asset_id        INT           NOT NULL,
  rang            INT           NOT NULL,

  /*  Die Punktzahl und ihre Bestandteile. Das JSON hält je Beitrag den Wert,
      den gemessenen Verdienst und den Anteil -- dieselbe Form wie `pillar_mix`
      bei der Prognose, aus demselben Grund: Ohne die Zerlegung ist eine
      Rangliste eine Behauptung.                                             */
  punktzahl       FLOAT         NOT NULL,
  bestandteile    NVARCHAR(MAX) NULL,

  /*  Die drei Zahlen, an denen der Beschluss hängt.                         */
  trefferquote    FLOAT         NULL,
  bewegung        FLOAT         NULL,
  erwartungswert  FLOAT         NULL,

  /*  kaufen | nachlegen | verkaufen | aufloesen | halten | abgelehnt        */
  beschluss       NVARCHAR(20)  NOT NULL,
  grund           NVARCHAR(600) NULL,
  betrag          DECIMAL(18,2) NULL,

  /*  Nemotron. `urteil` ist NULL, wenn das Modell nicht erreichbar war -- das
      ist ausdrücklich etwas anderes als „hat zugestimmt“, und die Spalte muss
      es unterscheiden können.                                               */
  urteil          BIT           NULL,
  urteil_text     NVARCHAR(2000) NULL,

  ausgefuehrt     BIT           NOT NULL CONSTRAINT DF_ae_aus DEFAULT(0),
  am_utc          DATETIME2(0)  NOT NULL CONSTRAINT DF_ae_am DEFAULT(SYSUTCDATETIME()),

  CONSTRAINT PK_autopilot_entscheidung PRIMARY KEY (entscheidung_id),
  CONSTRAINT FK_ae_lauf FOREIGN KEY (lauf_id) REFERENCES dbo.autopilot_lauf(lauf_id),
  CONSTRAINT FK_ae_asset FOREIGN KEY (asset_id) REFERENCES dbo.asset(asset_id)
);
GO

IF IndexProperty(OBJECT_ID('dbo.autopilot_entscheidung'), 'IX_ae_lauf_rang', 'IndexID') IS NULL
  CREATE INDEX IX_ae_lauf_rang ON dbo.autopilot_entscheidung(lauf_id, rang);
GO

-- --------------------------------------------------- Einstellungen je Depot --

/*  Startkapital, Zahl der Werte, Ein/Aus. Getrennt von `invest_konto`, weil
    das Konto eine Zähleinheit beschreibt und dies eine Strategie.           */
IF OBJECT_ID('dbo.autopilot_einstellung') IS NULL
CREATE TABLE dbo.autopilot_einstellung (
  depot        NVARCHAR(16)  NOT NULL,
  aktiv        BIT           NOT NULL CONSTRAINT DF_aes_aktiv DEFAULT(0),

  /*  Wieviele Werte das Depot gleichzeitig hält. Acht ist ein Kompromiss:
      Weniger hängt am Einzelwert, mehr verteilt das Kapital so fein, dass die
      Gebühr je Position ins Gewicht fällt.                                  */
  werte        INT           NOT NULL CONSTRAINT DF_aes_werte DEFAULT(8),

  /*  Höchstanteil eines Wertes am Depotvermögen.                            */
  max_anteil   DECIMAL(5,4)  NOT NULL CONSTRAINT DF_aes_max DEFAULT(0.20),

  /*  Wieviel Vorsprung ein Anwärter braucht, um einen gehaltenen Wert zu
      verdrängen. Ohne diese Sperre tauscht ein Rangwechsel um einen Platz
      täglich hin und her und zahlt jedes Mal den Rundlauf.                  */
  hysterese    DECIMAL(6,4)  NOT NULL CONSTRAINT DF_aes_hyst DEFAULT(0.0030),

  /*  Wie oft umgeschichtet werden darf: 1T, 1W, 1M, 3M, 6M, 1J.

      Bewertet und PROTOKOLLIERT wird trotzdem jeden Tag -- sonst wüsste man
      bei einem Jahrestakt elf Monate lang nicht, ob der Autopilot überhaupt
      läuft. Der Takt bestimmt allein, wann gehandelt werden darf.

      Voreinstellung 1T. Dass ein längerer Takt bei 0,3 % Rundlauf und 1,55 %
      Tagesbewegung meist der bessere ist, ist eine Vermutung und keine
      Messung -- genau deshalb ist er einstellbar und nicht festgelegt.      */
  takt         NVARCHAR(4)   NOT NULL CONSTRAINT DF_aes_takt DEFAULT('1T'),

  waehrung     NVARCHAR(8)   NOT NULL CONSTRAINT DF_aes_waehr DEFAULT('EUR'),
  /*  AUS als Voreinstellung, und das ist eine Messung, keine Vorsicht.

      Auf diesem Rechner braucht nemotron3:33b für ein Urteil länger als die
      Frist von 120 s; es kam zweimal in Folge eine Antwort ohne verwertbares
      JSON zurück („Die Frist ist abgelaufen"). Eingeschaltet kostete das Veto
      damit bei acht Käufen bis zu sechzehn Minuten je Tageslauf -- und wäre
      dabei wirkungslos, weil nie ein Urteil zustande kommt.

      Sinnvoll wird es mit einem schnellen Endpunkt, also einer gemieteten
      Karte. Dann einschalten; bis dahin wäre es ein Bedienelement, das
      Rechenzeit verbraucht und nichts entscheidet.                          */
  nemotron     BIT           NOT NULL CONSTRAINT DF_aes_nem DEFAULT(0),
  updated_utc  DATETIME2(0)  NOT NULL CONSTRAINT DF_aes_upd DEFAULT(SYSUTCDATETIME()),
  CONSTRAINT PK_autopilot_einstellung PRIMARY KEY (depot),
  CONSTRAINT CK_aes_takt CHECK (takt IN ('1T','1W','1M','3M','6M','1J'))
);
GO

IF COL_LENGTH('dbo.autopilot_einstellung', 'takt') IS NULL
  ALTER TABLE dbo.autopilot_einstellung
    ADD takt NVARCHAR(4) NOT NULL CONSTRAINT DF_aes_takt DEFAULT('1T');
GO

MERGE dbo.autopilot_einstellung WITH (HOLDLOCK) AS t
USING (VALUES ('streng'), ('aktiv'), ('halten')) AS s(depot) ON t.depot = s.depot
WHEN NOT MATCHED THEN INSERT (depot) VALUES (s.depot);
GO
