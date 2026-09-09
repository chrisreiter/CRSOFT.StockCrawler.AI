using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Ingest.Infrastructure.Services;

/// <summary>Was ein trainiertes Modell über sich selbst mitteilt.</summary>
public sealed record DeepModelInfo(
    string Band,
    string BandLabel,
    string Version,
    int SeqLen,
    IReadOnlyList<string> Features,
    IReadOnlyList<int> Horizons,
    IReadOnlyList<int> AssetIds,
    string TrainUntil,
    string ValidateUntil,
    string TestUntil,
    double ValidationErrorRatio,
    double TestErrorRatio,
    double TestHitRate,
    IReadOnlyList<double> TestErrorRatioByHorizon,
    IReadOnlyList<double> TestHitRateByHorizon,

    /* Die zweite Latte: die blosse Drift.

       „Schlägt den Stillstand“ reicht bei langen Horizonten nicht. Über ein
       Jahr steigen Aktien im Mittel; ein Modell, das nur „aufwärts“ sagt,
       schlägt den Stillstand zwangsläufig, ohne etwas gelernt zu haben. */
    IReadOnlyList<double> DriftErrorRatioByHorizon,
    IReadOnlyList<double> DriftHitRateByHorizon)
{
    /// <summary>
    /// Trägt das Modell — gemessen an beiden Latten?
    ///
    /// Es muss den Stillstand schlagen UND die blosse Drift. Die zweite Latte
    /// ist die härtere und wurde teuer gelernt: Das lange Modell meldete bei
    /// 250 Tagen ein Fehlerverhältnis von 0,9604 bei 64,3 % Richtung und sah
    /// damit nach dem ersten Erfolg dieses Projekts aus. Die blosse
    /// Trainingsdrift — EINE Zahl — kam auf 0,9139 bei 75,1 %. Das Netz hatte
    /// gelernt, dass Kurse steigen, und das schlechter als ein Mittelwert.
    /// </summary>
    public bool Carries(int horizonIndex)
    {
        if (TestErrorRatio >= 1) return false;
        if (TestHitRate <= 0.523) return false;

        var i = horizonIndex;

        // Ohne Driftzahlen (ältere Beschreibungsdatei) bleibt es bei der
        // ersten Latte — aber dann ohne die Behauptung, die zweite sei geprüft.
        if (i < 0 || i >= DriftErrorRatioByHorizon.Count) return true;

        return TestErrorRatioByHorizon.Count > i
               && TestErrorRatioByHorizon[i] < DriftErrorRatioByHorizon[i]
               && TestHitRateByHorizon.Count > i
               && TestHitRateByHorizon[i] > DriftHitRateByHorizon[i];
    }

    /// <summary>Trägt das Modell in mindestens einem seiner Horizonte?</summary>
    public bool CarriesAny() => Horizons.Where((_, i) => Carries(i)).Any();

    /// <summary>Das Urteil in einem Satz.</summary>
    public string Verdict(int horizonIndex = -1)
    {
        if (TestErrorRatio >= 1)
            return "schlägt die Annahme, es ändere sich nichts, NICHT — Gewicht null";

        var i = horizonIndex >= 0 ? horizonIndex : 0;

        if (i < DriftErrorRatioByHorizon.Count
            && TestErrorRatioByHorizon.Count > i
            && TestErrorRatioByHorizon[i] >= DriftErrorRatioByHorizon[i])
            return "schlägt den Stillstand, aber nicht die blosse Drift — "
                 + "es hat gelernt, dass Kurse steigen";

        if (TestHitRate <= 0.523)
            return "schlägt zwar den Stillstand, aber nicht die erste Säule (52,3 %)";

        return "trägt: besser als Stillstand, Drift und erste Säule";
    }
}

/// <summary>Eine Vorhersage mit ihrer eigenen Unsicherheit.</summary>
public sealed record DeepPrediction(
    string Band,
    int HorizonBars,
    double PredictedReturn,
    double StdDev,
    double PredictedClose,
    double ChangePct);

/// <summary>Ein Tag der Vergangenheit: was das Modell sagte, was eintrat.</summary>
public sealed record DeepBacktestPoint(
    DateTime TsUtc,
    double PredictedClose,
    double ActualClose,
    double PredictedPct,
    double ActualPct,
    double StdDev);

