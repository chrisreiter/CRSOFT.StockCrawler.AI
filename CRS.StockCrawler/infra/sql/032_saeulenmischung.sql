/*  Die Prognose aus allen Säulen — und der Beleg, welche sie getragen hat.

    ------------------------------------------------------------------------

    WARUM ZWEI ZAHLEN NEBENEINANDER

    `predicted_close` bleibt, was es ist: die Schätzung der ersten Säule allein.
    Daran hängt die Rückkopplung. `ScoringService` vergleicht sie mit dem
    eingetroffenen Kurs und verschiebt danach die Gewichte der fünf Teilmodelle
    in `model_weight`. Würde dort die gemischte Zahl stehen, lernte Säule 1 aus
    einem Fehler, den sie nicht gemacht hat -- die Komponenten erklärten die
    Vorhersage nicht mehr, und die Gewichte liefen ins Leere.

    `combined_close` ist die Mischung über alle Säulen. Sie ist das, was die
    Oberfläche zeigt und was der Nutzer meint, wenn er „die Prognose" sagt.

    Beide werden bewertet. Erst dadurch lässt sich die eigentliche Frage
    beantworten: Bringt das Mischen überhaupt etwas? Ohne die zweite Spalte
    wäre das eine Glaubensfrage.

    ------------------------------------------------------------------------

    WARUM DIE MISCHUNG MITGESCHRIEBEN WIRD

    `pillar_mix` hält fest, welche Säule mit welchem Anteil und mit welchem
    gemessenen Verdienst eingegangen ist. Ohne das ist die kombinierte Zahl
    nicht nachvollziehbar: Man sieht ein Ergebnis und weiss nicht, ob es von
    einer Säule mit Rückhalt kommt oder von vier ohne.

    Als JSON in einer Spalte, nicht als Kindtabelle: Es wird immer vollständig
    und immer zusammen mit der Prognose gelesen, nie einzeln abgefragt.
*/

IF COL_LENGTH('dbo.forecast', 'combined_close') IS NULL
BEGIN
    ALTER TABLE dbo.forecast ADD combined_close DECIMAL(18, 6) NULL;
END
GO

IF COL_LENGTH('dbo.forecast', 'combined_return') IS NULL
BEGIN
    ALTER TABLE dbo.forecast ADD combined_return FLOAT NULL;
END
GO

IF COL_LENGTH('dbo.forecast', 'pillar_mix') IS NULL
BEGIN
    ALTER TABLE dbo.forecast ADD pillar_mix NVARCHAR(2000) NULL;
END
GO

/*  Die Bewertung der gemischten Zahl -- getrennt von der der ersten Säule.

    Dieselben Spalten wie in `forecast_score`, damit sich beide Wege mit
    derselben Abfrage vergleichen lassen. Eine gemeinsame Tabelle mit einer
    Kennzeichnungsspalte wäre kürzer gewesen und hätte jede Auswertung um ein
    `WHERE` verlängert, das man vergessen kann.
*/
IF OBJECT_ID('dbo.forecast_score_combined', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.forecast_score_combined
    (
        forecast_id        BIGINT        NOT NULL,
        actual_close       DECIMAL(18,6) NOT NULL,
        actual_return      FLOAT         NOT NULL,
        abs_pct_error      FLOAT         NOT NULL,
        direction_correct  BIT           NULL,
        scored_at_utc      DATETIME2(0)  NOT NULL
            CONSTRAINT DF_fsc_scored DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_forecast_score_combined PRIMARY KEY CLUSTERED (forecast_id),
        CONSTRAINT FK_fsc_forecast FOREIGN KEY (forecast_id)
            REFERENCES dbo.forecast (forecast_id) ON DELETE CASCADE
    );
END
GO

/*  Der gemessene Rückhalt je Säule und Horizont.

    Hier steht, was eine Säule wert ist -- nicht, was sie von sich behauptet.
    Genau diese Trennung fehlte bisher: Die Säulengewichte in der Oberfläche
    waren eine Einstellung ohne Gegenprobe, und eine Säule ohne jeden Nachweis
    ging mit demselben Anteil ein wie die einzige mit gemessener Trefferquote.

    `skill` ist der Vorsprung gegenüber „der Kurs bleibt stehen", auf [0,1]
    gestaucht. 0 heisst: trägt nichts bei, unabhängig davon, wie hoch der
    Regler steht. Genau so gehört es sich -- ein Regler darf bestimmen, WIE VIEL
    von etwas Brauchbarem einfliesst, nicht, ob Unbrauchbares mitzählt.

    `n_obs` daneben, weil ein Skill aus drei Beobachtungen keiner ist.
*/
IF OBJECT_ID('dbo.pillar_skill', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.pillar_skill
    (
        pillar         VARCHAR(30)  NOT NULL,
        horizon_hours  INT          NOT NULL,
        /*  0 statt NULL für „über alle Werte".

            NULL wäre die ehrlichere Schreibweise, aber SQL Server lässt keine
            nullbare Spalte im Primärschlüssel zu (Fehler 8111). Die Alternative
            wäre ein eindeutiger Index statt eines Schlüssels gewesen -- dann
            hätte die Tabelle keinen, und ein MERGE bräuchte trotzdem eine
            eindeutige Bedingung. 0 ist keine gültige asset_id, also eindeutig. */
        asset_id       INT          NOT NULL
            CONSTRAINT DF_pillar_skill_asset DEFAULT (0),
        skill          FLOAT        NOT NULL,
        n_obs          INT          NOT NULL,
        detail         NVARCHAR(500) NULL,
        measured_utc   DATETIME2(0) NOT NULL
            CONSTRAINT DF_pillar_skill_utc DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_pillar_skill PRIMARY KEY CLUSTERED
            (pillar, horizon_hours, asset_id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_forecast_combined'
                 AND object_id = OBJECT_ID('dbo.forecast'))
BEGIN
    /*  Ein gewöhnlicher Index, kein gefilterter: Ein gefilterter verlangt
        QUOTED_IDENTIFIER ON für JEDES Update auf der Tabelle und liess in 025
        den Bewertungslauf mit Fehler 1934 scheitern -- zur Laufzeit und mit
        einer Meldung, die den Zusammenhang nicht nennt. */
    CREATE INDEX IX_forecast_combined
        ON dbo.forecast (target_ts_utc)
        INCLUDE (combined_close, combined_return);
END
GO
