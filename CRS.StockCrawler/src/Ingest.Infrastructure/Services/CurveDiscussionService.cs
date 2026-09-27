using System.Data;
using Dapper;
using Ingest.Infrastructure.Datenbank;
using Ingest.Core.Abstractions;
using Ingest.Infrastructure.Repositories;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>Das Ergebnis eines Durchgangs, so wie die Oberfläche es meldet.</summary>
public sealed record CurveRunResult(
    int RunId, int Assets, int Events, int Links, TimeSpan Duration, string Note);

/// <summary>Eine Zeile der Rasteransicht.</summary>
public sealed record CurveEventRow(
    long CurveEventId, int AssetId, string Symbol, string? Name, AssetClass AssetClass,
    DateTime TsUtc, string EventType, short Sign, double Severity,
    decimal ClosePrice, decimal Smoothed, double Slope, double Curvature);

/// <summary>Eine Seite davon.</summary>
public sealed record CurveEventPage(
    int RunId, int Page, int Size, long Total, IReadOnlyList<CurveEventRow> Rows);

public interface ICurveDiscussionService
{
    Task<CurveRunResult> RunAsync(
        string intervalCode, CurveDiscussion.Options opt,
        int[]? assetIds, DateTime? fromUtc, DateTime? toUtc,
        int linkWindowBars, CancellationToken ct = default);

    Task<CurveEventPage> PageAsync(
        int? runId, int page, int size,
        int? assetId, string? eventType, int? sign,
        double minSeverity, DateTime? fromUtc, DateTime? toUtc,
        string sort, CancellationToken ct = default);

    Task<IReadOnlyList<dynamic>> RunsAsync(CancellationToken ct = default);

    /// <summary>
    /// Der jüngste kausale Lauf — der einzige, der über die letzten Tage etwas
    /// sagen kann.
    /// </summary>
    Task<int?> LatestCausalRunAsync(CancellationToken ct = default);
    Task<IReadOnlyList<dynamic>> LinksAsync(
        int? runId, int limit, int? assetId = null, CancellationToken ct = default);
    Task<IReadOnlyList<dynamic>> TypeStatsAsync(int? runId, CancellationToken ct = default);
}

