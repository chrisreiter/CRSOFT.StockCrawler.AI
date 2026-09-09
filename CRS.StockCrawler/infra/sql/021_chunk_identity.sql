SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* Abschnitte bekommen eine Kennung aus ihrem Inhalt.

   Bisher war `vector_id` ein NEWID(). Das machte jedes Schreiben zu einem
   Neuschreiben: Erst den alten Bestand löschen, dann den neuen anlegen. Zwei
   gleichzeitige Läufe auf derselben Quelle löschen beide und schreiben danach
   beide — das Ergebnis ist die doppelte Menge, ohne Fehlermeldung. Genau das
   ist passiert.

   Mit einer Kennung aus (Quelle, Nummer, Inhalt) ist Schreiben idempotent:
   Derselbe Abschnitt bekommt dieselbe Kennung, ein zweiter Lauf überschreibt
   ihn an Ort und Stelle. Duplikate werden unmöglich, statt nachträglich
   entfernt zu werden — und nebenbei erspart es das Einbetten aller Abschnitte,
   die sich nicht geändert haben. */

IF COL_LENGTH('dbo.knowledge_chunk', 'chunk_hash') IS NULL
BEGIN
    ALTER TABLE dbo.knowledge_chunk ADD chunk_hash VARBINARY(32) NULL;
END;
GO

/* Alter Bestand: Kennung nachtragen, damit der eindeutige Index greifen kann. */
UPDATE dbo.knowledge_chunk
   SET chunk_hash = HASHBYTES('SHA2_256',
        CONVERT(NVARCHAR(20), source_id) + N'|' +
        CONVERT(NVARCHAR(20), ordinal) + N'|' +
        LEFT(content, 3000))
 WHERE chunk_hash IS NULL;
GO

/* Dubletten aus den Läufen vor dieser Änderung entfernen — der jüngere
   Eintrag bleibt, weil er aus dem aktuellen Verfahren stammt. */
;WITH d AS (
    SELECT chunk_id,
           ROW_NUMBER() OVER (PARTITION BY source_id, ordinal
                              ORDER BY chunk_id DESC) AS rn
      FROM dbo.knowledge_chunk
)
DELETE FROM dbo.knowledge_chunk
 WHERE chunk_id IN (SELECT chunk_id FROM d WHERE rn > 1);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_kc_source_ordinal'
                 AND object_id = OBJECT_ID('dbo.knowledge_chunk'))
BEGIN
    /* Die eigentliche Absicherung: Ein Abschnitt ist durch Quelle und Nummer
       eindeutig bestimmt. Was auch immer zwei gleichzeitige Läufe versuchen —
       die Datenbank lässt keinen zweiten Eintrag zu. */
    CREATE UNIQUE INDEX UX_kc_source_ordinal
        ON dbo.knowledge_chunk (source_id, ordinal);
END;
