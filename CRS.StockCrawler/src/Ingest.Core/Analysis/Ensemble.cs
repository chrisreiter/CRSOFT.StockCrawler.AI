namespace Ingest.Core.Analysis;

public sealed record ComponentPrediction(string ModelName, double PredictedReturn, double Weight);

public sealed record EnsembleResult(
    double PredictedReturn,
    double Confidence,
    IReadOnlyList<ComponentPrediction> Components);

/// <summary>
/// Gewichtete Kombination der Teilmodelle plus die Lernregel, die die
/// Gewichte nach jeder Auswertung nachzieht.
/// </summary>
public static class Ensemble
{
    public const string Version = "ens-1";

    /// <summary>Alle Teilmodelle in fester Reihenfolge.</summary>
    public static IReadOnlyList<IForecastModel> DefaultModels() =>
    [
        new NaiveModel(),
        new DriftModel(),
        new MomentumModel(),
        new MeanReversionModel(),
        new LeadLagModel()
    ];

    /// <summary>
    /// Kombiniert die Modelle. <paramref name="weights"/> darf unvollständig
    /// sein – fehlende Modelle starten mit Gleichgewicht.
    /// </summary>
    public static EnsembleResult Combine(
        IReadOnlyList<IForecastModel> models,
        ForecastInput input,
        IReadOnlyDictionary<string, double> weights,
        IReadOnlyDictionary<string, double>? hitRates = null)
    {
        var comps = new List<ComponentPrediction>(models.Count);
        double wSum = 0, acc = 0;

        foreach (var m in models)
        {
            var w = weights.TryGetValue(m.Name, out var ww) && ww > 0 ? ww : 1.0 / models.Count;
            double r;
            try { r = m.PredictReturn(input); }
            catch { r = 0; }   // ein defektes Teilmodell darf das Ensemble nicht kippen

            if (double.IsNaN(r) || double.IsInfinity(r)) r = 0;

            // Ausreißer kappen: über einen Horizont sind >50 % Log-Return
            // praktisch immer ein Datenfehler, kein Signal.
            r = Math.Clamp(r, -0.5, 0.5);

            comps.Add(new ComponentPrediction(m.Name, r, w));
            acc += w * r;
            wSum += w;
        }

        var predicted = wSum > double.Epsilon ? acc / wSum : 0;

        // Zuversicht: gewichtete Trefferquote der Teilmodelle, gedämpft mit
        // der Streuung ihrer Meinungen. Uneinigkeit senkt die Konfidenz.
        double confidence;
        if (hitRates is { Count: > 0 })
        {
            double hAcc = 0, hW = 0;
            foreach (var c in comps)
                if (hitRates.TryGetValue(c.ModelName, out var h)) { hAcc += c.Weight * h; hW += c.Weight; }
            confidence = hW > double.Epsilon ? hAcc / hW : 0.5;
        }
        else confidence = 0.5;

        var spread = comps.Count > 1
            ? Statistics.StdDev(comps.Select(c => c.PredictedReturn).ToArray())
            : 0;
        var agreement = 1.0 / (1.0 + spread * 20);
        confidence = Math.Clamp(confidence * agreement, 0.0, 1.0);

        return new EnsembleResult(predicted, confidence, comps);
    }

    /// <summary>
    /// Hedge / Multiplicative Weights: jedes Teilmodell wird proportional zu
    /// <c>exp(-eta * Verlust)</c> herabgewichtet. Verfahren mit bekannter
    /// Regret-Schranke – die Gewichte laufen nachweislich gegen das beste
    /// Teilmodell, ohne dass wir vorher wissen müssen, welches das ist.
    /// </summary>
    /// <param name="scale">Fehlerskala zur Normierung, typisch die
    /// durchschnittliche Bewegung des Assets über den Horizont.</param>
    /// <param name="directionPenalty">
    /// Aufschlag auf den Verlust, wenn das Teilmodell die Richtung verfehlt.
    ///
    /// Ohne diesen Term optimiert das Verfahren allein den Betragsfehler — und
    /// dann gewinnt zwangsläufig das Modell, das schlicht "keine Änderung"
    /// sagt. Das ist rechnerisch der beste Schätzer, als Aussage aber wertlos,
    /// weil es nie eine Richtung nennt. Der Aufschlag verschiebt das Gewicht zu
    /// Modellen, die tatsächlich eine Bewegung vorhersagen und damit richtig
    /// liegen.
    /// </param>
    public static Dictionary<string, double> UpdateWeights(
        IReadOnlyDictionary<string, double> current,
        IReadOnlyDictionary<string, double> componentReturns,
        double actualReturn,
        double eta = 0.5,
        double scale = 0.02,
        double floor = 0.01,
        double directionPenalty = 1.2)
    {
        if (scale <= double.Epsilon) scale = 0.02;

        var updated = new Dictionary<string, double>(current.Count);
        foreach (var (name, ret) in componentReturns)
        {
            var w = current.TryGetValue(name, out var cw) && cw > 0 ? cw : 1.0;
            updated[name] = w * Math.Exp(-eta * Loss(ret, actualReturn, scale, directionPenalty));
        }

        // Modelle, die diesmal nicht mitgelaufen sind, unverändert übernehmen.
        foreach (var (name, w) in current)
            if (!updated.ContainsKey(name)) updated[name] = w;

        var sum = updated.Values.Sum();
        if (sum <= double.Epsilon)
            return updated.Keys.ToDictionary(k => k, _ => 1.0 / updated.Count);

        // Normieren und Mindestgewicht sichern: ein Modell, das einmal
        // schlecht war, muss zurückkommen können, wenn sich das Regime dreht.
        var result = new Dictionary<string, double>(updated.Count);
        foreach (var (name, w) in updated) result[name] = Math.Max(floor, w / sum);

        var s2 = result.Values.Sum();
        foreach (var name in result.Keys.ToList()) result[name] /= s2;
        return result;
    }

