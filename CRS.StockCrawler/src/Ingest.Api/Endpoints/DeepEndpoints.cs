using System.Globalization;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Ingest.Infrastructure.Services;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Säule „Deep Learning": ein globales Sequenzmodell über alle Werte, in Python
/// trainiert und hier über ONNX ausgeführt.
///
/// <b>Was diese Säule anders macht als die übrigen.</b> Die anderen Verfahren
/// sind aufgeschrieben — man kann nachlesen, warum sie zu ihrem Ergebnis
/// kommen. Dieses nicht. Es ist gelernt, und was es gelernt hat, steht in
/// Gewichten und nicht in Sätzen.
///
/// Genau deshalb ist die Latte hier höher, nicht niedriger. Ein Verfahren, das
/// sich nicht erklären kann, muss sich umso deutlicher messen lassen: Es liefert
/// mit jeder Vorhersage die Zahlen mit, die im Sperrbereich gemessen wurden —
/// Fehlerverhältnis und Trefferquote —, und wer es benutzt, sieht sie
/// zusammen mit der Vorhersage und nicht auf Nachfrage.
/// </summary>
public static class DeepEndpoints
{
    public static void MapDeepEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/deep").WithTags("Deep Learning");

        /* Zustand: Ist ein Modell da, und was hat es im Sperrbereich geleistet?
           Ohne diese Auskunft wäre jede Vorhersage eine Behauptung. */
        g.MapGet("/status", (IDeepForecastService svc) =>
        {
            if (!svc.IsLoaded)
                return Results.Ok(new
                {
                    geladen = false,
                    modelle = Array.Empty<object>(),
                    hinweis = "Kein trainiertes Modell vorhanden. Erst exportieren "
                            + "(POST /api/model/export), dann ml/train_deep.py laufen lassen."
                });

            object Beschreiben(DeepModelInfo i) => new
            {
                i.Band,
                i.BandLabel,
                i.Version,
                i.SeqLen,
                merkmale = i.Features.Count,
                horizonte = i.Horizons,
                werte = i.AssetIds.Count,

                zeitsplit = new
                {
                    trainingBis = i.TrainUntil,
                    abstimmungBis = i.ValidateUntil,
                    sperrbereichBis = i.TestUntil
                },

                gemessen = new
                {
                    fehlerverhaeltnisAbstimmung = Math.Round(i.ValidationErrorRatio, 4),
                    fehlerverhaeltnisSperrbereich = Math.Round(i.TestErrorRatio, 4),
                    trefferquoteSperrbereich = Math.Round(i.TestHitRate * 100, 2),

                    /* Je Horizont, nicht nur im Mittel. Ein Modell kann bei
                       zehn Tagen tragen und bei sechzig nicht — der Mittelwert
                       verdeckt genau das, worauf es bei der Gewichtung
                       ankommt. */
                    jeHorizont = i.Horizons.Select((h, k) => new
                    {
                        horizontBars = h,
                        fehlerverhaeltnis = k < i.TestErrorRatioByHorizon.Count
                            ? Math.Round(i.TestErrorRatioByHorizon[k], 4)
                            : (double?)null,
                        trefferquote = k < i.TestHitRateByHorizon.Count
                            ? Math.Round(i.TestHitRateByHorizon[k] * 100, 2)
                            : (double?)null,

                        /* Die Driftlatte daneben, nicht darunter. Ohne sie
                           liest man 0,96 als Erfolg — und genau das war es
                           nicht. */
                        driftFehlerverhaeltnis = k < i.DriftErrorRatioByHorizon.Count
                            ? Math.Round(i.DriftErrorRatioByHorizon[k], 4)
                            : (double?)null,
                        driftTrefferquote = k < i.DriftHitRateByHorizon.Count
                            ? Math.Round(i.DriftHitRateByHorizon[k] * 100, 2)
                            : (double?)null,
                        traegt = i.Carries(k)
                    })
                },

                /* Der Vorschlag, nicht die Vorschrift.

                   Das Gewicht setzt der Nutzer. Was die Anwendung beisteuern
                   kann, ist die Zahl, auf der eine begründete Entscheidung
                   beruht: Ein Modell, das den Stillstand nicht schlägt, hat
                   keinen Anspruch auf Gewicht -- und das lässt sich messen,
                   nicht meinen. */
                gewichtsvorschlag = Vorschlag(i),

                urteil = i.Verdict()
            };

            var modelle = svc.Models.Select(Beschreiben).ToList();

            return Results.Ok(new
            {
                geladen = true,
                modelle,
                fehlend = new[] { "kurz", "mittel", "lang" }
                    .Where(b => svc.Model(b) is null).ToArray(),

                hinweis = "Ein Fehlerverhältnis unter 1 heißt, die Vorhersage ist näher an der "
                        + "Wirklichkeit als die Annahme, der Kurs bleibe stehen. Die Zahlen "
                        + "stammen aus dem Sperrbereich, den das Training nie gesehen hat."
            });
        });

