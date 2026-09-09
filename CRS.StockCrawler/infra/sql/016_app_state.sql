/*  Sitzungen, Benutzer und gemerkter Oberflächenzustand.

    Warum drei Tabellen und nicht eine:

    Heute gibt es noch keine Anmeldung — der Zustand hängt an einer Sitzung,
    die über ein Cookie wiedererkannt wird. Sobald es Benutzer gibt, soll
    derselbe Zustand an den Benutzer gebunden werden können, ohne die
    Datenhaltung umzubauen und ohne das Gemerkte zu verlieren.

    Deshalb trägt app_state einen Eigentümer aus zwei Teilen: die Art (Sitzung
    oder Benutzer) und den Schlüssel. Beim Anmelden werden die Zeilen der
    Sitzung auf den Benutzer umgeschrieben, mehr ist nicht nötig. Beim Lesen
    gilt der Benutzerzustand, die Sitzung dient als Rückfallebene — so behält
    jemand seine Einstellungen an einem neuen Gerät, verliert aber nicht, was
    er vor der Anmeldung eingestellt hatte.

    Der Zustand liegt als JSON in einer Spalte, nicht in einzelnen Feldern.
    Das ist hier die richtige Wahl: Die Oberfläche ändert sich häufig, und
    jedes neue Filterfeld hätte sonst eine Migration zur Folge. Abgefragt wird
    nach diesem Inhalt ohnehin nie — er wird nur als Ganzes geschrieben und
    als Ganzes gelesen.                                                     */
USE stockcrawler;
GO

IF OBJECT_ID('dbo.app_user') IS NULL
CREATE TABLE dbo.app_user (
  user_id      INT           IDENTITY(1,1) NOT NULL,
  login        NVARCHAR(128) NOT NULL,
  display_name NVARCHAR(128) NULL,
  created_utc  DATETIME2(0)  NOT NULL CONSTRAINT DF_au_created DEFAULT(SYSUTCDATETIME()),
  CONSTRAINT PK_app_user PRIMARY KEY (user_id),
  CONSTRAINT UQ_app_user_login UNIQUE (login)
);
GO

IF OBJECT_ID('dbo.app_session') IS NULL
CREATE TABLE dbo.app_session (
  session_key   UNIQUEIDENTIFIER NOT NULL,
  user_id       INT              NULL,
  created_utc   DATETIME2(0)     NOT NULL CONSTRAINT DF_as_created DEFAULT(SYSUTCDATETIME()),
  last_seen_utc DATETIME2(0)     NOT NULL CONSTRAINT DF_as_seen    DEFAULT(SYSUTCDATETIME()),
  CONSTRAINT PK_app_session PRIMARY KEY (session_key),
  CONSTRAINT FK_app_session_user FOREIGN KEY (user_id) REFERENCES dbo.app_user(user_id)
);
GO

/*  owner_kind: 0 = Sitzung, 1 = Benutzer.
    area: der Bereich der Oberfläche, etwa 'charts' oder 'pillars'. Getrennt
    abgelegt, damit ein Bereich geschrieben werden kann, ohne die übrigen
    mitzuschicken — sonst überschriebe ein Reiter beim Speichern das, was ein
    anderer gerade geändert hat.                                            */
IF OBJECT_ID('dbo.app_state') IS NULL
CREATE TABLE dbo.app_state (
  owner_kind  TINYINT        NOT NULL,
  owner_key   NVARCHAR(64)   NOT NULL,
  area        NVARCHAR(64)   NOT NULL,
  payload     NVARCHAR(MAX)  NOT NULL,
  updated_utc DATETIME2(0)   NOT NULL CONSTRAINT DF_ast_updated DEFAULT(SYSUTCDATETIME()),
  CONSTRAINT PK_app_state PRIMARY KEY (owner_kind, owner_key, area),
  CONSTRAINT CK_app_state_kind CHECK (owner_kind IN (0, 1)),
  CONSTRAINT CK_app_state_json CHECK (ISJSON(payload) = 1)
);
GO

/*  Aufräumen verwaister Sitzungen. Ohne das wächst die Tabelle mit jedem
    Besucher, der seine Cookies löscht, und niemand merkt es je.            */
IF OBJECT_ID('dbo.purge_stale_sessions') IS NOT NULL
  DROP PROCEDURE dbo.purge_stale_sessions;
GO

CREATE PROCEDURE dbo.purge_stale_sessions
  @days INT = 180
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @cut DATETIME2(0) = DATEADD(DAY, -@days, SYSUTCDATETIME());

  -- Angemeldete Sitzungen bleiben: dort haengt der Zustand am Benutzer.
  DELETE s
  FROM dbo.app_session AS s
  WHERE s.user_id IS NULL
    AND s.last_seen_utc < @cut;

  DELETE a
  FROM dbo.app_state AS a
  WHERE a.owner_kind = 0
    AND NOT EXISTS (SELECT 1 FROM dbo.app_session AS s
                    WHERE CAST(s.session_key AS NVARCHAR(64)) = a.owner_key);
END;
GO
