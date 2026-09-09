using Ingest.Infrastructure.Services;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Api.Endpoints;

public sealed record SeriesPoint(long T, decimal? C);

public sealed record SeriesDto(
    int AssetId,
    string Symbol,
    string? Name,
    string AssetClass,
    string? Currency,
    decimal? First,
    decimal? Last,
    double? ChangePct,
    IReadOnlyList<double?> Values,

    /* Prognose auf derselben Zeitachse. In der Vergangenheit durchweg null,
       ab der letzten echten Bar gefüllt — der erste Punkt ist bewusst der
       letzte Ist-Kurs, damit die Kurve nahtlos anschließt statt zu springen. */
    IReadOnlyList<double?>? Forecast = null,
    IReadOnlyList<ForecastPoint>? ForecastPoints = null,

    /* Rasterposition des Kurses, von dem die laufende Prognose ausgeht. Für
       die Anzeige des Bezugspunkts im Chart. */
    int? ForecastAnchorIdx = null,

    /* Rückschau: was wurde für jeden Zeitpunkt vorhergesagt, als er noch in
       der Zukunft lag? Auf derselben Zeitachse wie der Ist-Verlauf, damit
       Prognose und Wirklichkeit unmittelbar vergleichbar sind.

       Mehrere Horizonte nebeneinander, weil erst der Vergleich aussagekräftig
       ist: eine Wochenprognose folgt dem Kurs eng, eine Jahresprognose zeigt
       die unterstellte Grundrichtung. */
    IReadOnlyList<ForecastTrack>? PastTracks = null,

    /* Stichtags-Prognosen: der Pfad, den das Modell an einem bestimmten Tag
       der Vergangenheit in die Zukunft gezeichnet hat. Mehrere Stichtage
       nebeneinander zeigen, wie sich die Einschätzung über die Zeit änderte. */
    IReadOnlyList<AsOfForecast>? AsOfForecasts = null);

/// <summary>Der an einem Stichtag gezeichnete Prognosepfad.</summary>
public sealed record AsOfForecast(
    DateTime MadeAtUtc,
    decimal BaseClose,
    IReadOnlyList<double?> Values,
    IReadOnlyList<AsOfPoint> Points,

    /// <summary>Rasterposition des Stichtags — der Bezugspunkt des Pfades.</summary>
    int AnchorIdx = -1);

public sealed record AsOfPoint(
    long T, int HorizonHours, string HorizonLabel,
    decimal PredictedClose, decimal? ActualClose,
    double PredictedChangePct, double? ActualChangePct);

/// <summary>Ein Rückschau-Verlauf für genau einen Horizont.</summary>
public sealed record ForecastTrack(
    int HorizonHours,
    string HorizonLabel,
    IReadOnlyList<double?> Values,
    ForecastAccuracy Accuracy,

    /* Zu jedem Prognosewert die Rasterposition des Kurses, von dem aus er
       gerechnet wurde — bei der Rückschau ist das für jeden Punkt ein anderer,
       weil jeder Punkt an seinem eigenen Stichtag entstand. −1 heißt: kein
       Bezugspunkt im sichtbaren Bereich. */
    IReadOnlyList<int>? AnchorIdx = null,

    /// <summary>
    /// Wie viele Punkte aus ECHTEN, vorher abgegebenen Live-Prognosen stammen —
    /// im Unterschied zum nachträglich gerechneten Walk-Forward. Nur diese Zahl
    /// belegt etwas; der Walk-Forward kennt die Zukunft, die er vorhersagt.
    /// </summary>
    int LivePunkte = 0,

    /// <summary>Warum die Linie aussieht, wie sie aussieht. Null, wenn nichts zu sagen ist.</summary>
    string? Notiz = null);

/// <summary>Treffsicherheit der Rückschau im sichtbaren Zeitraum.</summary>
public sealed record ForecastAccuracy(
    int HorizonHours, string HorizonLabel, int Scored,
    double MeanAbsPctError, double HitRatePct);

