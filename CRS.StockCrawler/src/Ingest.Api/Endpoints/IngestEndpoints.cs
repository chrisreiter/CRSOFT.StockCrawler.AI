using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Ingest.Api.Endpoints;

public static class IngestEndpoints
{
    public static void MapIngestEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/ingest").WithTags("Ingest");

        // Historie holen. months ist frei einstellbar — das ist der
        // "Kurse der letzten n Monate"-Schalter.
        g.MapPost("/backfill", async (IIngestService svc, IOptions<IngestOptions> opt,
                                      int? months, string interval = BarInterval.Daily,
                                      CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var m = months ?? (interval == BarInterval.Hourly
                ? opt.Value.HourlyHistoryMonths
                : opt.Value.HistoryMonths);

            return Results.Ok(await svc.BackfillAsync(Math.Clamp(m, 1, 600), interval, ct));
        });

        g.MapPost("/update", async (IIngestService svc, string interval = BarInterval.Daily,
                                    CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            return Results.Ok(await svc.UpdateIncrementalAsync(interval, ct));
        });

        g.MapPost("/asset/{assetId:int}", async (IIngestService svc, int assetId,
                                                 int months = 24, string interval = BarInterval.Daily,
                                                 CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = $"Intervall muss 1h oder 1d sein, war: {interval}" });

            var to = DateTime.UtcNow;
            var from = to.AddMonths(-Math.Clamp(months, 1, 600));
            var rows = await svc.IngestOneAsync(assetId, interval, from, to, ct);

            return Results.Ok(new { assetId, interval, months, rowsWritten = rows });
        });

        // Kompletter Erstlauf: Universum, Tages- und Stundenhistorie, Analyse,
        // erste Prognosen. Läuft je nach Umfang einige Minuten.
        g.MapPost("/bootstrap", async (IUniverseService universe, IIngestService ingest,
                                       IAnalysisService analysis, IForecastService forecast,
                                       IOptions<IngestOptions> opt,
                                       int? months, CancellationToken ct = default) =>
        {
            var o = opt.Value;
            var daily = months ?? o.HistoryMonths;
            var hourly = Math.Min(months ?? o.HourlyHistoryMonths, o.HourlyHistoryMonths);

            var u = await universe.RefreshAllAsync(ct);
            var d = await ingest.BackfillAsync(daily, BarInterval.Daily, ct);
            var h = await ingest.BackfillAsync(hourly, BarInterval.Hourly, ct);
            var ad = await analysis.RecomputeAsync(BarInterval.Daily, 365, ct);
            var ah = await analysis.RecomputeAsync(BarInterval.Hourly, 720, ct);
            var f = await forecast.RunAsync(ct);

            return Results.Ok(new
            {
                universe = u,
                dailyBackfill = d,
                hourlyBackfill = h,
                analysisDaily = ad,
                analysisHourly = ah,
                forecasts = f
            });
        });
    }
}
