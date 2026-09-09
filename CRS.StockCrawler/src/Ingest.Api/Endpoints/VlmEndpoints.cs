using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Imaging;
using Ingest.Core.Models;
using Ingest.Infrastructure.Providers;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Vierte Säule: Formvergleich mit einem Bildmodell.
///
/// <b>Die Arbeitsteilung, und warum sie so herum ist.</b> Ein Bildmodell kann
/// nicht Millionen Paare vergleichen — bei 40 Werten über 4000 Tage entstehen
/// je Fensterlänge rund 160.000 Ausschnitte. Die Vorauswahl trifft deshalb
/// Rechnung: normierte Korrelation mit Verschiebungssuche, exakt und in
/// Sekunden. Das Modell bekommt danach die Kandidaten und beantwortet, was
/// Rechnung nicht beantwortet — ob es sich <i>wirklich</i> um dieselbe Gestalt
/// handelt und wie man sie nennen würde.
///
/// <b>Was zuerst zu klären ist.</b> Ob ein Modell dieser Größe Kurvenformen
/// überhaupt zuverlässig unterscheidet, ist eine offene Frage und keine
/// Annahme. Deshalb steht der Eichlauf am Anfang: Er legt dem Modell Paare
/// bekannter Ähnlichkeit vor und misst, ob sein Urteil damit zusammenhängt.
/// Fällt das durch, ist die Säule ein Beschriftungswerkzeug und keine
/// Prüfinstanz — und das muss man wissen, bevor man ihr Gewicht gibt.
/// </summary>
public static class VlmEndpoints
{
    private const string DefaultModel = "qwen2.5vl:3b";

    private const string ComparePrompt = """
        Two line charts are shown side by side, separated by a vertical line.
        Left is chart A, right is chart B. Ignore colours and absolute values.
        Judge ONLY the shape of the two lines: the sequence of rises, falls,
        peaks and troughs.

        Answer with JSON only, no other text:
        {"similarity": <0-100>, "pattern": "<short name of the shape, e.g. rising, falling, V, inverted-V, double-top, sideways>", "note": "<max 12 words>"}

        similarity 100 = the two shapes are identical.
        similarity 0 = the two shapes have nothing in common.
        """;

    public static void MapVlmEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/vlm").WithTags("Formvergleich");

        // --------------------------------------------------------- Zustand
        g.MapGet("/status", async (IVlmClient vlm, CancellationToken ct) =>
        {
            var up = await vlm.IsAvailableAsync(ct);
            var models = up ? await vlm.ListModelsAsync(ct) : [];

            return Results.Ok(new
            {
                erreichbar = up,
                modelle = models,
                bildmodelle = models.Where(m =>
                    m.Contains("vl", StringComparison.OrdinalIgnoreCase) ||
                    m.Contains("vision", StringComparison.OrdinalIgnoreCase)),
                standard = DefaultModel
            });
        });

        // ----------------------------------------------- Bild eines Fensters
        g.MapGet("/render/{assetId:int}", async (IAssetRepository assets, IPriceBarRepository bars,
                                                 int assetId, string interval = BarInterval.Daily,
                                                 int months = 300, int window = 128, int offset = 0,
                                                 int width = 384, int height = 256,
                                                 CancellationToken ct = default) =>
        {
            var s = await LoadAsync(assets, bars, assetId, interval, months, ct);
            if (s is null) return Results.BadRequest(new { error = "Zu wenige Kursdaten" });

            var (_, closes, _) = s.Value;

            var start = Math.Clamp(closes.Length - window - offset, 0, Math.Max(0, closes.Length - window));
            var seg = closes.Skip(start).Take(window).ToArray();

            if (seg.Length < 8) return Results.BadRequest(new { error = "Fenster zu klein" });

            var png = CurveRenderer.Render(
                [new CurveLayer("", seg, 0x6A, 0xA9, 0xFF)], width, height);

            return Results.File(png, "image/png");
        });

