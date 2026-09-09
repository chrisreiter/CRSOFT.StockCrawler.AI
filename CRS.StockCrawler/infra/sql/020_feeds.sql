SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* Feeds und ihre Artikel.

   Warum ein Artikel eine eigene Quelle wird und nicht ein Abschnitt seines
   Feeds: Die Anforderung lautet, bei jedem Lauf NUR das Neue einzubetten. Das
   geht nur, wenn ein Artikel eine eigene Kennung, einen eigenen Zeitstempel und
   einen eigenen Zustand hat. Wäre er Teil des Feeds, müsste bei jeder Meldung
   der gesamte Feed neu eingebettet werden — bei zwanzig Quellen und stündlichem
   Lauf wäre das die immer gleiche Arbeit für ein Ergebnis, das schon dasteht.

   Ein Artikel erbt die Säule seines Feeds und trägt dessen Kennung als
   parent_source_id. Damit greift die vorhandene Zerlegungs- und
   Einbettungsmaschinerie unverändert. */

IF COL_LENGTH('dbo.knowledge_source', 'parent_source_id') IS NULL
BEGIN
    ALTER TABLE dbo.knowledge_source ADD parent_source_id INT NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_ks_parent' AND object_id = OBJECT_ID('dbo.knowledge_source'))
BEGIN
    /* Der Zugriff kommt aus zwei Richtungen: „wie viele Artikel hat dieser
       Feed" und „welche Artikel sind noch nicht eingebettet". */
    CREATE INDEX IX_ks_parent
        ON dbo.knowledge_source (parent_source_id, added_utc DESC)
        INCLUDE (chunks, indexed_utc, title);
END;
GO

IF COL_LENGTH('dbo.knowledge_source', 'published_utc') IS NULL
BEGIN
    /* Der Zeitstempel der Meldung, nicht der des Abrufs.

       Ohne ihn lässt sich eine Aussage nicht in Bezug zu einer Kursbewegung
       setzen — und genau das ist der Zweck dieser Säule. Er kommt aus dem
       Feed und wird an jeden Abschnitt weitergereicht. */
    ALTER TABLE dbo.knowledge_source ADD published_utc DATETIME2(0) NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_ks_published' AND object_id = OBJECT_ID('dbo.knowledge_source'))
BEGIN
    CREATE INDEX IX_ks_published ON dbo.knowledge_source (pillar, published_utc DESC)
        WHERE published_utc IS NOT NULL;
END;
GO

IF COL_LENGTH('dbo.knowledge_source', 'region') IS NULL
BEGIN
    -- Woher die Quelle stammt. Erlaubt später, eine Frage auf einen Markt zu
    -- begrenzen, statt die halbe Welt mitzudurchsuchen.
    ALTER TABLE dbo.knowledge_source ADD region NVARCHAR(60) NULL;
END;
