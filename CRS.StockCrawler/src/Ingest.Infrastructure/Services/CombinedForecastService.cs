using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>Was eine einzelne Säule zu einem Horizont beiträgt.</summary>
/// <param name="Weight">Das eingestellte Gewicht, 0 bis 100.</param>
/// <param name="Merit">
/// Der gemessene Wert dieses Beitrags, 0 bis 1. Nicht was der Nutzer eingestellt
/// hat, sondern was der Sperrbereich hergibt.
/// </param>
public sealed record PillarContribution(
    string Pillar, double PredictedReturn, double? Confidence,
    int Weight, double Merit, string Basis);

/// <summary>Die kombinierte Vorhersage für einen Horizont.</summary>
public sealed record CombinedPoint(
    int HorizonHours,
    string HorizonLabel,
    DateTime TargetTsUtc,
    decimal BaseClose,
    decimal CombinedClose,
    double CombinedReturn,
    double ChangePct,
    IReadOnlyList<PillarContribution> Contributions,
    double EffectiveWeight,
    string Verdict);

/// <summary>Alles, was zu einem Wert zusammenkommt.</summary>
public sealed record CombinedForecast(
    int AssetId, string Symbol, string? Name,
    decimal LastClose, DateTime AsOfUtc,
    IReadOnlyList<CombinedPoint> Points,
    IReadOnlyDictionary<string, int> Weights,
    string Note);

public interface ICombinedForecastService
{
    Task<CombinedForecast?> BuildAsync(
        int assetId, IReadOnlyDictionary<string, int> pillarWeights,
        CancellationToken ct = default);
}

