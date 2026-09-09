/*  Analyse- und Prognose-Tabellen inkl. der Lernschleife.
    Idempotent.                                                            */
USE stockcrawler;
GO

/* ------------------------------------------------------------ pair_stat */
/* Ergebnis der Wechselwirkungs-Analyse: Korrelation + bester Lead/Lag.
   best_lag_bars > 0  =>  A läuft B voraus (A ist Frühindikator für B).    */
IF OBJECT_ID('dbo.pair_stat') IS NULL
CREATE TABLE dbo.pair_stat (
  asset_id_a     INT          NOT NULL,
  asset_id_b     INT          NOT NULL,
  interval_code  VARCHAR(3)   NOT NULL,
  window_bars    INT          NOT NULL,
  corr0          FLOAT        NOT NULL,   -- gleichzeitige Korrelation (lag 0)
  best_lag_bars  INT          NOT NULL,   -- Lag mit maximaler |Korrelation|
  best_lag_corr  FLOAT        NOT NULL,
  n_obs          INT          NOT NULL,
  computed_utc   DATETIME2(0) NOT NULL CONSTRAINT DF_ps_c DEFAULT SYSUTCDATETIME(),
  CONSTRAINT PK_pair_stat PRIMARY KEY (asset_id_a, asset_id_b, interval_code, window_bars),
  CONSTRAINT FK_ps_a FOREIGN KEY (asset_id_a) REFERENCES dbo.asset(asset_id),
  CONSTRAINT FK_ps_b FOREIGN KEY (asset_id_b) REFERENCES dbo.asset(asset_id)
);
GO
IF IndexProperty(OBJECT_ID('dbo.pair_stat'),'IX_ps_lead','IndexID') IS NULL
  CREATE INDEX IX_ps_lead ON dbo.pair_stat(asset_id_b, interval_code, best_lag_bars)
    INCLUDE(asset_id_a, best_lag_corr);
GO

/* ---------------------------------------------------------- crossing */
/* Erkannte Kreuzungen zweier normalisierter Kurven.                       */
IF OBJECT_ID('dbo.crossing') IS NULL
CREATE TABLE dbo.crossing (
  crossing_id    BIGINT IDENTITY(1,1) PRIMARY KEY,
  asset_id_a     INT          NOT NULL,
  asset_id_b     INT          NOT NULL,
  interval_code  VARCHAR(3)   NOT NULL,
  ts_utc         DATETIME2(0) NOT NULL,
  direction      TINYINT      NOT NULL,   -- 1 = A kreuzt B nach oben, 0 = nach unten
  spread_before  FLOAT        NOT NULL,
  spread_after   FLOAT        NOT NULL,
  detected_utc   DATETIME2(0) NOT NULL CONSTRAINT DF_cx_d DEFAULT SYSUTCDATETIME(),
  CONSTRAINT FK_cx_a FOREIGN KEY (asset_id_a) REFERENCES dbo.asset(asset_id),
  CONSTRAINT FK_cx_b FOREIGN KEY (asset_id_b) REFERENCES dbo.asset(asset_id)
);
GO
IF IndexProperty(OBJECT_ID('dbo.crossing'),'UX_crossing','IndexID') IS NULL
  CREATE UNIQUE INDEX UX_crossing ON dbo.crossing(asset_id_a, asset_id_b, interval_code, ts_utc);
GO

