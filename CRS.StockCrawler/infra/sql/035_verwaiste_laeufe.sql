/*  Läufe aufräumen, die ein Neustart mitten in der Arbeit erwischt hat.

    ------------------------------------------------------------------------

    Ein Eintrag in `ingest_run` bekommt sein `finished_utc` am Ende des Laufs.
    Wird der Prozess vorher beendet -- Neustart, Absturz, Ruhezustand --, bleibt
    er für immer ohne. Er sieht dann aus wie ein Lauf, der noch arbeitet.

    Am 26.08.2026 standen siebzehn solcher Einträge in der Tabelle, der älteste
    seit 5,5 Tagen. Für die Übersicht „Letzte Läufe" ist das nur unschön; für
    die Statusleiste unter der Kopfzeile wäre es falsch gewesen: Sie hätte einen
    fünf Tage alten Abruf als „läuft gerade" gemeldet.

    Zwei Stunden Frist: Der längste gemessene echte Schritt (update:1h über 600
    Werte) dauerte sieben Minuten. Alles jenseits von zwei Stunden ist mit
    Sicherheit abgebrochen und nicht etwa langsam.

    Die Prozedur läuft beim Anwendungsstart. Sie dort und nicht im Zeitplan
    aufzurufen ist Absicht: Genau der Start ist der Moment, in dem feststeht,
    dass kein früherer Lauf mehr arbeitet -- es gibt den Prozess nicht mehr, der
    ihn hätte fortsetzen können.
*/

CREATE OR ALTER PROCEDURE dbo.close_orphaned_runs
    @stunden INT = 2
AS
BEGIN
  SET NOCOUNT ON;

  UPDATE dbo.ingest_run
     SET finished_utc = started_utc,
         note = ISNULL(NULLIF(note, ''), '')
              + CASE WHEN ISNULL(note, '') = '' THEN '' ELSE ' — ' END
              + 'abgebrochen (Neustart), nachträglich geschlossen'
   WHERE finished_utc IS NULL
     AND started_utc < DATEADD(hour, -@stunden, SYSUTCDATETIME());

  SELECT @@ROWCOUNT AS geschlossen;
END
GO