/// <summary>Der Rückblick mit seinen Kennzahlen.</summary>
public sealed record DeepBacktest(
    string Band,
    int HorizonBars,
    string FromUtc,
    IReadOnlyList<DeepBacktestPoint> Points,
    double MeanAbsErrorPct,
    double NaiveAbsErrorPct,
    double ErrorRatio,
    double HitRate,
    double Bias);

public interface IDeepForecastService
{
    bool IsLoaded { get; }

    /// <summary>Alle geladenen Modelle, kurz vor lang.</summary>
    IReadOnlyList<DeepModelInfo> Models { get; }

    DeepModelInfo? Model(string band);

    /// <summary>
    /// Sagt für einen Wert alle Horizonte eines Bandes voraus.
    /// <paramref name="window"/> hält die letzten Merkmalszeilen, älteste zuerst.
    /// </summary>
    IReadOnlyList<DeepPrediction> Predict(
        string band, int assetId, double[][] window, double lastClose);

    /// <summary>
    /// Stellt die Vorhersage an jedem Tag der Vergangenheit neben das, was
    /// eintrat. <paramref name="fromUtc"/> begrenzt auf den Sperrbereich.
    /// </summary>
    DeepBacktest? Backtest(
        string band, int assetId, IReadOnlyList<double[]> rows, IReadOnlyList<double> close,
        IReadOnlyList<DateTime> ts, int horizonBars, DateTime? fromUtc, int maxPoints);
}

/// <summary>
/// Führt die in Python trainierten Modelle in .NET aus.
///
/// <b>Warum diese Trennung.</b> Training braucht PyTorch, automatische
/// Ableitung und Stunden Rechenzeit; Inferenz braucht Millisekunden und keine
/// dieser Voraussetzungen. ONNX ist genau die Naht dazwischen: Das Modell wird
/// einmal exportiert und läuft danach ohne Python, ohne Framework und ohne
/// Versionsabhängigkeit im selben Prozess wie der Rest der Anwendung.
///
/// <b>Warum drei Modelle und nicht eines.</b> Der erste Durchgang lief über
/// alle neun Horizonte zugleich und scheiterte am Maßstab der Zielgrößen: Deren
/// Streuung reicht von 0,032 bei einem Tag bis 0,445 bei 250 — Faktor vierzehn.
/// Mit gemeinsamer Normierung stammt fast der gesamte Verlust aus den langen
/// Horizonten, und die kurzen werden praktisch nicht trainiert. Inhaltlich sind
/// es ohnehin verschiedene Aufgaben: kurzfristig überwiegen Rückkehr zum Mittel
/// und Mikrostruktur, langfristig Trend und Faktorbindung.
///
/// <b>Was dabei schiefgehen kann und hier abgefangen wird.</b> Die Normierung
/// der Merkmale gehört zum Modell, steht aber nicht im ONNX-Graphen — sie
/// wurde beim Training aus den Trainingsdaten bestimmt. Wird sie hier anders
/// gerechnet als dort, liefert das Modell stillschweigend Unsinn. Deshalb
/// werden Mittelwert und Streuung aus derselben Datei geladen, die der
/// Trainingslauf geschrieben hat.
/// </summary>
public sealed class DeepForecastService : IDeepForecastService, IDisposable
{
    /// <summary>Ein geladenes Modell mit allem, was zu seiner Ausführung gehört.</summary>
    private sealed class Loaded : IDisposable
    {
        public required DeepModelInfo Info { get; init; }
        public required InferenceSession Session { get; init; }
        public required float[] Mean { get; init; }
        public required float[] Std { get; init; }
        public required double[] TargetStd { get; init; }
        public required Dictionary<int, int> AssetIndex { get; init; }

        public void Dispose() => Session.Dispose();
    }

    private readonly ILogger<DeepForecastService> _log;
    private readonly Dictionary<string, Loaded> _models = [];

    public bool IsLoaded => _models.Count > 0;

    public IReadOnlyList<DeepModelInfo> Models =>
        _models.Values.Select(m => m.Info).OrderBy(i => i.Horizons[0]).ToList();

    public DeepModelInfo? Model(string band) =>
        _models.TryGetValue(band, out var m) ? m.Info : null;

    public DeepForecastService(ILogger<DeepForecastService> log)
    {
        _log = log;
        TryLoadAll();
    }

