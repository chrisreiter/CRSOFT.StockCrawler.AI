using Ingest.Core.Enums;
using Ingest.Core.Models;

namespace Ingest.Core.Analysis;

/// <summary>
/// Netto-Kapitalfluss: nicht nur wie viel gehandelt wurde, sondern in welche
/// Richtung das Geld drückte.
///
/// <b>Was hier nicht geht und warum:</b> Aus Kursdaten lässt sich nicht
/// beobachten, dass Kapital von einem Wert in einen anderen fließt. Zu jedem
/// Kauf gehört ein Verkauf; das Volumen sagt nur, wie viel umgesetzt wurde,
/// nicht wer gekauft hat oder woher das Geld kam. Wer etwas anderes behauptet,
/// hat entweder Auftragsbuchdaten — die wir nicht haben — oder rät.
///
/// Was sich seriös berechnen lässt, ist der <i>Kaufdruck</i>: Schließt ein Wert
/// nahe seinem Tageshoch, wurde er über die Sitzung hinweg eingesammelt; nahe
/// dem Tief wurde abgegeben. Gewichtet mit dem Geldumsatz ergibt das ein
/// vorzeichenbehaftetes Maß, das sich über Werte und Zeit aufsummieren lässt.
/// Das ist die klassische Akkumulation/Distribution-Idee.
/// </summary>
public static class NetFlow
{
    /// <summary>
    /// Lage des Schlusskurses innerhalb der Tagesspanne, von −1 (Schluss auf
    /// dem Tief) bis +1 (Schluss auf dem Hoch).
    ///
    /// Fehlen Hoch und Tief oder fallen sie zusammen, entscheidet die Richtung
    /// gegenüber der Eröffnung — gröber, aber besser als nichts.
    /// </summary>
    public static double Multiplier(PriceBar bar)
    {
        if (bar.High is { } h && bar.Low is { } l && h > l)
        {
            var c = (double)bar.Close;
            var hi = (double)h;
            var lo = (double)l;

            var m = ((c - lo) - (hi - c)) / (hi - lo);
            return double.IsNaN(m) || double.IsInfinity(m) ? 0 : Math.Clamp(m, -1, 1);
        }

        if (bar.Open is { } o && o > 0)
            return Math.Sign((double)bar.Close - (double)o);

        return 0;
    }

    /// <summary>
    /// Vorzeichenbehafteter Geldfluss einer Bar. Positiv heißt Kaufdruck.
    /// Null, wenn kein Volumen vorliegt.
    /// </summary>
    public static double Signed(AssetClass assetClass, PriceBar bar)
    {
        var gross = FlowMetrics.MoneyFlow(assetClass, bar.Close, bar.Volume);
        if (gross is null) return 0;

        return Multiplier(bar) * (double)gross.Value;
    }

    /// <summary>Bruttoumsatz einer Bar, also ohne Richtung.</summary>
    public static double Gross(AssetClass assetClass, PriceBar bar)
        => (double?)FlowMetrics.MoneyFlow(assetClass, bar.Close, bar.Volume) ?? 0;

    /// <summary>
    /// Rotationsverdacht zwischen zwei Werten.
    ///
    /// Verglichen werden die Veränderungen ihrer <i>Anteile</i> am
    /// Gesamtumsatz. Ein stark negativer Wert heißt: gewinnt der eine an
    /// Anteil, verliert der andere — regelmäßig und gleichzeitig.
    ///
    /// Das ist ein Hinweis, kein Beweis. Es kann ebenso ein gemeinsamer
    /// Auslöser dahinterstecken, der den einen begünstigt und den anderen
    /// belastet, ohne dass ein Euro zwischen ihnen wechselt. Deshalb heißt es
    /// Verdacht.
    /// </summary>
    public static double RotationScore(ReadOnlySpan<double> shareA, ReadOnlySpan<double> shareB)
    {
        var n = Math.Min(shareA.Length, shareB.Length);
        if (n < 10) return 0;

        var da = new double[n - 1];
        var db = new double[n - 1];

        for (var i = 1; i < n; i++)
        {
            da[i - 1] = shareA[i] - shareA[i - 1];
            db[i - 1] = shareB[i] - shareB[i - 1];
        }

        return Statistics.Correlation(da, db);
    }
}