/// <summary>
/// Fährt die Kurvendiskussion über den gesamten Bestand und legt die Funde ab.
///
/// <b>Warum das ein eigener, ausdrücklich gestarteter Durchgang ist.</b> Über
/// 300 Werte mit je einigen tausend Bars ist das keine Sekundensache, und das
/// Ergebnis hängt an einem halben Dutzend Einstellungen. Bei jeder Abfrage neu
/// zu rechnen hieße: Eine Rasteransicht mit Blättern blätterte durch einen
/// Bestand, der sich zwischen Seite drei und vier ändert. Deshalb ein Lauf, ein
/// Bestand, eine Nummer — und die Einstellungen daneben, damit später
/// nachvollziehbar bleibt, wie die Funde zustande kamen.
/// </summary>
public sealed class CurveDiscussionService(
    ISqlConnectionFactory factory,
    IPriceBarRepository bars,
    IAssetRepository assets,
    ILogger<CurveDiscussionService> log) : ICurveDiscussionService
{
    private SqlDialekt d => factory.Dialekt;
    public async Task<CurveRunResult> RunAsync(
        string intervalCode, CurveDiscussion.Options opt,
        int[]? assetIds, DateTime? fromUtc, DateTime? toUtc,
        int linkWindowBars, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var conn = await factory.OpenAsync(ct);

        var runId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"""
            INSERT INTO dbo.curve_run
                (interval_code, from_utc, to_utc, half_window, causal,
                 ref_window, min_z, refractory, min_severity)
            {d.RueckgabeVor("run_id")} VALUES (@interval, @from, @to, @half, @causal,
                    @ref, @minZ, @refr, @minSev) {d.RueckgabeNach("run_id")};
            """,
            new
            {
                interval = intervalCode,
                from = fromUtc,
                to = toUtc,
                half = opt.HalfWindow,
                causal = opt.Causal,
                @ref = opt.RefWindow,
                minZ = opt.MinZ,
                refr = opt.Refractory,
                minSev = opt.MinSeverity
            }, cancellationToken: ct));

        var alle = await assets.GetTrackedAsync(ct);

        var gewaehlt = assetIds is { Length: > 0 }
            ? alle.Where(a => assetIds.Contains(a.AssetId)).ToList()
            : alle.ToList();

        var from = fromUtc ?? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = toUtc ?? DateTime.UtcNow;

        var alleEvents = new List<LinkableEvent>();
        var handelstage = new Dictionary<int, HashSet<DateTime>>();

        var tabelle = NewEventTable();
        var count = 0;
        var used = 0;

        foreach (var a in gewaehlt)
        {
            ct.ThrowIfCancellationRequested();

            var reihe = await bars.GetAsync(a.AssetId, intervalCode, from, to, ct);

            // Unter dieser Länge ist die Anpassung nicht überbestimmt genug,
            // und der Vergleichsmaßstab hätte kein Fenster.
            if (reihe.Count < opt.RefWindow + 4 * opt.HalfWindow) continue;

            used++;

            var closes = reihe.Select(b => (double)b.Close).ToArray();
            var stamps = reihe.Select(b => b.TsUtc).ToArray();

            handelstage[a.AssetId] = stamps.ToHashSet();

            foreach (var p in CurveDiscussion.Analyze(closes, opt))
            {
                var ts = stamps[p.Index];

                alleEvents.Add(new LinkableEvent(a.AssetId, ts, p.Type, p.Sign, p.Severity));

                var row = tabelle.NewRow();
                row["run_id"] = runId;
                row["asset_id"] = a.AssetId;
                row["ts_utc"] = ts;
                row["event_type"] = p.Type;
                row["sign"] = p.Sign;
                row["severity"] = p.Severity;
                row["close_price"] = (decimal)closes[p.Index];
                row["smoothed"] = (decimal)p.Smoothed;
                row["slope"] = p.Slope;
                row["curvature"] = p.Curvature;
                tabelle.Rows.Add(row);

                count++;
            }

            // In Blöcken schreiben, damit der Speicher bei 300 Werten nicht
            // eine Viertelmillion Zeilen zugleich hält.
            if (tabelle.Rows.Count >= 50_000)
            {
                await BulkAsync(tabelle, "dbo.curve_event", ct);
                tabelle.Clear();
            }
        }

        if (tabelle.Rows.Count > 0) await BulkAsync(tabelle, "dbo.curve_event", ct);

        log.LogInformation("Kurvendiskussion: {N} Ereignisse aus {A} Werten", count, used);

        /* Verknüpfung erst, wenn alle Werte durch sind — sie braucht den
           vollständigen Bestand, weil die Erwartungsrechnung auf den
           Ereigniszahlen beider Seiten beruht. */
        var barDauer = BarInterval.Duration(intervalCode);

        var links = CurveEventLinker.Link(
            alleEvents, handelstage, linkWindowBars, barDauer);

        if (links.Count > 0)
        {
            var lt = NewLinkTable();

            foreach (var l in links)
            {
                var row = lt.NewRow();
                row["run_id"] = runId;
                row["asset_a"] = l.AssetA;
                row["asset_b"] = l.AssetB;
                row["type_a"] = l.TypeA;
                row["type_b"] = l.TypeB;
                row["pairs"] = l.Pairs;
                row["expected"] = l.Expected;
                row["lift"] = l.Lift;
                row["median_lag_bars"] = l.MedianLagBars;
                row["lead_share"] = l.LeadShare;
                row["mean_severity"] = l.MeanSeverity;
                lt.Rows.Add(row);
            }

            await BulkAsync(lt, "dbo.curve_link", ct);
        }

        sw.Stop();

        var note = $"{used} Werte, {count} Ereignisse, {links.Count} Verknüpfungen, "
                 + $"{(opt.Causal ? "kausal" : "zentriert")} geglättet";

        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE dbo.curve_run
               SET finished_utc = {d.Jetzt},
                   assets = @a, events = @e, note = @n
             WHERE run_id = @id;
            """,
            new { a = used, e = count, n = note, id = runId }, cancellationToken: ct));

        return new CurveRunResult(runId, used, count, links.Count, sw.Elapsed, note);
    }

    public async Task<CurveEventPage> PageAsync(
        int? runId, int page, int size,
        int? assetId, string? eventType, int? sign,
        double minSeverity, DateTime? fromUtc, DateTime? toUtc,
        string sort, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var id = runId ?? await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(run_id) FROM dbo.curve_run WHERE finished_utc IS NOT NULL;",
            cancellationToken: ct)) ?? 0;

        if (id == 0) return new CurveEventPage(0, page, size, 0, []);

        page = Math.Max(1, page);
        size = Math.Clamp(size, 10, 500);

        /* Die Sortierung wird gegen eine feste Liste geprüft und nicht
           durchgereicht. Ein Feldname aus der Abfragezeichenkette direkt in
           ORDER BY wäre eine Einladung. */
        var order = sort switch
        {
            "ts" => "e.ts_utc DESC",
            "ts_asc" => "e.ts_utc ASC",
            "asset" => "a.symbol ASC, e.ts_utc DESC",
            "slope" => "ABS(e.slope) DESC",
            _ => "e.severity DESC"
        };

        /*  Die Bedingung wird zusammengesetzt, nicht abgeschaltet.

            Hier standen fünf Filter der Form `(spalte = @p OR @p IS NULL)` —
            eine bequeme Schreibweise für „filtere nur, wenn etwas angegeben
            ist". Sie ist auf jeder Datenbank schlecht: Der Planer muss einen
            Plan wählen, der für beide Fälle gilt, und nimmt dann den vollen
            Durchlauf. Über 380.000 Ereigniszeilen mit Verbund auf `asset`
            liefen diese Ansichten am 27.09.2026 deshalb in den Timeout —
            `/api/curve/events` und `/api/curve/stats` antworteten nach 35
            Sekunden mit 500, obwohl im Normalfall keiner der fünf Filter
            gesetzt ist.

            Jetzt steht nur in der Bedingung, wonach wirklich gefragt wurde.
            Die Parameternamen bleiben unverändert; Dapper stört ein Parameter
            nicht, der im SQL nicht vorkommt.                                  */
        var bedingungen = new List<string> { "e.run_id = @id" };

        /*  Die Mindeststufe steht nur drin, wenn sie etwas ausschliesst.

            `severity >= 0` schliesst nichts aus — die Stufe ist per Definition
            nicht negativ. Es ist trotzdem ein Bereichsvergleich auf einer
            Wertespalte, und der ist auf dem DataCell-Backend teuer: gemessen
            am 27.09.2026 über den jüngsten Lauf mit 243.312 Zeilen

              COUNT ... WHERE run_id = 9                   154 ms
              COUNT ... WHERE run_id = 9 AND severity >= 2  30.335 ms

            Ein Vergleich, der jede Zeile behält, kostete also das
            Zweihundertfache. Die Voreinstellung der Oberfläche ist 0 — diese
            Ansicht zahlte den Preis folglich immer.                           */
        if (minSeverity > 0) bedingungen.Add("e.severity >= @minSev");

        if (assetId is not null) bedingungen.Add("e.asset_id = @assetId");
        if (!string.IsNullOrWhiteSpace(eventType)) bedingungen.Add("e.event_type = @type");
        if (sign is not null) bedingungen.Add("e.sign = @sign");
        if (fromUtc is not null) bedingungen.Add("e.ts_utc >= @from");
        if (toUtc is not null) bedingungen.Add("e.ts_utc <= @to");

        var where = "WHERE " + string.Join("\n              AND ", bedingungen);

        var p = new
        {
            id,
            minSev = minSeverity,
            assetId,
            type = string.IsNullOrWhiteSpace(eventType) ? null : eventType,
            sign,
            from = fromUtc,
            to = toUtc,
            skip = (page - 1) * size,
            take = size
        };

        var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT CAST(COUNT(*) AS BIGINT) FROM dbo.curve_event e {where};", p, cancellationToken: ct));

        /*  Der Verbund auf `asset` nur dort, wo er für die Sortierung gebraucht
            wird — sonst holt die Anwendung die Namen selbst.

            `asset` hat 704 Zeilen, `curve_event` im jüngsten Lauf 243.312. Die
            Ansicht zeigt fünfzig davon. Den Verbund über die grosse Seite zu
            führen, um fünfzig Symbole zu beschriften, war auf dem
            DataCell-Backend am 27.09.2026 der Unterschied zwischen einer
            Antwort und einem Lesetimeout nach 30 Sekunden.

            Nur die Sortierung nach Symbol braucht den Verbund wirklich: Wer
            seitenweise nach Namen blättert, kann das nicht im Nachhinein
            ordnen. Für sie bleibt er stehen.                                  */
        var nachSymbol = order.StartsWith("a.symbol", StringComparison.Ordinal);

        var quelle = nachSymbol
            ? "FROM dbo.curve_event e JOIN dbo.asset a ON a.asset_id = e.asset_id"
            : "FROM dbo.curve_event e";

        var rohzeilen = (await conn.QueryAsync<CurveEventRow>(new CommandDefinition(
            $"""
             SELECT e.curve_event_id AS CurveEventId, e.asset_id AS AssetId,
                    e.ts_utc AS TsUtc, e.event_type AS EventType, e.sign AS Sign,
                    e.severity AS Severity, e.close_price AS ClosePrice,
                    e.smoothed AS Smoothed, e.slope AS Slope, e.curvature AS Curvature
             {quelle}
             {where}
              ORDER BY {order}
             OFFSET @skip ROWS FETCH NEXT (@take) ROWS ONLY;
             """, p, cancellationToken: ct))).ToList();

        var werte = (await conn.QueryAsync<(int AssetId, string Symbol, string? Name, byte Klasse)>(
            new CommandDefinition(
                """SELECT asset_id, symbol, "name", asset_class FROM dbo.asset""",
                cancellationToken: ct)))
            .ToDictionary(a => a.AssetId);

        var rows = rohzeilen.Select(r => werte.TryGetValue(r.AssetId, out var a)
            ? r with { Symbol = a.Symbol, Name = a.Name, AssetClass = (AssetClass)a.Klasse }
            : r).ToList();

        return new CurveEventPage(id, page, size, total, rows);
    }

    /// <summary>
    /// Der jüngste kausale Lauf.
    ///
    /// <b>Warum das eine eigene Abfrage braucht.</b> Ein zentriert geglätteter
    /// Lauf kann die letzten <c>halbfenster</c> Bars grundsätzlich nicht
    /// bewerten — sein Fenster reicht dort über das Ende der Reihe hinaus.
    /// Gemessen: Der zentrierte Lauf endete am 11. August, die jüngste Kursbar
    /// war vom 22.; elf Tage Lücke, genau die halbe Fensterbreite. Wer nach
    /// „was ist heute" fragt, bekommt aus einem zentrierten Lauf nie eine
    /// Antwort, egal wie frisch er gerechnet wurde.
    /// </summary>
    public async Task<int?> LatestCausalRunAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            $"""
            SELECT MAX(run_id) FROM dbo.curve_run
             WHERE causal = {d.Wahr} AND finished_utc IS NOT NULL;
            """, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<dynamic>> RunsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        return (await conn.QueryAsync(new CommandDefinition(
            """
            SELECT run_id, started_utc, finished_utc, interval_code,
                   half_window, causal, ref_window, min_z, refractory,
                   min_severity, assets, events, note
              FROM dbo.curve_run
             ORDER BY run_id DESC OFFSET 0 ROWS FETCH NEXT 40 ROWS ONLY;
            """, cancellationToken: ct))).ToList();
    }

    /* Der Wertefilter gehoert in die Abfrage, nicht dahinter.

       Der erste Entwurf holte die staerksten 300 Verknuepfungen und filterte
       danach nach Symbol. Bei 135.481 Verknuepfungen aus dem Volllauf ist ein
       einzelner Wert dort praktisch nie dabei -- der Agent meldete daraufhin
       wahrheitswidrig, es gebe keine. */
    public async Task<IReadOnlyList<dynamic>> LinksAsync(
        int? runId, int limit, int? assetId = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var id = runId ?? await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(run_id) FROM dbo.curve_run WHERE finished_utc IS NOT NULL;",
            cancellationToken: ct)) ?? 0;

        /*  Zwei Abfragen statt einer mit Sammelfilter.

            Vorher stand hier ein `(l.asset_a = @assetId OR l.asset_b = @assetId
            OR @assetId IS NULL)`. Ein solcher Filter, der sich je nach Parameter
            selbst abschaltet, ist auf JEDER Datenbank schlecht: Der Planer muss
            einen Plan waehlen, der fuer beide Faelle gilt, und nimmt dann den
            vollen Durchlauf. Bei 934.000 Verknuepfungen plus zwei Verbunden auf
            `asset` fuehrte das auf dem EventMesh-DataCell-Backend am 27.09.2026
            in den Lesetimeout -- die Kurvenansicht lieferte 500.

            Der Fall ohne Wertefilter braucht die Bedingung gar nicht, der Fall
            mit Wertefilter braucht kein `IS NULL`. Aufgeteilt bekommt jeder
            seinen eigenen, engen Plan.                                         */
        var wertefilter = assetId is not null
            ? "AND (l.asset_a = @assetId OR l.asset_b = @assetId)"
            : "";

        return (await conn.QueryAsync(new CommandDefinition(
            $"""
            SELECT l.lift, l.pairs, l.expected, l.median_lag_bars, l.lead_share,
                   l.mean_severity, l.type_a, l.type_b,
                   a.symbol AS symbol_a, b.symbol AS symbol_b,
                   l.asset_a, l.asset_b
              FROM dbo.curve_link l
              JOIN dbo.asset a ON a.asset_id = l.asset_a
              JOIN dbo.asset b ON b.asset_id = l.asset_b
             WHERE l.run_id = @id {wertefilter}
             ORDER BY l.lift DESC OFFSET 0 ROWS FETCH NEXT (@limit) ROWS ONLY;
            """, new { id, limit = Math.Clamp(limit, 10, 500), assetId },
            commandTimeout: 25, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<dynamic>> TypeStatsAsync(
        int? runId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var id = runId ?? await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(run_id) FROM dbo.curve_run WHERE finished_utc IS NOT NULL;",
            cancellationToken: ct)) ?? 0;

        return (await conn.QueryAsync(new CommandDefinition(
            """
            SELECT event_type, CAST(COUNT(*) AS INT) AS anzahl,
                   AVG(severity) AS mittlere_stufe,
                   MAX(severity) AS hoechste_stufe,
                   CAST(COUNT(DISTINCT asset_id) AS INT) AS werte
              FROM dbo.curve_event
             WHERE run_id = @id
             GROUP BY event_type
             ORDER BY anzahl DESC;
            """, new { id }, cancellationToken: ct))).ToList();
    }

    // ----------------------------------------------------------------------

    private async Task BulkAsync(DataTable t, string ziel, CancellationToken ct)
    {
        // Massenkopie statt einzelner INSERTs: Ein Durchgang erzeugt leicht
        // hunderttausend Zeilen, und dafür ist die Zeilenschnittstelle
        // chancenlos.
        await using var conn = await factory.OpenAsync(ct);
        await Massenkopie.SchreibeAsync(conn, t, ziel, 600, ct);
    }

    private static DataTable NewEventTable()
    {
        var t = new DataTable();
        t.Columns.Add("run_id", typeof(int));
        t.Columns.Add("asset_id", typeof(int));
        t.Columns.Add("ts_utc", typeof(DateTime));
        t.Columns.Add("event_type", typeof(string));
        t.Columns.Add("sign", typeof(short));
        t.Columns.Add("severity", typeof(double));
        t.Columns.Add("close_price", typeof(decimal));
        t.Columns.Add("smoothed", typeof(decimal));
        t.Columns.Add("slope", typeof(double));
        t.Columns.Add("curvature", typeof(double));
        return t;
    }

    private static DataTable NewLinkTable()
    {
        var t = new DataTable();
        t.Columns.Add("run_id", typeof(int));
        t.Columns.Add("asset_a", typeof(int));
        t.Columns.Add("asset_b", typeof(int));
        t.Columns.Add("type_a", typeof(string));
        t.Columns.Add("type_b", typeof(string));
        t.Columns.Add("pairs", typeof(int));
        t.Columns.Add("expected", typeof(double));
        t.Columns.Add("lift", typeof(double));
        t.Columns.Add("median_lag_bars", typeof(double));
        t.Columns.Add("lead_share", typeof(double));
        t.Columns.Add("mean_severity", typeof(double));
        return t;
    }
}
