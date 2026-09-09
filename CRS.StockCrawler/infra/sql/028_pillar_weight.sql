/*  Säulengewichte dauerhaft ablegen.

    Bisher lagen sie im Sitzungszustand (`app_state`, owner_kind 0). Das ist
    der falsche Ort, und zwar aus einem einfachen Grund: Sie ändern die
    Prognose. Wer den Browser wechselt oder seine Cookies löscht, bekam eine
    Anwendung, in der jede Säule mit null gewichtet ist — und die Tagesübersicht
    meldete das als Zustand des Systems statt als fehlende Eingabe.

    Filtereinstellungen dürfen an einer Sitzung hängen. Etwas, das die Zahlen
    im Diagramm verändert, nicht.                                            */
USE stockcrawler;
GO

IF OBJECT_ID('dbo.pillar_weight') IS NULL
CREATE TABLE dbo.pillar_weight (
  pillar      NVARCHAR(32) NOT NULL,
  weight      INT          NOT NULL,
  updated_utc DATETIME2(0) NOT NULL CONSTRAINT DF_pw_upd DEFAULT(SYSUTCDATETIME()),
  begruendung NVARCHAR(400) NULL,
  CONSTRAINT PK_pillar_weight PRIMARY KEY (pillar),
  CONSTRAINT CK_pw_weight CHECK (weight BETWEEN 0 AND 100)
);
GO

/*  Erstbelegung. Sie ist begründet und nicht geraten:

    - `learning` bekommt das meiste, weil es als einziges Verfahren überhaupt
      eine gemessene Trefferquote über dem Münzwurf zeigt (52,4 % auf einer
      Stunde) -- auch wenn sie die Kosten nicht deckt.
    - `deep` steht bewusst nicht auf null, obwohl kein Band trägt: Die Mischung
      gewichtet ohnehin mit Gewicht MAL Verdienst, und der Verdienst ist null.
      Eine Null hier verstellte den Blick darauf, dass das Gewicht wirkt und
      der Verdienst fehlt.
    - `math`, `flow`, `knowledge`, `semantic` liefern keinen eigenen
      Zahlenbeitrag zur Prognose. Ihr Gewicht wirkt heute nur auf die
      Gewichtung der Befunde in der Tagesübersicht.                          */
MERGE dbo.pillar_weight AS t
USING (VALUES
  (N'learning',  50, N'Einziges Verfahren mit gemessener Trefferquote über dem Münzwurf.'),
  (N'math',      20, N'Beschreibt zuverlässig, prognostiziert nicht — Gewicht wirkt auf die Befundauswahl.'),
  (N'deep',      10, N'Kein Band schlägt die Drift; die Mischung setzt den Verdienst deshalb auf null.'),
  (N'flow',      10, N'Kein eigener Zahlenbeitrag zur Prognose.'),
  (N'knowledge',  5, N'Kein eigener Zahlenbeitrag zur Prognose.'),
  (N'semantic',   5, N'Kein eigener Zahlenbeitrag zur Prognose.')
) AS s(pillar, weight, begruendung)
   ON t.pillar = s.pillar
 WHEN NOT MATCHED THEN
   INSERT (pillar, weight, begruendung) VALUES (s.pillar, s.weight, s.begruendung);
GO
