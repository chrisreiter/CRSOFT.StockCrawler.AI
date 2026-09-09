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
  WHEN MATCHED THEN UPDATE SET symbol = COALESCE(@symbol, t.symbol),
                              [name] = COALESCE(@name, t.[name]),
                              asset_type = @asset_type,
                              exchange = COALESCE(@exchange, t.exchange)
  WHEN NOT MATCHED THEN
    INSERT (vendor_key, symbol, [name], asset_type, exchange, source_system)
    VALUES (@vendor_key, @symbol, @name, @asset_type, @exchange, @source_system);
  SELECT asset_id FROM dbo.asset WHERE vendor_key=@vendor_key AND source_system=@source_system;
END
GO
