/*  Anmeldung mit zwei Rollen.

    Bis hierher war die Anwendung ohne jede Zugangskontrolle. Das war für den
    Betrieb auf dem eigenen Rechner richtig und ist es in dem Moment nicht
    mehr, in dem sie öffentlich erreichbar wird.

    Die Tabellen app_user und app_session gab es bereits -- angelegt in
    016_app_state.sql, mit dem ausdrücklichen Gedanken, den Oberflächenzustand
    später an einen Benutzer binden zu können. Was fehlte, war alles zur
    Anmeldung selbst.

    ------------------------------------------------------------------------

    ZWEI ROLLEN, und die Trennung verläuft entlang der HTTP-Methode:

      admin  darf alles -- Benutzer anlegen, Läufe anstoßen, Daten holen.
      user   sieht denselben Inhalt, aber nur lesend. Keine Läufe.

    Die Durchsetzung hängt bewusst NICHT an einer Liste von Endpunkten,
    sondern an der Methode: GET, HEAD und OPTIONS sind erlaubt, alles andere
    nicht. Eine Liste müsste bei jedem neuen Endpunkt gepflegt werden, und
    vergisst man einen, ist es ein Loch. Die Methodenregel gilt auch für
    Endpunkte, die es heute noch nicht gibt.

    ------------------------------------------------------------------------

    KENNWÖRTER liegen als PBKDF2-HMAC-SHA256 mit 210.000 Runden und einem
    Salz je Benutzer. Das Format steht in der Spalte selbst, damit ein
    späterer Wechsel des Verfahrens keine Migration braucht:

        pbkdf2$sha256$210000$<salz base64>$<schluessel base64>

    Klartext wird nirgends gespeichert und nirgends protokolliert.           */
USE stockcrawler;
GO

IF COL_LENGTH('dbo.app_user', 'password_hash') IS NULL
  ALTER TABLE dbo.app_user ADD password_hash NVARCHAR(400) NULL;
GO

IF COL_LENGTH('dbo.app_user', 'role') IS NULL
  ALTER TABLE dbo.app_user ADD role NVARCHAR(16) NOT NULL
    CONSTRAINT DF_au_role DEFAULT('user');
GO

IF COL_LENGTH('dbo.app_user', 'is_active') IS NULL
  ALTER TABLE dbo.app_user ADD is_active BIT NOT NULL
    CONSTRAINT DF_au_active DEFAULT(1);
GO

IF COL_LENGTH('dbo.app_user', 'last_login_utc') IS NULL
  ALTER TABLE dbo.app_user ADD last_login_utc DATETIME2(0) NULL;
GO

/*  Fehlversuche zählen und sperren.

    Ohne das ist ein Kennwort auf einem öffentlichen Server nur so gut wie
    die Zahl der Versuche, die jemand in einer Stunde schafft -- und das sind
    Millionen. Fünf Fehlversuche sperren fünfzehn Minuten; ein Erfolg setzt
    den Zähler zurück.                                                       */
IF COL_LENGTH('dbo.app_user', 'failed_logins') IS NULL
  ALTER TABLE dbo.app_user ADD failed_logins INT NOT NULL
    CONSTRAINT DF_au_failed DEFAULT(0);
GO

IF COL_LENGTH('dbo.app_user', 'locked_until_utc') IS NULL
  ALTER TABLE dbo.app_user ADD locked_until_utc DATETIME2(0) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_app_user_role')
  ALTER TABLE dbo.app_user ADD CONSTRAINT CK_app_user_role
    CHECK (role IN (N'admin', N'user'));
GO

/*  Eine Sitzung braucht ein Ablaufdatum.

    Bisher lebte sie ein Jahr, weil sie nur einen Oberflächenzustand hielt.
    Sobald daran eine Anmeldung hängt, ist das zu lang: Ein gestohlenes
    Cookie wäre ein Jahr gültig. Vierzehn Tage, bei jedem Zugriff verlängert. */
IF COL_LENGTH('dbo.app_session', 'expires_utc') IS NULL
  ALTER TABLE dbo.app_session ADD expires_utc DATETIME2(0) NULL;
GO

IF IndexProperty(OBJECT_ID('dbo.app_user'), 'UX_app_user_login', 'IndexID') IS NULL
 AND NOT EXISTS (SELECT 1 FROM sys.indexes
                  WHERE object_id = OBJECT_ID('dbo.app_user') AND name = 'UQ_app_user_login')
  CREATE UNIQUE INDEX UX_app_user_login ON dbo.app_user(login);
GO

/*  Abgelaufene Sitzungen wegräumen. Der vorhandene purge_stale_sessions
    räumt nach Untätigkeit; hier geht es um den harten Ablauf.               */
CREATE OR ALTER PROCEDURE dbo.purge_expired_sessions
AS
BEGIN
  SET NOCOUNT ON;

  DELETE a
    FROM dbo.app_state AS a
   WHERE a.owner_kind = 0
     AND EXISTS (SELECT 1 FROM dbo.app_session s
                  WHERE CAST(s.session_key AS NVARCHAR(64)) = a.owner_key
                    AND s.expires_utc IS NOT NULL
                    AND s.expires_utc < SYSUTCDATETIME());

  DELETE FROM dbo.app_session
   WHERE expires_utc IS NOT NULL AND expires_utc < SYSUTCDATETIME();

  SELECT @@ROWCOUNT AS entfernt;
END
GO
