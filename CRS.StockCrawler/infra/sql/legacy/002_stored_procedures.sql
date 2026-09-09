-- Stored Procedures for Market Data Ingestion

USE MarketData;
GO

-- Upsert asset procedure
CREATE OR ALTER PROCEDURE dbo.upsert_asset
  @vendor_key NVARCHAR(128),
  @source_system TINYINT,
  @symbol NVARCHAR(50) = NULL,
  @name NVARCHAR(200) = NULL,
  @asset_type TINYINT,
  @exchange NVARCHAR(50) = NULL
AS
BEGIN
  SET NOCOUNT ON;
  
  MERGE dbo.asset AS t
  USING (SELECT @vendor_key AS vendor_key, @source_system AS source_system) AS s
  ON (t.vendor_key = s.vendor_key AND t.source_system = s.source_system)
  WHEN MATCHED THEN 
    UPDATE SET 
      symbol = COALESCE(@symbol, t.symbol),
      [name] = COALESCE(@name, t.[name]),
      asset_type = @asset_type,
      exchange = COALESCE(@exchange, t.exchange)
  WHEN NOT MATCHED THEN
    INSERT (vendor_key, symbol, [name], asset_type, exchange, source_system)
    VALUES (@vendor_key, @symbol, @name, @asset_type, @exchange, @source_system);
  
  SELECT asset_id FROM dbo.asset WHERE vendor_key = @vendor_key AND source_system = @source_system;
END
GO

-- Create staging table for bulk price inserts
CREATE OR ALTER PROCEDURE dbo.create_price_staging_table
AS
BEGIN
  IF OBJECT_ID('tempdb..#price_daily_stage') IS NOT NULL
    DROP TABLE #price_daily_stage;
    
  CREATE TABLE #price_daily_stage (
    asset_id        INT           NOT NULL,
    [date]          DATE          NOT NULL,
    [open]          DECIMAL(19,8) NULL,
    [high]          DECIMAL(19,8) NULL,
    [low]           DECIMAL(19,8) NULL,
    [close]         DECIMAL(19,8) NOT NULL,
    adj_close       DECIMAL(19,8) NULL,
    volume          DECIMAL(38,8) NULL,
    source_system   TINYINT       NOT NULL
  );
END
GO

-- Merge staged prices into main table
CREATE OR ALTER PROCEDURE dbo.merge_staged_prices
AS
BEGIN
  SET NOCOUNT ON;
  
  MERGE dbo.price_daily AS t
  USING #price_daily_stage AS s
  ON (t.asset_id = s.asset_id AND t.[date] = s.[date] AND t.source_system = s.source_system)
  WHEN MATCHED THEN
    UPDATE SET
      [open] = s.[open],
      [high] = s.[high],
      [low] = s.[low],
      [close] = s.[close],
      adj_close = s.adj_close,
      volume = s.volume,
      ingested_at_utc = SYSUTCDATETIME()
  WHEN NOT MATCHED THEN
    INSERT (asset_id, [date], [open], [high], [low], [close], adj_close, volume, source_system)
    VALUES (s.asset_id, s.[date], s.[open], s.[high], s.[low], s.[close], s.adj_close, s.volume, s.source_system);
    
  SELECT @@ROWCOUNT AS affected_rows;
END
GO

-- Upsert corporate action
CREATE OR ALTER PROCEDURE dbo.upsert_corp_action
  @asset_id INT,
  @date DATE,
  @action_type TINYINT,
  @ratio_n DECIMAL(19,8) = NULL,
  @ratio_d DECIMAL(19,8) = NULL,
  @amount DECIMAL(19,8) = NULL,
  @source_system TINYINT
AS
BEGIN
  SET NOCOUNT ON;
  
  MERGE dbo.corp_action AS t
  USING (SELECT @asset_id AS asset_id, @date AS [date], @action_type AS action_type, @source_system AS source_system) AS s
  ON (t.asset_id = s.asset_id AND t.[date] = s.[date] AND t.action_type = s.action_type AND t.source_system = s.source_system)
  WHEN MATCHED THEN
    UPDATE SET
      ratio_n = @ratio_n,
      ratio_d = @ratio_d,
      amount = @amount,
      ingested_at_utc = SYSUTCDATETIME()
  WHEN NOT MATCHED THEN
    INSERT (asset_id, [date], action_type, ratio_n, ratio_d, amount, source_system)
    VALUES (@asset_id, @date, @action_type, @ratio_n, @ratio_d, @amount, @source_system);
END
GO

-- Get or create ingest run
CREATE OR ALTER PROCEDURE dbo.start_ingest_run
  @source_system TINYINT,
  @job_name NVARCHAR(100)
AS
BEGIN
  SET NOCOUNT ON;
  
  INSERT INTO dbo.ingest_run (source_system, job_name)
  VALUES (@source_system, @job_name);
  
  SELECT SCOPE_IDENTITY() AS run_id;
END
GO

-- Complete ingest run
CREATE OR ALTER PROCEDURE dbo.complete_ingest_run
  @run_id BIGINT,
  @ok_count INT = 0,
  @err_count INT = 0,
  @note NVARCHAR(4000) = NULL
AS
BEGIN
  SET NOCOUNT ON;
  
  UPDATE dbo.ingest_run
  SET finished_utc = SYSUTCDATETIME(),
      ok_count = @ok_count,
      err_count = @err_count,
      note = @note
  WHERE run_id = @run_id;
END
GO

-- Helper view for data quality checks
CREATE OR ALTER VIEW dbo.vw_asset_coverage AS
SELECT 
  a.symbol,
  a.[name],
  a.asset_type,
  a.exchange,
  a.source_system,
  COUNT(p.[date]) AS trading_days,
  MIN(p.[date]) AS first_date,
  MAX(p.[date]) AS last_date
FROM dbo.asset a
LEFT JOIN dbo.price_daily p ON a.asset_id = p.asset_id
GROUP BY a.asset_id, a.symbol, a.[name], a.asset_type, a.exchange, a.source_system;
GO