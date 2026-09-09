namespace Ingest.Core.Analysis;

/// <summary>
/// Eingangsdaten für alle Teilmodelle eines Prognoseschritts.
///
/// Kurse und Log-Returns liegen als <c>double[]</c> vor und werden <b>einmal</b>
/// berechnet, nicht je Modell und Horizont erneut. Beim Walk-Forward über die
/// gesamte Stundenhistorie fallen rund 1,3 Millionen Zeitschritte an; würde
/// jedes der fünf Teilmodelle für jeden der sechs Horizonte seine eigene
/// Renditereihe erzeugen, entstünden zweistellige Gigabyte an kurzlebigen
/// Arrays. Deshalb wird der Puffer geteilt und nur <see cref="HorizonBars"/>
/// zwischen den Horizonten umgesetzt.
/// </summary>
public sealed class ForecastInput
{
    /// <summary>Kurse aufsteigend, jüngster zuletzt.</summary>
    public double[] Closes { get; }

    /// <summary>Log-Returns zu <see cref="Closes"/>. Länge = Closes.Length − 1.</summary>
    public double[] Returns { get; }

    /// <summary>Prognosehorizont in Bars (nicht Stunden).</summary>
    public int HorizonBars { get; set; }

    /// <summary>
    /// Assets, die dem Ziel nachweislich vorauslaufen: die jüngsten Log-Returns
    /// des Frühindikators, sein Lag in Bars und die Regressions-Beta.
    /// </summary>
    public IReadOnlyList<LeadSignal> Leaders { get; set; } = [];

    public ForecastInput(double[] closes, int horizonBars, IReadOnlyList<LeadSignal>? leaders = null)
    {
        Closes = closes;
        Returns = ComputeReturns(closes);
        HorizonBars = horizonBars;
        Leaders = leaders ?? [];
    }

    public ForecastInput(IReadOnlyList<decimal> closes, int horizonBars,
                         IReadOnlyList<LeadSignal>? leaders = null)
        : this(ToDoubles(closes), horizonBars, leaders)
    {
    }

    private static double[] ToDoubles(IReadOnlyList<decimal> src)
    {
        var arr = new double[src.Count];
        for (var i = 0; i < src.Count; i++) arr[i] = (double)src[i];
        return arr;
    }

    private static double[] ComputeReturns(double[] closes)
    {
        if (closes.Length < 2) return [];

        var r = new double[closes.Length - 1];
        for (var i = 1; i < closes.Length; i++)
        {
            var prev = closes[i - 1];
            var cur = closes[i];
            r[i - 1] = prev > 0 && cur > 0 ? Math.Log(cur / prev) : 0;
        }
        return r;
    }

    /// <summary>Die letzten <paramref name="n"/> Renditen, ohne zu kopieren.</summary>
    public ReadOnlySpan<double> TailReturns(int n)
    {
        if (Returns.Length == 0) return [];
        var take = Math.Min(n, Returns.Length);
        return Returns.AsSpan(Returns.Length - take, take);
    }

    /// <summary>Die letzten <paramref name="n"/> Kurse, ohne zu kopieren.</summary>
    public ReadOnlySpan<double> TailCloses(int n)
    {
        if (Closes.Length == 0) return [];
        var take = Math.Min(n, Closes.Length);
        return Closes.AsSpan(Closes.Length - take, take);
    }
}

public sealed record LeadSignal(int LeaderAssetId, double RecentReturn, int LagBars, double Beta, double Corr);

/// <summary>Ein Teilmodell liefert einen erwarteten Log-Return über den Horizont.</summary>
public interface IForecastModel
{
    string Name { get; }
    double PredictReturn(ForecastInput input);
}

/// <summary>Random Walk: bester Schätzer ist der aktuelle Kurs. Harte Messlatte.</summary>
public sealed class NaiveModel : IForecastModel
{
    public string Name => "naive";
    public double PredictReturn(ForecastInput input) => 0.0;
}

/// <summary>Konstante Drift aus dem historischen Mittel der Log-Returns.</summary>
public sealed class DriftModel : IForecastModel
{
    private readonly int _lookback;
    public DriftModel(int lookback = 200) => _lookback = lookback;
    public string Name => "drift";

