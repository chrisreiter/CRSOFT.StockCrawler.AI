using Ingest.Core.Enums;

namespace Ingest.Core.Analysis;

/// <summary>
/// Kapitalfluss: wie viel Geld in einer Bar tatsächlich bewegt wurde.
///
/// Der springende Punkt ist die Einheit des Volumens, und sie unterscheidet
/// sich je Anlageklasse:
///
/// <list type="bullet">
/// <item>Aktien und ETFs melden das Volumen in <b>Stück</b>. Der Geldumsatz
/// ergibt sich erst aus Kurs × Volumen.</item>
/// <item>Krypto meldet das Volumen bereits in <b>Dollar</b>. Eine weitere
/// Multiplikation mit dem Kurs wäre grob falsch.</item>
/// </list>
///
/// Gemessen an echten Daten: AAPL kam am 19.08.2026 auf 50,5 Mio. Stück bei
/// 316,83 $, also 16,0 Mrd $ Umsatz — plausibel. Bitcoin meldete 46,2 Mrd bei
/// 69.266 $; multipliziert ergäbe das 3,2 Billiarden $, das Tausendfache des
/// gesamten Weltmarkts. Der Wert 46,2 Mrd <i>ist</i> bereits der Dollarumsatz.
///
/// Ohne diese Unterscheidung wäre jede Kapitalfluss-Kennzahl wertlos, weil
/// Krypto alles andere um Größenordnungen überdecken würde.
/// </summary>
public static class FlowMetrics
{
    /// <summary>Geldumsatz einer Bar in Währungseinheiten, oder null.</summary>
    public static decimal? MoneyFlow(AssetClass assetClass, decimal close, decimal? volume)
    {
        if (volume is null or <= 0 || close <= 0) return null;

        return assetClass switch
        {
            // Bereits in Dollar gemeldet.
            AssetClass.Crypto => volume,

            // In Stück gemeldet.
            _ => close * volume.Value
        };
    }

    /// <summary>
    /// Anteil am Gesamtumsatz. Erst dieser relative Wert macht Werte
    /// unterschiedlicher Größe vergleichbar — ein absoluter Umsatz sagt nur,
    /// wie groß das Papier ist, nicht ob gerade Geld hineinfließt.
    /// </summary>
    public static double Share(decimal flow, decimal total)
        => total <= 0 ? 0 : (double)(flow / total);

    /// <summary>
    /// Rotation: Veränderung des Anteils gegenüber dem Mittel des Rückblicks.
    /// Positiv heißt, dieser Wert zieht gerade überdurchschnittlich Kapital an.
    /// </summary>
    public static double Rotation(double currentShare, double baselineShare)
        => baselineShare <= 0 ? 0 : currentShare / baselineShare - 1.0;

    /// <summary>
    /// Z-Wert des Umsatzes gegenüber dem eigenen Rückblick. Beantwortet die
    /// Frage „ist heute für dieses Papier viel los?" unabhängig von seiner Größe.
    /// </summary>
    public static double VolumeZScore(ReadOnlySpan<double> history, double current)
    {
        if (history.Length < 5) return 0;

        var mean = Statistics.Mean(history);
        var sd = Statistics.StdDev(history, mean);

        return sd <= double.Epsilon ? 0 : (current - mean) / sd;
    }
}
