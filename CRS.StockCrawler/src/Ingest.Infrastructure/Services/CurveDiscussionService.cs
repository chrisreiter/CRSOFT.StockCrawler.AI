using System.Data;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Infrastructure.Repositories;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Microsoft.Data.SqlClient;
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
    public async Task<CurveRunResult> RunAsync(
        string intervalCode, CurveDiscussion.Options opt,
        int[]? assetIds, DateTime? fromUtc, DateTime? toUtc,
        int linkWindowBars, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var conn = await factory.OpenAsync(ct);

        var runId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO dbo.curve_run
                (interval_code, from_utc, to_utc, half_window, causal,
                 ref_window, min_z, refractory, min_severity)
            OUTPUT INSERTED.run_id
            VALUES (@interval, @from, @to, @half, @causal,
                    @ref, @minZ, @refr, @minSev);
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
            """
            UPDATE dbo.curve_run
               SET finished_utc = SYSUTCDATETIME(),
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

        var where = """
            WHERE e.run_id = @id
              AND e.severity >= @minSev
              AND (@assetId IS NULL OR e.asset_id = @assetId)
              AND (@type IS NULL OR e.event_type = @type)
              AND (@sign IS NULL OR e.sign = @sign)
              AND (@from IS NULL OR e.ts_utc >= @from)
              AND (@to IS NULL OR e.ts_utc <= @to)
            """;

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
            $"SELECT COUNT_BIG(*) FROM dbo.curve_event e {where};", p, cancellationToken: ct));

        var rows = (await conn.QueryAsync<CurveEventRow>(new CommandDefinition(
            $"""
             SELECT e.curve_event_id AS CurveEventId, e.asset_id AS AssetId,
                    a.symbol AS Symbol, a.name AS Name, a.asset_class AS AssetClass,
                    e.ts_utc AS TsUtc, e.event_type AS EventType, e.sign AS Sign,
                    e.severity AS Severity, e.close_price AS ClosePrice,
                    e.smoothed AS Smoothed, e.slope AS Slope, e.curvature AS Curvature
               FROM dbo.curve_event e
               JOIN dbo.asset a ON a.asset_id = e.asset_id
             {where}
              ORDER BY {order}
             OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
             """, p, cancellationToken: ct))).ToList();

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
            """
            SELECT MAX(run_id) FROM dbo.curve_run
             WHERE causal = 1 AND finished_utc IS NOT NULL;
            """, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<dynamic>> RunsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        return (await conn.QueryAsync(new CommandDefinition(
            """
            SELECT TOP 40 run_id, started_utc, finished_utc, interval_code,
                   half_window, causal, ref_window, min_z, refractory,
                   min_severity, assets, events, note
              FROM dbo.curve_run
             ORDER BY run_id DESC;
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

        return (await conn.QueryAsync(new CommandDefinition(
            """
            SELECT TOP (@limit)
                   l.lift, l.pairs, l.expected, l.median_lag_bars, l.lead_share,
                   l.mean_severity, l.type_a, l.type_b,
                   a.symbol AS symbol_a, b.symbol AS symbol_b,
                   l.asset_a, l.asset_b
              FROM dbo.curve_link l
              JOIN dbo.asset a ON a.asset_id = l.asset_a
              JOIN dbo.asset b ON b.asset_id = l.asset_b
             WHERE l.run_id = @id
               AND (@assetId IS NULL OR l.asset_a = @assetId OR l.asset_b = @assetId)
             ORDER BY l.lift DESC;
            """, new { id, limit = Math.Clamp(limit, 10, 500), assetId },
            cancellationToken: ct))).ToList();
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
            SELECT event_type, COUNT(*) AS anzahl,
                   AVG(severity) AS mittlere_stufe,
                   MAX(severity) AS hoechste_stufe,
                   COUNT(DISTINCT asset_id) AS werte
              FROM dbo.curve_event
             WHERE run_id = @id
             GROUP BY event_type
             ORDER BY anzahl DESC;
            """, new { id }, cancellationToken: ct))).ToList();
    }

    // ----------------------------------------------------------------------

    private async Task BulkAsync(DataTable t, string ziel, CancellationToken ct)
    {
        // SqlBulkCopy statt einzelner INSERTs: Ein Durchgang erzeugt leicht
        // hunderttausend Zeilen, und dafür ist die Zeilenschnittstelle
        // chancenlos.
        await using var conn = await factory.OpenAsync(ct);

        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = ziel,
            BulkCopyTimeout = 600,
            BatchSize = 10_000
        };

        foreach (DataColumn c in t.Columns)
            bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);

        await bulk.WriteToServerAsync(t, ct);
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
