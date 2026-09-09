using Ingest.Api.Endpoints;
using Ingest.Api.Scheduling;
using Ingest.Infrastructure;
using Ingest.Infrastructure.Options;
using Serilog;
using Ingest.Infrastructure.Services;
using Ingest.Api;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddIngestInfrastructure(builder.Configuration);
/* Der Scheduler wird zusätzlich als Singleton registriert, damit die
   Schnittstelle einen Lauf von Hand anstoßen kann. AddHostedService allein
   gäbe keinen Zugriff auf die Instanz. */
builder.Services.AddSingleton<SchedulerState>();
builder.Services.AddSingleton<CronScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CronScheduler>());

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;

    // Enums als Text ausgeben. Die Oberfläche zeigt "Aktie"/"Krypto" anhand des
    // Namens an — als blanke Zahl stünde dort sonst "0" beziehungsweise "2".
    o.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();

app.UseSerilogRequestLogging();

// Die Weboberfläche liegt als statisches SPA in wwwroot.
/* Die eigene Startseite mit Fassungsnummer ausliefern.

   Statische Dateien werden vom Browser gehalten -- was bei uPlot erwuenscht
   ist, bei app.js aber dazu fuehrt, dass nach einer Aenderung stillschweigend
   der alte Stand laeuft. Man sucht dann Fehler in Code, der gar nicht geladen
   ist. Der Platzhalter wird beim Ausliefern durch die Startzeit ersetzt; jeder
   Neustart erzwingt damit genau einmal ein Nachladen. */
/* Die Fassungsnummer kommt aus dem Änderungszeitpunkt der Datei selbst.

   Der erste Entwurf nahm die Startzeit der Anwendung. Das bustet den Cache bei
   jedem Neustart und — schlimmer — NICHT, wenn man app.js ändert, ohne neu zu
   starten. Genau das ist beim Entwickeln der Normalfall: Man bearbeitet die
   Datei, lädt die Seite neu und debuggt anschließend Code, der gar nicht
   geladen ist. Der Änderungszeitpunkt ändert sich genau dann, wenn sich die
   Datei ändert. */
string StempelFuer(string relativ)
{
    var pfad = Path.Combine(app.Environment.WebRootPath, relativ);

    return File.Exists(pfad)
        ? File.GetLastWriteTimeUtc(pfad).Ticks.ToString("x")
        : "0";
}

async Task StartseiteAsync(HttpContext ctx)
{
    var file = Path.Combine(app.Environment.WebRootPath, "index.html");

    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";

    var html = await File.ReadAllTextAsync(file);
    /* Je Datei ein eigener Stempel: Eine Änderung an app.css soll nicht
       app.js neu laden lassen und umgekehrt. */
    html = html.Replace("app.js?v=@BUILD@", $"app.js?v={StempelFuer("app.js")}")
               .Replace("loc.js?v=@BUILD@", $"loc.js?v={StempelFuer("loc.js")}")
               .Replace("app.css?v=@BUILD@", $"app.css?v={StempelFuer("app.css")}");

    await ctx.Response.WriteAsync(html);
}

/* Sicherheitskopfzeilen. Auf einem oeffentlich erreichbaren Server sind das
   keine Formalitaeten:

     HSTS                    Ohne das genuegt EIN Aufruf ueber http, um das
                             Sitzungscookie im Klartext zu verlieren.
     X-Content-Type-Options  Verhindert, dass der Browser eine Antwort anders
                             deutet, als sie ausgezeichnet ist.
     X-Frame-Options         Kein Einbetten in fremde Seiten.
     Referrer-Policy         Keine Adressen dieser Anlage an fremde Server.

   HSTS nur ueber HTTPS setzen -- ueber http ist die Kopfzeile wirkungslos und
   laut Vorgabe zu ignorieren. */
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;

    if (ctx.Request.IsHttps)
        h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";

    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";

    await next();
});

app.MapGet("/", StartseiteAsync);

/* Die Zugangskontrolle sitzt VOR den statischen Dateien.

   Andersherum lief sie ins Leere: `UseStaticFiles()` beantwortet die Anfrage
   selbst und beendet die Kette -- app.js und app.css gingen an jeden, der die
   Adresse kannte, ohne die Sperre je zu berühren. Gemessen an der laufenden
   Auslieferung: 237 KB Anwendungscode und 91 KB Oberfläche samt allen
   Erklärtexten, ohne Anmeldung abrufbar.

   Ohne gültige Sitzung wird jetzt nur `anmeldung.html` ausgeliefert -- eine
   eigenständige Seite, die nichts nachlädt. */
app.UseAnmeldepflicht();

app.UseStaticFiles();

app.MapAuthEndpoints();

