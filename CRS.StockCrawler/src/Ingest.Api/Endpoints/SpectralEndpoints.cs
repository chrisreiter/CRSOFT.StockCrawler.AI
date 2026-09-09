using Ingest.Core.Abstractions;
using Ingest.Core.Analysis.Spectral;
using Ingest.Core.Enums;
using Ingest.Core.Models;

namespace Ingest.Api.Endpoints;

/// <summary>Ein Verfahren mit seinem Beitrag zur Prognose.</summary>
public sealed record StrategyContribution(
    string Key,
    string Label,
    bool Enabled,
    double Weight,
    double Confidence,
    string Note,
    IReadOnlyList<double>? Path);

/// <summary>
/// Zweite Säule: mathematische Analyse.
///
/// Die Verfahren sind bewusst einzeln zu- und abschaltbar, denn sie
/// beantworten verschiedene Fragen. Zwei von ihnen erzeugen überhaupt keine
/// Prognose — sie sagen, ob den anderen zu trauen ist. Das ist kein Mangel,
/// sondern die Lehre aus der Vorlage: Eine Frequenzspitze beweist weder einen
/// stabilen Zyklus noch dessen Handelbarkeit.
/// </summary>
public static class SpectralEndpoints
{
    public static void MapSpectralEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/spectral").WithTags("Mathematische Analyse");

        /* Vollständige Auswertung eines Wertes: Spektrum, Stabilität,
           Momentanzyklus, Zerlegung und Marktverhalten. */
        g.MapGet("/{assetId:int}", async (IAssetRepository assets, IPriceBarRepository bars,
                                          int assetId,
                                          string interval = BarInterval.Daily,
                                          int months = 36,
                                          int segment = 256,
                                          double minPeriod = 5,
                                          double maxPeriod = 120,
                                          int ssaWindow = 0,
                                          int ssaComponents = 4,
                                          int horizon = 0,
                                          CancellationToken ct = default) =>
        {
            var series = await LoadAsync(assets, bars, assetId, interval, months, ct);
            if (series is null)
                return Results.BadRequest(new { error = "Zu wenige Kursdaten für eine Spektralanalyse" });

            var (asset, closes, stamps) = series.Value;

            var returns = Spectrum.LogReturns(closes);
            var banded = Spectrum.BandPass(closes, 2, maxPeriod * 3);

            /* Zwei Grundlagen, und das ist Absicht. Renditen betonen die
               kurzfristigen Änderungen; das Differenzieren verstärkt hohe
               Frequenzen und drückt genau die mittelfristigen Schwingungen,
               nach denen gesucht wird. Der Bandpass lässt diese stehen. Wer nur
               eine der beiden rechnet, sieht nur die halbe Antwort.

               Der Durchlassbereich des Filters ist bewusst DEUTLICH weiter
               gefasst als das ausgewertete Band. Läge seine eigene Flanke im
               ausgewerteten Bereich, fände die Auswertung die Form des Filters
               statt die des Marktes — und zwar bei jedem Wert an derselben
               Stelle. */
            var spReturns = Spectrum.Welch(returns, segment, 0.5, minPeriod, maxPeriod);
            var spBanded = Spectrum.Welch(banded, segment, 0.5, minPeriod, maxPeriod);

            var stability = CycleStability.Analyze(
                banded, Math.Min(512, banded.Length / 2), 64,
                Math.Min(segment, 256), minPeriod, maxPeriod);

            var hilbert = HilbertCycle.Analyze(banded, 20, minPeriod, maxPeriod);
            var regime = Regime.Analyze(returns);

            var ssa = Ssa.Decompose(
                LogOf(closes), ssaWindow, ssaComponents, Math.Max(0, horizon));

            return Results.Ok(new
            {
                assetId,
                asset.Symbol,
                asset.Name,
                interval,
                punkte = closes.Length,
                von = stamps[0],
                bis = stamps[^1],

                spektrum = new
                {
                    aufRenditen = Compact(spReturns),
                    aufBandpass = Compact(spBanded),

                    /* Fürs Diagramm nur das betrachtete Band, und ausgedünnt.
                       Ein vollständiges Spektrum sind über tausend Punkte, von
                       denen die allermeisten außerhalb jedes wirtschaftlich
                       sinnvollen Bereichs liegen. */
                    /* Ausgegeben wird das Verhaeltnis zum oertlichen Untergrund,
                       nicht die rohe Leistung.

                       Die rohe Leistung steigt bei Kursreihen zu langen Perioden
                       hin monoton an -- das ist die Farbe des Rauschens und
                       nicht ein Zyklus. Ein Diagramm davon lockt zwangslaeufig
                       zu dem Schluss, die laengste dargestellte Periode sei die
                       wichtigste. Beim Verhaeltnis liegt eine strukturlose
                       Reihe flach bei eins, und nur echte Spitzen ragen heraus. */
                    kurve = spBanded.Points
                        .Select((p, i) => new
                        {
                            periode = Math.Round(p.PeriodBars, 2),
                            leistung = p.Power,
                            verhaeltnis = spBanded.Background is { } bg && i < bg.Count && bg[i] > 0
                                ? Math.Round(p.Power / bg[i], 3)
                                : 0
                        })
                        .Where(p => p.periode >= minPeriod && p.periode <= maxPeriod)
                        .OrderBy(p => p.periode)
                        .ToList()
                },

                stabilitaet = new
                {
                    stability.Stability,
                    stability.MedianPeriod,
                    stability.Spread,
                    stability.MedianProminence,
                    stability.Windows,
                    urteil = Urteil(stability)
                },

                momentanzyklus = new
                {
                    hilbert.Valid,
                    hilbert.CyclePeriod,
                    hilbert.Phase,
                    hilbert.PhaseQuality,
                    lage = PhaseLabel(hilbert)
                },

                marktverhalten = new
                {
                    regime.Hurst,
                    regime.Label,
                    regime.Confidence,
                    regime.FitQuality,
                    vorabgewichte = Regime.Prior(regime)
                },

                zerlegung = new
                {
                    ssa.WindowLength,
                    ssa.UsedComponents,
                    ssa.ExplainedShare,
                    ssa.ForecastValid,
                    ssa.Note,
                    komponenten = ssa.Components.Select(c => new
                    {
                        c.Index, c.Kind, c.DominantPeriod, c.VarianceShare
                    })
                }
            });
        });