        /* Rückblick: dieselbe Frage an jedem Tag der Vergangenheit gestellt und
           neben das gelegt, was tatsächlich eintrat.

           Standardmäßig nur der Sperrbereich. Der Rückblick über den
           Trainingszeitraum sähe erheblich besser aus und wäre wertlos — das
           Modell hat diese Tage auswendig gelernt. Wer ihn trotzdem sehen will,
           setzt `abTraining=true`; die Antwort weist ihn dann als solchen aus. */
        g.MapGet("/backtest/{assetId:int}", async (IDeepForecastService svc,
                                                   IAssetRepository assets,
                                                   IFeatureExportService features,
                                                   int assetId,
                                                   string band = "kurz",
                                                   int horizon = 0,
                                                   bool abTraining = false,
                                                   int maxPunkte = 600,
                                                   string interval = BarInterval.Daily,
                                                   CancellationToken ct = default) =>
        {
            if (!svc.IsLoaded)
                return Results.BadRequest(new { error = "Kein trainiertes Modell geladen" });

            var asset = await assets.GetAsync(assetId, ct);
            if (asset is null) return Results.BadRequest(new { error = "Wert unbekannt" });

            var info = svc.Model(band);

            if (info is null)
                return Results.BadRequest(new
                {
                    error = $"Kein Modell für das Band \"{band}\"",
                    verfuegbar = svc.Models.Select(x => x.Band)
                });

            var h = horizon > 0 ? horizon : info.Horizons[0];

            if (!info.Horizons.Contains(h))
                return Results.BadRequest(new
                {
                    error = $"Horizont {h} gehört nicht zu diesem Modell",
                    verfuegbar = info.Horizons
                });

            var hist = await features.BuildHistoryAsync(assetId, interval, ct);

            if (hist is null || hist.Rows.Count < info.SeqLen + h + 1)
                return Results.BadRequest(new { error = "Zu wenig Historie für einen Rückblick" });

            DateTime? from = abTraining || !DateTime.TryParse(
                info.ValidateUntil, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var cut) ? null : cut;

            var bt = svc.Backtest(band, assetId, hist.Rows, hist.Close, hist.TsUtc,
                                  h, from, Math.Clamp(maxPunkte, 50, 5000));

            if (bt is null)
                return Results.BadRequest(new { error = "Rückblick nicht berechenbar" });

            return Results.Ok(new
            {
                assetId,
                symbol = asset.Symbol,
                band = bt.Band,
                bandLabel = info.BandLabel,
                horizontBars = bt.HorizonBars,
                bereich = from is null ? "gesamte Historie (enthält Trainingszeitraum)"
                                       : "Sperrbereich — vom Training nie gesehen",
                abUtc = bt.FromUtc,
                punkte = bt.Points.Select(pt => new
                {
                    ts = pt.TsUtc,
                    prognose = pt.PredictedClose,
                    tatsaechlich = pt.ActualClose,
                    prognosePct = pt.PredictedPct,
                    tatsaechlichPct = pt.ActualPct,
                    streuung = pt.StdDev,
                    abweichungPct = Math.Round(pt.PredictedPct - pt.ActualPct, 4)
                }),
                kennzahlen = new
                {
                    mittlererFehlerPct = bt.MeanAbsErrorPct,
                    fehlerOhneModellPct = bt.NaiveAbsErrorPct,
                    fehlerverhaeltnis = bt.ErrorRatio,
                    trefferquotePct = bt.HitRate,
                    verzerrungPct = bt.Bias,
                    anzahl = bt.Points.Count
                },
                urteil = bt.ErrorRatio < 1 && bt.HitRate > 52.3
                    ? "schlägt beide Latten"
                    : bt.ErrorRatio < 1
                        ? "schlägt den Stillstand, aber nicht die erste Säule"
                        : "schlägt den Stillstand nicht — die Prognose ist im Mittel "
                        + "weiter von der Wahrheit entfernt als die Annahme, es ändere sich nichts"
            });
        });