        /* Eichlauf: Sieht das Modell, was messbar da ist?

           Vorgelegt werden Paare, deren Übereinstimmung vorher bekannt ist —
           von deckungsgleich bis zufällig. Gemessen wird, wie stark das Urteil
           des Modells mit der Rechnung zusammenhängt. Ohne diesen Schritt wäre
           jede spätere Aussage der Säule ungedeckt. */
        g.MapGet("/calibrate", async (IAssetRepository assets, IPriceBarRepository bars,
                                      IVlmClient vlm,
                                      int assetId = 101, string interval = BarInterval.Daily,
                                      int months = 300, int window = 128,
                                      int pairs = 12, string model = DefaultModel,
                                      CancellationToken ct = default) =>
        {
            if (!await vlm.IsAvailableAsync(ct))
                return Results.BadRequest(new { error = "Ollama nicht erreichbar" });

            var s = await LoadAsync(assets, bars, assetId, interval, months, ct);
            if (s is null) return Results.BadRequest(new { error = "Zu wenige Kursdaten" });

            var (asset, closes, stamps) = s.Value;

            var pool = ShapeMatcher.Extract(assetId, asset.Symbol, closes, stamps, window, window / 2, 20);
            if (pool.Count < 20) return Results.BadRequest(new { error = "Zu wenige Ausschnitte" });

            /* Die Paare werden gezielt über den ganzen Bereich verteilt: von
               sehr ähnlich bis unähnlich. Zufällig gezogene Paare lägen fast
               alle im mittleren Bereich, und dort ist der Zusammenhang am
               schwersten zu erkennen. */
            var rnd = new Random(20260821);
            var probe = new List<(ShapeWindow A, ShapeWindow B, double Corr)>();

            var query = pool[pool.Count / 2];
            var ranked = ShapeMatcher.FindSimilar(query, pool, pool.Count, 0);

            for (var i = 0; i < pairs && ranked.Count > 0; i++)
            {
                var idx = (int)Math.Round((double)i / Math.Max(1, pairs - 1) * (ranked.Count - 1));
                var m = ranked[idx];
                probe.Add((m.A, m.B, m.Correlation));
            }

            var rows = new List<object>(probe.Count);
            var xs = new List<double>();
            var ys = new List<double>();

            foreach (var (a, b, corr) in probe)
            {
                var png = CurveRenderer.Pair(a.Shape, b.Shape);
                var v = await vlm.AskAsync(png, ComparePrompt, model, ct);

                rows.Add(new
                {
                    gerechnet = Math.Round(corr, 3),
                    modell = v.Similarity,
                    muster = v.Pattern,
                    notiz = v.Note,
                    ms = v.ElapsedMs,
                    ok = v.Ok,
                    roh = v.Ok ? null : v.RawText
                });

                if (v.Ok) { xs.Add(corr); ys.Add(v.Similarity); }
            }

            var rho = Spearman(xs, ys);

            return Results.Ok(new
            {
                model,
                asset.Symbol,
                window,
                paare = rows.Count,
                rangkorrelation = rho is null ? null : (double?)Math.Round(rho.Value, 3),
                urteil = rho switch
                {
                    null => "zu wenige verwertbare Antworten",
                    > 0.7 => "das Modell sieht die Ähnlichkeit zuverlässig",
                    > 0.4 => "das Modell sieht die Ähnlichkeit ansatzweise",
                    _ => "das Urteil des Modells hängt nicht mit der Form zusammen"
                },
                hinweis = "Die Rangkorrelation zwischen gerechneter und geschätzter Ähnlichkeit "
                        + "entscheidet, ob diese Säule als Prüfinstanz taugt oder nur als "
                        + "Beschriftung.",
                ergebnisse = rows
            });
        });

