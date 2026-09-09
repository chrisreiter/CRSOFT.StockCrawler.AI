/*  StockCrawler – einheitliches Datenmodell für Aktien, ETFs/Fonds und Krypto.
    Idempotent: kann beliebig oft ausgeführt werden.                        */

IF DB_ID('stockcrawler') IS NULL
  EXEC('CREATE DATABASE stockcrawler');
GO
USE stockcrawler;
GO

/* ---------------------------------------------------------------- asset */
IF OBJECT_ID('dbo.asset') IS NULL
CREATE TABLE dbo.asset (
  asset_id         INT IDENTITY(1,1) PRIMARY KEY,
  asset_class      TINYINT       NOT NULL,   -- 0=stock 1=etf 2=crypto 3=index
  symbol           NVARCHAR(64)  NOT NULL,   -- kanonisch: AAPL, SPY, BTC-USD
  [name]           NVARCHAR(200) NULL,
  currency         NVARCHAR(10)  NULL,
  exchange         NVARCHAR(64)  NULL,
  provider         TINYINT       NOT NULL,   -- 0=yahoo 1=twelvedata 2=coingecko
  provider_symbol  NVARCHAR(96)  NOT NULL,   -- symbol wie ihn der Provider will
  market_cap       DECIMAL(38,2) NULL,
  market_cap_rank  INT           NULL,
  is_tracked       BIT           NOT NULL CONSTRAINT DF_asset_tracked DEFAULT(0),
  first_seen_utc   DATETIME2(0)  NOT NULL CONSTRAINT DF_asset_seen DEFAULT SYSUTCDATETIME(),
  updated_utc      DATETIME2(0)  NOT NULL CONSTRAINT DF_asset_upd  DEFAULT SYSUTCDATETIME()
);
GO
IF IndexProperty(OBJECT_ID('dbo.asset'),'UX_asset_class_symbol','IndexID') IS NULL
  CREATE UNIQUE INDEX UX_asset_class_symbol ON dbo.asset(asset_class, symbol);
GO
IF IndexProperty(OBJECT_ID('dbo.asset'),'IX_asset_tracked','IndexID') IS NULL
  CREATE INDEX IX_asset_tracked ON dbo.asset(is_tracked, asset_class) INCLUDE(symbol, market_cap_rank);
GO

/* ------------------------------------------------------------ price_bar */
/* Ein Tisch für alles: Aktie/ETF/Krypto × 1h/1d. Das ist der Kern der
   Vereinheitlichung – jede Analyse liest nur noch hier.                   */
IF OBJECT_ID('dbo.price_bar') IS NULL
CREATE TABLE dbo.price_bar (
  asset_id         INT           NOT NULL,
  interval_code    VARCHAR(3)    NOT NULL,   -- '1h' | '1d'
  ts_utc           DATETIME2(0)  NOT NULL,   -- Bar-Beginn, immer UTC
  [open]           DECIMAL(19,8) NULL,
  [high]           DECIMAL(19,8) NULL,
  [low]            DECIMAL(19,8) NULL,
  [close]          DECIMAL(19,8) NOT NULL,
  adj_close        DECIMAL(19,8) NULL,
  volume           DECIMAL(38,8) NULL,
  provider         TINYINT       NOT NULL,
  ingested_at_utc  DATETIME2(0)  NOT NULL CONSTRAINT DF_bar_ing DEFAULT SYSUTCDATETIME(),
  CONSTRAINT PK_price_bar PRIMARY KEY (asset_id, interval_code, ts_utc),
  CONSTRAINT FK_price_bar_asset FOREIGN KEY (asset_id) REFERENCES dbo.asset(asset_id)
);
GO
IF IndexProperty(OBJECT_ID('dbo.price_bar'),'IX_price_bar_scan','IndexID') IS NULL
  CREATE INDEX IX_price_bar_scan ON dbo.price_bar(interval_code, ts_utc) INCLUDE([close], asset_id);
GO

/* ---------------------------------------------------------- corp_action */
IF OBJECT_ID('dbo.corp_action') IS NULL
CREATE TABLE dbo.corp_action (
  asset_id        INT           NOT NULL,
  [date]          DATE          NOT NULL,
  action_type     TINYINT       NOT NULL,    -- 0=split 1=dividend
  ratio_n         DECIMAL(19,8) NULL,
  ratio_d         DECIMAL(19,8) NULL,
  amount          DECIMAL(19,8) NULL,
  provider        TINYINT       NOT NULL,
  ingested_at_utc DATETIME2(0)  NOT NULL CONSTRAINT DF_ca_ing DEFAULT SYSUTCDATETIME(),
  CONSTRAINT PK_corp_action PRIMARY KEY (asset_id, [date], action_type),
  CONSTRAINT FK_corp_action_asset FOREIGN KEY (asset_id) REFERENCES dbo.asset(asset_id)
);
GO

/* ----------------------------------------------------------- ingest_run */
IF OBJECT_ID('dbo.ingest_run') IS NULL
CREATE TABLE dbo.ingest_run (
  run_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
  job_name      NVARCHAR(100) NOT NULL,
  provider      TINYINT       NULL,
  started_utc   DATETIME2(0)  NOT NULL CONSTRAINT DF_run_start DEFAULT SYSUTCDATETIME(),
  finished_utc  DATETIME2(0)  NULL,
  ok_count      INT           NULL,
  err_count     INT           NULL,
  rows_written  INT           NULL,
  note          NVARCHAR(4000) NULL
);
GO