/* ------------------------------------------------------------- forecast */
IF OBJECT_ID('dbo.forecast') IS NULL
CREATE TABLE dbo.forecast (
  forecast_id      BIGINT IDENTITY(1,1) PRIMARY KEY,
  asset_id         INT           NOT NULL,
  horizon_hours    INT           NOT NULL,   -- 1,4,24,168,720 ...
  made_at_utc      DATETIME2(0)  NOT NULL,
  target_ts_utc    DATETIME2(0)  NOT NULL,
  base_close       DECIMAL(19,8) NOT NULL,   -- Kurs zum Prognosezeitpunkt
  predicted_close  DECIMAL(19,8) NOT NULL,
  predicted_return FLOAT         NOT NULL,   -- log-Return gegenüber base_close
  confidence       FLOAT         NOT NULL,   -- 0..1, aus bisheriger Trefferquote
  contributions    NVARCHAR(MAX) NULL,       -- JSON: welches Modell wie stark beitrug
  model_version    NVARCHAR(32)  NOT NULL,
  CONSTRAINT FK_fc_asset FOREIGN KEY (asset_id) REFERENCES dbo.asset(asset_id)
);
GO
IF IndexProperty(OBJECT_ID('dbo.forecast'),'UX_forecast','IndexID') IS NULL
  CREATE UNIQUE INDEX UX_forecast ON dbo.forecast(asset_id, horizon_hours, made_at_utc);
GO
/* offene, fällige Prognosen schnell finden */
IF IndexProperty(OBJECT_ID('dbo.forecast'),'IX_forecast_target','IndexID') IS NULL
  CREATE INDEX IX_forecast_target ON dbo.forecast(target_ts_utc) INCLUDE(asset_id, horizon_hours);
GO

/* -------------------------------------------------------- forecast_score */
/* Das "war ich richtig oder falsch" – wird nachträglich befüllt.          */
IF OBJECT_ID('dbo.forecast_score') IS NULL
CREATE TABLE dbo.forecast_score (
  forecast_id       BIGINT        NOT NULL PRIMARY KEY,
  actual_close      DECIMAL(19,8) NOT NULL,
  actual_return     FLOAT         NOT NULL,
  abs_pct_error     FLOAT         NOT NULL,
  direction_correct BIT           NOT NULL,
  scored_at_utc     DATETIME2(0)  NOT NULL CONSTRAINT DF_fs_s DEFAULT SYSUTCDATETIME(),
  CONSTRAINT FK_fs_fc FOREIGN KEY (forecast_id) REFERENCES dbo.forecast(forecast_id)
);
GO

/* ---------------------------------------------------------- model_weight */
/* Der adaptive Teil: je Asset × Horizont × Teilmodell ein Gewicht, das
   nach jedem Scoring anhand des tatsächlichen Fehlers nachgezogen wird.   */
IF OBJECT_ID('dbo.model_weight') IS NULL
CREATE TABLE dbo.model_weight (
  asset_id        INT          NOT NULL,
  horizon_hours   INT          NOT NULL,
  model_name      NVARCHAR(32) NOT NULL,
  weight          FLOAT        NOT NULL,
  n_obs           INT          NOT NULL CONSTRAINT DF_mw_n DEFAULT(0),
  mean_abs_pct_err FLOAT       NOT NULL CONSTRAINT DF_mw_e DEFAULT(0),
  hit_rate        FLOAT        NOT NULL CONSTRAINT DF_mw_h DEFAULT(0.5),
  updated_utc     DATETIME2(0) NOT NULL CONSTRAINT DF_mw_u DEFAULT SYSUTCDATETIME(),
  CONSTRAINT PK_model_weight PRIMARY KEY (asset_id, horizon_hours, model_name),
  CONSTRAINT FK_mw_asset FOREIGN KEY (asset_id) REFERENCES dbo.asset(asset_id)
);
GO

/* Einzelbeiträge je Teilmodell, damit das Scoring jedes Modell separat
   bewerten kann statt nur das Ensemble.                                   */
IF OBJECT_ID('dbo.forecast_component') IS NULL
CREATE TABLE dbo.forecast_component (
  forecast_id   BIGINT       NOT NULL,
  model_name    NVARCHAR(32) NOT NULL,
  predicted_return FLOAT     NOT NULL,
  weight        FLOAT        NOT NULL,
  CONSTRAINT PK_forecast_component PRIMARY KEY (forecast_id, model_name),
  CONSTRAINT FK_fcp_fc FOREIGN KEY (forecast_id) REFERENCES dbo.forecast(forecast_id)
);
GO