app.MapAssetEndpoints();
app.MapIngestEndpoints();
app.MapSeriesEndpoints();
app.MapAnalysisEndpoints();
app.MapForecastEndpoints();
app.MapFlowEndpoints();
app.MapMarketFlowEndpoints();
app.MapFlowInsightEndpoints();
app.MapStateEndpoints();
app.MapSpectralEndpoints();
app.MapVlmEndpoints();
app.MapTransmissionEndpoints();
app.MapDeepEndpoints();
app.MapCurveEndpoints();
app.MapKnowledgeEndpoints();
app.MapPrognosegueteEndpoints();
app.MapSaeulenmischungEndpoints();
app.MapReasoningEndpoints();
app.MapHygieneEndpoints();
app.MapPortfolioEndpoints();
app.MapInvestEndpoints();
app.MapAutopilotEndpoints();
app.MapNeuzugangEndpoints();
app.MapLocEndpoints();
app.MapDayTradingEndpoints();
app.MapLangfristEndpoints();
app.MapHerdeEndpoints();
app.MapPillarWeightEndpoints();
app.MapAssetAddEndpoints();
app.MapOllamaEndpointEndpoints();
app.MapLearningEndpoints();
app.MapModelEndpoints();
app.MapSchedulerEndpoints();
app.MapHealthEndpoints();

// Fallback auf die SPA, damit ein Reload auf einer Unterseite nicht 404 gibt.
/* Der Rückfall auf die Startseite gilt NICHT für /api.

   Vorher lieferte jeder vertippte API-Pfad die HTML-Seite mit Status 200. Das
   ist die unangenehmste Art von Fehler: Ein Test, der nur auf den Statuscode
   sieht, meldet Erfolg — und genau das ist im eigenen Prüflauf passiert, drei
   falsche Pfade galten als bestanden. Ein unbekannter Endpunkt muss 404
   sagen. */
app.MapFallback("/api/{**rest}", () => Results.NotFound(new
{
    error = "Unbekannter Endpunkt",
    hinweis = "Die Übersicht aller Endpunkte steht unter /swagger."
}));

app.MapFallback(StartseiteAsync);

try
{
    var opt = app.Services.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<IngestOptions>>().Value;

    Log.Information(
        "CRSOFT.StockCrawler startet. Horizonte: {Horizons}h, Historie: {Months} Monate",
        string.Join("/", opt.EffectiveHorizons), opt.HistoryMonths);

    /* Das Einrichtungswort ins Protokoll -- aber nur, solange es noch keinen
       Benutzer gibt. Wer den Server sieht, darf den ersten Verwalter anlegen;
       wer nur die Adresse kennt, nicht. */
    using (var start = app.Services.CreateScope())
    {
        var auth = start.ServiceProvider.GetRequiredService<IAuthService>();

        if (await auth.IstEingerichtetAsync())
        {
            Log.Information("Zugangskontrolle aktiv.");
        }
        else if (app.Environment.IsDevelopment() && await auth.EntwicklerzugangAsync())
        {
            /*  Bequemlichkeit fuer den, der das Projekt zum ersten Mal
                auscheckt -- und nur fuer den. Die Auslieferung setzt
                ASPNETCORE_ENVIRONMENT auf Production; dort faellt dieser
                Zweig weg, und es bleibt beim Einrichtungswort.

                Die Warnung ist laut und bleibt laut: Ein Standardzugang, den
                man vergisst, ist genau das Loch, das die Anmeldung schliessen
                soll -- und dieses Repository ist oeffentlich, das Kennwort
                steht also fuer jeden lesbar in der README.                   */
            Log.Warning("ENTWICKLERZUGANG angelegt: admin / admin — gilt nur, weil "
                      + "ASPNETCORE_ENVIRONMENT=Development steht. Vor dem ersten "
                      + "erreichbaren Betrieb ein eigenes Kennwort setzen "
                      + "(System → Benutzer) oder den Benutzer loeschen.");
        }
        else
        {
            Log.Warning(
                "Noch kein Benutzer angelegt. EINRICHTUNGSWORT: {Wort} — damit über "
                + "POST /api/auth/einrichten den ersten Verwalter anlegen. Das Wort gilt "
                + "nur für diesen Programmlauf.", auth.Einrichtungswort);
        }

        /* Läufe schliessen, die ein früherer Prozess offen gelassen hat.

           Genau hier gehört es hin und nicht in den Zeitplan: Der Start ist der
           Moment, in dem feststeht, dass kein früherer Lauf mehr arbeitet -- es gibt
           den Prozess nicht mehr, der ihn hätte fortsetzen können.

           Ohne das sammeln sich Einträge ohne `finished_utc` an; am 26.08.2026 waren
           es siebzehn, der älteste seit 5,5 Tagen. Für die Übersicht „Letzte Läufe"
           ist das nur unschön, für die Statusleiste wäre es falsch gewesen. */
        try
        {
            var fabrik = start.ServiceProvider
                              .GetRequiredService<Ingest.Infrastructure.Repositories.ISqlConnectionFactory>();

            await using var conn = await fabrik.OpenAsync();

            var n = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn,
                "dbo.close_orphaned_runs", new { stunden = 2 },
                commandType: System.Data.CommandType.StoredProcedure);

            if (n > 0)
                Log.Information("{Anzahl} abgebrochene Läufe nachträglich geschlossen", n);
        }
        catch (Exception ex)
        {
            /* Aufräumen ist Beiwerk. Wer daran den Start scheitern lässt, macht aus
               einer Unschönheit einen Ausfall. */
            Log.Warning(ex, "Verwaiste Läufe konnten nicht geschlossen werden");
        }
    }

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Anwendung unerwartet beendet");
}
finally
{
    Log.CloseAndFlush();
}
