using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Die Lernschleife. Für jede fällig gewordene Prognose wird der tatsächliche
/// Kurs nachgeschlagen, der Fehler bestimmt und anschließend das Gewicht jedes
/// Teilmodells nachgezogen — Modelle, die daneben lagen, verlieren an Einfluss,
/// treffsichere gewinnen. Über die Zeit verschiebt sich das Ensemble damit von
/// selbst auf die Teilmodelle, die für dieses Asset und diesen Horizont
/// tatsächlich funktionieren.
/// </summary>
public sealed class ScoringService : IScoringService
{
    private readonly IForecastRepository _forecasts;
    private readonly IPriceBarRepository _bars;
    private readonly IIngestRunRepository _runs;
    private readonly ILogger<ScoringService> _log;

    private const int BatchSize = 5000;

    /// <summary>
    /// Wie lange nach dem Zielzeitpunkt noch ein Kurs akzeptiert wird. Ohne
    /// diese Grenze würde eine Freitagabend-Prognose gegen den Montagskurs
    /// bewertet und der Fehler wäre bedeutungslos.
    /// </summary>
    private static readonly TimeSpan MaxStaleness = TimeSpan.FromDays(4);

    /// <summary>Untergrenze des SQL-Server-Datumsbereichs.</summary>
    private static readonly DateTime SqlMinDate = new(1753, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public ScoringService(
        IForecastRepository forecasts,
        IPriceBarRepository bars,
        IIngestRunRepository runs,
        ILogger<ScoringService> log)
    {
        _forecasts = forecasts;
        _bars = bars;
        _runs = runs;
        _log = log;
    }

    public async Task<ScoringResult> ScoreDueAsync(CancellationToken ct = default)
    {
        var runId = await _runs.StartAsync("scoring", null, ct);
        var now = DateTime.UtcNow;

        try
        {
            /* Zuerst die dauerhaft nicht Bewertbaren aussortieren.

               Ohne das staut sich die Warteschlange am Kopf: `get_due_forecasts`
               nimmt die ältesten 5.000 nach Zielzeitpunkt, und wenn deren
               Zielzeitpunkt in einem geschlossenen Marktfenster liegt, kann dort
               nie eine neue Bar erscheinen. Gemessen am 23.08.2026: 13.988 von
               18.086 fälligen Prognosen betroffen, und seit dem Vortag 16:09 kam
               kein einziger Bewertungslauf mehr durch. Damit hörte auch
               `UpdateWeights` auf zu lernen -- ohne Fehler, ohne Meldung, nur
               ein Lauf, der jede Stunde dasselbe erfolglos versucht. */
            var zurueckgestellt = await _forecasts.MarkUnscoreableAsync(ct);

            if (zurueckgestellt > 0)
                _log.LogInformation(
                    "{Anzahl} Prognosen dauerhaft nicht bewertbar — zurückgestellt", zurueckgestellt);

            var due = await _forecasts.GetDueAsync(now, BatchSize, ct);
            if (due.Count == 0)
            {
                await _runs.FinishAsync(runId, 0, 0, 0, "nichts fällig", ct);
                return new ScoringResult(0, 0, 0, 0);
            }

            var components = (await _forecasts.GetComponentsAsync(due.Select(f => f.ForecastId), ct))
                .GroupBy(c => c.ForecastId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var scores = new List<ForecastScore>();
            var weightUpdates = new List<ModelWeight>();

            /* Die gemischte Zahl wird SEPARAT bewertet.

               Nur so lässt sich die eigentliche Frage beantworten: Bringt das Mischen
               über alle Säulen etwas gegenüber der ersten allein? Ohne diese zweite
               Bewertung wäre das eine Glaubensfrage -- man sähe eine andere Zahl und
               wüsste nicht, ob sie besser ist.

               Die Gewichte der Teilmodelle bleiben davon unberührt: Sie lernen weiter
               aus `predicted_close`, denn nur dafür erklären die Komponenten die
               Vorhersage. */
            var mischScores = new List<(long Id, decimal Ist, double IstRendite,
                                        double Fehler, bool? Richtung)>();

            double errSum = 0;
            var hits = 0;
            var scored = 0;

            // Gewichte je (Asset, Horizont) sammeln, damit mehrere Prognosen
            // derselben Gruppe nacheinander auf denselben Stand aufbauen.
            var weightCache = new Dictionary<(int, int), Dictionary<string, ModelWeight>>();

            var skipped = 0;
            var notDueYet = 0;

            foreach (var f in due)
            {
                ct.ThrowIfCancellationRequested();

                /* Unplausible Datensätze überspringen statt den Lauf abzubrechen.
                   Ein Zeitstempel außerhalb des SQL-Bereichs (etwa 0001-01-01 aus
                   einem fehlgeschlagenen Mapping) hat die gesamte Lernschleife
                   zum Absturz gebracht — sie darf an einer einzelnen kaputten
                   Zeile nicht scheitern. */
                if (f.TargetTsUtc < SqlMinDate || f.AssetId <= 0 || f.BaseClose <= 0)
                {
                    skipped++;
                    continue;
                }

                var interval = f.HorizonHours >= 96 ? BarInterval.Daily : BarInterval.Hourly;

                var bar = await _bars.GetBarAtOrBeforeAsync(f.AssetId, interval, f.TargetTsUtc, ct);
                if (bar is null || bar.Value.Close <= 0) continue;

                /* Entscheidend: Es muss eine NEUE Bar sein.

                   Der Stundenlauf feuert auch außerhalb der US-Handelszeiten.
                   Für Aktien und ETFs existiert dann keine Bar zwischen
                   Prognose und Zielzeitpunkt, und die Suche liefert genau die
                   Bar zurück, auf der die Prognose beruhte. Der Kurs würde mit
                   sich selbst verglichen: die Rendite ist exakt null, und eine
                   Prognose nahe null gälte als Treffer.

                   Gemessen an echten Daten waren so 300 von 300 Aktien- und
                   283 von 283 ETF-Bewertungen entstanden — eine Trefferquote
                   von 93,6 %, die nichts aussagt. Solche Prognosen sind noch
                   nicht fällig, nicht falsch. */
                if (bar.Value.TsUtc <= f.MadeAtUtc)
                {
                    notDueYet++;
                    continue;
                }

                // Der gefundene Kurs muss auch zeitlich nah am Ziel liegen.
                if (bar.Value.TsUtc < f.TargetTsUtc - MaxStaleness) continue;

                var actual = (decimal?)bar.Value.Close;

                var actualReturn = f.BaseClose > 0 && actual > 0
                    ? Math.Log((double)actual.Value / (double)f.BaseClose)
                    : 0;

                var absPctError = f.BaseClose > 0
                    ? Math.Abs((double)(actual.Value - f.PredictedClose) / (double)f.BaseClose)
                    : 0;

                // Als Treffer zählt die richtige Richtung. Bei praktisch
                // unbewegtem Kurs ist die Richtungsfrage sinnlos — dann gilt
                // sie als getroffen, wenn auch die Prognose kaum Bewegung sah.
                var directionCorrect = Math.Abs(actualReturn) < 1e-6
                    ? Math.Abs(f.PredictedReturn) < 1e-3
                    : Math.Sign(f.PredictedReturn) == Math.Sign(actualReturn);

                scores.Add(new ForecastScore
                {
                    ForecastId = f.ForecastId,
                    ActualClose = actual.Value,
                    ActualReturn = actualReturn,
                    AbsPctError = absPctError,
                    DirectionCorrect = directionCorrect
                });

                /* Dieselbe Rechnung für die Mischung -- sofern eine da ist. Ältere
                   Prognosen aus der Zeit vor der Säulenmischung haben keine. */
                if (f.CombinedClose is { } misch && misch > 0)
                {
                    var mischFehler = Math.Abs((double)((misch - actual.Value) / actual.Value));

                    var mischRichtung = f.CombinedReturn is null || Math.Abs(actualReturn) < 1e-12
                        ? (bool?)null
                        : Math.Sign(f.CombinedReturn.Value) == Math.Sign(actualReturn);

                    mischScores.Add((f.ForecastId, actual.Value, actualReturn,
                                     mischFehler, mischRichtung));
                }

                errSum += absPctError;
                if (directionCorrect) hits++;
                scored++;

                if (!components.TryGetValue(f.ForecastId, out var comps) || comps.Count == 0) continue;

                var key = (f.AssetId, f.HorizonHours);
                if (!weightCache.TryGetValue(key, out var current))
                {
                    var stored = await _forecasts.GetWeightsAsync(f.AssetId, f.HorizonHours, ct);
                    current = stored.ToDictionary(w => w.ModelName, w => w);
                    weightCache[key] = current;
                }

                UpdateWeights(current, comps, f, actualReturn);
            }

            await _forecasts.ScoreAsync(scores, ct);
            await _forecasts.ScoreCombinedAsync(mischScores, ct);

            foreach (var group in weightCache.Values)
                weightUpdates.AddRange(group.Values);

            await _forecasts.UpsertWeightsAsync(weightUpdates, ct);

            var mape = scored > 0 ? errSum / scored : 0;
            var hitRate = scored > 0 ? (double)hits / scored : 0;

            await _runs.FinishAsync(runId, scored, skipped, weightUpdates.Count,
                $"MAPE {mape:P2}, Trefferquote {hitRate:P1}"
                + (skipped > 0 ? $", {skipped} unplausible übersprungen" : "")
                + (notDueYet > 0 ? $", {notDueYet} mangels neuer Bar zurückgestellt" : ""), ct);

            if (notDueYet > 0)
                _log.LogInformation(
                    "Scoring: {Count} Prognosen zurückgestellt — zum Zielzeitpunkt lag " +
                    "keine neue Bar vor (Börse geschlossen)", notDueYet);

            if (skipped > 0)
                _log.LogWarning("Scoring: {Skipped} Prognosen mit unplausiblen Werten übersprungen", skipped);

            _log.LogInformation(
                "Scoring: {Scored} Prognosen bewertet, {Weights} Gewichte aktualisiert, " +
                "mittlerer Fehler {Mape:P2}, Richtung {Hit:P1}",
                scored, weightUpdates.Count, mape, hitRate);

            return new ScoringResult(scored, weightUpdates.Count, mape, hitRate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _runs.FinishAsync(runId, 0, 1, 0, ex.Message, ct);
            throw;
        }
    }

    /// <summary>
    /// Wendet die Hedge-Regel an und schreibt Fehler- und Trefferstatistik fort.
    /// </summary>
    private static void UpdateWeights(
        Dictionary<string, ModelWeight> current,
        List<ForecastComponent> comps,
        Forecast f,
        double actualReturn)
    {
        var currentWeights = current.ToDictionary(kv => kv.Key, kv => kv.Value.Weight);
        var componentReturns = comps.ToDictionary(c => c.ModelName, c => c.PredictedReturn);

        // Fehlerskala: die tatsächliche Bewegung dient als Maßstab dafür, was
        // ein "großer" Fehler ist. Bei einem ruhigen Wert wiegt derselbe
        // absolute Fehler schwerer als bei einem volatilen.
        var scale = Math.Max(0.002, Math.Abs(actualReturn) * 2);

        var updated = Ensemble.UpdateWeights(currentWeights, componentReturns, actualReturn, scale: scale);

        foreach (var c in comps)
        {
            if (!current.TryGetValue(c.ModelName, out var mw))
            {
                mw = new ModelWeight
                {
                    AssetId = f.AssetId,
                    HorizonHours = f.HorizonHours,
                    ModelName = c.ModelName,
                    Weight = 1.0 / Math.Max(1, comps.Count),
                    NObs = 0,
                    MeanAbsPctErr = 0,
                    HitRate = 0.5
                };
                current[c.ModelName] = mw;
            }

            var compError = Math.Abs(c.PredictedReturn - actualReturn);

            var compHit = Math.Abs(actualReturn) < 1e-6
                ? Math.Abs(c.PredictedReturn) < 1e-3
                : Math.Sign(c.PredictedReturn) == Math.Sign(actualReturn);

            var (newErr, n) = Ensemble.RunningMean(mw.MeanAbsPctErr, mw.NObs, compError);
            var (newHit, _) = Ensemble.RunningMean(mw.HitRate, mw.NObs, compHit ? 1.0 : 0.0);

            mw.MeanAbsPctErr = newErr;
            mw.HitRate = newHit;
            mw.NObs = n;
            mw.Weight = updated.TryGetValue(c.ModelName, out var w) ? w : mw.Weight;
        }
    }
}