        /* Kohärenz zweier Werte: wo hängen sie zusammen und wer geht voran. */
        g.MapGet("/coherence", async (IAssetRepository assets, IPriceBarRepository bars,
                                      int a, int b,
                                      string interval = BarInterval.Daily,
                                      int months = 36, int segment = 256,
                                      double minPeriod = 5, double maxPeriod = 120,
                                      CancellationToken ct = default) =>
        {
            var sa = await LoadAsync(assets, bars, a, interval, months, ct);
            var sb = await LoadAsync(assets, bars, b, interval, months, ct);

            if (sa is null || sb is null)
                return Results.BadRequest(new { error = "Zu wenige Kursdaten" });

            /* Auf gemeinsame Zeitpunkte beschränken. Ohne das misst die
               Rechnung den Handelskalender: Fehlt einer Reihe der Samstag,
               entsteht eine Wochenperiodik, die mit dem Markt nichts zu tun
               hat und in der Kohärenz als starker Zusammenhang erscheint. */
            var (assetA, ca, ta) = sa.Value;
            var (assetB, cb, tb) = sb.Value;

            var common = Align(ta, ca, tb, cb);
            if (common.A.Length < 128)
                return Results.Ok(new { hinweis = "Zu wenige gemeinsame Zeitpunkte", punkte = common.A.Length });

            var ra = Spectrum.LogReturns(common.A);
            var rb = Spectrum.LogReturns(common.B);

            var res = CrossSpectrum.Analyze(ra, rb, segment, 0.5, minPeriod, maxPeriod);

            return Results.Ok(new
            {
                a = assetA.Symbol,
                b = assetB.Symbol,
                gemeinsamePunkte = common.A.Length,
                res.MeanCoherence,
                staerkste = res.Strongest,
                baender = res.Bands,
                hinweis = "Ein Vorlauf ist nur innerhalb seiner Periode eindeutig — "
                        + "bei Periode 20 sind 30 Bars Vorlauf von 10 nicht zu unterscheiden."
            });
        });

        /* Gemeinsame Marktschwingungen über das gesamte Universum.

           Die eigentliche Frage dieser Säule: Gibt es Zeitskalen, auf denen
           viele Kurse dasselbe tun -- und wer läuft dabei voran? */
        g.MapGet("/modes", async (IAssetRepository assets, IPriceBarRepository bars,
                                  string? ids, string interval = BarInterval.Daily,
                                  int months = 300, int segment = 192,
                                  double overlap = 0.85,
                                  double minPeriod = 5, double maxPeriod = 250,
                                  int surrogates = 60, int limit = 12,
                                  int members = 15, int modes = 1,
                                  string basis = "returns",
                                  CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var scope = await ResolveScopeAsync(assets, ids, ct);
            if (scope.Count < 4)
                return Results.BadRequest(new { error = "Mindestens vier Werte nötig" });

            var (fromUtc, toUtc) = TimeRange.Resolve(null, null, months);
            var series = await bars.GetManyAsync(scope.Keys.ToArray(), interval, fromUtc, toUtc, ct);

            /* Nur Zeitpunkte, an denen ALLE beteiligten Werte gehandelt haben.

               Ohne das misst die Zerlegung den Handelskalender: Fehlen einem
               Teil der Werte die Wochenenden, entsteht eine gemeinsame
               Wochenperiodik, die alle Aktien teilen und kein Krypto -- und
               genau so etwas findet ein Verfahren, das nach gemeinsamen
               Schwingungen sucht, mit Begeisterung. */
            var (grid, common, filledShare) = AlignByCoverage(series, scope.Keys);
            if (common.Count == 0)
                return Results.BadRequest(new
                {
                    error = "Kein tragfähiges gemeinsames Zeitraster",
                    hinweis = "Werte mit sehr kurzer Historie oder abweichendem Handelskalender trennen."
                });

            var data = new List<(int AssetId, string Symbol, double[] Series)>(scope.Count);

            foreach (var (id, closes) in common)
            {
                if (!scope.TryGetValue(id, out var a)) continue;
                if (closes.Length < 256) continue;

                /* Zwei Grundlagen zur Wahl. Renditen sind trendfrei und damit
                   die konservative Wahl; der Bandpass lässt mittelfristige
                   Schwingungen stehen, die das Differenzieren wegdrückt. */
                var x = basis == "bandpass"
                    ? Spectrum.BandPass(closes, 2, maxPeriod * 3)
                    : Spectrum.LogReturns(closes);

                if (x.Length > 128) data.Add((id, a.Symbol, x));
            }

            if (data.Count < 4)
                return Results.BadRequest(new { error = $"Nur {data.Count} Werte mit ausreichender Historie" });

            // Alle auf dieselbe Länge bringen -- die kürzeste bestimmt.
            var minLen = data.Min(d => d.Series.Length);
            data = data.Select(d => (d.AssetId, d.Symbol, d.Series[^minLen..])).ToList();

            var res = MarketModes.Analyze(
                data, segment, overlap, minPeriod, maxPeriod,
                Math.Clamp(surrogates, 0, 200), Math.Clamp(members, 0, 300),
                3, Math.Clamp(modes, 1, 8));

            return Results.Ok(new
            {
                interval,
                basis,
                rasterPunkte = grid.Length,
                aufgefuelltAnteil = Math.Round(filledShare, 4),
                verworfen = scope.Count - common.Count,
                res.Assets,
                res.Points,
                res.Segments,
                res.Surrogates,
                res.DegreesOfFreedom,
                res.Threshold,
                res.Note,
                bedeutsam = res.Modes.Count(m => m.Significant),
                geprueft = res.Modes.Count,
                moden = res.Modes.Take(Math.Clamp(limit, 1, 400))
            });
        });