        // ------------------------------------ Ähnliche Verläufe, mit Prüfung
        g.MapGet("/similar/{assetId:int}", async (IAssetRepository assets, IPriceBarRepository bars,
                                                  IVlmClient vlm,
                                                  int assetId, string? ids,
                                                  string interval = BarInterval.Daily,
                                                  int months = 300, int window = 128,
                                                  int step = 8, int forward = 20,
                                                  int maxLag = 0, int topK = 8,
                                                  bool useVlm = true,
                                                  string model = DefaultModel,
                                                  CancellationToken ct = default) =>
        {
            var target = await LoadAsync(assets, bars, assetId, interval, months, ct);
            if (target is null) return Results.BadRequest(new { error = "Zu wenige Kursdaten" });

            var (asset, closes, stamps) = target.Value;

            // Der gesuchte Ausschnitt ist immer der jüngste — das ist die Gegenwart.
            var qStart = closes.Length - window;
            if (qStart < 0) return Results.BadRequest(new { error = "Fenster länger als die Historie" });

            /* Der gesuchte Ausschnitt braucht keine Fortsetzung — er IST die
               Gegenwart, danach kommt nichts. Deshalb forward = 0; sonst würde
               er mangels Zukunft gar nicht erst gebildet. */
            var query = ShapeMatcher.Extract(assetId, asset.Symbol,
                closes[qStart..], stamps[qStart..], window, window, 0)
                .FirstOrDefault();

            if (query is null) return Results.BadRequest(new { error = "Ausschnitt nicht bildbar" });

            // Bestand: die gewählten Werte, sonst der eigene.
            var scope = await ResolveScopeAsync(assets, ids ?? assetId.ToString(), ct);
            var pool = new List<ShapeWindow>();

            foreach (var (id, a) in scope)
            {
                var s2 = await LoadAsync(assets, bars, id, interval, months, ct);
                if (s2 is null) continue;

                var (_, c2, t2) = s2.Value;
                pool.AddRange(ShapeMatcher.Extract(id, a.Symbol, c2, t2, window, step, forward));
            }

            if (pool.Count < 10) return Results.BadRequest(new { error = "Zu wenige Ausschnitte im Bestand" });

            var matches = ShapeMatcher.FindSimilar(query, pool, Math.Clamp(topK, 1, 30),
                Math.Clamp(maxLag, 0, window / 4));

            var (meanFwd, agree, n) = ShapeMatcher.Consensus(matches);

            var rows = new List<object>(matches.Count);

            foreach (var m in matches)
            {
                VlmVerdict? v = null;

                if (useVlm && await vlm.IsAvailableAsync(ct))
                {
                    var png = CurveRenderer.Pair(m.A.Shape, m.B.Shape);
                    v = await vlm.AskAsync(png, ComparePrompt, model, ct);
                }

                rows.Add(new
                {
                    symbol = m.B.Symbol,
                    von = m.B.StartUtc,
                    bis = m.B.EndUtc,
                    korrelation = m.Correlation,
                    versatzBars = m.LagBars,
                    spaeterePct = Math.Round((Math.Exp(m.B.ForwardReturn) - 1) * 100, 3),
                    modellAehnlichkeit = v?.Similarity,
                    muster = v?.Pattern,
                    notiz = v?.Note
                });
            }

            return Results.Ok(new
            {
                asset.Symbol,
                interval,
                window,
                forward,
                bestand = pool.Count,
                treffer = rows.Count,

                /* Der Konsens ist die eigentliche Aussage: Wenn ähnliche
                   Verläufe in der Vergangenheit hinterher überwiegend in
                   dieselbe Richtung liefen, ist das ein Hinweis. Einigkeit
                   nahe 50 Prozent heißt: kein Hinweis, egal wie ähnlich die
                   Formen aussahen. */
                konsens = new
                {
                    mittlereFolgePct = Math.Round((Math.Exp(meanFwd) - 1) * 100, 3),
                    einigkeitPct = agree,
                    beruhtAuf = n
                },

                hinweis = "Ähnlichkeit der Form ist eine Beobachtung. Erst die Einigkeit der "
                        + "Fortsetzungen macht daraus einen Hinweis — und auch dann keine Gewissheit.",

                treffer_liste = rows
            });
        });
        /* Der Rückvergleich — die einzige Zahl, die über diese Säule entscheidet.

           Für viele Zeitpunkte der Vergangenheit wird gefragt: Wie ging es
           weiter bei den Verläufen, die dem damaligen am ähnlichsten sahen?
           Und stimmte diese Antwort mit dem überein, was tatsächlich geschah?

           <b>Die Falle, die dieses Verfahren zuverlässig ruiniert:</b> Wird als
           Vergleichsbestand die gesamte Historie zugelassen, findet die Suche
           Ausschnitte, deren Fortsetzung ZEITLICH NACH dem gesuchten Zeitpunkt
           liegt. Deren „Zukunft“ ist dann in Wahrheit die Gegenwart des
           gesuchten Ausschnitts — das Ergebnis sieht hervorragend aus und ist
           wertlos. Hier darf ein Vergleichsausschnitt deshalb nur zählen, wenn
           auch seine Fortsetzung vollständig vor dem gesuchten Zeitpunkt liegt. */
        g.MapGet("/backtest", async (IAssetRepository assets, IPriceBarRepository bars,
                                     string? ids, string interval = BarInterval.Daily,
                                     int months = 300, int window = 64,
                                     int step = 4, int forward = 20,
                                     int topK = 20, int maxLag = 0,
                                     double minCorrelation = 0.8,
                                     int queries = 400,
                                     CancellationToken ct = default) =>
        {
            var scope = await ResolveScopeAsync(assets, ids, ct);
            if (scope.Count == 0) return Results.BadRequest(new { error = "Kein Wert gewählt" });

            var pool = new List<ShapeWindow>();

            foreach (var (id, a) in scope)
            {
                var s2 = await LoadAsync(assets, bars, id, interval, months, ct);
                if (s2 is null) continue;

                var (_, c2, t2) = s2.Value;
                pool.AddRange(ShapeMatcher.Extract(id, a.Symbol, c2, t2, window, step, forward));
            }

            if (pool.Count < 200)
                return Results.BadRequest(new { error = $"Zu wenige Ausschnitte ({pool.Count})" });

            // Nach Endzeitpunkt sortieren -- der Vergleich braucht die Reihenfolge.
            pool = pool.OrderBy(w => w.EndUtc).ToList();

            var rnd = new Random(20260821);
            var picks = new List<int>();

            // Aus der zweiten Hälfte ziehen: davor gibt es zu wenig Vergangenheit.
            for (var i = pool.Count / 2; i < pool.Count; i++) picks.Add(i);

            while (picks.Count > queries)
                picks.RemoveAt(rnd.Next(picks.Count));

            int hits = 0, scored = 0;
            double errModel = 0, errNull = 0;

            var bySign = new int[2];

            foreach (var qi in picks)
            {
                var q = pool[qi];

                /* Nur was VOLLSTÄNDIG abgeschlossen war, bevor der gesuchte
                   Ausschnitt begann. Die Fortsetzung eines Vergleichsfalls
                   endet forward Bars nach seinem Ende -- auch die muss davor
                   liegen. */
                var cutoff = q.StartUtc;

                var usable = new List<ShapeWindow>(256);

                foreach (var w in pool)
                {
                    if (w.EndUtc >= cutoff) break;          // sortiert, also fertig
                    usable.Add(w);
                }

                if (usable.Count < 50) continue;

                var matches = ShapeMatcher.FindSimilar(q, usable, topK, maxLag);
                var (mean, agree, n) = ShapeMatcher.Consensus(matches, minCorrelation);

                if (n < 5) continue;

                scored++;

                if (Math.Sign(mean) == Math.Sign(q.ForwardReturn) && mean != 0) hits++;

                errModel += Math.Abs(mean - q.ForwardReturn);
                errNull += Math.Abs(q.ForwardReturn);

                bySign[mean >= 0 ? 0 : 1]++;
            }

            if (scored < 20)
                return Results.Ok(new
                {
                    bewertet = scored,
                    hinweis = "Zu wenige auswertbare Fälle — Schwelle senken oder Bestand vergrößern."
                });

            return Results.Ok(new
            {
                interval,
                window,
                forward,
                topK,
                minCorrelation,
                werte = scope.Count,
                ausschnitte = pool.Count,
                bewertet = scored,

                trefferquotePct = Math.Round(100.0 * hits / scored, 2),

                /* Gegen die Nullannahme „keine Veränderung". Ein Wert unter
                   eins heißt: Der Formvergleich sagt mehr als nichts zu sagen. */
                fehlerVerhaeltnis = Math.Round(errModel / Math.Max(1e-12, errNull), 4),

                richtungAufwaerts = bySign[0],
                richtungAbwaerts = bySign[1],

                urteil = 100.0 * hits / scored > 55 && errModel < errNull
                    ? "trägt"
                    : 100.0 * hits / scored > 52
                        ? "schwach, nicht belastbar"
                        : "trägt nicht",

                hinweis = "Verglichen wird nur mit Ausschnitten, deren Fortsetzung VOR dem "
                        + "gesuchten Zeitpunkt abgeschlossen war. Ohne diese Bedingung sieht "
                        + "das Verfahren hervorragend aus und misst die eigene Zukunft."
            });
        });

    }

    // ------------------------------------------------------------- Helfer ---

    private static double? Spearman(List<double> a, List<double> b)
    {
        if (a.Count < 5) return null;

        static double[] Ranks(List<double> v)
        {
            var idx = Enumerable.Range(0, v.Count).OrderBy(i => v[i]).ToArray();
            var r = new double[v.Count];
            for (var p = 0; p < idx.Length; p++) r[idx[p]] = p;
            return r;
        }

        var x = Ranks(a);
        var y = Ranks(b);

        var n = x.Length;
        var mx = x.Average();
        var my = y.Average();

        double num = 0, dx = 0, dy = 0;

        for (var i = 0; i < n; i++)
        {
            num += (x[i] - mx) * (y[i] - my);
            dx += (x[i] - mx) * (x[i] - mx);
            dy += (y[i] - my) * (y[i] - my);
        }

        return dx * dy > 0 ? num / Math.Sqrt(dx * dy) : 0;
    }

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

    private static async Task<(Asset Asset, double[] Closes, DateTime[] Stamps)?> LoadAsync(
        IAssetRepository assets, IPriceBarRepository bars,
        int assetId, string interval, int months, CancellationToken ct)
    {
        if (!BarInterval.IsValid(interval)) return null;

        var asset = await assets.GetAsync(assetId, ct);
        if (asset is null) return null;

        var (fromUtc, toUtc) = TimeRange.Resolve(null, null, months);
        var series = await bars.GetManyAsync([assetId], interval, fromUtc, toUtc, ct);

        if (!series.TryGetValue(assetId, out var list) || list.Count < 64) return null;

        var closes = new double[list.Count];
        var stamps = new DateTime[list.Count];

        for (var i = 0; i < list.Count; i++)
        {
            closes[i] = (double)list[i].Close;
            stamps[i] = list[i].TsUtc;
        }

        return (asset, closes, stamps);
    }
}
