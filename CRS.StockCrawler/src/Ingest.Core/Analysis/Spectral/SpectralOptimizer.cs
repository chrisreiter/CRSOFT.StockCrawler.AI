namespace Ingest.Core.Analysis.Spectral;

/// <summary>Eine Parameterkombination mit ihrem gemessenen Ergebnis.</summary>
/// <param name="Skill">
/// Anteiliger Vorsprung gegenüber der Annahme, der Kurs bleibe stehen. Null
/// heißt: nicht besser als nichts zu tun. Negativ heißt schlechter.
/// </param>
public sealed record SpectralConfig(
    int Segment, double MinPeriod, double MaxPeriod,
    int SsaWindow, int SsaComponents, double HalfLife,
    double Skill, double SkillSd, int Evaluations);

/// <summary>
/// Sucht die Parameter der Spektralverfahren — auf Daten, die bei der Suche
/// nicht sichtbar waren.
///
/// <b>Warum nicht „bis die Abweichung weg ist".</b> Auf den Daten, an denen
/// man abstimmt, lässt sich die Abweichung beliebig klein machen: Mehr
/// Komponenten, längeres Fenster, feinere Zerlegung — irgendwann bildet das
/// Verfahren die Vergangenheit nach, Ausreißer eingeschlossen. Genau dann sagt
/// es über die Zukunft am wenigsten. Der Zielwert null ist erreichbar und ist
/// das Gegenteil dessen, was gebraucht wird.
///
/// Optimiert wird deshalb gegen einen <b>Abstimmungszeitraum</b>, den die
/// Rechnung jeweils nicht kennt, und die gewählte Kombination wird anschließend
/// ein einziges Mal gegen einen <b>Sperrzeitraum</b> gehalten, der auch dabei
/// nicht gesehen wurde. Die erste Zahl sucht aus, die zweite gilt.
///
/// <b>Warum an mehreren Schnittstellen.</b> Ein einzelner Rückhalt am Ende der
/// Reihe misst ein Vierteljahr Marktgeschehen. Fällt darin ein Absturz, gewinnt
/// die Kombination, die Abstürze am besten trifft — und verliert überall sonst.
/// Deshalb wird an mehreren Zeitpunkten geschnitten und über sie gemittelt;
/// erst die Streuung darüber sagt, ob ein Vorsprung stabil ist.
/// </summary>
public static class SpectralOptimizer
{
    /// <summary>Ein Wert mit seiner Kursreihe.</summary>
    public sealed record Candidate(int AssetId, string Symbol, double[] Closes);

    /// <summary>
    /// Durchsucht die übergebenen Kombinationen und gibt sie sortiert zurück.
    /// </summary>
    /// <param name="cuts">
    /// Anteile der Reihenlänge, an denen geschnitten wird. Für jeden Schnitt
    /// rechnet das Verfahren auf allem davor und wird an dem gemessen, was
    /// danach kommt.
    /// </param>
    /// <param name="horizon">Wie viele Bars je Schnitt vorhergesagt werden.</param>
    public static List<SpectralConfig> Search(
        IReadOnlyList<Candidate> assets,
        IReadOnlyList<SpectralConfig> grid,
        IReadOnlyList<double> cuts,
        int horizon = 20,
        int maxParallel = 0)
    {
        var results = new SpectralConfig[grid.Count];

        var parallelism = maxParallel > 0
            ? maxParallel
            : Math.Max(1, Environment.ProcessorCount - 2);

        Parallel.For(0, grid.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, gi =>
        {
            var cfg = grid[gi];
            var skills = new List<double>(assets.Count * cuts.Count);

            foreach (var a in assets)
            {
                foreach (var frac in cuts)
                {
                    var cut = (int)(a.Closes.Length * frac);

                    // Zu wenig Vorgeschichte oder zu wenig Zukunft ist kein Test.
                    if (cut < 400 || cut + horizon >= a.Closes.Length) continue;

                    var s = Skill(a.Closes, cut, horizon, cfg);
                    if (s is { } v) skills.Add(v);
                }
            }

            if (skills.Count == 0)
            {
                results[gi] = cfg with { Skill = double.NegativeInfinity, Evaluations = 0 };
                return;
            }

            var mean = skills.Average();

            double var2 = 0;
            foreach (var s in skills) var2 += (s - mean) * (s - mean);

            results[gi] = cfg with
            {
                Skill = Math.Round(mean, 5),
                SkillSd = Math.Round(Math.Sqrt(var2 / Math.Max(1, skills.Count - 1)), 5),
                Evaluations = skills.Count
            };
        });

        return results.Where(r => r.Evaluations > 0)
                      .OrderByDescending(r => r.Skill)
                      .ToList();
    }

