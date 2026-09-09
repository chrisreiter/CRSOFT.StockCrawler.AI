using Ingest.Api.Scheduling;
using Ingest.Infrastructure.Options;
using Microsoft.Extensions.Options;

using Dapper;
using Ingest.Infrastructure.Repositories;
namespace Ingest.Api.Endpoints;

public static class SchedulerEndpoints
{
    public static void MapSchedulerEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/scheduler").WithTags("Scheduler");

        g.MapGet("/", async (SchedulerState state, IOptions<IngestOptions> opt,
                             ISqlConnectionFactory factory, CancellationToken ct) =>
        {
            /* Einzelschritte aus der Datenbank, nicht nur der Rahmen aus dem Zustand.

               `SchedulerState` weiss, dass ein Stundenlauf läuft -- nicht, ob er gerade
               Kurse holt oder bewertet. Ein Lauf dauert Minuten; eine Leiste, die
               zehn Minuten lang „Stundenlauf" zeigt, sagt nichts darüber, ob etwas
               vorangeht. `ingest_run` führt jeden Schritt mit Start und Ende. */
            /* Nur FRISCHE Läufe zählen als laufend.

               `finished_utc IS NULL` allein genügt nicht: Ein Lauf, den ein Neustart
               mitten in der Arbeit erwischt, wird nie fertig markiert und steht für
               immer als laufend da. Am 26.08.2026 waren das siebzehn Einträge, der
               älteste seit 5,5 Tagen — eine Statusleiste hätte davon einen als
               „läuft gerade" gemeldet und wäre damit dauerhaft falsch gewesen.

               Zwei Stunden Frist: Der längste echte Schritt (update:1h über 600 Werte)
               dauerte gemessen sieben Minuten; alles jenseits von zwei Stunden ist mit
               Sicherheit eine Leiche. */
            var schritt = state.Busy
                ? await (await factory.OpenAsync(ct)).QuerySingleOrDefaultAsync<Schritt>(
                    new CommandDefinition(
                        """
                        SELECT TOP 1 job_name AS Name, started_utc AS SeitUtc
                          FROM dbo.ingest_run
                         WHERE finished_utc IS NULL
                           AND started_utc >= DATEADD(hour, -2, SYSUTCDATETIME())
                         ORDER BY started_utc DESC
                        """, cancellationToken: ct))
                : null;

            var jetzt = DateTime.UtcNow;

            /* Der nächste Termin ist der frühere der beiden -- und welcher das ist,
               wechselt im Tagesverlauf. Ihn fest auf „stündlich" zu setzen wäre kurz
               vor 02:20 falsch. */
            DateTime? naechsterZeit = null;
            string? naechsterName = null;

            if (state.NextHourlyUtc is { } sh && state.NextDailyUtc is { } sd)
            {
                (naechsterZeit, naechsterName) = sh <= sd
                    ? (sh, "Stundenlauf")
                    : (sd, "Tageslauf");
            }
            else if (state.NextHourlyUtc is { } h2) (naechsterZeit, naechsterName) = (h2, "Stundenlauf");
            else if (state.NextDailyUtc is { } d2) (naechsterZeit, naechsterName) = (d2, "Tageslauf");

            return Results.Ok(new
            {
                enabled = state.Enabled,
                busy = state.Busy,

                /* Der Zustand entscheidet, ob etwas läuft -- nicht die Datenbank. Er
                   weiss es sicher; die Datenbank weiss nur, was nicht abgeschlossen
                   wurde, und das ist etwas anderes. */
                laufend = state.LaufendJob is null ? null : new
                {
                    job = state.LaufendJob,
                    seitUtc = state.LaufendSeitUtc,
                    sekunden = state.LaufendSeitUtc is { } s2
                        ? (int)(jetzt - s2).TotalSeconds : (int?)null,

                    // Der Einzelschritt sagt, ob es vorangeht.
                    schritt = schritt?.Name,
                    schrittSekunden = schritt is null
                        ? (int?)null : (int)(jetzt - schritt.SeitUtc).TotalSeconds
                },

                naechster = naechsterZeit is null ? null : new
                {
                    name = naechsterName,
                    wannUtc = naechsterZeit,
                    inSekunden = (int)Math.Max(0, (naechsterZeit.Value - jetzt).TotalSeconds)
                },

                nextHourlyUtc = state.NextHourlyUtc,
                nextDailyUtc = state.NextDailyUtc,
                lastHourlyUtc = state.LastHourlyUtc,
                lastDailyUtc = state.LastDailyUtc,
                lastJob = state.LastJob,
                lastResult = state.LastResult,
                skippedWhileDisabled = state.SkippedWhileDisabled,
                hourlyCronUtc = opt.Value.HourlyCronUtc,
                dailyCronUtc = opt.Value.DailyCronUtc
            });
        });

        /* Umschalten wirkt sofort und ohne Neustart. Der Takt läuft weiter —
           angehaltene Termine werden übersprungen, nicht nachgeholt. Nach dem
           Wiedereinschalten steht damit direkt der nächste reguläre Termin an. */
        g.MapPost("/enabled", (SchedulerState state, bool value) =>
        {
            state.Enabled = value;
            return Results.Ok(new
            {
                enabled = state.Enabled,
                note = value
                    ? "Scheduler läuft. Nächster Termin wie geplant."
                    : "Scheduler angehalten. Termine werden übersprungen, nicht nachgeholt."
            });
        });

        // Einen Lauf sofort anstoßen, unabhängig vom Zeitplan.
        g.MapPost("/run", async (CronScheduler scheduler, SchedulerState state,
                                 string job = "hourly", CancellationToken ct = default) =>
        {
            if (state.Busy)
                return Results.Conflict(new { error = "Es läuft bereits ein Job." });

            var name = job.ToLowerInvariant() is "daily" or "tag" ? "Tageslauf" : "Stundenlauf";

            using var scope = state.BeginRun(name + " (manuell)");
            try
            {
                await scheduler.TriggerAsync(job, ct);
                state.SetResult("ok");
                return Results.Ok(new { started = name, result = "fertig" });
            }
            catch (Exception ex)
            {
                state.SetResult("Fehler: " + ex.Message);
                return Results.Problem($"{name} fehlgeschlagen: {ex.Message}");
            }
        });
    }

    /// <summary>Ein einzelner laufender Schritt aus <c>ingest_run</c>.</summary>
    private sealed class Schritt
    {
        public string Name { get; set; } = "";
        public DateTime SeitUtc { get; set; }
    }
}