    /// <summary>
    /// Verlust eines Teilmodells: normierter Betragsfehler plus Aufschlag,
    /// wenn die Richtung verfehlt wurde. Einzige Stelle, an der die Verlust-
    /// definition steht — die Wörterbuch- und die Array-Variante teilen sie.
    /// </summary>
    public static double Loss(double predicted, double actual, double scale, double directionPenalty)
    {
        // Gedeckelt, damit ein einzelner Ausreißer ein Modell nicht dauerhaft
        // auf null drückt.
        var loss = Math.Min(4.0, Math.Abs(predicted - actual) / scale);

        // Bei praktisch unbewegtem Kurs ist die Richtungsfrage sinnlos.
        if (Math.Abs(actual) <= 1e-5 || directionPenalty <= 0) return loss;

        // Eine Prognose ohne erkennbare Richtung zählt wie eine falsche:
        // sie hilft bei der Entscheidung nicht weiter.
        var noCall = Math.Abs(predicted) < 1e-4;
        var wrongWay = Math.Sign(predicted) != Math.Sign(actual);

        return noCall || wrongWay ? loss + directionPenalty : loss;
    }

    /// <summary>
    /// Dieselbe Hedge-Regel, aber auf einem festen Array statt einem Wörterbuch
    /// und ohne jede Allokation.
    ///
    /// Der Walk-Forward über die gesamte Stundenhistorie führt Millionen von
    /// Lernschritten aus; mit der Wörterbuch-Variante entstünde allein hier
    /// zweistelliger Gigabyte-Müll für den Garbage Collector.
    /// <paramref name="weights"/> wird an Ort und Stelle geändert.
    /// </summary>
    public static void UpdateWeightsInPlace(
        double[] weights,
        double[] componentReturns,
        double actualReturn,
        double eta = 0.5,
        double scale = 0.02,
        double floor = 0.01,
        double directionPenalty = 1.2)
    {
        if (scale <= double.Epsilon) scale = 0.02;

        var n = Math.Min(weights.Length, componentReturns.Length);
        double sum = 0;

        for (var i = 0; i < n; i++)
        {
            var w = weights[i] > 0 ? weights[i] : 1.0;
            w *= Math.Exp(-eta * Loss(componentReturns[i], actualReturn, scale, directionPenalty));
            weights[i] = w;
            sum += w;
        }

        if (sum <= double.Epsilon)
        {
            for (var i = 0; i < n; i++) weights[i] = 1.0 / n;
            return;
        }

        // Normieren und Mindestgewicht sichern: ein Modell, das einmal schlecht
        // war, muss zurückkommen können, wenn sich das Regime dreht.
        double sum2 = 0;
        for (var i = 0; i < n; i++)
        {
            weights[i] = Math.Max(floor, weights[i] / sum);
            sum2 += weights[i];
        }

        for (var i = 0; i < n; i++) weights[i] /= sum2;
    }

    /// <summary>
    /// Kombination auf Arrays — Gegenstück zu <see cref="UpdateWeightsInPlace"/>.
    /// Schreibt die Einzelprognosen nach <paramref name="componentReturns"/> und
    /// liefert den gewichteten Gesamtwert.
    /// </summary>
    public static double CombineInPlace(
        IReadOnlyList<IForecastModel> models,
        ForecastInput input,
        double[] weights,
        double[] componentReturns)
    {
        double acc = 0, wSum = 0;

        for (var i = 0; i < models.Count; i++)
        {
            double r;
            try { r = models[i].PredictReturn(input); }
            catch { r = 0; }   // ein defektes Teilmodell darf das Ensemble nicht kippen

            if (double.IsNaN(r) || double.IsInfinity(r)) r = 0;

            // Ausreißer kappen: über einen Horizont sind mehr als 50 % Log-Return
            // praktisch immer ein Datenfehler, kein Signal.
            r = Math.Clamp(r, -0.5, 0.5);

            componentReturns[i] = r;

            var w = weights[i] > 0 ? weights[i] : 1.0 / models.Count;
            acc += w * r;
            wSum += w;
        }

        return wSum > double.Epsilon ? acc / wSum : 0;
    }

    /// <summary>Laufender Mittelwert für Fehler- und Trefferstatistik.</summary>
    public static (double Mean, int N) RunningMean(double oldMean, int n, double value)
        => ((oldMean * n + value) / (n + 1), n + 1);
}