/// <summary>Ein Prognosepunkt mit seiner Herkunft — für den Mauszeiger.</summary>
public sealed record ForecastPoint(
    long T, int HorizonHours, string HorizonLabel, decimal PredictedClose,
    double ChangePct, double Confidence, int? ScoredCount, double? HitRate);

public sealed record SeriesResponse(
    string Interval,
    int Months,
    bool Rebased,
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<long> Timestamps,
    IReadOnlyList<SeriesDto> Series,
    IReadOnlyList<SkippedSeries>? Skipped = null);

/// <summary>
/// Ein angefragter Wert, für den keine Linie entstanden ist — mit Begründung.
///
/// Stillschweigend auszulassen war ein Fehler mit Ansage: Wer drei Werte wählt
/// und zwei Linien sieht, sucht den Fehler in der Auswahl oder im Diagramm.
/// Tatsächlich fehlten die Daten, und das kann nur der Server wissen.
/// </summary>
public sealed record SkippedSeries(int AssetId, string Symbol, string Reason);

/// <summary>
/// Liest die Säulengewichte aus der Abfragezeichenkette.
///
/// Form: <c>learning:40,math:20,flow:25,deep:10</c>. Fehlt der Parameter,
/// bleibt es beim gespeicherten Ensemble — das ist die Grundlage, gegen die man
/// vergleicht, und darf sich nicht ändern, nur weil jemand einen Regler
/// bewegt.
/// </summary>
internal static class Saeulengewichte
{
  public static Dictionary<string, int>? Parse(string? roh)
  {
    if (string.IsNullOrWhiteSpace(roh)) return null;

    var w = new Dictionary<string, int>
    {
        ["learning"] = 0, ["math"] = 0, ["flow"] = 0,
        ["deep"] = 0, ["knowledge"] = 0, ["semantic"] = 0
    };

    var gesetzt = false;

    foreach (var teil in roh.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var kv = teil.Split(':', 2);

        if (kv.Length == 2 && int.TryParse(kv[1], out var v))
        {
            w[kv[0].Trim()] = Math.Clamp(v, 0, 100);
            gesetzt = true;
        }
    }

    return gesetzt ? w : null;
  }
}