/// <summary>
/// Mischt die Beiträge der Säulen zu einer Prognose.
///
/// <b>Warum es das braucht.</b> Die Säulengewichte in der Oberfläche wurden
/// gespeichert und von nichts gelesen — die Prognose kam allein aus dem
/// Ensemble. Wer an den Reglern zog, änderte nichts an den Zahlen im Diagramm.
/// Das ist die schlimmste Art von Bedienelement: eines, das aussieht, als täte
/// es etwas.
///
/// <b>Wie gemischt wird.</b> Nicht nach dem eingestellten Gewicht allein,
/// sondern nach <c>Gewicht × Verdienst</c>. Der Verdienst ist das, was die
/// Säule im Sperrbereich gezeigt hat — beim Deep-Learning-Modell also null,
/// solange es die blosse Drift nicht schlägt. Ein Regler auf hundert für eine
/// Säule, die nichts kann, verschiebt damit nichts.
///
/// Das ist bewusst so und nicht bevormundend gemeint: Wer den Regler zieht,
/// sieht in der Antwort, welchen Anteil sein Gewicht tatsächlich bekommen hat
/// und warum. Die Alternative wäre, ein nachweislich wertloses Signal in die
/// Zahl zu lassen, weil jemand einen Schieber bewegt hat.
/// </summary>
public sealed class CombinedForecastService(
    IForecastRepository forecasts,
    IAssetRepository assets,
    IPriceBarRepository bars,
    IDeepForecastService deep,
    IFeatureExportService features) : ICombinedForecastService
{
    public async Task<CombinedForecast?> BuildAsync(
        int assetId, IReadOnlyDictionary<string, int> pillarWeights,
        CancellationToken ct = default)
    {
        var asset = await assets.GetAsync(assetId, ct);
        if (asset is null) return null;

        var reihe = await bars.GetAsync(assetId, BarInterval.Daily,
                                        DateTime.UtcNow.AddDays(-30), DateTime.UtcNow, ct);

        if (reihe.Count == 0) return null;

        var letzte = reihe[^1];

        int W(string p) => pillarWeights.TryGetValue(p, out var w) ? Math.Clamp(w, 0, 100) : 0;

        // ------------------------------------------------- Säule 1: Ensemble

        var ensemble = (await forecasts.GetLatestAsync(assetId, ct))
            .GroupBy(f => f.HorizonHours)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.MadeAtUtc).First());

        // --------------------------------------------- Säule 4: Deep Learning

        var tief = new Dictionary<int, (double R, double Sd, DeepModelInfo Info, int Index)>();

        if (deep.IsLoaded && W("deep") > 0)
        {
            foreach (var info in deep.Models)
            {
                var fenster = await features.BuildWindowAsync(
                    assetId, BarInterval.Daily, info.SeqLen, ct);

                if (fenster is null) continue;

                var preds = deep.Predict(info.Band, assetId,
                                         fenster.Rows.ToArray(), fenster.LastClose);

                for (var k = 0; k < preds.Count; k++)
                {
                    // Bars in Stunden, damit beide Säulen dieselbe Achse haben.
                    var stunden = preds[k].HorizonBars * 24;
                    tief[stunden] = (preds[k].PredictedReturn, preds[k].StdDev, info, k);
                }
            }
        }

        /* Die Rückhaltemessung einmal holen, nicht je Horizont.

           Sie liegt ohnehin als eine Zeile je Horizont vor; sie im Schleifen-
           rumpf abzurufen wäre für dieselbe Antwort ein Dutzend Fahrten zur
           Datenbank. */
        var genauigkeit = (await forecasts.GetAccuracyAsync(assetId, ct))
            .ToDictionary(a => a.HorizonHours, a => (a.N, a.HitRate));

        var punkte = new List<CombinedPoint>();

        var horizonte = ensemble.Keys.Union(tief.Keys).OrderBy(h => h).ToList();

        foreach (var h in horizonte)
        {
            var beitraege = new List<PillarContribution>();

            if (ensemble.TryGetValue(h, out var e))
            {
                /* Der Verdienst des Ensembles kommt aus seiner eigenen
                   Rückhaltemessung: der Trefferquote der bereits bewerteten
                   Prognosen dieses Horizonts. 0,523 ist der Nullpunkt —
                   darunter ist ein Vorzeichen nicht besser als eine Münze.

                   Unter zwanzig bewerteten Fällen wird ein vorsichtiger
                   Zwischenwert angesetzt statt null: Eine Säule, die erst seit
                   kurzem läuft, hat sich noch nicht bewährt, aber auch noch
                   nicht widerlegt. */
                genauigkeit.TryGetValue(h, out var acc);

                var merit = acc.N >= 20
                    ? Math.Clamp((acc.HitRate - 0.523) / 0.10, 0, 1)
                    : 0.25;

                beitraege.Add(new PillarContribution(
                    "learning", (double)e.PredictedReturn, e.Confidence,
                    W("learning"), Math.Round(merit, 3),
                    acc.N >= 20
                        ? $"Trefferquote {acc.HitRate:P1} über {acc.N} bewertete Prognosen"
                        : "noch zu wenige bewertete Prognosen — vorsichtiger Ansatz"));
            }

            if (tief.TryGetValue(h, out var t))
            {
                /* Der Verdienst des Modells ist null, solange es die Drift
                   nicht schlägt — und das tut derzeit keines der drei Bänder.
                   Der Regler bleibt bedienbar, aber er bewegt nichts. */
                var traegt = t.Info.Carries(t.Index);

                var merit = traegt
                    ? Math.Clamp((1 - t.Info.TestErrorRatio) / 0.05, 0, 1)
                    : 0;

                beitraege.Add(new PillarContribution(
                    "deep", t.R, t.Sd > 1e-12 ? Math.Abs(t.R) / t.Sd : 0,
                    W("deep"), merit,
                    traegt
                        ? $"Band {t.Info.Band}: Fehlerverhältnis {t.Info.TestErrorRatio:F4}"
                        : $"Band {t.Info.Band} schlägt die blosse Drift nicht — Verdienst null"));
            }

            if (beitraege.Count == 0) continue;

            /* Gewichtet wird mit Gewicht × Verdienst.

               Bleibt die Summe bei null — alle Regler auf null, oder keine
               Säule mit gemessenem Verdienst —, wird auf gleiche Anteile
               zurückgefallen. Sonst gäbe es gar keine Zahl, und ein leeres
               Diagramm wäre die schlechtere Auskunft als eine Zahl mit dem
               Hinweis, dass ihr Rückhalt fehlt. */
            var summe = beitraege.Sum(b => b.Weight * b.Merit);

            double kombiniert;
            string urteil;

            if (summe > 1e-9)
            {
                kombiniert = beitraege.Sum(b => b.PredictedReturn * b.Weight * b.Merit) / summe;

                urteil = beitraege.Count(b => b.Merit > 0) == 1
                    ? $"getragen allein von der Säule „{beitraege.First(b => b.Merit > 0).Pillar}“"
                    : "aus mehreren Säulen gemischt";
            }
            else
            {
                /* Kein gemessener Verdienst — dann zählt das eingestellte
                   Gewicht allein.

                   Der erste Entwurf mittelte in diesem Fall gleich. Das war
                   falsch: Es nimmt dem Nutzer die Kontrolle genau dort, wo das
                   System ihm nichts Besseres anzubieten hat, und es lässt ein
                   nachweislich schlechteres Signal mit demselben Anteil
                   einfließen wie das beste vorhandene.

                   Wenn die Messung nichts hergibt, ist die Einstellung das
                   einzig verbliebene Argument. Sie bekommt es — und die
                   Antwort sagt daneben, dass sie ohne Rückhalt ist. */
                var gewichtssumme = beitraege.Sum(b => b.Weight);

                kombiniert = gewichtssumme > 0
                    ? beitraege.Sum(b => b.PredictedReturn * b.Weight) / gewichtssumme
                    : beitraege.Average(b => b.PredictedReturn);

                urteil = gewichtssumme > 0
                    ? "keine Säule hat gemessenen Verdienst — allein nach den "
                    + "eingestellten Gewichten gemischt, ohne Rückhalt"
                    : "alle Gewichte auf null — gleichgewichtet, ohne Rückhalt";
            }

            var basis = ensemble.TryGetValue(h, out var eb) ? eb.BaseClose : letzte.Close;

            punkte.Add(new CombinedPoint(
                h,
                HorizonLabel(h),
                (ensemble.TryGetValue(h, out var et) ? et.TargetTsUtc
                    : letzte.TsUtc.AddHours(h)),
                basis,
                Math.Round(basis * (decimal)Math.Exp(kombiniert), 6),
                Math.Round(kombiniert, 8),
                Math.Round((Math.Exp(kombiniert) - 1) * 100, 4),
                beitraege,
                Math.Round(summe / 100.0, 3),
                urteil));
        }

        return new CombinedForecast(
            assetId, asset.Symbol, asset.Name,
            letzte.Close, letzte.TsUtc, punkte,
            pillarWeights.ToDictionary(k => k.Key, v => v.Value),
            punkte.Any(p => p.EffectiveWeight > 0)
                ? "Gemischt nach Gewicht × Verdienst. Der Verdienst kommt aus dem "
                + "Sperrbereich, nicht aus der Einstellung."
                : "Keine Säule hat im Sperrbereich einen Verdienst gezeigt. Die Zahlen "
                + "stehen da, weil ein leeres Diagramm die schlechtere Auskunft wäre — "
                + "eine Handelsgrundlage sind sie nicht.");
    }

    private static string HorizonLabel(int h) => h switch
    {
        1 => "1 h",
        4 => "4 h",
        24 => "1 Tag",
        72 => "3 Tage",
        168 => "1 Woche",
        336 => "2 Wochen",
        720 => "1 Monat",
        2160 => "3 Monate",
        4380 => "6 Monate",   // 4320 stand hier -- das sind 180 Tage, nicht 6 Monate
        8760 => "1 Jahr",
        _ => h < 24 ? $"{h} h" : $"{h / 24} Tage"
    };
}
