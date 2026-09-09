using System.Diagnostics;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Ingest.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Erzeugt für jedes getrackte Asset Prognosen über mehrere Horizonte —
/// von einer Stunde bis zu einem Monat. Kurze Horizonte rechnen auf
/// Stundenbars, lange auf Tagesbars, weil Stundenhistorie nur begrenzt
/// verfügbar ist und für Monatsprognosen ohnehin zu kleinteilig wäre.
/// </summary>
public sealed class ForecastService : IForecastService
{
    private readonly IAssetRepository _assets;
    private readonly IPriceBarRepository _bars;
    private readonly IPairStatRepository _pairs;
    private readonly IForecastRepository _forecasts;
    private readonly IIngestRunRepository _runs;
    private readonly IngestOptions _opt;
    private readonly ILogger<ForecastService> _log;

    /// <summary>Die Zahlenbeiträge der übrigen Säulen.</summary>
    private readonly ISaeulenbeitragService _saeulen;

    private readonly IReadOnlyList<IForecastModel> _models = Ensemble.DefaultModels();

    /// <summary>Ab diesem Horizont wird auf Tagesbars umgestellt.</summary>
    private const int DailySwitchHours = 96;

    /// <summary>Wie viele Bars Historie die Modelle mindestens brauchen.</summary>
    private const int MinHistoryBars = 60;

    /// <summary>Wie viele Frühindikatoren je Asset berücksichtigt werden.</summary>
    private const int MaxLeaders = 5;