/// <summary>
/// Löst die Zeitraum-Angaben auf. Ein ausdrücklicher Bereich schlägt die
/// Monatsangabe; fehlt beides, gilt der Monats-Standard.
/// </summary>
public static class TimeRange
{
    /* Was SQL Server als DATETIME überhaupt darstellen kann.

       Ein Datum davor ist keine ungewöhnliche Eingabe, sondern eine unmögliche --
       und SQL wirft dafür `SqlDateTime overflow`, mitten aus der Abfrage heraus.
       Beobachtet am 26.08.2026: Die Kursansicht antwortete mit 500 und einem
       Stapelabzug, und in der Statuszeile stand „500 Internal Server Error" --
       eine Meldung, aus der niemand ableiten kann, dass ein Datumsfeld schuld ist.

       Ein leeres Datumsfeld im Browser sendet nichts und ist harmlos; ein halb
       getipptes sendet `0002-01-01`, und schon ist es soweit. Deshalb wird hier
       geklemmt statt durchgereicht. */
    private static readonly DateTime SqlMin = new(1753, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SqlMax = new(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    public static (DateTime From, DateTime To) Resolve(DateTime? from, DateTime? to, int months)
    {
        var toUtc = to.HasValue
            ? DateTime.SpecifyKind(to.Value, DateTimeKind.Utc)
            : DateTime.UtcNow;

        var fromUtc = from.HasValue
            ? DateTime.SpecifyKind(from.Value, DateTimeKind.Utc)
            : toUtc.AddMonths(-Math.Clamp(months, 1, 1200));

        // Verdrehte Eingaben stillschweigend richtigstellen statt abweisen.
        if (fromUtc > toUtc) (fromUtc, toUtc) = (toUtc, fromUtc);

        /* Klemmen, nicht abweisen.

           Ein Datum vor 1753 kann nur ein Vertipper oder ein leeres Feld sein --
           Kurse gibt es dort ohnehin keine. Die Anfrage abzuweisen wäre formal
           sauberer und praktisch schlechter: Der Nutzer bekäme eine Fehlermeldung
           für etwas, das er gar nicht wollte, statt das Diagramm, das er erwartet. */
        fromUtc = fromUtc < SqlMin ? SqlMin : fromUtc > SqlMax ? SqlMax : fromUtc;
        toUtc = toUtc < SqlMin ? SqlMin : toUtc > SqlMax ? SqlMax : toUtc;

        return (fromUtc, toUtc);
    }
}

public static class SeriesEndpoints
{
    public static void MapSeriesEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/series").WithTags("Series");

        /* Liefert mehrere Kurse auf einer gemeinsamen Zeitachse. Genau das
           braucht das Frontend, um Kurven übereinanderzulegen: gleiche
           X-Achse für alle, optional auf 100 normalisiert, damit eine
           300-Dollar-Aktie und ein 70.000-Dollar-Bitcoin vergleichbar werden. */
        g.MapGet("/", async (IAssetRepository assets, IPriceBarRepository bars,
                             IForecastRepository forecasts,
                             IForecastTrackRepository track,
                             ICombinedForecastService kombiniert,
                             string ids, string interval = BarInterval.Daily,
                             int months = 12, bool rebase = true,
                             DateTime? from = null, DateTime? to = null,
                             bool forecast = false,
                             bool forecastPast = false, string? fcHorizons = null,
                             string? asOf = null,
                             string? gewichte = null,
                             CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var assetIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .Select(s => int.TryParse(s, out var v) ? v : -1)
                              .Where(v => v > 0)
                              .Distinct()
                              // Deckel gegen versehentlich riesige Abfragen; die
                              // Oberfläche begrenzt die Auswahl ohnehin auf 40.
                              .Take(60)
                              .ToArray();

            if (assetIds.Length == 0)
                return Results.BadRequest(new { error = "Keine gültigen assetIds übergeben" });

            /* Ein ausdrücklicher Datumsbereich hat Vorrang; months bleibt als
               bequeme Abkürzung erhalten. */
            var (fromUtc, toUtc) = TimeRange.Resolve(from, to, months);

            var raw = await bars.GetManyAsync(assetIds, interval, fromUtc, toUtc, ct);

            if (raw.Count == 0)
            {
                var none = new List<SkippedSeries>();

                foreach (var id in assetIds)
                {
                    var a = await assets.GetAsync(id, ct);
                    none.Add(new SkippedSeries(id, a?.Symbol ?? $"#{id}",
                        $"keine {interval}-Bars im gewählten Zeitraum"));
                }

                return Results.Ok(new SeriesResponse(
                    interval, months, rebase, fromUtc, toUtc, [], [], none));
            }

            /* Vereinigung statt Schnittmenge: Aktien handeln nicht am
               Wochenende, Krypto schon. Eine Schnittmenge würde bei gemischter
               Auswahl fast alle Punkte wegwerfen. Lücken werden stattdessen
               vorwärts gefüllt. */
            var grid = raw.Values
                .SelectMany(v => v.Select(b => b.TsUtc))
                .Distinct()
                .OrderBy(t => t)
                .ToArray();

            var meta = new Dictionary<int, Core.Models.Asset>();
            foreach (var id in assetIds)
            {
                var a = await assets.GetAsync(id, ct);
                if (a is not null) meta[id] = a;
            }

            /* Prognosen einsammeln, bevor das Zeitraster feststeht — sie
               verlängern es in die Zukunft. Zeitpunkte, für die es bereits
               echte Bars gibt, bleiben außen vor: dort gilt der Ist-Kurs. */
            var fcByAsset = new Dictionary<int, List<Core.Models.Forecast>>();
            var futureStamps = new SortedSet<DateTime>();

            if (forecast)
            {
                var lastActual = grid.Length > 0 ? grid[^1] : DateTime.UtcNow;

                // Nur begrenzen, wenn ausdrücklich ein Enddatum in der Zukunft
                // gewählt wurde; sonst zählt der volle verfügbare Horizont.
                var limit = to.HasValue && toUtc > lastActual ? toUtc : DateTime.MaxValue;

                /* Sind Säulengewichte gesetzt, kommt die Prognose aus der
                   kombinierten Rechnung statt aus dem gespeicherten Ensemble.

                   Ohne diesen Zweig wären die Regler in der Oberfläche ein
                   Bedienelement, das aussieht, als täte es etwas — sie wurden
                   gespeichert und von nichts gelesen. Jetzt entscheidet der
                   Aufruf, welche Prognose gezeigt wird, und der Nutzer sieht
                   den Unterschied unmittelbar im Diagramm. */
                var saeulen = Saeulengewichte.Parse(gewichte);

                foreach (var id in assetIds)
                {
                    if (saeulen is not null)
                    {
                        var k = await kombiniert.BuildAsync(id, saeulen, ct);

                        if (k is not null && k.Points.Count > 0)
                        {
                            var umgesetzt = k.Points
                                .Where(pt => pt.TargetTsUtc > lastActual && pt.TargetTsUtc <= limit)
                                .Select(pt => new Core.Models.Forecast
                                {
                                    AssetId = id,
                                    HorizonHours = pt.HorizonHours,
                                    MadeAtUtc = DateTime.UtcNow,
                                    TargetTsUtc = pt.TargetTsUtc,
                                    BaseClose = pt.BaseClose,
                                    PredictedClose = pt.CombinedClose,
                                    PredictedReturn = pt.CombinedReturn,

                                    /* Als Zuversicht steht hier das wirksame
                                       Gewicht — also der gemessene Rückhalt,
                                       nicht die Selbstauskunft eines Modells.
                                       Im Diagramm entscheidet es über die
                                       Deckkraft der Prognoselinie. */
                                    Confidence = pt.EffectiveWeight,
                                    ModelVersion = "kombiniert"
                                })
                                .OrderBy(f => f.TargetTsUtc)
                                .ToList();

                            if (umgesetzt.Count > 0)
                            {
                                fcByAsset[id] = umgesetzt;
                                foreach (var f in umgesetzt) futureStamps.Add(f.TargetTsUtc);
                            }

                            continue;
                        }
                    }

                    var list = (await forecasts.GetLatestAsync(id, ct))
                        .Where(f => f.TargetTsUtc > lastActual && f.TargetTsUtc <= limit)
                        .OrderBy(f => f.TargetTsUtc)
                        .ToList();

                    if (list.Count == 0) continue;

                    fcByAsset[id] = list;
                    foreach (var f in list) futureStamps.Add(f.TargetTsUtc);
                }
            }

            var asOfDates = ParseDates(asOf);
            var fullGrid = grid.Concat(futureStamps).ToArray();
            var accuracy = new Dictionary<int, Dictionary<int, (int N, double Mape, double Hit)>>();

            var series = new List<SeriesDto>(assetIds.Length);

            var skipped = new List<SkippedSeries>();

            foreach (var id in assetIds)
            {
                if (!meta.TryGetValue(id, out var asset))
                {
                    skipped.Add(new SkippedSeries(id, $"#{id}", "Wert nicht in der Datenbank"));
                    continue;
                }

                if (!raw.TryGetValue(id, out var b) || b.Count == 0)
                {
                    skipped.Add(new SkippedSeries(id, asset.Symbol,
                        $"keine {interval}-Bars im gewählten Zeitraum"));
                    continue;
                }

                var filled = SeriesAligner.ForwardFill(b, grid);

                var firstVal = b[0].Close;
                var lastVal = b[^1].Close;
                var basis = (double)firstVal;

                double? Scale(double v) => rebase
                    ? (basis <= 0 ? null : Math.Round(v / basis * 100.0, 4))
                    : Math.Round(v, 8);

                var values = new double?[fullGrid.Length];
                for (var i = 0; i < grid.Length; i++)
                    values[i] = filled[i] is null ? null : Scale((double)filled[i]!.Value);

                var changePct = firstVal > 0
                    ? Math.Round(((double)lastVal - (double)firstVal) / (double)firstVal * 100.0, 3)
                    : (double?)null;

                double?[]? fcValues = null;
                List<ForecastPoint>? fcPoints = null;

                if (fcByAsset.TryGetValue(id, out var fcs) && grid.Length > 0)
                {
                    fcValues = new double?[fullGrid.Length];

                    // Erster Punkt ist der letzte Ist-Kurs — sonst begänne die
                    // Prognosekurve mit einem Sprung ins Leere.
                    fcValues[grid.Length - 1] = Scale((double)lastVal);

                    if (!accuracy.TryGetValue(id, out var acc))
                    {
                        acc = (await forecasts.GetAccuracyAsync(id, ct))
                            .ToDictionary(a => a.HorizonHours, a => (a.N, a.Mape, a.HitRate));
                        accuracy[id] = acc;
                    }

                    fcPoints = [];

                    foreach (var f in fcs)
                    {
                        var idx = Array.IndexOf(fullGrid, f.TargetTsUtc);
                        if (idx < 0) continue;

                        fcValues[idx] = Scale((double)f.PredictedClose);

                        acc.TryGetValue(f.HorizonHours, out var a);

                        fcPoints.Add(new ForecastPoint(
                            new DateTimeOffset(DateTime.SpecifyKind(f.TargetTsUtc, DateTimeKind.Utc))
                                .ToUnixTimeMilliseconds(),
                            f.HorizonHours,
                            HorizonLabel(f.HorizonHours),
                            f.PredictedClose,
                            lastVal > 0
                                ? Math.Round(((double)f.PredictedClose - (double)lastVal) / (double)lastVal * 100.0, 3)
                                : 0,
                            Math.Round(f.Confidence, 4),
                            a.N > 0 ? a.N : null,
                            a.N > 0 ? Math.Round(a.Hit, 4) : null));
                    }
                }

                List<ForecastTrack>? pastTracks = null;

                if (forecastPast && grid.Length > 0)
                {
                    /* Ohne ausdrückliche Angabe den kleinsten Horizont wählen,
                       der zur Auflösung passt: bei Tagesbars ist eine
                       Ein-Stunden-Prognose nicht darstellbar, sie fiele mit dem
                       Ist-Wert zusammen. */
                    var wanted = ParseHorizons(fcHorizons)
                                 ?? [interval == BarInterval.Hourly ? 1 : 24];

                    pastTracks = [];

                    foreach (var horizon in wanted)
                    {

                    /* Zwei Quellen, bewusst in dieser Reihenfolge:

                       forecast_track hält den durchgehenden Verlauf aus dem
                       Walk-Forward — Bar für Bar über die gesamte Historie.
                       dbo.forecast enthält nur die Prognosen des Livebetriebs,
                       also die jüngsten. Beides zusammen ergibt eine
                       lückenlose Linie bis heute. */
                        var trackRows = await track.GetAsync(id, horizon, interval, fromUtc, toUtc, ct);
                        var live = await forecasts.GetHistoryAsync(id, horizon, fromUtc, toUtc, ct);

                        if (trackRows.Count == 0 && live.Count == 0) continue;

                        var pastValues = new double?[fullGrid.Length];

                        var anchors = new int[fullGrid.Length];
                        Array.Fill(anchors, -1);

                        /* Welcher der vielen Kandidaten auf einem Rasterpunkt gewinnt.

                           Vorher gewann schlicht der zuletzt verarbeitete. Bei Tagesbars
                           fallen aber ALLE stündlich gestellten Prognosen desselben Ziel-
                           tages auf einen Punkt — für BTC-USD am 25.08.2026 zehn Stück,
                           zwischen 76.875 und 80.054, also 4,13 % auseinander. Welche
                           davon die Linie zeigte, hing an der Zeilenreihenfolge der
                           Abfrage. Damit ist eine Rückschau nicht nachprüfbar: Morgen
                           kann ein anderer Wert dastehen als der, den man heute gesehen
                           hat, ohne dass sich etwas geändert hätte.

                           Die Regel jetzt: Es gewinnt der Kandidat, dessen Zielzeitpunkt
                           dem Rasterpunkt am nächsten liegt; bei Gleichstand der zuletzt
                           gestellte. Und eine Live-Prognose schlägt IMMER den
                           Walk-Forward, auch wenn dessen Ziel genauer sitzt — der
                           Walk-Forward ist nachträglich gerechnet, die Live-Prognose
                           wurde wirklich vorher abgegeben. Nur die ist ein Beleg. */
                        var bestAbstand = new long[fullGrid.Length];
                        Array.Fill(bestAbstand, long.MaxValue);

                        var istLive = new bool[fullGrid.Length];
                        var zusammengefasst = new int[fullGrid.Length];

                        int scored = 0, hits = 0, liveTreffer = 0;
                        double errSum = 0;
                        DateTime? fruehesteLive = null, spaetesteLive = null;

                        void Apply(DateTime target, DateTime madeAt, decimal predicted,
                                   double? absErr, bool? dirOk, bool live)
                        {
                            var idx = NearestIndex(fullGrid, target, interval);
                            if (idx >= 0)
                            {
                                zusammengefasst[idx]++;

                                var abstand = Math.Abs((target - fullGrid[idx]).Ticks);

                                // Live verdrängt Nicht-Live; sonst entscheidet der Abstand.
                                var nimm = (live && !istLive[idx])
                                           || (live == istLive[idx] && abstand <= bestAbstand[idx]);

                                if (nimm)
                                {
                                    bestAbstand[idx] = abstand;
                                    istLive[idx] = live;
                                    pastValues[idx] = Scale((double)predicted);

                                    /* Der Bezugspunkt liegt oft vor dem sichtbaren
                                       Ausschnitt — dann bleibt er −1 und die
                                       Oberfläche zeigt für diesen Punkt nichts an,
                                       statt auf den Rand zu zeigen. */
                                    anchors[idx] = NearestIndex(fullGrid, madeAt, interval);
                                }
                            }

                            if (live)
                            {
                                liveTreffer++;
                                if (fruehesteLive is null || target < fruehesteLive) fruehesteLive = target;
                                if (spaetesteLive is null || target > spaetesteLive) spaetesteLive = target;
                            }

                            if (absErr is null) return;

                            scored++;
                            errSum += absErr.Value;
                            if (dirOk == true) hits++;
                        }

                        foreach (var r in trackRows)
                            Apply(r.TargetTsUtc, r.MadeAtUtc, r.PredictedClose,
                                  r.AbsPctError, r.DirectionCorrect, false);

                        foreach (var h in live)
                            Apply(h.TargetTsUtc, h.MadeAtUtc, h.PredictedClose,
                                  h.AbsPctError, h.DirectionCorrect, true);

                        /* Was hier steht, entscheidet, ob eine leere Linie als Fehler
                           oder als Tatsache gelesen wird.

                           Eine Prognose über N Stunden ist frühestens N Stunden nach
                           ihrer Abgabe nachprüfbar. Wer die Jahresbahn ansieht, bekommt
                           für die letzten Tage nichts — nicht weil etwas fehlt, sondern
                           weil noch kein Jahr vergangen ist. Ohne diesen Satz sieht das
                           aus wie ein Datenverlust, und man sucht ihn auch. */
                        var maxZusammen = zusammengefasst.Length > 0 ? zusammengefasst.Max() : 0;

                        var notiz = liveTreffer == 0
                            ? $"Für {HorizonLabel(horizon)} gibt es in diesem Ausschnitt noch "
                              + "keine abgelaufenen Live-Prognosen. Eine Prognose über diesen "
                              + $"Horizont ist frühestens {HorizonLabel(horizon)} nach ihrer "
                              + "Abgabe nachprüfbar; was Sie sehen, stammt aus dem "
                              + "nachträglich gerechneten Walk-Forward."
                            : maxZusammen > 1
                                ? $"Bis zu {maxZusammen} Prognosen fallen bei dieser Auflösung "
                                  + "auf einen Punkt; gezeigt wird die, deren Zielzeitpunkt am "
                                  + "nächsten liegt — Live vor Walk-Forward."
                                : null;

                        pastTracks.Add(new ForecastTrack(
                            horizon, HorizonLabel(horizon), pastValues,
                            new ForecastAccuracy(
                                horizon, HorizonLabel(horizon), scored,
                                scored > 0 ? Math.Round(errSum / scored * 100, 3) : 0,
                                scored > 0 ? Math.Round((double)hits / scored * 100, 2) : 0),
                            anchors, liveTreffer, notiz));
                    }

                    if (pastTracks.Count == 0) pastTracks = null;
                }

                List<AsOfForecast>? asOfList = null;

                if (asOfDates.Count > 0 && grid.Length > 0)
                {
                    asOfList = [];

                    // Toleranz: eine Bar. Fällt der Stichtag auf ein Wochenende,
                    // zählt die nächstgelegene Prognose.
                    var tolerance = BarInterval.Duration(interval) * 3;

                    foreach (var day in asOfDates)
                    {
                        var rows = await track.GetAsOfAsync(id, interval, day, tolerance, ct);
                        if (rows.Count == 0) continue;

                        var baseClose = rows[0].BaseClose;
                        var madeAt = rows[0].MadeAtUtc;

                        var vals = new double?[fullGrid.Length];

                        // Der Pfad beginnt beim Kurs des Stichtags selbst.
                        var startIdx = NearestIndex(fullGrid, madeAt, interval);
                        if (startIdx >= 0) vals[startIdx] = Scale((double)baseClose);

                        var points = new List<AsOfPoint>(rows.Count);

                        foreach (var r in rows)
                        {
                            var idx = NearestIndex(fullGrid, r.TargetTsUtc, interval);
                            if (idx >= 0) vals[idx] = Scale((double)r.PredictedClose);

                            points.Add(new AsOfPoint(
                                new DateTimeOffset(DateTime.SpecifyKind(r.TargetTsUtc, DateTimeKind.Utc))
                                    .ToUnixTimeMilliseconds(),
                                r.HorizonHours, HorizonLabel(r.HorizonHours),
                                r.PredictedClose, r.ActualClose,
                                baseClose > 0
                                    ? Math.Round(((double)r.PredictedClose - (double)baseClose)
                                                 / (double)baseClose * 100, 2)
                                    : 0,
                                r.ActualClose is > 0 && baseClose > 0
                                    ? Math.Round(((double)r.ActualClose.Value - (double)baseClose)
                                                 / (double)baseClose * 100, 2)
                                    : null));
                        }

                        asOfList.Add(new AsOfForecast(madeAt, baseClose, vals, points, startIdx));
                    }

                    if (asOfList.Count == 0) asOfList = null;
                }

                series.Add(new SeriesDto(
                    id, asset.Symbol, asset.Name, asset.AssetClass.ToString(), asset.Currency,
                    firstVal, lastVal, changePct, values, fcValues, fcPoints,
                    fcValues is null ? null : grid.Length - 1,
                    pastTracks, asOfList));
            }

            var ts = fullGrid.Select(t => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc))
                                          .ToUnixTimeMilliseconds()).ToArray();

            return Results.Ok(new SeriesResponse(interval, months, rebase, fromUtc, toUtc, ts, series,
                skipped.Count > 0 ? skipped : null));
        });

