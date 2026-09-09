/* Kurvendiskussion: gefundene Stellen und ihre Verknüpfungen.

   Warum die Ereignisse überhaupt abgelegt werden, statt sie bei jeder Abfrage
   neu zu rechnen: Ein Durchgang über 300 Werte mit je 6.000 Bars ist keine
   Sekundensache, und die Antwort hängt an einem halben Dutzend Einstellungen.
   Wer eine Rasteransicht mit Blättern will, muss über einen festen Bestand
   blättern können — nicht über etwas, das sich zwischen Seite 3 und Seite 4
   neu berechnet.

   Idempotent: mehrfach ausführbar. */

IF OBJECT_ID('dbo.curve_run', 'U') IS NULL
BEGIN
    /* Ein Durchgang mit seinen Einstellungen.

       Ohne diese Tabelle wäre ein Ereignis eine Behauptung ohne Herkunft: Bei
       halbem Fenster und halber Schwelle findet dasselbe Verfahren andere
       Stellen, und man sähe den Unterschied nicht mehr an. */
    CREATE TABLE dbo.curve_run (
        run_id          INT IDENTITY(1,1) PRIMARY KEY,
        started_utc     DATETIME2(0)  NOT NULL CONSTRAINT DF_curve_run_started DEFAULT SYSUTCDATETIME(),
        finished_utc    DATETIME2(0)  NULL,
        interval_code   VARCHAR(4)    NOT NULL,
        from_utc        DATETIME2(0)  NULL,
        to_utc          DATETIME2(0)  NULL,
        half_window     INT           NOT NULL,
        causal          BIT           NOT NULL,
        ref_window      INT           NOT NULL,
        min_z           FLOAT         NOT NULL,
        refractory      INT           NOT NULL,
        min_severity    FLOAT         NOT NULL,
        assets          INT           NOT NULL CONSTRAINT DF_curve_run_assets DEFAULT 0,
        events          INT           NOT NULL CONSTRAINT DF_curve_run_events DEFAULT 0,
        note            NVARCHAR(400) NULL
    );
END;

IF OBJECT_ID('dbo.curve_event', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.curve_event (
        curve_event_id  BIGINT IDENTITY(1,1) PRIMARY KEY,
        run_id          INT           NOT NULL,
        asset_id        INT           NOT NULL,
        ts_utc          DATETIME2(0)  NOT NULL,
        event_type      VARCHAR(24)   NOT NULL,
        sign            SMALLINT      NOT NULL,
        severity        FLOAT         NOT NULL,
        close_price     DECIMAL(18,6) NOT NULL,
        smoothed        DECIMAL(18,6) NOT NULL,
        slope           FLOAT         NOT NULL,
        curvature       FLOAT         NOT NULL
    );

    /* Der Zugriff kommt aus der Rasteransicht: nach Stufe sortiert, gefiltert
       nach Wert, Art und Zeitraum. Der führende run_id hält die Durchgänge
       auseinander — ohne ihn mischt eine Abfrage zwei Parametersätze. */
    CREATE INDEX IX_curve_event_run_sev
        ON dbo.curve_event (run_id, severity DESC)
        INCLUDE (asset_id, ts_utc, event_type, sign, close_price);

    CREATE INDEX IX_curve_event_asset_ts
        ON dbo.curve_event (run_id, asset_id, ts_utc);

    CREATE INDEX IX_curve_event_type_ts
        ON dbo.curve_event (run_id, event_type, ts_utc);
END;

IF OBJECT_ID('dbo.curve_link', 'U') IS NULL
BEGIN
    /* Werte, deren Ereignisse auffällig oft dicht beieinanderliegen.

       `lift` ist das Verhältnis von beobachtet zu erwartet — ohne die Erwartung
       gewännen immer die Werte mit den meisten Ereignissen. `lead_share` und
       `median_lag_bars` sind die einzigen Spalten, die eine RICHTUNG behaupten;
       alles andere sagt nur, dass etwas gemeinsam auftritt. */
    CREATE TABLE dbo.curve_link (
        curve_link_id   BIGINT IDENTITY(1,1) PRIMARY KEY,
        run_id          INT          NOT NULL,
        asset_a         INT          NOT NULL,
        asset_b         INT          NOT NULL,
        type_a          VARCHAR(24)  NOT NULL,
        type_b          VARCHAR(24)  NOT NULL,
        pairs           INT          NOT NULL,
        expected        FLOAT        NOT NULL,
        lift            FLOAT        NOT NULL,
        median_lag_bars FLOAT        NOT NULL,
        lead_share      FLOAT        NOT NULL,
        mean_severity   FLOAT        NOT NULL
    );

    CREATE INDEX IX_curve_link_run_lift
        ON dbo.curve_link (run_id, lift DESC)
        INCLUDE (asset_a, asset_b, type_a, type_b, pairs, median_lag_bars, lead_share);
END;

/* Nachtrag: Zugriff je Wert.

   Die Abfrage „welche Verknüpfungen betreffen diesen Wert" lief in einen
   Zeitüberlauf. Der vorhandene Index steht auf (run_id, lift DESC) und hilft
   dabei nicht: Das ODER über asset_a und asset_b zwingt zum vollständigen
   Durchgang mit anschließender Sortierung.

   Zwei Indizes statt eines zusammengesetzten, weil die beiden Spalten in einem
   ODER stehen — der Optimierer kann sie dann einzeln nutzen und die Ergebnisse
   vereinigen. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_curve_link_a' AND object_id = OBJECT_ID('dbo.curve_link'))
BEGIN
    CREATE INDEX IX_curve_link_a ON dbo.curve_link (run_id, asset_a, lift DESC)
        INCLUDE (asset_b, type_a, type_b, pairs, expected, median_lag_bars, lead_share, mean_severity);

    CREATE INDEX IX_curve_link_b ON dbo.curve_link (run_id, asset_b, lift DESC)
        INCLUDE (asset_a, type_a, type_b, pairs, expected, median_lag_bars, lead_share, mean_severity);
END;