    public ForecastService(
        IAssetRepository assets,
        IPriceBarRepository bars,
        IPairStatRepository pairs,
        IForecastRepository forecasts,
        IIngestRunRepository runs,
        IOptions<IngestOptions> opt,
        ISaeulenbeitragService saeulen,
        ILogger<ForecastService> log)
    {
        _saeulen = saeulen;
        _assets = assets;
        _bars = bars;
        _pairs = pairs;
        _forecasts = forecasts;
        _runs = runs;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<ForecastResult> RunAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var runId = await _runs.StartAsync("forecast", null, ct);

        var tracked = await _assets.GetTrackedAsync(ct);
        var made = 0;
        var failed = 0;

        /* Die Beiträge der übrigen Säulen EINMAL für alle Werte holen.

           Je Wert einzeln wären es bei 600 Werten mehrere tausend Fahrten zur Datenbank
           für einen Lauf, der heute dreizehn Sekunden dauert. Gesammelt wird deshalb
           vorab; die Zuordnung geschieht danach im Speicher.

           Scheitert das Sammeln, läuft die Prognose trotzdem -- dann eben nur aus der
           ersten Säule. Eine Nebensäule darf den Lauf nicht anhalten. */
        IReadOnlyDictionary<int, Saeulenlage> lage;

        try
        {
            lage = await _saeulen.SammleAsync(_opt.EffectiveHorizons, ct);
            _log.LogInformation("Säulenbeiträge für {Anzahl} Werte gesammelt", lage.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Säulenbeiträge konnten nicht gesammelt werden — "
                              + "es wird allein aus der ersten Säule prognostiziert");
            lage = new Dictionary<int, Saeulenlage>();
        }

        var gewichte = await _saeulen.GewichteAsync(ct);

        foreach (var asset in tracked)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await RunForAssetInternAsync(asset.AssetId, ct,
                                                     lage.GetValueOrDefault(asset.AssetId), gewichte);
                made += r.Forecasts;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _log.LogWarning(ex, "Prognose für {Symbol} fehlgeschlagen", asset.Symbol);
            }
        }

        await _runs.FinishAsync(runId, made, failed, made, null, ct);
        _log.LogInformation("Prognosen: {Made} für {Assets} Assets in {Elapsed:n1}s",
            made, tracked.Count, sw.Elapsed.TotalSeconds);

        return new ForecastResult(tracked.Count, made, sw.Elapsed);
    }

    /// <summary>
    /// Einzelaufruf von aussen — holt sich die Säulenlage selbst.
    ///
    /// <para>Für einen einzelnen Wert lohnt die Massenabfrage nicht; sie hielte nur den
    /// Aufrufer auf, der genau einen Wert wissen will.</para>
    /// </summary>
    public async Task<ForecastResult> RunForAssetAsync(int assetId, CancellationToken ct = default)
    {
        var lage = (await _saeulen.SammleAsync(_opt.EffectiveHorizons, ct))
            .GetValueOrDefault(assetId);

        var gewichte = await _saeulen.GewichteAsync(ct);

        return await RunForAssetInternAsync(assetId, ct, lage, gewichte);
    }

    private async Task<ForecastResult> RunForAssetInternAsync(int assetId, CancellationToken ct,
        Saeulenlage? lage = null,
        IReadOnlyDictionary<string, int>? saeulengewichte = null)
    {
        var sw = Stopwatch.StartNew();
        var now = DateTime.UtcNow;

        // Auf volle Minute runden, damit ein erneuter Lauf dieselbe Prognose
        // ersetzt statt eine zweite anzulegen.
        var madeAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);

        var made = 0;

        // Historie je Intervall nur einmal laden, nicht je Horizont.
        var cache = new Dictionary<string, List<decimal>>();
        var leaderCache = new Dictionary<string, IReadOnlyList<LeadSignal>>();

        /* Die Spektralsäule wird EINMAL je Wert gerechnet, nicht je Horizont.

           Eine Zerlegung über 400 Bars kostet spürbar Zeit; sie siebenmal für denselben
           Wert zu rechnen wäre siebenmal dasselbe. Fortgeschrieben wird bis zum längsten
           Horizont, die kürzeren werden unterwegs abgelesen. */
        Spektrallage? spektral = null;

        /* Die gemessene Trefferquote der ersten Säule -- je Horizont, aus den bereits
           bewerteten Prognosen dieses Wertes. Einmal je Wert geholt.

           Vorher stand hier ein pauschaler Verdienst von 0,25, sobald die Zuversicht des
           Ensembles unter der Schwelle lag. Das ist fast immer der Fall, und es machte
           Säule 1 grundsätzlich schwach: Jede andere Säule mit bescheidenem gemessenem
           Vorsprung bekam damit die Hälfte des Gewichts. Ein Rückfallwert ist keine
           Messung, und wo eine Messung vorliegt, hat er nichts verloren. */
        var genauigkeit = (await _forecasts.GetAccuracyAsync(assetId, ct))
            .ToDictionary(a => a.HorizonHours, a => (a.N, a.HitRate));

        foreach (var horizonHours in _opt.EffectiveHorizons)
        {
            var interval = horizonHours >= DailySwitchHours ? BarInterval.Daily : BarInterval.Hourly;

            if (!cache.TryGetValue(interval, out var closes))
            {
                var lookback = BarInterval.Duration(interval) * 400;
                var bars = await _bars.GetAsync(assetId, interval, now - lookback, now, ct);
                closes = bars.Select(b => b.Close).ToList();
                cache[interval] = closes;
            }

            if (closes.Count < MinHistoryBars)
            {
                _log.LogDebug("Asset {AssetId}: nur {Count} {Interval}-Bars, Horizont {H}h übersprungen",
                    assetId, closes.Count, interval, horizonHours);
                continue;
            }

            if (!leaderCache.TryGetValue(interval, out var leaders))
            {
                leaders = await BuildLeadSignalsAsync(assetId, interval, now, ct);
                leaderCache[interval] = leaders;
            }

            var horizonBars = interval == BarInterval.Hourly
                ? horizonHours
                : Math.Max(1, horizonHours / 24);

            var input = new ForecastInput(closes, horizonBars, leaders);

            var stored = await _forecasts.GetWeightsAsync(assetId, horizonHours, ct);
            var weights = stored.ToDictionary(w => w.ModelName, w => w.Weight);
            var hitRates = stored.ToDictionary(w => w.ModelName, w => w.HitRate);

            var result = Ensemble.Combine(_models, input, weights, hitRates);

            var baseClose = closes[^1];
            var predicted = baseClose * (decimal)Math.Exp(result.PredictedReturn);

            var forecast = new Forecast
            {
                AssetId = assetId,
                HorizonHours = horizonHours,
                MadeAtUtc = madeAt,
                TargetTsUtc = madeAt.AddHours(horizonHours),
                BaseClose = baseClose,
                PredictedClose = decimal.Round(predicted, 8),
                PredictedReturn = result.PredictedReturn,
                Confidence = result.Confidence,
                ModelVersion = Ensemble.Version,

                /* Die Mischung über alle Säulen -- gewichtet mit Gewicht × gemessenem
                   Verdienst. Steht neben der ersten Säule, ersetzt sie nicht: An
                   PredictedClose hängt die Rückkopplung. */
                CombinedClose = null,
                Components = result.Components
                    .Select(c => new ForecastComponent
                    {
                        ModelName = c.ModelName,
                        PredictedReturn = c.PredictedReturn,
                        Weight = c.Weight
                    })
                    .ToList()
            };

            /* Die Spektralsäule beim ersten Tageshorizont anlegen -- vorher stehen die
               Tagesbars noch nicht im Zwischenspeicher. */
            if (spektral is null && cache.TryGetValue(BarInterval.Daily, out var tages)
                && tages.Count >= 120)
            {
                spektral = Spektralsaeule.Rechne(tages, _opt.EffectiveHorizons.Max() / 24);
            }

            var mitSpektral = lage;

            if (spektral is { Skill: > 0 })
            {
                var tageH = Math.Max(1, horizonHours / 24);
                var r = spektral.RenditeNach(tageH);

                if (r is not null)
                {
                    mitSpektral ??= new Saeulenlage();

                    mitSpektral.Fuege(horizonHours, new Saeulenbeitrag(
                        "math", r.Value, spektral.Skill,
                        $"SSA über {spektral.Fenster} Bars, {spektral.ErklaerteStreuung:P0} "
                        + $"der Streuung erklärt — Rückhalt {spektral.Skill:P0} besser als Stillstand",
                        Beitragsart.Grundlage));
                }
            }

            genauigkeit.TryGetValue(horizonHours, out var acc);

            Mische(forecast, result.PredictedReturn, baseClose,
                   mitSpektral, saeulengewichte, horizonHours, acc.N, acc.HitRate);

            await _forecasts.InsertAsync(forecast, ct);
            made++;
        }

        return new ForecastResult(1, made, sw.Elapsed);
    }

    /// <summary>
    /// Übersetzt die Lead-Lag-Analyse in konkrete Signale: für jeden
    /// Frühindikator dessen jüngste Bewegung, sein Vorlauf und die Beta,
    /// mit der sich diese Bewegung erfahrungsgemäß auf das Ziel überträgt.
    /// </summary>
    /// <summary>
    /// Mischt die erste Säule mit den übrigen — <b>Gewicht × gemessener Verdienst</b>.
    ///
    /// <para><b>Warum nicht einfach nach den Reglern.</b> Ein Regler sagt, wie wichtig
    /// jemand eine Säule findet. Er sagt nichts darüber, ob sie etwas kann. Multipliziert
    /// wird deshalb mit dem Verdienst aus der Messung: Eine Säule ohne nachgewiesenen
    /// Vorsprung bekommt null und bewegt nichts, auch wenn ihr Regler auf hundert steht.
    /// Der Regler bestimmt, WIE VIEL von etwas Brauchbarem einfliesst — nicht, ob
    /// Unbrauchbares mitzählt.</para>
    ///
    /// <para><b>Der Verdienst der ersten Säule</b> kommt aus ihrer eigenen
    /// Rückhaltemessung: der Trefferquote der bereits bewerteten Prognosen. 0,523 ist der
    /// Nullpunkt — darunter ist ein Vorzeichen nicht besser als eine Münze. Solange zu
    /// wenige Fälle vorliegen, gilt ein vorsichtiger Zwischenwert; eine Säule, die erst
    /// seit kurzem läuft, ist weder bewährt noch widerlegt.</para>
    ///
    /// <para><b>Bleibt nichts übrig</b> — kein Regler oben, kein Verdienst irgendwo —,
    /// steht die erste Säule allein da. Nicht ein Mittelwert über alles: Das liesse ein
    /// nachweislich schlechteres Signal mit demselben Anteil einfliessen wie das beste
    /// vorhandene.</para>
    /// </summary>
    private static void Mische(
        Forecast forecast, double ersteSaeule, decimal baseClose,
        Saeulenlage? lage, IReadOnlyDictionary<string, int>? gewichte, int horizonHours,
        int bewertet = 0, double trefferquote = 0)
    {
        int W(string p) => gewichte is not null && gewichte.TryGetValue(p, out var w)
            ? Math.Clamp(w, 0, 100) : 0;

        var grundlagen = new List<(string Saeule, double R, double Anteil, string Warum)>();
        var aufschlaege = new List<(string Saeule, double R, double Anteil, string Warum)>();

        /* Die erste Säule immer -- sie ist gerade gerechnet worden. Ihr Verdienst steckt
           in der Zuversicht des Ensembles, die aus den gelernten Trefferquoten stammt. */
        /* Zwanzig bewertete Fälle als Untergrenze -- darunter ist eine Trefferquote
           Rauschen. Solange sie fehlt, gilt ein vorsichtiger Zwischenwert: Eine Säule,
           die erst seit kurzem läuft, ist weder bewährt noch widerlegt. */
        var (eigenerVerdienst, warum) = bewertet >= 20
            ? (Math.Clamp((trefferquote - 0.523) / 0.10, 0, 1),
               $"Trefferquote {trefferquote:P1} über {bewertet} bewertete Prognosen")
            : (0.25,
               $"erst {bewertet} bewertete Prognosen — vorsichtiger Ansatz");

        grundlagen.Add(("learning", ersteSaeule,
                        Math.Max(1, W("learning")) * eigenerVerdienst, warum));

        if (lage is not null && lage.JeHorizont.TryGetValue(horizonHours, out var weitere))
        {
            foreach (var b in weitere)
            {
                var anteil = W(b.Saeule) / 100.0 * b.Verdienst;

                if (b.Art == Beitragsart.Aufschlag)
                    aufschlaege.Add((b.Saeule, b.Rendite, anteil, b.Begruendung));
                else
                    grundlagen.Add((b.Saeule, b.Rendite, W(b.Saeule) * b.Verdienst, b.Begruendung));
            }
        }

        var summe = grundlagen.Sum(b => b.Anteil);

        var basis = summe > 1e-9
            ? grundlagen.Sum(b => b.R * b.Anteil) / summe
            : ersteSaeule;

        /* Die Aufschläge kommen obendrauf, nicht in den Durchschnitt.

           Und sie werden gedeckelt: höchstens so viel, wie die Grundlage selbst sagt,
           mindestens aber ein Prozentpunkt Spielraum. Ohne Deckel könnte ein Muster mit
           hohem Regler die Prognose beliebig verschieben -- der Aufschlag ist eine
           Nuance zum üblichen Gang, kein Ersatz für die Schätzung. */
        var roh = aufschlaege.Sum(b => b.R * b.Anteil);
        var deckel = Math.Max(0.01, Math.Abs(basis));
        var aufschlag = Math.Clamp(roh, -deckel, deckel);

        var gemischt = basis + aufschlag;

        var beitraege = grundlagen
            .Select(b => (b.Saeule, b.R, Anteil: summe > 1e-9 ? b.Anteil / summe : 0.0,
                          b.Warum, Art: "Grundlage"))
            .Concat(aufschlaege.Select(b => (b.Saeule, b.R, Anteil: b.Anteil, b.Warum,
                                             Art: "Aufschlag")))
            .ToList();

        forecast.CombinedReturn = gemischt;
        forecast.CombinedClose = decimal.Round(
            baseClose * (decimal)Math.Exp(gemischt), 8);

        forecast.PillarMix = System.Text.Json.JsonSerializer.Serialize(new
        {
            basis = Math.Round(basis, 8),
            aufschlag = Math.Round(aufschlag, 8),
            gedeckelt = Math.Abs(roh - aufschlag) > 1e-12,
            beitraege = beitraege.Select(b => new
            {
                saeule = b.Saeule,
                art = b.Art,
                rendite = Math.Round(b.R, 8),
                anteil = Math.Round(b.Anteil, 4),
                warum = b.Warum.Length > 160 ? b.Warum[..160] : b.Warum
            })
        });
    }

    private async Task<IReadOnlyList<LeadSignal>> BuildLeadSignalsAsync(
        int assetId, string interval, DateTime now, CancellationToken ct)
    {
        var top = await _pairs.GetTopLeadersAsync(assetId, interval, MaxLeaders, ct);
        if (top.Count == 0) return [];

        var leaderIds = top.Select(p => p.AssetIdA).Distinct().ToArray();
        var lookback = BarInterval.Duration(interval) * 200;

        var series = await _bars.GetManyAsync(leaderIds, interval, now - lookback, now, ct);
        var ownBars = await _bars.GetAsync(assetId, interval, now - lookback, now, ct);
        var ownReturns = Statistics.LogReturns(ownBars.Select(b => b.Close).ToList());

        var signals = new List<LeadSignal>();

        foreach (var p in top)
        {
            if (!series.TryGetValue(p.AssetIdA, out var bars) || bars.Count < 30) continue;

            var leaderReturns = Statistics.LogReturns(bars.Select(b => b.Close).ToList());
            if (leaderReturns.Length < 30 || ownReturns.Length < 30) continue;

            // Beta aus der um den Lag verschobenen Reihe: wie stark schlägt
            // eine Bewegung des Vorläufers später beim Ziel durch.
            var lag = Math.Max(1, p.BestLagBars);
            var n = Math.Min(leaderReturns.Length - lag, ownReturns.Length - lag);
            if (n < 20) continue;

            // Bewusst als Arrays statt Spans: in einer async-Methode sind
            // ref-Typen als lokale Variablen nicht erlaubt.
            var x = new double[n];
            var y = new double[n];
            Array.Copy(leaderReturns, leaderReturns.Length - n - lag, x, 0, n);
            Array.Copy(ownReturns, ownReturns.Length - n, y, 0, n);
            var beta = Statistics.Beta(x, y);

            // Jüngste Bewegung des Vorläufers über die Dauer seines Vorlaufs.
            double recent = 0;
            for (var k = Math.Max(0, leaderReturns.Length - lag); k < leaderReturns.Length; k++)
                recent += leaderReturns[k];

            signals.Add(new LeadSignal(p.AssetIdA, recent, lag, beta, p.BestLagCorr));
        }

        return signals;
    }
}
