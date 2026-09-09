CREATE TABLE dbo.asset (
  asset_id        INT IDENTITY(1,1) PRIMARY KEY,
  vendor_key      NVARCHAR(128) NOT NULL,       -- z.B. "AAPL.US" oder "bitcoin" (coingecko_id)
  symbol          NVARCHAR(50)  NULL,           -- Anzeige/Kürzel (BTC, AAPL)
  [name]          NVARCHAR(200) NULL,
  asset_type      TINYINT       NOT NULL,       -- 0=stock,1=crypto,2=index,3=etf
  exchange        NVARCHAR(50)  NULL,           -- z.B. NASDAQ, XETRA, bei Krypto NULL
  source_system   TINYINT       NOT NULL,       -- 0=eod,1=coingecko
  created_at_utc  DATETIME2(0)  NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE UNIQUE INDEX UX_asset_vendor ON dbo.asset(vendor_key, source_system);

CREATE TABLE dbo.price_daily (
  asset_id        INT           NOT NULL,
  [date]          DATE          NOT NULL,
  [open]          DECIMAL(19,8) NULL,
  [high]          DECIMAL(19,8) NULL,
  [low]           DECIMAL(19,8) NULL,
  [close]         DECIMAL(19,8) NOT NULL,
  adj_close       DECIMAL(19,8) NULL,          -- bei Krypto NULL
  volume          DECIMAL(38,8) NULL,
  source_system   TINYINT       NOT NULL,      -- 0=eod,1=coingecko
  ingested_at_utc DATETIME2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
  CONSTRAINT PK_price_daily PRIMARY KEY (asset_id, [date], source_system),
  CONSTRAINT FK_price_daily_asset FOREIGN KEY (asset_id) REFERENCES dbo.asset(asset_id)
);

CREATE INDEX IX_price_daily_asset_date ON dbo.price_daily(asset_id, [date]);

CREATE TABLE dbo.corp_action (
  asset_id        INT           NOT NULL,
  [date]          DATE          NOT NULL,
  action_type     TINYINT       NOT NULL,      -- 0=split,1=dividend
  ratio_n         DECIMAL(19,8) NULL,          -- z.B. 2
  ratio_d         DECIMAL(19,8) NULL,          -- z.B. 1
  amount          DECIMAL(19,8) NULL,          -- Dividend amount
  source_system   TINYINT       NOT NULL,      -- 0=eod
  ingested_at_utc DATETIME2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
  CONSTRAINT PK_corp_action PRIMARY KEY (asset_id, [date], action_type, source_system),
  CONSTRAINT FK_corp_action_asset FOREIGN KEY (asset_id) REFERENCES dbo.asset(asset_id)
);

CREATE TABLE dbo.ingest_run (
  run_id          BIGINT IDENTITY(1,1) PRIMARY KEY,
  source_system   TINYINT      NOT NULL,
  job_name        NVARCHAR(100) NOT NULL,
  started_utc     DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
  finished_utc    DATETIME2(0) NULL,
  ok_count        INT          NULL,
  err_count       INT          NULL,
  note            NVARCHAR(4000) NULL
);
