namespace Ingest.Core.Models;

/// <summary>
/// Wechselwirkung zweier Kurse. <see cref="BestLagBars"/> &gt; 0 bedeutet:
/// A läuft B voraus – Bewegungen von A tauchen erst später in B auf.
/// </summary>
public sealed class PairStat
{
    public int AssetIdA { get; set; }
    public int AssetIdB { get; set; }
    public string IntervalCode { get; set; } = "";
    public int WindowBars { get; set; }
    public double Corr0 { get; set; }
    public int BestLagBars { get; set; }
    public double BestLagCorr { get; set; }
    public int NObs { get; set; }
    public DateTime ComputedUtc { get; set; }
}

/// <summary>Kreuzung zweier normalisierter Kurven.</summary>
public sealed class Crossing
{
    public long CrossingId { get; set; }
    public int AssetIdA { get; set; }
    public int AssetIdB { get; set; }
    public string IntervalCode { get; set; } = "";
    public DateTime TsUtc { get; set; }
    /// <summary>true = A kreuzt B nach oben.</summary>
    public bool Upward { get; set; }
    public double SpreadBefore { get; set; }
    public double SpreadAfter { get; set; }
}
