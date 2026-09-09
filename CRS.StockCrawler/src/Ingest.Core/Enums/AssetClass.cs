namespace Ingest.Core.Enums;

/// <summary>Anlageklasse. Wert = Spalte asset.asset_class.</summary>
public enum AssetClass : byte
{
    Stock  = 0,
    Etf    = 1,   // Fonds/ETFs
    Crypto = 2,
    Index  = 3
}