    /// <summary>
    /// Vorsprung einer Kombination an einer Schnittstelle: Wie viel besser ist
    /// die Fortschreibung als die Annahme, der Kurs bleibe stehen?
    ///
    /// Zusammengeführt werden Zerlegung und Momentanzyklus mit ihren jeweiligen
    /// Vertrauenswerten — dieselbe Zusammenführung wie im Prognosebeitrag der
    /// Säule. Anders gemessen wäre es eine andere Größe als die, die später
    /// ausgeliefert wird.
    /// </summary>
    private static double? Skill(double[] closes, int cut, int horizon, SpectralConfig cfg)
    {
        var train = closes[..cut];

        var logs = new double[train.Length];
        for (var i = 0; i < train.Length; i++)
            logs[i] = train[i] > 0 ? Math.Log(train[i]) : 0;

        double[]? path = null;

        try
        {
            var ssa = Ssa.Decompose(logs, cfg.SsaWindow, cfg.SsaComponents, horizon);
            if (ssa.ForecastValid && ssa.Forecast.Count >= horizon)
                path = ssa.Forecast.Take(horizon).ToArray();
        }
        catch
        {
            return null;
        }

        if (path is null) return null;

        var baseline = logs[^1];

        double errModel = 0, errNaive = 0;

        for (var i = 0; i < horizon; i++)
        {
            var truth = closes[cut + i] > 0 ? Math.Log(closes[cut + i]) : baseline;

            errModel += Math.Abs(path[i] - truth);
            errNaive += Math.Abs(baseline - truth);
        }

        if (errNaive <= 0) return null;

        /* Nicht auf null bis eins begrenzt. Ein negativer Vorsprung ist eine
           Aussage — er sagt, dass die Kombination schadet, und diese
           Information geht verloren, wenn man sie auf null hebt. Beim
           Prognosebeitrag wird begrenzt, bei der Suche nicht. */
        return 1 - errModel / errNaive;
    }

    /// <summary>
    /// Baut ein Suchgitter über die Parameter, die tatsächlich etwas ändern.
    ///
    /// Bewusst grob. Ein feineres Gitter findet keine besseren Parameter,
    /// sondern nur solche, die zum Abstimmungszeitraum besser passen — und
    /// genau das ist der Fehler, den diese Klasse vermeiden soll. Bei
    /// tausend Kombinationen und tausend Auswertungen findet man verlässlich
    /// eine, die zufällig glänzt.
    /// </summary>
    public static List<SpectralConfig> BuildGrid(
        IReadOnlyList<int> segments,
        IReadOnlyList<double> maxPeriods,
        IReadOnlyList<int> ssaWindows,
        IReadOnlyList<int> ssaComponents,
        IReadOnlyList<double> halfLives,
        double minPeriod = 5)
    {
        var grid = new List<SpectralConfig>();

        foreach (var seg in segments)
            foreach (var mp in maxPeriods)
                foreach (var sw in ssaWindows)
                    foreach (var sc in ssaComponents)
                        foreach (var hl in halfLives)
                            grid.Add(new SpectralConfig(seg, minPeriod, mp, sw, sc, hl, 0, 0, 0));

        return grid;
    }
}