        /* Vorläufer und Nachzügler aus den Moden jenseits des Marktfaktors.

           Die führende Mode wird bewusst übersprungen. Sie erklärt über die
           Hälfte der Bewegung, aber alle Werte laufen darin gleichzeitig —
           gemessene Phasenspreizung 2,8 Bars gegenüber 19 bis 21 in den Moden
           darunter. Was gleichzeitig geschieht, sagt zwischen den Werten nichts
           voraus. Erst Rotation und Umschichtung haben eine Reihenfolge. */
        g.MapGet("/leaders", async (IAssetRepository assets, IPriceBarRepository bars,
                                    string? ids, string interval = BarInterval.Daily,
                                    int months = 300, int segment = 192,
                                    double overlap = 0.85,
                                    double minPeriod = 5, double maxPeriod = 250,
                                    int modes = 5, int skipModes = 1,
                                    int limit = 20,
                                    string basis = "returns",
                                    DateTime? from = null, DateTime? to = null,
                                    CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var scope = await ResolveScopeAsync(assets, ids, ct);
            if (scope.Count < 8)
                return Results.BadRequest(new { error = "Mindestens acht Werte nötig" });

            /* Ausdrückliche Zeitgrenzen, nicht nur eine Monatszahl ab heute.

               Ohne sie lässt sich die einzige Frage nicht beantworten, auf die
               es ankommt: ob dieselben Werte auch in einem Zeitraum vorauslaufen,
               den die Rechnung nie gesehen hat. */
            var (fromUtc, toUtc) = TimeRange.Resolve(from, to, months);
            var series = await bars.GetManyAsync(scope.Keys.ToArray(), interval, fromUtc, toUtc, ct);

            var (grid, common, filled) = AlignByCoverage(series, scope.Keys);
            if (common.Count < 8)
                return Results.BadRequest(new { error = "Kein tragfähiges gemeinsames Zeitraster" });

            var data = new List<(int AssetId, string Symbol, double[] Series)>(common.Count);

            foreach (var (id, closes) in common)
            {
                if (!scope.TryGetValue(id, out var a)) continue;

                var x = basis == "bandpass"
                    ? Spectrum.BandPass(closes, 2, maxPeriod * 3)
                    : Spectrum.LogReturns(closes);

                if (x.Length > 128) data.Add((id, a.Symbol, x));
            }

            var minLen = data.Min(d => d.Series.Length);
            data = data.Select(d => (d.AssetId, d.Symbol, d.Series[^minLen..])).ToList();

            /* Ohne Ersatzläufe: Hier geht es nicht um die Frage, OB die Moden
               bedeutsam sind — das ist mit z über 80 beantwortet —, sondern
               darum, wer sich in ihnen wo einordnet. Die Ersatzläufe kosten das
               Sechzigfache an Rechenzeit und trügen zu dieser Frage nichts bei. */
            var res = MarketModes.Analyze(
                data, segment, overlap, minPeriod, maxPeriod,
                surrogates: 0, topMembers: 300, smoothBins: 3,
                modes: Math.Clamp(modes, 2, 8));

            /* Zusammengefasst wird nach Zeitskala getrennt.

               Ein Vorlauf von drei Bars auf der Wochenskala und drei Bars auf
               der Jahresskala sind nicht dasselbe: der eine ist ein halber
               Zyklus, der andere ein Prozent davon. Über beide zu mitteln
               ergäbe eine Zahl ohne Bedeutung. */
            var bands = new (string Name, double Lo, double Hi)[]
            {
                ("kurz (5-15 Tage)",   5,  15),
                ("mittel (15-60)",    15,  60),
                ("lang (60-250)",     60, 250)
            };

            var report = new List<object>(bands.Length);

            foreach (var (name, lo, hi) in bands)
            {
                var sel = res.Modes
                    .Where(m => m.Rank > skipModes && m.PeriodBars >= lo && m.PeriodBars < hi)
                    .ToList();

                if (sel.Count == 0) continue;

                /* Je Wert: der mit Ladung UND Modenstärke gewichtete Vorlauf,
                   dazu die Streuung. Die Streuung ist die eigentlich
                   interessante Zahl — ein Wert, der mal vorne und mal hinten
                   liegt, hat im Mittel null Vorlauf und ist trotzdem kein
                   Frühindikator. */
                var acc = new Dictionary<int, (string Symbol, double W, double Sum, double SumSq, int N)>();

                foreach (var m in sel)
                {
                    foreach (var mem in m.Members)
                    {
                        // Vorlauf als Anteil der Periode, damit Skalen vergleichbar bleiben.
                        var rel = m.PeriodBars > 0 ? mem.LeadBars / m.PeriodBars : 0;
                        var w = mem.Loading * m.ExplainedShare;

                        acc.TryGetValue(mem.AssetId, out var e);

                        acc[mem.AssetId] = (mem.Symbol,
                            e.W + w,
                            e.Sum + w * rel,
                            e.SumSq + w * rel * rel,
                            e.N + 1);
                    }
                }

                var rows = acc
                    .Where(kv => kv.Value.W > 0 && kv.Value.N >= 3)
                    .Select(kv =>
                    {
                        var v = kv.Value;
                        var mean = v.Sum / v.W;
                        var var2 = Math.Max(0, v.SumSq / v.W - mean * mean);
                        var sd = Math.Sqrt(var2);

                        var mid = (lo + hi) / 2;

                        return new
                        {
                            assetId = kv.Key,
                            symbol = v.Symbol,
                            sektor = scope.TryGetValue(kv.Key, out var a) ? a.Sector : null,
                            klasse = scope.TryGetValue(kv.Key, out var b) ? b.AssetClass.ToString() : null,
                            vorlaufAnteil = Math.Round(mean, 4),
                            vorlaufBars = Math.Round(mean * mid, 2),
                            streuung = Math.Round(sd, 4),

                            /* Verhältnis von Vorlauf zu seiner eigenen Streuung.
                               Nur wer beständig vorn liegt, taugt als Hinweis;
                               ein großer Vorlauf mit noch größerer Streuung ist
                               eine Zufallsfolge. */
                            bestaendigkeit = sd > 1e-9 ? Math.Round(Math.Abs(mean) / sd, 3) : 0,
                            gewicht = Math.Round(v.W, 4),
                            beobachtungen = v.N
                        };
                    })
                    .ToList();

                report.Add(new
                {
                    band = name,
                    moden = sel.Count,
                    werte = rows.Count,
                    vorlaeufer = rows.OrderByDescending(r => r.vorlaufAnteil).Take(Math.Clamp(limit, 1, 400)),
                    nachzuegler = rows.OrderBy(r => r.vorlaufAnteil).Take(Math.Clamp(limit, 1, 400)),
                    bestaendigste = rows.OrderByDescending(r => r.bestaendigkeit).Take(Math.Clamp(limit, 1, 400))
                });
            }

            return Results.Ok(new
            {
                interval,
                basis,
                von = fromUtc,
                bis = toUtc,
                werte = data.Count,
                rasterPunkte = grid.Length,
                res.Points,
                res.Segments,
                res.DegreesOfFreedom,
                aufgefuelltAnteil = Math.Round(filled, 4),
                uebersprungeneModen = skipModes,
                hinweis = "Der Vorlauf ist als Anteil der jeweiligen Periode angegeben, damit "
                        + "Zeitskalen vergleichbar bleiben. Beständigkeit ist Vorlauf geteilt "
                        + "durch seine eigene Streuung — erst darüber wird ein Mittelwert zur Aussage.",
                baender = report
            });
        });

