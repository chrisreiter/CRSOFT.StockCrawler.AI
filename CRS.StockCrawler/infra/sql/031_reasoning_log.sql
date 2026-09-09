/*  Journal der Reasoning-Antworten.

    Bis hierher lebte ein Gespräch mit dem Agenten ausschliesslich im
    Browser-Tab. Ein Neuladen löschte es, ein zweiter Rechner sah es nie, und
    eine Antwort, die drei Minuten gerechnet hat, war damit weg.

    ------------------------------------------------------------------------

    WARUM AUTOMATISCH UND NICHT AUF KNOPFDRUCK

    Ein „In Journal speichern"-Knopf setzt voraus, dass man vor dem Lesen
    weiss, ob die Antwort es wert ist. Das weiss man nie. Gespeichert wird
    deshalb jede Frage, und geräumt wird hinterher -- die Zeilen sind klein,
    und die teure Ressource ist die Rechenzeit, die schon verbraucht ist.

    ------------------------------------------------------------------------

    WARUM DIE WERKZEUGSPUR MITGESCHRIEBEN WIRD

    Ohne sie ist eine gespeicherte Antwort eine Behauptung. Der ganze Sinn der
    Säule ist, dass jede Zahl aus einem Werkzeugaufruf stammt und nachprüfbar
    bleibt; eine Ablage, die nur die Prosa behält, wirft genau das weg, was den
    Unterschied zu einem Sprachmodell ohne Anbindung ausmacht.

    Sie liegt als JSON in einer Spalte, nicht in einer eigenen Tabelle: Sie
    wird immer vollständig und immer zusammen mit der Antwort gelesen, nie
    einzeln abgefragt. Eine Kindtabelle wäre hier Aufwand ohne Nutzen.

    ------------------------------------------------------------------------

    WARUM MODELL, ENDPUNKT UND DAUER DAZUGEHÖREN

    Dieselbe Frage an dasselbe System liefert je nach Modell verschiedene
    Antworten -- gemessen: nemotron3:33b korrekt, qwen3-vl:4b inhaltlich
    falsch. Eine gespeicherte Antwort ohne die Angabe, WER sie gegeben hat, ist
    beim späteren Nachlesen nicht einzuordnen. Die Dauer steht daneben, weil
    sie den Unterschied zwischen lokaler CPU und gemieteter GPU sichtbar macht
    (251 s gegen 83 s bei sonst gleicher Frage).
*/

IF OBJECT_ID('dbo.reasoning_log', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.reasoning_log
    (
        log_id      INT IDENTITY(1,1) NOT NULL,
        asked_utc   DATETIME2(0)      NOT NULL CONSTRAINT DF_reasoning_log_asked
                                               DEFAULT (SYSUTCDATETIME()),

        /* Wer gefragt hat. NULL bleibt möglich: Ein Lauf ohne Anmeldung -- etwa
           aus einem Zeitplan -- soll die Zeile nicht verhindern. ON DELETE SET
           NULL, damit das Löschen eines Benutzers das Journal nicht mitnimmt;
           die Messung gehört der Anlage, nicht der Person. */
        user_id     INT               NULL,

        frage       NVARCHAR(MAX)     NOT NULL,
        antwort     NVARCHAR(MAX)     NOT NULL,

        modell      NVARCHAR(200)     NULL,
        endpunkt    NVARCHAR(200)     NULL,
        sekunden    DECIMAL(9,1)      NULL,
        runden      INT               NULL,

        /* Die Werkzeugaufrufe als JSON-Feld: [{name, argumente, ergebnis}, …] */
        werkzeuge   NVARCHAR(MAX)     NULL,

        /* Vom Benutzer angeheftet -- diese Zeilen überlebt das Aufräumen. */
        gemerkt     BIT               NOT NULL CONSTRAINT DF_reasoning_log_gemerkt
                                               DEFAULT (0),
        notiz       NVARCHAR(1000)    NULL,

        CONSTRAINT PK_reasoning_log PRIMARY KEY CLUSTERED (log_id),
        CONSTRAINT FK_reasoning_log_user FOREIGN KEY (user_id)
            REFERENCES dbo.app_user (user_id) ON DELETE SET NULL
    );
END
GO

/*  Gelesen wird immer „das Neueste zuerst". Ein gewöhnlicher Index, kein
    gefilterter: Ein gefilterter Index verlangt QUOTED_IDENTIFIER ON für JEDES
    Update auf der Tabelle und liess in 025 den Bewertungslauf mit Fehler 1934
    scheitern -- zur Laufzeit und mit einer Meldung, die den Zusammenhang nicht
    nennt.                                                                   */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_reasoning_log_zeit'
                 AND object_id = OBJECT_ID('dbo.reasoning_log'))
BEGIN
    CREATE INDEX IX_reasoning_log_zeit
        ON dbo.reasoning_log (asked_utc DESC)
        INCLUDE (modell, sekunden, gemerkt);
END
GO