        // Rohbars eines einzelnen Assets, inklusive OHLC — für Detailansichten.
        g.MapGet("/{assetId:int}", async (IAssetRepository assets, IPriceBarRepository bars,
                                          int assetId, string interval = BarInterval.Daily,
                                          int months = 12,
                                          DateTime? from = null, DateTime? to = null,
                                          CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var asset = await assets.GetAsync(assetId, ct);
            if (asset is null) return Results.NotFound(new { error = $"Asset {assetId} unbekannt" });

            var (fromUtc, toUtc) = TimeRange.Resolve(from, to, months);
            var b = await bars.GetAsync(assetId, interval, fromUtc, toUtc, ct);

            return Results.Ok(new
            {
                asset.AssetId,
                asset.Symbol,
                asset.Name,
                AssetClass = asset.AssetClass.ToString(),
                asset.Currency,
                interval,
                count = b.Count,
                bars = b.Select(x => new
                {
                    t = new DateTimeOffset(DateTime.SpecifyKind(x.TsUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                    o = x.Open,
                    h = x.High,
                    l = x.Low,
                    c = x.Close,
                    v = x.Volume
                })
            });
        });
    }

    /// <summary>
    /// Nächstgelegener Rasterpunkt zu einem Zielzeitpunkt.
    ///
    /// Nötig, weil Prognosen aus dem Livebetrieb ihren Zielzeitpunkt aus
    /// "jetzt plus Horizont" bilden und damit selten exakt auf einer Bar
    /// liegen. Toleriert wird höchstens eine halbe Bar Abstand — sonst würde
    /// eine Prognose einem Zeitpunkt zugeordnet, den sie gar nicht meinte.
    /// </summary>
    private static int NearestIndex(DateTime[] grid, DateTime target, string interval)
    {
        if (grid.Length == 0) return -1;

        var idx = Array.BinarySearch(grid, target);
        if (idx >= 0) return idx;

        idx = ~idx;

        var candidates = new[] { idx - 1, idx };
        var tolerance = BarInterval.Duration(interval) / 2;

        var best = -1;
        var bestDelta = TimeSpan.MaxValue;

        foreach (var c in candidates)
        {
            if (c < 0 || c >= grid.Length) continue;

            var delta = (grid[c] - target).Duration();
            if (delta <= tolerance && delta < bestDelta)
            {
                best = c;
                bestDelta = delta;
            }
        }

        return best;
    }

    /// <summary>Stichtage aus der Abfrage, höchstens vier.</summary>
    private static List<DateTime> ParseDates(string? raw)
    {
        var list = new List<DateTime>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateTime.TryParse(part, System.Globalization.CultureInfo.InvariantCulture,
                                  System.Globalization.DateTimeStyles.AssumeUniversal
                                  | System.Globalization.DateTimeStyles.AdjustToUniversal,
                                  out var d))
            {
                list.Add(d);
            }

            if (list.Count >= 4) break;
        }

        return list;
    }

    /// <summary>Horizontliste aus der Abfrage, höchstens fünf.</summary>
    private static int[]? ParseHorizons(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var list = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(x => int.TryParse(x, out var v) ? v : -1)
                      .Where(v => v > 0)
                      .Distinct()
                      .OrderBy(v => v)
                      .Take(5)
                      .ToArray();

        return list.Length > 0 ? list : null;
    }

    private static string HorizonLabel(int hours) => hours switch
    {
        < 24 => $"{hours} h",
        24 => "1 Tag",
        168 => "1 Woche",
        336 => "2 Wochen",
        720 => "1 Monat",
        2160 => "3 Monate",
        4380 => "6 Monate",
        8760 => "1 Jahr",
        < 168 => $"{hours / 24} Tage",
        < 720 => $"{hours / 168} Wochen",
        _ => $"{hours / 24} Tage"
    };
}
