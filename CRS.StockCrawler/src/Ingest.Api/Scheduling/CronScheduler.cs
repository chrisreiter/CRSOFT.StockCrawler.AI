using Cronos;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Options;
using Ingest.Infrastructure.Services;
using Microsoft.Extensions.Options;

using Dapper;
using Ingest.Infrastructure.Repositories;
namespace Ingest.Api.Scheduling;

/// <summary>
/// Führt die wiederkehrenden Läufe aus. Bewusst zwei Takte:
///
/// Stündlich — neue Stundenbars holen, fällige Prognosen bewerten (dabei
/// lernen die Gewichte), anschließend neue Prognosen stellen.
///
/// Täglich — Tagesbars holen, das Universum auffrischen und die
/// Wechselwirkungsanalyse neu rechnen. Letztere ist teuer und ändert sich
/// von Stunde zu Stunde kaum.
/// </summary>
public sealed class CronScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IngestOptions _opt;
    private readonly SchedulerState _state;
    private readonly ILogger<CronScheduler> _log;

    public CronScheduler(IServiceScopeFactory scopes, IOptions<IngestOptions> opt,
                         SchedulerState state, ILogger<CronScheduler> log)
    {
        _scopes = scopes;
        _opt = opt.Value;
        _state = state;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var hourly = Parse(_opt.HourlyCronUtc, "8 * * * *");
        var daily = Parse(_opt.DailyCronUtc, "20 2 * * *");

        var nextHourly = hourly.GetNextOccurrence(DateTime.UtcNow);
        var nextDaily = daily.GetNextOccurrence(DateTime.UtcNow);

        _state.Enabled = _opt.SchedulerEnabled;
        _state.NextHourlyUtc = nextHourly;
        _state.NextDailyUtc = nextDaily;

        _log.LogInformation(
            "Scheduler {Status}. Nächster Stundenlauf {H:u}, nächster Tageslauf {D:u}",
            _state.Enabled ? "aktiv" : "angehalten", nextHourly, nextDaily);

        while (!ct.IsCancellationRequested)
        {
            var next = Min(nextHourly, nextDaily);
            if (next is null)
            {
                _log.LogWarning("Kein gültiger Cron-Ausdruck — Scheduler beendet sich.");
                return;
            }

            var delay = next.Value - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { return; }
            }

            var now = DateTime.UtcNow;

            if (nextHourly is not null && now >= nextHourly.Value)
            {
                await SafeRun("Stundenlauf", RunHourlyAsync, ct);
                nextHourly = hourly.GetNextOccurrence(DateTime.UtcNow);
                _state.NextHourlyUtc = nextHourly;
            }

            if (nextDaily is not null && now >= nextDaily.Value)
            {
                await SafeRun("Tageslauf", RunDailyAsync, ct);
                nextDaily = daily.GetNextOccurrence(DateTime.UtcNow);
                _state.NextDailyUtc = nextDaily;
            }
        }
    }

    /// <summary>Erlaubt der Schnittstelle, einen Lauf von Hand anzustoßen.</summary>
    public Task TriggerAsync(string job, CancellationToken ct) => job.ToLowerInvariant() switch
    {
        "daily" or "tag" => RunDailyAsync(ct),
        _ => RunHourlyAsync(ct)
    };

    private async Task RunHourlyAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;

        await sp.GetRequiredService<IIngestService>().UpdateIncrementalAsync(BarInterval.Hourly, ct);

        // Neue Bars heißt: der zwischengespeicherte Bestand ist veraltet.
        Ingest.Infrastructure.Services.FeatureExportService.BestandVerwerfen();

        // Erst bewerten, dann prognostizieren: so fließen die frisch gelernten
        // Gewichte direkt in die nächste Prognose ein.
        await sp.GetRequiredService<IScoringService>().ScoreDueAsync(ct);
        await sp.GetRequiredService<IForecastService>().RunAsync(ct);

        /* Beobachtete Adressen der Säule „Semantik".

           Im Stundentakt, nicht im Tagestakt: Meldungen sind schnell alt, und
           eine Zinsentscheidung erst am nächsten Morgen einzulesen wäre für
           eine Prognose wertlos. Der Dienst selbst holt nur, was nach seinem
           eigenen Abstand fällig ist — hier steht also der schnellste
           mögliche Takt, nicht der tatsächliche.

           Fehler bleiben in dieser Säule: Eine nicht erreichbare Adresse oder
           ein nicht gestartetes Qdrant darf den Stundenlauf nicht mitreißen,
           an dem auch Kursabruf und Prognose hängen. */
        try
        {
            var svc = sp.GetRequiredService<IKnowledgeService>();

            /* Erst die Feeds, dann die einzelnen Seiten.

               Feeds sind der Regelfall und liefern Artikel mit Zeitstempel;
               einzelne Seiten sind die Ausnahme fuer Quellen ohne Feed. */
            var f = await svc.RefreshFeedsAsync("semantic", 25, ct);

            if (f.NewArticles > 0)
                _log.LogInformation(
                    "Semantik: {Neu} neue Artikel aus {Feeds} Feeds, {Ein} eingebettet",
                    f.NewArticles, f.Feeds, f.Embedded);

            var n = await svc.RefreshWebAsync("semantic", ct);

            if (n > 0) _log.LogInformation("Semantik: {N} Einzelseiten neu eingebettet", n);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Semantik-Auffrischung übersprungen");
        }
    }

    private async Task RunDailyAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;

        await sp.GetRequiredService<IUniverseService>().RefreshAllAsync(ct);
        await sp.GetRequiredService<IIngestService>().UpdateIncrementalAsync(BarInterval.Daily, ct);

        Ingest.Infrastructure.Services.FeatureExportService.BestandVerwerfen();

        var analysis = sp.GetRequiredService<IAnalysisService>();
        await analysis.RecomputeAsync(BarInterval.Daily, 365, ct);
        await analysis.RecomputeAsync(BarInterval.Hourly, 720, ct);

        await sp.GetRequiredService<IScoringService>().ScoreDueAsync(ct);

        /* ------------------------------------------- Verdienst nachmessen --

           Die Mischung gewichtet mit `Regler × gemessener Verdienst`. Der Verdienst
           zweier Säulen kam bisher nur auf Knopfdruck zustande, und was nicht
           nachgemessen wird, veraltet:

             semantic   Die Kalibrierung entscheidet, ob Nachrichtenstimmung überhaupt
                        etwas beitragen darf. Das Archiv wächst täglich; ohne erneute
                        Messung bliebe die Säule für immer bei Verdienst null -- auch
                        dann, wenn ein Zusammenhang entstünde. Kostet Sekunden.

             knowledge  Die Bot-Muster sind über fünf Jahre gemessen. Der Zeitraum
                        verschiebt sich mit jedem Tag, und ein Muster, das seine
                        Wirkung verliert, behielte sonst seinen alten Verdienst.
                        Höchstens einmal die Woche -- nicht wegen der Kosten
                        (gemessen 42 s, nicht Minuten, wie hier zuerst stand),
                        sondern wegen der Ruhe: Ein Fünfjahresmedian bewegt sich
                        in einem Tag kaum, und ein Verdienst, der täglich leicht
                        wackelt, trägt Rauschen in die Prognose statt Information.

           Beide sind eingefasst: Eine Nebenmessung darf den Tageslauf nicht anhalten. */
        try
        {
            var kal = sp.GetRequiredService<ISemantikKalibrierung>();
            var opt = sp.GetRequiredService<IOptions<IngestOptions>>();

            var e = await kal.LaufeAsync(opt.Value.EffectiveHorizons, 120, ct);

            var traegt = e.Count(x => x.Skill > 0);

            _log.LogInformation(
                "Semantik nachkalibriert: {Traegt} von {Anzahl} Horizonten mit Rückhalt",
                traegt, e.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Semantik-Kalibrierung übersprungen");
        }

        try
        {
            await using var conn = await sp.GetRequiredService<ISqlConnectionFactory>()
                                           .OpenAsync(ct);

            /* Nur, wenn die letzte Messung älter als eine Woche ist.

               Nicht aus Sparsamkeit -- der Lauf über fünf Jahre und alle verfolgten
               Werte dauert gemessen 42 Sekunden. Sondern weil ein Fünfjahresmedian
               sich in einem Tag kaum bewegt: Ein täglich neu gewürfelter Verdienst
               liesse die Prognose schwanken, ohne dass sich etwas geändert hätte. */
            var alter = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT DATEDIFF(day, MAX(von_utc), SYSUTCDATETIME()) FROM dbo.bot_trigger_run",
                cancellationToken: ct));

            if (alter is null || alter >= 7)
            {
                _log.LogInformation("Bot-Muster werden neu gemessen (letzte Messung "
                                  + "{Alter} Tage her)", alter);

                await conn.ExecuteAsync(new CommandDefinition(
                    "dbo.run_bot_trigger_test", new { jahre = 5 },
                    commandType: System.Data.CommandType.StoredProcedure,
                    commandTimeout: 3600, cancellationToken: ct));

                _log.LogInformation("Bot-Muster neu gemessen");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Bot-Muster-Messung übersprungen");
        }

        /* Nach dem Holen wird neu geschätzt -- auch im Tageslauf.

           Bisher stand hier nur die Bewertung. Der Tageslauf holte also die neuen
           Tagesbars, rechnete die Analyse und bewertete die fälligen Prognosen -- aber
           die Schätzung blieb auf dem Stand des letzten Stundenlaufs. Für Werte mit
           Tagesbars und ohne Stundendaten hiess das: Der Kurs war neu, die Prognose
           dazu nicht.

           Die Regel ist einfach und gilt für jeden Lauf: Wer Kurse holt, schätzt
           danach neu. */
        await sp.GetRequiredService<IForecastService>().RunAsync(ct);

        /* Und ganz zuletzt der Autopilot.

           Nach der Neuprognose, weil er genau darauf aufbaut: Er bewertet die
           frischen Schätzungen und die gemessenen Trefferquoten. Vor der Prognose
           gerechnet handelte er nach den Zahlen von gestern.

           Im TAGESLAUF und nicht im Stundenlauf: Bei 0,33 % Stundenbewegung und
           0,3 % Rundlauf wären 95,4 % Trefferquote nötig, damit sich ein
           stündliches Geschäft überhaupt trägt. Der eingestellte Takt kann
           darüber hinaus auf Wochen, Monate oder ein Jahr stehen — bewertet und
           protokolliert wird trotzdem jeden Tag, sonst wüsste bei Jahrestakt elf
           Monate lang niemand, ob der Autopilot noch läuft.

           Eigenes try/catch: Ein Fehler hier darf den Tageslauf nicht nachträglich
           als gescheitert erscheinen lassen -- Kurse, Analyse, Bewertung und
           Prognose sind zu diesem Zeitpunkt längst erledigt. */
        /* Vorankuendigungen abholen -- VOR dem Autopiloten.

           Die Kohorte waechst nur vorwaerts: Was heute nicht eingesammelt wird,
           ist morgen nicht rueckwirkend zu bekommen, weil der eigene Bestand
           nur die Werte kennt, die es in die Rangliste geschafft haben. Ein
           verpasster Tag ist eine Luecke, die bleibt.

           Eigenes try/catch: Die Quellen liegen ausserhalb, und ein Ausfall dort
           darf weder den Autopiloten noch den Tageslauf mitreissen. */
        try
        {
            var nz = await sp.GetRequiredService<INeuzugangService>().SammleAsync(ct);

            _log.LogInformation("Neuzugänge: {N} Quellen, {Neu} neu",
                nz.Count, nz.Sum(x => x.Neu));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Neuzugänge übersprungen");
        }

        try
        {
            var laeufe = await sp.GetRequiredService<IAutopilotService>().LaufeAlleAsync(ct);

            if (laeufe.Count > 0)
                _log.LogInformation("Autopilot: {N} Strategien gelaufen, {G} Geschäfte",
                    laeufe.Count, laeufe.Sum(l => l.Geschaefte));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Autopilot übersprungen");
        }
    }

    private async Task SafeRun(string name, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        /* Der Takt läuft weiter, nur die Arbeit unterbleibt. So steht nach dem
           Wiedereinschalten sofort der nächste reguläre Termin an, statt dass
           erst ein Zeitplan neu aufgebaut werden müsste. */
        if (!_state.Enabled)
        {
            _state.NoteSkipped();
            _log.LogInformation("{Name} übersprungen — Scheduler ist angehalten", name);
            return;
        }

        using var scope = _state.BeginRun(name);

        try
        {
            _log.LogInformation("{Name} startet", name);
            await action(ct);
            _state.SetResult("ok");
            _log.LogInformation("{Name} beendet", name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Herunterfahren, kein Fehler.
        }
        catch (Exception ex)
        {
            // Ein fehlgeschlagener Lauf darf den Scheduler nie beenden.
            _state.SetResult("Fehler: " + ex.Message);
            _log.LogError(ex, "{Name} fehlgeschlagen", name);
        }
    }

    private CronExpression Parse(string expr, string fallback)
    {
        try { return CronExpression.Parse(expr); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cron-Ausdruck '{Expr}' ungültig, nutze '{Fallback}'", expr, fallback);
            return CronExpression.Parse(fallback);
        }
    }

    private static DateTime? Min(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : a < b ? a : b;
}