    /// <summary>
    /// Ordnet ein Modell seinem Band zu — nach dem, was es kann, nicht nach
    /// seinem Dateinamen.
    ///
    /// Der Dateiname wäre die bequemere Quelle und die schlechtere: Er lässt
    /// sich vertippen, und ein „deep_mid" mit Tageshorizonten würde dann als
    /// mittelfristig geführt. Die Horizonte stehen in der Beschreibung und sind
    /// das, worauf es ankommt.
    /// </summary>
    private static (string Band, string Label) BandOf(IReadOnlyList<int> horizons)
    {
        var longest = horizons.Max();

        return longest switch
        {
            <= 5 => ("kurz", "kurzfristig — bis eine Woche"),
            <= 60 => ("mittel", "mittelfristig — zwei Wochen bis ein Quartal"),
            _ => ("lang", "langfristig — halbes bis ganzes Jahr")
        };
    }

    private void TryLoadAll()
    {
        var dir = Ablage.Modelle;

        if (!Directory.Exists(dir))
        {
            _log.LogInformation("Kein Modellverzeichnis unter {Pfad} — die Säule bleibt still", dir);
            return;
        }

        /* Gesucht wird nach Beschreibungsdateien, nicht nach festen Namen. Wer
           ein viertes Modell trainiert, legt es dazu und muss hier nichts
           ändern. */
        foreach (var meta in Directory.GetFiles(dir, "deep_*.json").OrderBy(f => f))
        {
            var onnx = Path.ChangeExtension(meta, ".onnx");

            if (!File.Exists(onnx))
            {
                _log.LogWarning("Beschreibung {Meta} ohne zugehöriges ONNX — übergangen", meta);
                continue;
            }

            TryLoadOne(meta, onnx);
        }

        if (_models.Count == 0)
            _log.LogInformation("Kein trainiertes Modell in {Pfad} — die Säule bleibt still", dir);
    }

    private void TryLoadOne(string meta, string onnx)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            var r = doc.RootElement;

            double[] Arr(string name) => r.GetProperty(name).EnumerateArray()
                                          .Select(x => x.GetDouble()).ToArray();

