/*  048 -- Index auf dem Bewertungszeitpunkt.

    WARUM. Die Kachel „Prognose" der Startseite fragt die Trefferquote der
    bewerteten Live-Prognosen der letzten dreissig Tage. Der Filter steht auf
    `scored_at_utc`, und auf dieser Spalte lag kein Index. Gemessen am
    28.09.2026 auf dem EventMesh-DataCell-Backend:

      SELECT count(*) FROM forecast_score                            916 ms
      SELECT count(*) FROM forecast_score WHERE asset_id = N          2,7 ms
      SELECT count(*) FROM forecast_score WHERE scored_at_utc >= X  33.198 ms

    Die erste Zahl trifft den schnellen Aggregatpfad, die zweite einen Index.
    Die dritte hat beides nicht und laeuft durch alle 483.588 Zeilen -- fuer
    EINE Zahl. Die Kachel hat zwanzig Sekunden Frist; sie lief also
    zuverlaessig hinein, und die Startseite brauchte 24 Sekunden.

    Der Index nimmt die beiden gelesenen Masse gleich mit: Danach beantwortet
    er die Frage aus sich selbst, ohne die Zeilen anzufassen.

    NEBENBEI KORRIGIERT, denn es gehoert zur selben Ursache: `scored_at_utc`
    wurde bis heute gar nicht geschrieben. Die Spalte traegt ein `DEFAULT`,
    und auf diesem Backend greift ein Spaltenstandard beim INSERT nicht (siehe
    Migration 047 und CLAUDE.md). `ScoreAsync` und `ScoreCombinedAsync` binden
    den Zeitpunkt jetzt aus C#. Ein Index auf einer Spalte, die niemand
    fuellt, haette nichts genuetzt -- und die Kachel haette weiter nichts
    gefunden, nur schneller.

    Idempotent. Gegenstueck: infra/pgsql/017_forecast_score_bewertet.sql      */

IF NOT EXISTS (SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID('dbo.forecast_score')
                  AND name = 'IX_forecast_score_bewertet')
  CREATE INDEX IX_forecast_score_bewertet
      ON dbo.forecast_score (scored_at_utc)
     INCLUDE (direction_correct, abs_pct_error);
GO