        /* Der Prognosebeitrag der Säule, aufgeschlüsselt nach Verfahren. */
        g.MapGet("/forecast/{assetId:int}", async (IAssetRepository assets, IPriceBarRepository bars,
                                                   int assetId,
                                                   string interval = BarInterval.Daily,
                                                   int months = 36, int horizon = 30,
                                                   string? strategies = null,
                                                   int ssaWindow = 0, int ssaComponents = 4,
                                                   double minPeriod = 5, double maxPeriod = 120,
                                                   double halfLife = 20,
                                                   CancellationToken ct = default) =>
        {
            horizon = Math.Clamp(horizon, 1, 400);

            var series = await LoadAsync(assets, bars, assetId, interval, months, ct);
            if (series is null)
                return Results.BadRequest(new { error = "Zu wenige Kursdaten" });

            var (asset, closes, stamps) = series.Value;

            var on = ParseStrategies(strategies);

            var logs = LogOf(closes);
            var lastLog = logs[^1];

            var contributions = new List<StrategyContribution>();

            // ------------------------------------------------------ Stabilität
            var banded = Spectrum.BandPass(closes, 2, maxPeriod * 3);

            var stability = CycleStability.Analyze(
                banded, Math.Min(512, banded.Length / 2), 64, 256, minPeriod, maxPeriod);

            var gate = on.Contains("stability") ? stability.Stability : 1.0;

            contributions.Add(new StrategyContribution(
                "stability", "Zyklenstabilität", on.Contains("stability"), 0,
                Math.Round(stability.Stability, 4),
                on.Contains("stability")
                    ? $"dämpft alle Zyklusbeiträge auf {stability.Stability:P0} — {Urteil(stability)}"
                    : "abgeschaltet: Zyklusbeiträge werden ungedämpft übernommen",
                null));

            /* Wie lang der Rückhalt ist: so lang wie der Horizont, aber
               mindestens zehn und höchstens ein Fünftel der Reihe. Kürzer wäre
               das Ergebnis Zufall, länger bliebe zu wenig zum Lernen. */
            var hold = Math.Clamp(horizon, 10, logs.Length / 5);

            // ------------------------------------------------------------- SSA
            if (on.Contains("ssa"))
            {
                var ssa = Ssa.Decompose(logs, ssaWindow, ssaComponents, horizon);

                var path = ssa.ForecastValid
                    ? ssa.Forecast.Select(v => v - lastLog).ToList()
                    : null;

                /* Das Vertrauen kommt aus einer Rückhalteprüfung, nicht aus dem
                   Anteil erklärter Streuung.

                   Grund: Auf Log-Kursen erklärt die erste Komponente fast immer
                   über 95 Prozent -- sie fängt den Trend ein. Diese Zahl sagt
                   nichts darüber, ob sich die Struktur fortschreiben lässt, und
                   führte dazu, dass die Zerlegung stets mit annähernd vollem
                   Gewicht in die Zusammenführung ging.

                   Stattdessen wird der letzte Abschnitt zurückgehalten, die
                   Zerlegung auf dem Rest gerechnet und ihre Fortschreibung gegen
                   die Wirklichkeit gehalten -- gemessen daran, ob sie besser
                   war als die Annahme, der Kurs bleibe stehen. Wer diese Latte
                   reißt, bekommt null und trägt nichts bei. */
                var skill = Holdout(logs, hold, train =>
                {
                    var t = Ssa.Decompose(train, ssaWindow, ssaComponents, hold);
                    return t.ForecastValid ? t.Forecast.ToArray() : null;
                });

                contributions.Add(new StrategyContribution(
                    "ssa", "Singuläre Spektralanalyse", true, 0,
                    Math.Round(skill, 4),
                    ssa.ForecastValid
                        ? $"{ssa.UsedComponents} Komponenten, {ssa.ExplainedShare:P1} der Streuung erklärt · "
                          + SkillNote(skill, hold)
                        : ssa.Note,
                    path));
            }

            // --------------------------------------------------- Momentanzyklus
            if (on.Contains("hilbert"))
            {
                var st = HilbertCycle.Analyze(banded, 20, minPeriod, maxPeriod);
                var proj = HilbertCycle.Project(st, horizon, halfLife);

                /* Auch hier eine Rückhalteprüfung. Der Zyklus wird auf dem
                   verkürzten Verlauf bestimmt und fortgeschrieben; verglichen
                   wird gegen den zurückgehaltenen Teil. Weil die Fortschreibung
                   eine Abweichung um den letzten Kurs ist, wird sie dort
                   aufgeschlagen. */
                var skill = Holdout(logs, hold, train =>
                {
                    var trainCloses = train.Select(Math.Exp).ToArray();
                    var b2 = Spectrum.BandPass(trainCloses, 2, maxPeriod * 3);

                    var s2 = HilbertCycle.Analyze(b2, 20, minPeriod, maxPeriod);
                    var p2 = HilbertCycle.Project(s2, hold, halfLife);
                    if (p2.Length < hold) return null;

                    var baseLog = train[^1];
                    return p2.Select(v => baseLog + v).ToArray();
                });

                // Die Stabilität dämpft zusätzlich: sie beurteilt genau dieses Verfahren.
                var conf = Math.Round(skill * gate, 4);

                contributions.Add(new StrategyContribution(
                    "hilbert", "Momentanzyklus", true, 0, conf,
                    st.Valid
                        ? $"Zykluslänge {st.CyclePeriod:F1} Bars, Phase {st.Phase:F0}°, "
                          + $"Güte {st.PhaseQuality:P0} · {SkillNote(skill, hold)}"
                        : "kein brauchbarer Zyklus erkennbar",
                    proj.Length > 0 ? proj.Select(v => v * gate).ToList() : null));
            }

            // -------------------------------------------------- Marktverhalten
            RegimeState? regime = null;
            if (on.Contains("regime"))
            {
                regime = Regime.Analyze(Spectrum.LogReturns(closes));

                contributions.Add(new StrategyContribution(
                    "regime", "Marktverhalten", true, 0,
                    regime.Confidence,
                    $"{regime.Label} (H={regime.Hurst:F3}) — verschiebt die Gewichte der ersten Säule, "
                    + "liefert selbst keine Prognose",
                    null));
            }

            /* Zusammengeführt wird über die Vertrauenswerte, nicht gleichmäßig.
               Ein Verfahren, das selbst sagt, dass es gerade nichts erkennt,
               darf das Ergebnis nicht mitbestimmen. */
            var withPath = contributions.Where(c => c.Path is { Count: > 0 }).ToList();

            var combined = new double[horizon];
            var weightSum = withPath.Sum(c => Math.Max(0, c.Confidence));

            if (weightSum > 0)
            {
                foreach (var c in withPath)
                {
                    var w = Math.Max(0, c.Confidence) / weightSum;
                    for (var h = 0; h < horizon && h < c.Path!.Count; h++)
                        combined[h] += w * c.Path[h];
                }
            }

            var step = BarInterval.Duration(interval);
            var lastTs = stamps[^1];

            var points = new List<object>(horizon);
            for (var h = 0; h < horizon; h++)
            {
                points.Add(new
                {
                    t = new DateTimeOffset(DateTime.SpecifyKind(lastTs.AddTicks(step.Ticks * (h + 1)),
                        DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                    close = Math.Round(Math.Exp(lastLog + combined[h]), 6),
                    changePct = Math.Round((Math.Exp(combined[h]) - 1) * 100, 4)
                });
            }

            return Results.Ok(new
            {
                assetId,
                asset.Symbol,
                interval,
                horizon,
                letzterKurs = Math.Round(closes[^1], 6),
                verfahren = contributions.Select(c => new
                {
                    c.Key, c.Label, c.Enabled, c.Confidence, c.Note,
                    liefertPfad = c.Path is { Count: > 0 }
                }),
                vorabgewichte = regime is null ? null : Regime.Prior(regime),
                tragfaehig = weightSum > 0,
                hinweis = weightSum > 0
                    ? "Beitrag der zweiten Säule. Fließt in die Anzeige ein, aber noch nicht in "
                    + "die gespeicherten Prognosen — erst nach einem Nachweis außerhalb der Trainingsdaten."
                    : "Kein Verfahren liefert derzeit einen tragfähigen Pfad.",
                punkte = points
            });
        });
        /* Parametersuche — auf Daten, die bei der Suche nicht sichtbar sind.

           Zwei getrennte Zeitbereiche: Die Kombination wird an mehreren
           Schnittstellen im Abstimmungsbereich ausgewählt und danach genau
           einmal gegen den Sperrbereich gehalten. Wer nur die erste Zahl
           berichtet, berichtet, wie gut er gesucht hat. */
        g.MapGet("/optimize", async (IAssetRepository assets, IPriceBarRepository bars,
                                     string? ids, string interval = BarInterval.Daily,
                                     int months = 300, int horizon = 20,
                                     int maxAssets = 12, int top = 10,
                                     CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var scope = await ResolveScopeAsync(assets, ids, ct);
            if (scope.Count == 0) return Results.BadRequest(new { error = "Kein Wert gewählt" });

            /* Nur eine Handvoll Werte. Die Suche rechnet Gitter mal Werte mal
               Schnittstellen Zerlegungen — bei zweihundert Werten wären das
               Hunderttausende, und die Parameter werden davon nicht besser. */
            var chosen = scope.Values
                .OrderBy(a => a.MarketCapRank ?? int.MaxValue)
                .Take(Math.Clamp(maxAssets, 3, 40))
                .ToList();

            var (fromUtc, toUtc) = TimeRange.Resolve(null, null, months);

            var candidates = new List<SpectralOptimizer.Candidate>(chosen.Count);

            foreach (var a in chosen)
            {
                var s2 = await bars.GetManyAsync([a.AssetId], interval, fromUtc, toUtc, ct);
                if (!s2.TryGetValue(a.AssetId, out var list) || list.Count < 900) continue;

                candidates.Add(new SpectralOptimizer.Candidate(
                    a.AssetId, a.Symbol, list.Select(b => (double)b.Close).ToArray()));
            }

            if (candidates.Count < 3)
                return Results.BadRequest(new { error = "Zu wenige Werte mit ausreichender Historie" });

            var grid = SpectralOptimizer.BuildGrid(
                segments: [128, 192, 256],
                maxPeriods: [60, 120, 250],
                ssaWindows: [0, 60, 120, 180],
                ssaComponents: [2, 4, 6, 8],
                halfLives: [10, 20, 40]);

            // Abstimmung im mittleren Bereich, Sperre am Ende.
            var tune = new[] { 0.55, 0.62, 0.69, 0.76 };
            var lockd = new[] { 0.86, 0.93 };

            var tuned = SpectralOptimizer.Search(candidates, grid, tune, horizon);
            if (tuned.Count == 0)
                return Results.BadRequest(new { error = "Keine Kombination auswertbar" });

            var best = tuned[0];

            // Genau eine Kombination gegen den Sperrbereich — mehr wäre erneute Suche.
            var locked = SpectralOptimizer.Search(candidates, [best], lockd, horizon).FirstOrDefault();

            /* Zum Vergleich die Standardeinstellung: Ohne sie sagt der beste
               Wert nichts darüber, ob die Suche überhaupt etwas gebracht hat. */
            var standard = new SpectralConfig(256, 5, 120, 0, 4, 20, 0, 0, 0);
            var stdLocked = SpectralOptimizer.Search(candidates, [standard], lockd, horizon).FirstOrDefault();

            return Results.Ok(new
            {
                interval,
                horizon,
                werte = candidates.Select(c => c.Symbol),
                kombinationen = grid.Count,
                abstimmungsSchnitte = tune,
                sperrSchnitte = lockd,

                beste = new
                {
                    best.Segment, best.MinPeriod, best.MaxPeriod,
                    best.SsaWindow, best.SsaComponents, best.HalfLife,
                    vorsprungAbstimmung = best.Skill,
                    streuung = best.SkillSd,
                    auswertungen = best.Evaluations
                },

                imSperrbereich = locked is null ? null : new
                {
                    vorsprung = locked.Skill,
                    streuung = locked.SkillSd,
                    auswertungen = locked.Evaluations
                },

                standardImSperrbereich = stdLocked is null ? null : new
                {
                    vorsprung = stdLocked.Skill,
                    streuung = stdLocked.SkillSd
                },

                urteil = locked is null ? "nicht auswertbar"
                    : locked.Skill <= 0
                        ? "auch die beste Kombination schlägt den Stillstand nicht — "
                        + "die Verfahren tragen auf diesem Horizont nichts bei"
                        : stdLocked is not null && locked.Skill <= stdLocked.Skill
                            ? "die Suche hat nichts gebracht: die Standardeinstellung ist "
                            + "im Sperrbereich mindestens so gut"
                            : "die gefundene Kombination trägt im Sperrbereich",

                hinweis = "Der Vorsprung ist der anteilige Gewinn gegenüber der Annahme, der "
                        + "Kurs bleibe stehen. Der Wert im Abstimmungsbereich ist die "
                        + "Auswahlgröße und immer zu optimistisch — es gilt der Sperrbereich.",

                rangliste = tuned.Take(Math.Clamp(top, 1, 40)).Select(c => new
                {
                    c.Segment, c.MaxPeriod, c.SsaWindow, c.SsaComponents, c.HalfLife,
                    vorsprung = c.Skill, streuung = c.SkillSd
                })
            });
        });

        /* Grundfrequenzen ablegen und über alle Werte abgleichen.

           Epochenweise, nicht über die ganze Historie: Eine Frequenz, die über
           fünfundzwanzig Jahre gemittelt auftaucht, kann drei Jahre stark und
           zweiundzwanzig abwesend gewesen sein. Nur Werte, die dieselbe
           Frequenz zur SELBEN Zeit tragen, sind ein Fund. */
        g.MapGet("/patterns", async (IAssetRepository assets, IPriceBarRepository bars,
                                     string? ids, string interval = BarInterval.Daily,
                                     int months = 300,
                                     int epochBars = 1024, int stepBars = 256,
                                     int segment = 256,
                                     double minPeriod = 5, double maxPeriod = 120,
                                     int topK = 5, double tolerance = 0.12,
                                     int minEpochs = 3, int limit = 40,
                                     CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var scope = await ResolveScopeAsync(assets, ids, ct);
            if (scope.Count < 4) return Results.BadRequest(new { error = "Mindestens vier Werte nötig" });

            var (fromUtc, toUtc) = TimeRange.Resolve(null, null, months);
            var series = await bars.GetManyAsync(scope.Keys.ToArray(), interval, fromUtc, toUtc, ct);

            var patterns = new List<FreqPattern>();

            foreach (var (id, list) in series)
            {
                if (!scope.TryGetValue(id, out var a)) continue;
                if (list.Count < epochBars + 8) continue;

                var closes = list.Select(b => (double)b.Close).ToArray();
                var stamps = list.Select(b => b.TsUtc).ToArray();

                patterns.AddRange(FrequencyPatterns.Extract(
                    id, a.Symbol, closes, stamps,
                    epochBars, stepBars, segment, minPeriod, maxPeriod, topK));
            }

            if (patterns.Count == 0)
                return Results.Ok(new { hinweis = "Keine ausreichend stabilen Spitzen gefunden", muster = 0 });

            var matches = FrequencyPatterns.Match(patterns, tolerance);
            var recurring = FrequencyPatterns.Recurring(matches, minEpochs);

            var epochs = patterns.Select(p => p.EpochTo).Distinct().OrderBy(t => t).ToList();

            return Results.Ok(new
            {
                interval,
                epochBars,
                stepBars,
                werte = series.Count,
                muster = patterns.Count,
                epochen = epochs.Count,
                vonEpoche = epochs.Count > 0 ? epochs[0] : (DateTime?)null,
                bisEpoche = epochs.Count > 0 ? epochs[^1] : (DateTime?)null,
                treffer = matches.Count,

                hinweis = "Ein einzelner Treffer in einer einzelnen Epoche ist bei vielen "
                        + "Werten Zufall. Aussagekräftig sind nur Paare, die über MEHRERE "
                        + "Epochen dieselbe Frequenz mit gleichbleibendem Versatz teilen — "
                        + "und ein Versatz ungleich null ist die Voraussetzung dafür, dass "
                        + "der eine über den anderen etwas verrät.",

                wiederkehrend = recurring.Take(Math.Clamp(limit, 1, 200)).Select(x => new
                {
                    a = x.SymA,
                    b = x.SymB,
                    epochen = x.Epochs,
                    periode = x.MeanPeriod,
                    versatzBars = x.MeanLag,
                    versatzStreuung = x.LagSd,
                    bewertung = x.MeanScore,

                    /* Ein Versatz ist nur dann nutzbar, wenn er über die Epochen
                       hinweg derselbe bleibt. Springt er, war es Zufall in
                       hübscher Form. */
                    bestaendig = x.LagSd > 0 && Math.Abs(x.MeanLag) > 2 * x.LagSd
                }),

                staerksteEinzeltreffer = matches.Take(Math.Clamp(limit, 1, 200)).Select(m => new
                {
                    epoche = m.EpochTo,
                    a = m.SymbolA, periodeA = m.PeriodA,
                    b = m.SymbolB, periodeB = m.PeriodB,
                    abweichung = m.PeriodDelta,
                    phasendifferenz = m.PhaseDeltaDeg,
                    versatzBars = m.LagBars,
                    bewertung = m.Score
                })
            });
        });

    }

    // ------------------------------------------------------------- Helfer ---

    /// <summary>
    /// Hält den letzten Abschnitt zurück, lässt das Verfahren auf dem Rest
    /// rechnen und misst, ob seine Fortschreibung besser war als die Annahme,
    /// der Kurs bleibe stehen.
    ///
    /// Zurückgegeben wird die anteilige Verbesserung gegenüber dieser Annahme,
    /// begrenzt auf 0 bis 1. Null heißt: nicht besser als nichts zu tun — und
    /// dann soll das Verfahren auch nichts beitragen.
    ///
    /// <b>Was diese Prüfung nicht ist:</b> ein Nachweis. Sie benutzt genau
    /// einen Abschnitt am Ende der Reihe. Ein Verfahren kann dort zufällig gut
    /// abschneiden. Sie schließt aber den umgekehrten Fall aus, und der ist der
    /// gefährlichere: dass ein Verfahren mit voller Überzeugung mitrechnet,
    /// obwohl es auf diesen Daten noch nie etwas getroffen hat.
    /// </summary>
    private static double Holdout(double[] logs, int hold, Func<double[], double[]?> predict)
    {
        if (hold < 5 || logs.Length < hold * 3) return 0;

        var cut = logs.Length - hold;
        var train = logs[..cut];

        double[]? pred;
        try { pred = predict(train); }
        catch { return 0; }

        if (pred is null || pred.Length < hold) return 0;

        var baseline = train[^1];
        double errModel = 0, errNaive = 0;

        for (var i = 0; i < hold; i++)
        {
            var truth = logs[cut + i];
            errModel += Math.Abs(pred[i] - truth);
            errNaive += Math.Abs(baseline - truth);
        }

        if (errNaive <= 0) return 0;

        return Math.Clamp(1 - errModel / errNaive, 0, 1);
    }

    private static string SkillNote(double skill, int hold) => skill <= 0
        ? $"Rückhalteprüfung über {hold} Bars: nicht besser als Stillstand, trägt nichts bei"
        : $"Rückhalteprüfung über {hold} Bars: {skill:P0} besser als Stillstand";

    private static readonly string[] AllStrategies = ["spectrum", "stability", "hilbert", "ssa", "regime"];

    private static HashSet<string> ParseStrategies(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? new HashSet<string>(AllStrategies, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

    private static double[] LogOf(double[] closes)
    {
        var l = new double[closes.Length];
        for (var i = 0; i < closes.Length; i++) l[i] = closes[i] > 0 ? Math.Log(closes[i]) : 0;
        return l;
    }

    private static object Compact(SpectrumResult r) => new
    {
        r.DominantPeriod, r.Prominence, r.SpectralEntropy, r.LowBandShare, r.HighBandShare
    };

    private static string Urteil(CycleStabilityResult s) => s.Windows < 2
        ? "zu wenig Historie für ein Urteil"
        : s.Stability >= 0.7 ? "belastbar"
        : s.Stability >= 0.4 ? "wechselhaft"
        : "kein tragfähiger Zyklus";

    private static string PhaseLabel(HilbertState h)
    {
        if (!h.Valid) return "unbestimmt";

        return h.Phase switch
        {
            < 45 or >= 315 => "Hochpunkt",
            < 135 => "fallende Flanke",
            < 225 => "Tiefpunkt",
            _ => "steigende Flanke"
        };
    }

    /// <summary>
    /// Betrachtungsraum: die übergebenen Werte oder — ohne Angabe — alles
    /// Verfolgte.
    /// </summary>
    private static async Task<Dictionary<int, Asset>> ResolveScopeAsync(
        IAssetRepository assets, string? ids, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ids))
            return (await assets.GetTrackedAsync(ct)).ToDictionary(a => a.AssetId);

        var wanted = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(v => int.TryParse(v, out var x) ? x : -1)
                        .Where(v => v > 0)
                        .Distinct()
                        .ToHashSet();

        var result = new Dictionary<int, Asset>(wanted.Count);

        foreach (var id in wanted)
        {
            var a = await assets.GetAsync(id, ct);
            if (a is not null) result[id] = a;
        }

        return result;
    }

    /// <summary>
    /// Bringt die Kurse auf ein gemeinsames Zeitraster.
    ///
    /// <b>Warum nicht die strenge Schnittmenge.</b> Der erste Versuch verlangte
    /// Zeitpunkte, an denen jeder beteiligte Wert gehandelt hat. Das Ergebnis
    /// war leer — und zwar auch innerhalb einer einzigen Anlageklasse. Über
    /// fünfundzwanzig Jahre reicht ein einziger junger Wert im Korb, um die
    /// Schnittmenge auf dessen Lebensdauer zu stutzen, und ein einziger
    /// fehlender Handelstag irgendwo, um weitere herauszuschneiden. Bei dreißig
    /// Werten bleibt nichts übrig.
    ///
    /// Stattdessen wird das Raster aus den Zeitpunkten gebildet, an denen
    /// <i>die meisten</i> Werte gehandelt haben; Werte, die dieses Raster
    /// schlecht abdecken, fallen heraus statt es zu verkürzen. Verbleibende
    /// einzelne Lücken werden mit dem letzten bekannten Kurs gefüllt.
    ///
    /// <b>Was dabei zu beachten ist:</b> Aufgefüllte Punkte sind keine Daten. Sie
    /// erzeugen Nullrenditen, und zu viele davon senken die gemessene
    /// Gemeinsamkeit künstlich ab. Deshalb die Deckungsschwelle je Wert — und
    /// deshalb wird ausgewiesen, wie viel aufgefüllt wurde.
    /// </summary>
    private static (DateTime[] Grid, Dictionary<int, double[]> Series, double FilledShare) AlignByCoverage(
        IReadOnlyDictionary<int, IReadOnlyList<PriceBar>> series,
        IEnumerable<int> ids,
        double gridCoverage = 0.8,
        double assetCoverage = 0.9)
    {
        var wanted = ids.Where(series.ContainsKey).ToArray();
        if (wanted.Length == 0) return ([], [], 0);

        // Wie viele Werte handelten je Zeitpunkt?
        var count = new Dictionary<DateTime, int>();

        foreach (var id in wanted)
            foreach (var b in series[id])
                count[b.TsUtc] = count.GetValueOrDefault(b.TsUtc) + 1;

        var need = Math.Max(2, (int)Math.Ceiling(wanted.Length * gridCoverage));

        var grid = count.Where(kv => kv.Value >= need)
                        .Select(kv => kv.Key)
                        .OrderBy(t => t)
                        .ToArray();

        if (grid.Length < 64) return ([], [], 0);

        var index = new Dictionary<DateTime, int>(grid.Length);
        for (var i = 0; i < grid.Length; i++) index[grid[i]] = i;

        var result = new Dictionary<int, double[]>(wanted.Length);

        long filled = 0, total = 0;

        foreach (var id in wanted)
        {
            var arr = new double[grid.Length];
            var have = new bool[grid.Length];
            var hits = 0;

            foreach (var b in series[id])
            {
                if (!index.TryGetValue(b.TsUtc, out var k)) continue;
                arr[k] = (double)b.Close;
                have[k] = true;
                hits++;
            }

            // Wer das Raster schlecht abdeckt, fliegt raus statt es zu verkürzen.
            if ((double)hits / grid.Length < assetCoverage) continue;

            /* Vorwärts füllen, und zwar nur vorwärts: Rückwärts zu füllen hieße,
               einen späteren Kurs an einen früheren Zeitpunkt zu schreiben. */
            double last = 0;
            var started = false;

            for (var i = 0; i < grid.Length; i++)
            {
                if (have[i]) { last = arr[i]; started = true; continue; }
                if (!started) continue;

                arr[i] = last;
                filled++;
            }

            // Vor dem ersten echten Kurs gibt es nichts zu füllen — Wert verwerfen.
            if (!started || arr[0] <= 0) continue;

            total += grid.Length;
            result[id] = arr;
        }

        return (grid, result, total > 0 ? (double)filled / total : 0);
    }

    private static async Task<(Asset Asset, double[] Closes, DateTime[] Stamps)?> LoadAsync(
        IAssetRepository assets, IPriceBarRepository bars,
        int assetId, string interval, int months, CancellationToken ct)
    {
        if (!BarInterval.IsValid(interval)) return null;

        var asset = await assets.GetAsync(assetId, ct);
        if (asset is null) return null;

        var (fromUtc, toUtc) = TimeRange.Resolve(null, null, months);
        var series = await bars.GetManyAsync([assetId], interval, fromUtc, toUtc, ct);

        if (!series.TryGetValue(assetId, out var list) || list.Count < 128) return null;

        var closes = new double[list.Count];
        var stamps = new DateTime[list.Count];

        for (var i = 0; i < list.Count; i++)
        {
            closes[i] = (double)list[i].Close;
            stamps[i] = list[i].TsUtc;
        }

        return (asset, closes, stamps);
    }

    /// <summary>Beide Reihen auf ihre gemeinsamen Zeitpunkte zusammenführen.</summary>
    private static (double[] A, double[] B) Align(
        DateTime[] ta, double[] ca, DateTime[] tb, double[] cb)
    {
        var map = new Dictionary<DateTime, double>(tb.Length);
        for (var i = 0; i < tb.Length; i++) map[tb[i]] = cb[i];

        var a = new List<double>(Math.Min(ta.Length, tb.Length));
        var b = new List<double>(a.Capacity);

        for (var i = 0; i < ta.Length; i++)
        {
            if (!map.TryGetValue(ta[i], out var v)) continue;
            a.Add(ca[i]);
            b.Add(v);
        }

        return (a.ToArray(), b.ToArray());
    }
}