            /* Null-Einträge vertragen: Wo ein Horizont keine Zielwerte im
               Sperrbereich hatte, schreibt der Trainingslauf null. Ein
               GetDouble darauf wirft, und das Modell liesse sich dann gar
               nicht mehr laden. */
            double[] ArrOpt(string name) => r.TryGetProperty(name, out var e)
                                            && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray()
                   .Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN)
                   .ToArray()
                : [];

            /* Streuung je Horizont, nicht global. Eine einzige Zahl war der
               Fehler des ersten Laufs: Die Zielgrößen reichen von 0,032 bei
               einem Tag bis 0,445 bei 250 — mit gemeinsamer Normierung stammt
               fast der ganze Verlust aus den langen Horizonten. */
            var ts = r.GetProperty("target_std");

            var targetStd = ts.ValueKind == JsonValueKind.Array
                ? ts.EnumerateArray().Select(x => x.GetDouble()).ToArray()
                : [ts.GetDouble()];

            var horizons = r.GetProperty("horizons").EnumerateArray()
                            .Select(x => x.GetInt32()).ToList();

            var assetIds = r.GetProperty("asset_ids").EnumerateArray()
                            .Select(x => x.GetInt32()).ToList();

            var (band, label) = BandOf(horizons);

            var info = new DeepModelInfo(
                band, label,
                r.GetProperty("version").GetString() ?? "?",
                r.GetProperty("seq_len").GetInt32(),
                r.GetProperty("features").EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
                horizons,
                assetIds,
                r.GetProperty("train_until").GetString() ?? "",
                r.GetProperty("validate_until").GetString() ?? "",
                r.GetProperty("test_until").GetString() ?? "",
                r.GetProperty("validation_error_ratio").GetDouble(),
                r.GetProperty("test_error_ratio").GetDouble(),
                r.GetProperty("test_hit_rate").GetDouble(),
                ArrOpt("test_error_ratio_by_horizon"),
                ArrOpt("test_hit_rate_by_horizon"),
                ArrOpt("drift_error_ratio_by_horizon"),
                ArrOpt("drift_hit_rate_by_horizon"));

            if (_models.TryGetValue(band, out var vorhanden))
            {
                _log.LogWarning(
                    "Zwei Modelle für das Band {Band}: {Alt} bleibt, {Neu} übergangen",
                    band, vorhanden.Info.Version, info.Version);
                return;
            }

            _models[band] = new Loaded
            {
                Info = info,
                Session = new InferenceSession(onnx),
                Mean = Arr("feature_mean").Select(x => (float)x).ToArray(),
                Std = Arr("feature_std").Select(x => (float)x).ToArray(),
                TargetStd = targetStd,
                AssetIndex = assetIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i)
            };

            _log.LogInformation(
                "Modell {Version} geladen ({Band}): {N} Werte, Horizonte {H}, "
                + "Fehlerverhältnis im Sperrbereich {R:F4}",
                info.Version, band, assetIds.Count, string.Join(",", horizons), info.TestErrorRatio);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Modell {Meta} konnte nicht geladen werden", meta);
        }
    }

    public IReadOnlyList<DeepPrediction> Predict(
        string band, int assetId, double[][] window, double lastClose)
    {
        if (!_models.TryGetValue(band, out var m)) return [];

        /* Ein Wert, den das Modell nie gesehen hat, hat keine Einbettung. Ihn
           auf einen beliebigen Index abzubilden ergäbe eine Vorhersage, die
           aussieht wie eine — deshalb lieber keine. */
        if (!m.AssetIndex.TryGetValue(assetId, out var ai)) return [];

        var seq = m.Info.SeqLen;
        var nf = m.Info.Features.Count;

        if (window.Length < seq) return [];

        var horizons = m.Info.Horizons;
        var batch = horizons.Count;

        var x = new DenseTensor<float>([batch, seq, nf]);
        var aIdx = new DenseTensor<long>([batch]);
        var hIdx = new DenseTensor<long>([batch]);

        for (var b = 0; b < batch; b++)
        {
            aIdx[b] = ai;
            hIdx[b] = b;

            for (var t = 0; t < seq; t++)
            {
                var row = window[window.Length - seq + t];

                for (var f = 0; f < nf; f++)
                {
                    var v = f < row.Length ? row[f] : 0;
                    if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;

                    // Dieselbe Normierung wie im Training — sonst rechnet das
                    // Modell mit Zahlen in einer Größenordnung, die es nie sah.
                    x[b, t, f] = (float)((v - m.Mean[f]) / m.Std[f]);
                }
            }
        }

        using var results = m.Session.Run(
        [
            NamedOnnxValue.CreateFromTensor("features", x),
            NamedOnnxValue.CreateFromTensor("asset_idx", aIdx),
            NamedOnnxValue.CreateFromTensor("horizon_idx", hIdx)
        ]);

        var mean = results.First(v => v.Name == "mean").AsEnumerable<float>().ToArray();
        var logvar = results.First(v => v.Name == "logvar").AsEnumerable<float>().ToArray();

        var outp = new List<DeepPrediction>(batch);

        for (var b = 0; b < batch; b++)
        {
            var scale = b < m.TargetStd.Length ? m.TargetStd[b] : m.TargetStd[^1];

            var r = mean[b] * scale;
            var sd = Math.Sqrt(Math.Exp(logvar[b])) * scale;

            outp.Add(new DeepPrediction(
                band,
                horizons[b],
                Math.Round(r, 6),
                Math.Round(sd, 6),
                Math.Round(lastClose * Math.Exp(r), 6),
                Math.Round((Math.Exp(r) - 1) * 100, 4)));
        }

        return outp;
    }

    public DeepBacktest? Backtest(
        string band, int assetId, IReadOnlyList<double[]> rows, IReadOnlyList<double> close,
        IReadOnlyList<DateTime> ts, int horizonBars, DateTime? fromUtc, int maxPoints)
    {
        if (!_models.TryGetValue(band, out var m)) return null;
        if (!m.AssetIndex.TryGetValue(assetId, out var ai)) return null;

        var hi = m.Info.Horizons.ToList().IndexOf(horizonBars);
        if (hi < 0) return null;

        var seq = m.Info.SeqLen;
        var nf = m.Info.Features.Count;

        /* Nur Zeitpunkte, an denen BEIDES vorliegt: genug Vorlauf für das
           Fenster und der tatsächlich eingetretene Kurs h Schritte später. Der
           letzte mögliche Anker liegt deshalb h Schritte vor dem Ende — für
           spätere gibt es noch keine Wahrheit, gegen die man messen könnte. */
        var first = Math.Max(seq - 1, 0);
        var last = rows.Count - 1 - horizonBars;

        if (last < first) return null;

        var anchors = new List<int>();

        for (var i = first; i <= last; i++)
            if (fromUtc is null || ts[i] >= fromUtc.Value)
                anchors.Add(i);

        if (anchors.Count == 0) return null;

        // Bei langer Historie ausdünnen — die Kurve im Diagramm wird sonst zur
        // Fläche, und die Kennzahlen ändern sich durch gleichmäßiges Auslassen
        // nicht nennenswert.
        var step = Math.Max(1, (int)Math.Ceiling(anchors.Count / (double)Math.Max(1, maxPoints)));

        var picked = new List<int>(anchors.Count / step + 1);
        for (var k = 0; k < anchors.Count; k += step) picked.Add(anchors[k]);

        var scale = hi < m.TargetStd.Length ? m.TargetStd[hi] : m.TargetStd[^1];

        var points = new List<DeepBacktestPoint>(picked.Count);

        double sumAbs = 0, sumNaive = 0, sumSigned = 0;
        var hits = 0;

        // In Blöcken durch das Netz — ein Aufruf je Tag wäre um ein Vielfaches
        // langsamer, ohne dass sich am Ergebnis etwas ändert.
        const int chunk = 256;

        for (var off = 0; off < picked.Count; off += chunk)
        {
            var n = Math.Min(chunk, picked.Count - off);

            var x = new DenseTensor<float>([n, seq, nf]);
            var aIdx = new DenseTensor<long>([n]);
            var hIdx = new DenseTensor<long>([n]);

            for (var b = 0; b < n; b++)
            {
                var anchor = picked[off + b];

                aIdx[b] = ai;
                hIdx[b] = hi;

                for (var t = 0; t < seq; t++)
                {
                    var row = rows[anchor - seq + 1 + t];

                    for (var f = 0; f < nf; f++)
                    {
                        var v = f < row.Length ? row[f] : 0;
                        if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;

                        x[b, t, f] = (float)((v - m.Mean[f]) / m.Std[f]);
                    }
                }
            }

            using var res = m.Session.Run(
            [
                NamedOnnxValue.CreateFromTensor("features", x),
                NamedOnnxValue.CreateFromTensor("asset_idx", aIdx),
                NamedOnnxValue.CreateFromTensor("horizon_idx", hIdx)
            ]);

            var mean = res.First(v => v.Name == "mean").AsEnumerable<float>().ToArray();
            var logvar = res.First(v => v.Name == "logvar").AsEnumerable<float>().ToArray();

            for (var b = 0; b < n; b++)
            {
                var anchor = picked[off + b];

                var baseClose = close[anchor];
                var actual = close[anchor + horizonBars];

                var predR = mean[b] * scale;
                var sd = Math.Sqrt(Math.Exp(logvar[b])) * scale;

                var actualR = Math.Log(actual / baseClose);

                sumAbs += Math.Abs(predR - actualR);
                sumNaive += Math.Abs(actualR);
                sumSigned += predR - actualR;

                if (Math.Sign(predR) == Math.Sign(actualR) && actualR != 0) hits++;

                points.Add(new DeepBacktestPoint(
                    ts[anchor + horizonBars],
                    Math.Round(baseClose * Math.Exp(predR), 4),
                    Math.Round(actual, 4),
                    Math.Round((Math.Exp(predR) - 1) * 100, 4),
                    Math.Round((Math.Exp(actualR) - 1) * 100, 4),
                    Math.Round(sd, 6)));
            }
        }

        var cnt = points.Count;

        return new DeepBacktest(
            band,
            horizonBars,
            (fromUtc ?? ts[picked[0]]).ToString("O"),
            points,
            Math.Round(sumAbs / cnt * 100, 4),
            Math.Round(sumNaive / cnt * 100, 4),
            Math.Round(sumNaive > 0 ? sumAbs / sumNaive : double.NaN, 4),
            Math.Round(hits / (double)cnt * 100, 3),
            Math.Round(sumSigned / cnt * 100, 4));
    }

    public void Dispose()
    {
        foreach (var m in _models.Values) m.Dispose();
        _models.Clear();
    }
}
