SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* Säulen „Knowledge" und „Semantik": Quellen, Abschnitte und ihre Einbettung.

   Warum die Abschnitte in SQL stehen und nur die Vektoren in Qdrant:
   Qdrant ist eine Vektordatenbank, kein Dokumentenspeicher. Den Volltext dort
   abzulegen hieße, zwei Systeme zur Wahrheit über denselben Text zu machen —
   und beim ersten Neuaufbau der Sammlung wäre er weg. Hier liegt der Text,
   dort die Nachbarschaft; verbunden über dieselbe Kennung.

   Idempotent: mehrfach ausführbar. */

IF OBJECT_ID('dbo.knowledge_source', 'U') IS NULL
BEGIN
    /* Eine Quelle: hochgeladene Datei ODER beobachtete Adresse.

       Beide Säulen teilen sich diese Tabelle, weil sie dasselbe Problem lösen
       — Text hereinholen, zerlegen, einbetten, durchsuchbar machen. Sie
       getrennt zu führen hieße, dieselbe Zerlegung zweimal zu schreiben und
       beim nächsten Modellwechsel zweimal zu ändern. Unterschieden wird über
       `kind`. */
    CREATE TABLE dbo.knowledge_source (
        source_id       INT IDENTITY(1,1) PRIMARY KEY,
        kind            VARCHAR(12)    NOT NULL,   -- 'file' | 'web'
        pillar          VARCHAR(12)    NOT NULL,   -- 'knowledge' | 'semantic'
        title           NVARCHAR(400)  NOT NULL,
        origin          NVARCHAR(1000) NOT NULL,   -- Dateiname oder Adresse

        /* Eindeutigkeit ueber die Pruefsumme statt ueber die Adresse selbst.
           Ein eindeutiger Index auf NVARCHAR(1000) waere 2000 Byte breit und
           damit ueber der Grenze von 1700 -- und die Adresse zu kuerzen ist
           keine Loesung, denn lange Adressen sind gerade bei Kanaelen die
           Regel. */
        origin_hash     AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', origin)) PERSISTED,
        content_type    VARCHAR(80)    NULL,
        bytes           BIGINT         NOT NULL CONSTRAINT DF_ks_bytes DEFAULT 0,
        added_utc       DATETIME2(0)   NOT NULL CONSTRAINT DF_ks_added DEFAULT SYSUTCDATETIME(),
        indexed_utc     DATETIME2(0)   NULL,
        last_checked_utc DATETIME2(0)  NULL,

        /* Nur aktive Quellen werden abgefragt und durchsucht. Löschen wäre die
           schlechtere Voreinstellung: Die Abschnitte hängen daran, und wer eine
           Quelle versehentlich entfernt, verliert die Einbettung mit. */
        active          BIT            NOT NULL CONSTRAINT DF_ks_active DEFAULT 1,

        /* Wie oft nachgesehen wird, in Minuten. Nur für 'web' sinnvoll. */
        poll_minutes    INT            NULL,

        chunks          INT            NOT NULL CONSTRAINT DF_ks_chunks DEFAULT 0,
        status          NVARCHAR(400)  NULL,

        /* Prüfsumme des Inhalts. Verhindert, dass dieselbe Seite bei jedem Lauf
           erneut eingebettet wird, obwohl sich nichts geändert hat — das kostet
           sonst bei jedem Durchgang Rechenzeit für nichts. */
        content_hash    VARCHAR(64)    NULL
    );

    CREATE INDEX IX_ks_pillar ON dbo.knowledge_source (pillar, active, added_utc DESC);
    CREATE UNIQUE INDEX UX_ks_origin ON dbo.knowledge_source (pillar, origin_hash);
END;

IF OBJECT_ID('dbo.knowledge_chunk', 'U') IS NULL
BEGIN
    /* Ein Abschnitt Text mit seiner Stelle in der Quelle.

       Die Abschnittsgrenzen überlappen bewusst: Eine Aussage, die genau auf
       einer Grenze steht, wäre sonst in beiden Hälften unvollständig und in
       keiner auffindbar. */
    CREATE TABLE dbo.knowledge_chunk (
        chunk_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
        source_id       INT           NOT NULL,
        ordinal         INT           NOT NULL,
        page_from       INT           NULL,
        page_to         INT           NULL,
        content         NVARCHAR(MAX) NOT NULL,
        tokens          INT           NOT NULL CONSTRAINT DF_kc_tokens DEFAULT 0,

        /* Zeitliche Kennung, wenn der Text eine trägt — Meldungsdatum,
           Beitragsdatum. Ohne sie ließe sich eine Aussage nicht in Bezug zu
           einer Kursbewegung setzen. */
        occurred_utc    DATETIME2(0)  NULL,

        embedded_utc    DATETIME2(0)  NULL,
        vector_id       UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_kc_vec DEFAULT NEWID(),

        CONSTRAINT FK_kc_source FOREIGN KEY (source_id)
            REFERENCES dbo.knowledge_source (source_id) ON DELETE CASCADE
    );

    CREATE INDEX IX_kc_source ON dbo.knowledge_chunk (source_id, ordinal);
    CREATE UNIQUE INDEX UX_kc_vector ON dbo.knowledge_chunk (vector_id);
    CREATE INDEX IX_kc_occurred ON dbo.knowledge_chunk (occurred_utc) WHERE occurred_utc IS NOT NULL;
END;