    public double PredictReturn(ForecastInput input)
    {
        var r = input.TailReturns(_lookback);
        if (r.Length < 5) return 0;
        return Statistics.Mean(r) * input.HorizonBars;
    }
}

/// <summary>
/// Trendfortschreibung über EWMA der jüngsten Returns. Gedämpft, weil sich
/// Momentum über längere Horizonte erfahrungsgemäß abschwächt.
/// </summary>
public sealed class MomentumModel : IForecastModel
{
    private readonly double _alpha;
    private readonly double _damping;
    private readonly int _lookback;

    public MomentumModel(double alpha = 0.2, double damping = 0.5, int lookback = 120)
    {
        _alpha = alpha;
        _damping = damping;
        _lookback = lookback;
    }

    public string Name => "momentum";

    public double PredictReturn(ForecastInput input)
    {
        var r = input.TailReturns(_lookback);
        if (r.Length < 5) return 0;

        var trend = Statistics.Ewma(r, _alpha);

        // Dämpfung: Effekt wächst nicht linear mit dem Horizont, sondern
        // läuft gegen einen Grenzwert.
        var effective = _damping <= 0
            ? input.HorizonBars
            : (1 - Math.Pow(1 - _damping, input.HorizonBars)) / _damping;

        return trend * effective;
    }
}

/// <summary>
/// Rückkehr zum Mittelwert: Wie weit ist der Kurs von seinem gleitenden
/// Durchschnitt weg? Je weiter, desto stärker der erwartete Rückzug.
/// </summary>
public sealed class MeanReversionModel : IForecastModel
{
    private readonly int _window;
    private readonly double _strength;

    public MeanReversionModel(int window = 50, double strength = 0.15)
    {
        _window = window;
        _strength = strength;
    }

    public string Name => "meanrev";

    public double PredictReturn(ForecastInput input)
    {
        var tail = input.TailCloses(_window);
        if (tail.Length < 10) return 0;

        // Mittelwert und Streuung der Log-Kurse, ohne Zwischenarray.
        double sum = 0;
        var n = 0;
        foreach (var c in tail)
        {
            if (c <= 0) continue;
            sum += Math.Log(c);
            n++;
        }
        if (n < 10) return 0;

        var mean = sum / n;

        double varSum = 0;
        foreach (var c in tail)
        {
            if (c <= 0) continue;
            var d = Math.Log(c) - mean;
            varSum += d * d;
        }

        var sd = Math.Sqrt(varSum / (n - 1));
        if (sd <= double.Epsilon) return 0;

        var last = tail[^1];
        if (last <= 0) return 0;

        var z = (Math.Log(last) - mean) / sd;

        // Erwartete Korrektur: ein Bruchteil der Abweichung, mit dem Horizont
        // wachsend, aber gegen die volle Abweichung gedeckelt.
        var pull = Math.Min(1.0, _strength * Math.Sqrt(input.HorizonBars));
        return -z * sd * pull;
    }
}

/// <summary>
/// Nutzt Wechselwirkungen: Assets, die dem Ziel vorauslaufen, haben sich schon
/// bewegt — diese Bewegung wird über die Beta auf das Ziel übertragen.
/// Das ist das Modell, das die Lead-Lag-Analyse in eine Prognose übersetzt.
/// </summary>
public sealed class LeadLagModel : IForecastModel
{
    private readonly double _minCorr;
    public LeadLagModel(double minCorr = 0.3) => _minCorr = minCorr;
    public string Name => "leadlag";

    public double PredictReturn(ForecastInput input)
    {
        if (input.Leaders.Count == 0) return 0;

        double num = 0, den = 0;
        foreach (var l in input.Leaders)
        {
            if (Math.Abs(l.Corr) < _minCorr) continue;

            // Nur Frühindikatoren, deren Vorlauf den Horizont noch abdeckt.
            // Ein Lag von 2 Bars sagt nichts über 24 Bars in der Zukunft.
            if (l.LagBars <= 0 || l.LagBars < input.HorizonBars) continue;

            var w = Math.Abs(l.Corr);
            num += w * l.Beta * l.RecentReturn;
            den += w;
        }
        return den <= double.Epsilon ? 0 : num / den;
    }
}