        /* Vorhersage für einen Wert über alle Horizonte des Modells. */
        /* Vorhersage über alle geladenen Modelle.

           Die drei Bänder stehen nicht in Konkurrenz — sie beantworten
           verschiedene Fragen. „Wo steht der Kurs morgen“ und „wo in einem
           Jahr" sind nicht zwei Meinungen zur selben Sache, die man mitteln
           könnte. Die Gewichte sagen deshalb nicht, welchem Modell man mehr
           glaubt, sondern welchen Anteil ein Band am Urteil der Säule hat. */
        g.MapGet("/forecast/{assetId:int}", async (IDeepForecastService svc,
                                                   IAssetRepository assets,
                                                   IFeatureExportService features,
                                                   int assetId,
                                                   int wKurz = 100,
                                                   int wMittel = 100,
                                                   int wLang = 100,
                                                   string interval = BarInterval.Daily,
                                                   CancellationToken ct = default) =>
        {
            if (!svc.IsLoaded)
                return Results.BadRequest(new { error = "Kein trainiertes Modell geladen" });

            var asset = await assets.GetAsync(assetId, ct);
            if (asset is null) return Results.BadRequest(new { error = "Wert unbekannt" });

            var gewicht = new Dictionary<string, int>
            {
                ["kurz"] = Math.Clamp(wKurz, 0, 100),
                ["mittel"] = Math.Clamp(wMittel, 0, 100),
                ["lang"] = Math.Clamp(wLang, 0, 100)
            };

            var step = BarInterval.Duration(interval);

            var baender = new List<object>();
            var fehlend = new List<object>();

            double lastClose = 0;
            DateTime stand = default;

            /* Gewichtssumme nur über Bänder, die tatsächlich geantwortet haben.
               Sonst verschöbe ein Modell, das gar nichts liefert, still die
               Anteile der übrigen — der Nutzer sähe Prozente, die nicht
               stimmen. */
            var wirksam = new Dictionary<string, int>();

            foreach (var info in svc.Models)
            {
                var w = gewicht.GetValueOrDefault(info.Band, 0);

                /* Jedes Band hat seine eigene Fensterlänge — 48, 96, 128 Bars.
                   Das Fenster muss deshalb je Modell neu gebaut werden; eines
                   für alle wäre entweder zu kurz oder verschwendete Historie.

                   Neu gebaut wird es mit demselben Aufbau wie beim Export. Eine
                   zweite Quelle der Wahrheit wäre hier eine Fehlerquelle:
                   Weicht sie in einem einzigen Merkmal ab, rechnet das Modell
                   stillschweigend falsch. */
                var window = await features.BuildWindowAsync(assetId, interval, info.SeqLen, ct);

                if (window is null)
                {
                    fehlend.Add(new
                    {
                        info.Band,
                        info.BandLabel,
                        grund = "zu wenig Historie für das Zeitfenster dieses Modells",
                        benoetigt = info.SeqLen + FeatureSet.MinHistoryBars
                    });
                    continue;
                }

                var preds = svc.Predict(info.Band, assetId, window.Rows.ToArray(), window.LastClose);

                if (preds.Count == 0)
                {
                    fehlend.Add(new
                    {
                        info.Band,
                        info.BandLabel,
                        grund = "das Modell kennt diesen Wert nicht — beim Training nicht dabei",
                        benoetigt = 0
                    });
                    continue;
                }

                lastClose = window.LastClose;
                stand = window.LastTsUtc;

                wirksam[info.Band] = w;

                baender.Add(new
                {
                    info.Band,
                    info.BandLabel,
                    info.Version,
                    gewicht = w,

                    gemessen = new
                    {
                        fehlerverhaeltnisSperrbereich = Math.Round(info.TestErrorRatio, 4),
                        trefferquoteSperrbereich = Math.Round(info.TestHitRate * 100, 2)
                    },

                    traegt = info.CarriesAny(),
                    gewichtsvorschlag = Vorschlag(info),

                    prognosen = preds.Select(pr => new
                    {
                        horizontBars = pr.HorizonBars,
                        ziel = window.LastTsUtc.AddTicks(step.Ticks * pr.HorizonBars),
                        kurs = pr.PredictedClose,
                        pr.ChangePct,

                        /* Die Streuung ist keine Zierde. Sie ist das, was das
                           Modell über seine eigene Unsicherheit gelernt hat,
                           und sie gehört zu jeder einzelnen Vorhersage — eine
                           Zahl ohne sie legt eine Genauigkeit nahe, die nicht
                           da ist. */
                        streuungPct = Math.Round((Math.Exp(pr.StdDev) - 1) * 100, 4),

                        bandUntenPct = Math.Round((Math.Exp(pr.PredictedReturn - pr.StdDev) - 1) * 100, 4),
                        bandObenPct = Math.Round((Math.Exp(pr.PredictedReturn + pr.StdDev) - 1) * 100, 4),

                        /* Verhältnis von Aussage zu Unsicherheit. Unter eins
                           sagt das Modell selbst, dass sein Vorzeichen nicht
                           trägt. */
                        aussagekraft = pr.StdDev > 1e-12
                            ? Math.Round(Math.Abs(pr.PredictedReturn) / pr.StdDev, 3)
                            : 0
                    })
                });
            }

            if (baender.Count == 0)
                return Results.BadRequest(new
                {
                    error = "Kein Band konnte eine Vorhersage liefern",
                    fehlend
                });

            var summe = wirksam.Values.Sum();

            var anteile = wirksam.ToDictionary(
                kv => kv.Key,
                kv => summe > 0 ? Math.Round(kv.Value * 100.0 / summe, 1) : 0.0);

            /* Wie viel von der Gewichtung auf Bänder entfällt, die sich im
               Sperrbereich bewährt haben.

               Diese Zahl kann der Nutzer nicht selbst ausrechnen, ohne drei
               Messwerte im Kopf zu behalten — und ohne sie sieht eine Säule mit
               Gewicht 100 genauso aus, ob sie nun auf tragenden Modellen ruht
               oder auf dreien, die den Stillstand nicht schlagen. */
            var tragend = svc.Models
                .Where(m => wirksam.ContainsKey(m.Band) && m.CarriesAny())
                .Sum(m => wirksam[m.Band]);

            return Results.Ok(new
            {
                asset.Symbol,
                asset.Name,
                interval,
                letzterKurs = Math.Round(lastClose, 6),
                stand,

                baender,
                fehlend,

                gewichtung = new
                {
                    je = wirksam,
                    anteile,
                    summe,
                    tragenderAnteil = summe > 0 ? Math.Round(tragend * 100.0 / summe, 1) : 0.0
                },

                hinweis = tragend == 0
                    ? "Kein geladenes Modell schlägt im Sperrbereich die Annahme, es ändere "
                    + "sich nichts. Die Gewichte lassen sich setzen, aber sie verteilen dann "
                    + "Anteile an einem Beitrag, den die Messung nicht bestätigt."
                    : "Die Anteile verteilen sich auf die Bänder; das Gewicht der Säule selbst "
                    + "entscheidet, wie stark sie gegenüber den übrigen Säulen zählt."
            });
        });

        /* Der Gewichtsvorschlag der Anwendung.

           Bewusst grob und bewusst streng: Wer den Stillstand nicht schlägt,
           bekommt null — nicht wenig, sondern null. Ein Verfahren, das
           schlechter ist als gar keines, wird durch ein kleines Gewicht nicht
           besser, sondern nur unauffälliger. */
        static int Vorschlag(DeepModelInfo i)
        {
            if (i.TestErrorRatio >= 1) return 0;
            if (i.TestHitRate <= 0.523) return 0;

            // Und die zweite Latte: Wer die blosse Drift nicht schlägt, hat
            // gelernt, dass Kurse steigen. Dafür braucht es kein Netz.
            if (!i.CarriesAny()) return 0;

            // Vorsprung im Fehler und in der Richtung, beide gedeckelt.
            var fehler = Math.Clamp((1 - i.TestErrorRatio) / 0.05, 0, 1);
            var richtung = Math.Clamp((i.TestHitRate - 0.523) / 0.05, 0, 1);

            return (int)Math.Round(100 * Math.Min(fehler, richtung));
        }
    }

}
