/*  Referenzpreis zur Prüfung der Symbolzuordnung.

    Die Zuordnung CoinGecko-Coin -> Yahoo-Ticker über "SYMBOL-USD" trifft bei
    kleineren Token gelegentlich ein anderes Papier. Wenn die Reihe dabei
    allmählich davonläuft statt zu springen, greift der Sprungfilter nicht:
    beobachtet bei USDG-USD, das als Stablecoin bei 1 $ liegen müsste,
    tatsächlich aber zwischen 0,91 $ und 15,22 $ schwankt.

    Der Preis, den CoinGecko zum Zeitpunkt der Universum-Aktualisierung meldet,
    ist die unabhängige Gegenprobe.                                          */
USE stockcrawler;
GO

IF COL_LENGTH('dbo.asset', 'reference_price') IS NULL
  ALTER TABLE dbo.asset ADD reference_price DECIMAL(19,8) NULL;
GO

IF COL_LENGTH('dbo.asset', 'reference_price_utc') IS NULL
  ALTER TABLE dbo.asset ADD reference_price_utc DATETIME2(0) NULL;
GO
