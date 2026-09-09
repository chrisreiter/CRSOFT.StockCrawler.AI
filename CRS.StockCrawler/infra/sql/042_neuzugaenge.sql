/*  Vorankündigungen: was demnächst neu an den Markt kommt.

    ------------------------------------------------------------------------

    WOZU DAS ÜBERHAUPT NÖTIG IST. In CLAUDE.md steht seit Monaten:

      „`first_seen_utc` ist kein Erstnotiz-Datum. Das Universum ist die
       Rangliste nach Marktkapitalisierung, ein Wert taucht also erst auf,
       NACHDEM er gestiegen ist. Eine Statistik über Neuzugänge misst damit
       Gewinner mit bereits gelaufenem Anstieg. Wer Neulinge auswerten will,
       braucht die VOLLSTÄNDIGE Kohorte aus Listing-Ankündigungen,
       Fehlschläge eingeschlossen."

    Genau diese Tabelle ist die vollständige Kohorte. Eingetragen wird ein
    Wert, sobald er ANGEKÜNDIGT ist -- nicht, wenn er auffällt. Was danach
    passiert, entscheidet der Markt und nicht die Auswahl.

    DIE KOHORTE BEGINNT HEUTE UND WÄCHST VORWÄRTS. Sie lässt sich nicht aus
    dem Bestand rekonstruieren: Dort stehen nur die Werte, die es in die
    Rangliste geschafft haben. Ein Neuzugang, der nach dem ersten Tag um
    achtzig Prozent fiel und aus jeder Rangliste verschwand, ist dort nie
    aufgetaucht -- und er ist der Fall, auf den es ankommt. Wer die Tabelle
    rückwirkend füllt, baut sich denselben Überlebensirrtum ein, den sie
    beheben soll.

    STATUS STATT LÖSCHEN. Eine Ankündigung, aus der nichts wird, bleibt
    stehen und bekommt `ausgefallen`. Sie aus der Tabelle zu nehmen wäre
    dasselbe Auswahlproblem noch einmal: Am Ende stünden nur die drin, die
    tatsächlich an den Markt kamen, und die Quote „wie oft wird aus einer
    Ankündigung etwas" liesse sich nicht mehr bilden.                        */
USE stockcrawler;
GO

IF OBJECT_ID('dbo.neuzugang') IS NULL
CREATE TABLE dbo.neuzugang (
  neuzugang_id    INT            IDENTITY(1,1) NOT NULL,

  /*  nasdaq-ipo | binance | coinbase | kraken | sec-s1                     */
  quelle          NVARCHAR(24)   NOT NULL,

  /*  ipo | listing -- eine Erstnotiz ist etwas anderes als die Aufnahme
      eines schon bestehenden Werts an einer weiteren Börse. Beides ist
      interessant, aber nicht dasselbe, und in einer Spalte wäre es später
      nicht mehr zu trennen.                                                */
  art             NVARCHAR(12)   NOT NULL,

  symbol          NVARCHAR(40)   NOT NULL,
  name            NVARCHAR(200)  NULL,
  markt           NVARCHAR(60)   NULL,

  /*  Wann WIR davon erfahren haben, und wann es laut Ankündigung losgeht.
      Der Abstand der beiden ist die eigentliche Frage dieser Seite: Wieviel
      Vorlauf hat man überhaupt?                                            */
  entdeckt_utc    DATETIME2(0)   NOT NULL CONSTRAINT DF_nz_ent DEFAULT(SYSUTCDATETIME()),
  erwartet_am     DATE           NULL,

  preis_von       DECIMAL(18,6)  NULL,
  preis_bis       DECIMAL(18,6)  NULL,
  volumen         DECIMAL(20,2)  NULL,

  /*  angekuendigt | gehandelt | ausgefallen                                */
  status          NVARCHAR(16)   NOT NULL CONSTRAINT DF_nz_st DEFAULT('angekuendigt'),

  /*  Sobald der Wert handelt: der erste Kurs, den wir sehen, und der Wert,
      auf den er verfolgt wird. Ohne diese beiden ist die Kohorte eine Liste
      von Namen; mit ihnen wird sie messbar.                                */
  asset_id        INT            NULL,
  erster_kurs     DECIMAL(28,10) NULL,
  erster_kurs_utc DATETIME2(0)   NULL,

  url             NVARCHAR(500)  NULL,
  titel           NVARCHAR(400)  NULL,
  aktualisiert_utc DATETIME2(0)  NOT NULL CONSTRAINT DF_nz_akt DEFAULT(SYSUTCDATETIME()),

  CONSTRAINT PK_neuzugang PRIMARY KEY (neuzugang_id),
  CONSTRAINT FK_nz_asset FOREIGN KEY (asset_id) REFERENCES dbo.asset(asset_id),

  /*  Je Quelle und Symbol nur EINMAL. Die Sammler laufen täglich über
      dieselben Listen; ohne diesen Riegel stünde nach einer Woche jede
      Ankündigung siebenmal da -- dieselbe Scheinmenge wie bei den stündlich
      wiederholten Prognosen auf denselben Zielbar.                         */
  CONSTRAINT UQ_neuzugang UNIQUE (quelle, symbol)
);
GO

IF IndexProperty(OBJECT_ID('dbo.neuzugang'), 'IX_nz_erwartet', 'IndexID') IS NULL
  CREATE INDEX IX_nz_erwartet ON dbo.neuzugang(status, erwartet_am)
    INCLUDE (symbol, name, markt, quelle, art);
GO

/*  Ein Lauf des Sammlers -- damit sich sagen lässt, ob die Quellen noch
    antworten. Eine Quelle, die stillschweigend nichts mehr liefert, sieht
    sonst aus wie ein Markt ohne Neuzugänge.                               */
IF OBJECT_ID('dbo.neuzugang_lauf') IS NULL
CREATE TABLE dbo.neuzugang_lauf (
  lauf_id       INT           IDENTITY(1,1) NOT NULL,
  gestartet_utc DATETIME2(0)  NOT NULL CONSTRAINT DF_nzl_st DEFAULT(SYSUTCDATETIME()),
  quelle        NVARCHAR(24)  NOT NULL,
  gefunden      INT           NOT NULL CONSTRAINT DF_nzl_gef DEFAULT(0),
  neu           INT           NOT NULL CONSTRAINT DF_nzl_neu DEFAULT(0),
  erfolg        BIT           NOT NULL CONSTRAINT DF_nzl_ok DEFAULT(1),
  meldung       NVARCHAR(500) NULL,
  CONSTRAINT PK_neuzugang_lauf PRIMARY KEY (lauf_id)
);
GO
