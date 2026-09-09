using Ingest.Core.Abstractions;
using Ingest.Infrastructure.Options;
using Ingest.Infrastructure.Providers;
using Ingest.Infrastructure.Repositories;
using Ingest.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Yahoo und CoinGecko lehnen Anfragen ohne plausiblen Browser-Kennzeichner ab.
    /// </summary>
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/122.0 Safari/537.36";

    public static IServiceCollection AddIngestInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<YahooOptions>(config.GetSection("Sources:Yahoo"));
        services.Configure<TwelveDataOptions>(config.GetSection("Sources:TwelveData"));
        services.Configure<CoinGeckoOptions>(config.GetSection("Sources:CoinGecko"));
        services.Configure<IngestOptions>(config.GetSection("Ingest"));

        var cs = config.GetConnectionString("Sql")
                 ?? throw new InvalidOperationException("ConnectionStrings:Sql fehlt in der Konfiguration.");

        services.AddSingleton<ISqlConnectionFactory>(_ => new SqlConnectionFactory(cs));

        services.AddScoped<IAssetRepository, AssetRepository>();
        services.AddScoped<IPriceBarRepository, PriceBarRepository>();
        services.AddScoped<IIngestRunRepository, IngestRunRepository>();
        services.AddScoped<IForecastRepository, ForecastRepository>();
        services.AddScoped<IPairStatRepository, PairStatRepository>();
        services.AddScoped<ILearningRepository, LearningRepository>();
        services.AddScoped<IForecastTrackRepository, ForecastTrackRepository>();
        services.AddScoped<IAppStateRepository, AppStateRepository>();

        /* Einmal je Anwendung: Das ONNX-Modell zu laden kostet Zeit und
           Speicher, und es aendert sich nur, wenn neu trainiert wurde. */
        services.AddSingleton<IDeepForecastService, DeepForecastService>();
        services.AddScoped<ICurveDiscussionService, CurveDiscussionService>();
        services.AddScoped<ICrossingOpportunityService, CrossingOpportunityService>();
        services.AddScoped<IPortfolioService, PortfolioService>();
        services.AddScoped<IInvestService, InvestService>();
        services.AddScoped<IAutopilotService, AutopilotService>();
        services.AddScoped<INeuzugangService, NeuzugangService>();

        /*  Singleton, nicht Scoped: Die Sprachdateien werden beim Start gelesen
            und aendern sich nicht je Anfrage. Scoped hiesse, 1.711 Eintraege je
            Sprachwechsel neu zu zerlegen -- und zwar je Anfrage, nicht je
            Wechsel. Dieselbe Ueberlegung wie beim AuthService.               */
        services.AddSingleton<ILocService, LocService>();
        services.AddScoped<IDayTradingService, DayTradingService>();
        services.AddScoped<ILangfristService, LangfristService>();

        /* Als Singleton, nicht Scoped: Das Einrichtungswort dieses Starts muss
           über die Aufrufe hinweg dasselbe bleiben. Bei Scoped bekäme jeder
           Aufruf ein neues, und die Einrichtung wäre unmöglich. */
        services.AddSingleton<IAuthService, AuthService>();

        /* Wissen und Semantik.

           Beide Klienten sprechen mit lokalen Diensten (Ollama, Qdrant). Fehlt
           einer davon, laeuft die Anwendung weiter und die Saeule meldet, was
           fehlt -- ein nicht gestartetes Qdrant darf nicht den Start der API
           verhindern. Geprueft wird deshalb erst beim Zugriff, nicht hier. */
        /* Der Endpunktdienst haelt die SSH-Tunnel offen und muss deshalb
           einmal je Anwendung bestehen -- ein Tunnel je Anfrage waere teurer
           als die Anfrage. */
        services.AddSingleton<IOllamaEndpointService, OllamaEndpointService>();
        services.AddScoped<IReasoningLogService, ReasoningLogService>();

        /* Der vast.ai-Kontoschlüssel kommt aus der UMGEBUNG, nicht aus appsettings.json.

           Die Datei liegt in der Versionsverwaltung und wird mit ausgeliefert; ein Schlüssel
           darin wäre in jedem Klon und in jedem Auslieferungsordner. Die Konfiguration bleibt
           als zweiter Weg stehen — wer ihn dort hinterlegen will, soll das können, aber er
           muss es tun, statt es zu erben. */
        services.AddSingleton<IVastAiClient>(sp => new VastAiClient(
            sp.GetRequiredService<ILogger<VastAiClient>>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            Environment.GetEnvironmentVariable("CRS_VASTAI_KEY")
            ?? config["VastAi:ApiKey"]));

        services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>();

        services.AddHttpClient<IVectorStore, QdrantClient>(c =>
        {
            c.BaseAddress = new Uri(
                Environment.GetEnvironmentVariable("CRS_QDRANT_URL") ?? "http://localhost:6333");
            c.Timeout = TimeSpan.FromMinutes(5);
        });

        /* Ein benannter Klient fuers Abholen von Seiten.

           Ohne Browser-Kennung antworten viele Seiten mit 403 -- dieselbe
           Falle wie bei Yahoo und CoinGecko. Der Klient folgt Umleitungen und
           gibt nach kurzer Zeit auf: Eine haengende Seite darf einen
           Auffrischungslauf ueber zwanzig Adressen nicht blockieren. */
        services.AddHttpClient("browser", c =>
        {
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            c.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            c.Timeout = TimeSpan.FromSeconds(45);
        });

        services.AddScoped<IKnowledgeService, KnowledgeService>();
        services.AddScoped<IPrognosegueteService, PrognosegueteService>();
        services.AddScoped<ISaeulenbeitragService, SaeulenbeitragService>();
        services.AddScoped<ISemantikKalibrierung, SemantikKalibrierung>();

        /* Singleton: Der Sammellauf ueberlebt die Anfrage, die ihn gestartet hat,
           und sein Fortschritt muss von jeder weiteren Anfrage lesbar sein. */
        services.AddSingleton<IEinbettungsLauf, EinbettungsLauf>();
        services.AddScoped<IBriefingService, BriefingService>();
        services.AddScoped<ICombinedForecastService, CombinedForecastService>();
        services.AddScoped<IJournalService, JournalService>();

        /* Der Gespraechsagent. Eigener Klient mit langer Zeitueberschreitung:
           Ein 33-Milliarden-Modell auf der CPU braucht je Runde Minuten, und
           der Agent darf mehrere Runden Werkzeuge aufrufen. */
        /* Keine feste Basisadresse: Sie kommt je Aufruf vom Endpunktdienst,
           weil sie sich mit dem gewaehlten Endpunkt aendert. */
        services.AddHttpClient<IReasoningService, ReasoningService>(c =>
            c.Timeout = TimeSpan.FromMinutes(20));

        // Kursdaten-Provider. Die Reihenfolge bestimmt, wer einspringt, wenn
        // der am Asset hinterlegte Provider nicht einsatzbereit ist.
        services.AddHttpClient<YahooProvider>(ConfigureYahoo);
        services.AddHttpClient<TwelveDataProvider>((sp, c) =>
        {
            c.BaseAddress = new Uri(GetOpt<TwelveDataOptions>(sp, config, "Sources:TwelveData").BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddScoped<IMarketDataProvider>(sp => sp.GetRequiredService<YahooProvider>());
        services.AddScoped<IMarketDataProvider>(sp => sp.GetRequiredService<TwelveDataProvider>());

        // Universum-Provider: Yahoo-Screener für Aktien/Fonds, CoinGecko für Krypto.
        services.AddHttpClient<YahooScreenerUniverseProvider>(ConfigureYahoo);
        services.AddHttpClient<YahooSectorProvider>(ConfigureYahoo);
        /* Das Bildmodell läuft örtlich. Die Zeitspanne ist großzügig gesetzt:
           Ollama lädt ein Modell beim ersten Aufruf in den Speicher, und dieser
           erste Aufruf dauert um ein Vielfaches länger als die folgenden. Eine
           knappe Zeitspanne würde genau diesen ersten Aufruf abbrechen und das
           Verfahren als kaputt erscheinen lassen. */
        services.AddHttpClient<IVlmClient, OllamaClient>(c =>
        {
            c.BaseAddress = new Uri("http://localhost:11434");
            c.Timeout = TimeSpan.FromMinutes(5);
        });

        services.AddHttpClient<CoinGeckoUniverseProvider>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);

            // Ohne User-Agent weist CoinGecko anonyme Anfragen mit 403 ab.
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        });

        services.AddScoped<IUniverseProvider>(sp => sp.GetRequiredService<YahooScreenerUniverseProvider>());
        services.AddScoped<IUniverseProvider>(sp => sp.GetRequiredService<CoinGeckoUniverseProvider>());

        services.AddScoped<IUniverseService, UniverseService>();
        services.AddScoped<IIngestService, IngestService>();
        services.AddScoped<IAnalysisService, AnalysisService>();
        services.AddScoped<IForecastService, ForecastService>();
        services.AddScoped<IScoringService, ScoringService>();
        services.AddScoped<IBacktestService, BacktestService>();
        services.AddScoped<IWalkForwardService, WalkForwardService>();
        services.AddScoped<IFeatureExportService, FeatureExportService>();

        return services;

        void ConfigureYahoo(IServiceProvider sp, HttpClient c)
        {
            c.BaseAddress = new Uri(GetOpt<YahooOptions>(sp, config, "Sources:Yahoo").BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(45);

            // Ohne plausiblen User-Agent antwortet Yahoo mit 403.
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        }
    }

    private static T GetOpt<T>(IServiceProvider sp, IConfiguration config, string section) where T : new()
    {
        var o = new T();
        config.GetSection(section).Bind(o);
        return o;
    }
}
