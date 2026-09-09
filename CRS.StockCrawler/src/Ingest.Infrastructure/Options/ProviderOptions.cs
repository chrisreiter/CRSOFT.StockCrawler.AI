namespace Ingest.Infrastructure.Options;

public sealed class YahooOptions
{
    public string BaseUrl { get; set; } = "https://query1.finance.yahoo.com";

    /// <summary>Yahoo drosselt anonyme Clients; kleine Pause zwischen Calls.</summary>
    public int DelayMs { get; set; } = 350;
}

public sealed class TwelveDataOptions
{
    public string BaseUrl { get; set; } = "https://api.twelvedata.com";
    public string? ApiKey { get; set; }

    /// <summary>Free-Tier erlaubt 8 Calls/Minute. Default lässt Luft.</summary>
    public int DelayMs { get; set; } = 8000;

    public int MaxOutputSize { get; set; } = 5000;
}

public sealed class CoinGeckoOptions
{
    public string BaseUrl { get; set; } = "https://api.coingecko.com/api/v3";
    public string? ApiKey { get; set; }
    public string VsCurrency { get; set; } = "usd";
    public int DelayMs { get; set; } = 2500;
}

public sealed class IngestOptions
{
    /// <summary>Wie viele Monate Tageshistorie der Backfill holt.</summary>
    public int HistoryMonths { get; set; } = 24;

    /// <summary>Monate Stundenhistorie. Yahoo liefert hier maximal rund 24.</summary>
    public int HourlyHistoryMonths { get; set; } = 12;

    public int TopStocks { get; set; } = 100;
    public int TopEtfs { get; set; } = 100;
    public int TopCrypto { get; set; } = 100;

    public string DailyCronUtc { get; set; } = "20 2 * * *";
    public string HourlyCronUtc { get; set; } = "8 * * * *";

    /// <summary>
    /// Prognosehorizonte in Stunden: Stunden, Tage, Langfrist.
    ///
    /// Bewusst leer initialisiert: der Konfigurations-Binder hängt Array-Werte
    /// an einen vorhandenen Standard AN, statt ihn zu ersetzen. Mit einem
    /// vorbelegten Array stünde jeder Horizont doppelt in der Liste.
    /// Der Standard kommt deshalb erst über <see cref="EffectiveHorizons"/>.
    /// </summary>
    public int[] Horizons { get; set; } = [];

    /* Der Satz, über den prognostiziert wird: 1 Tag bis 1 Jahr.

       Die Stundenhorizonte 1 und 4 sind draussen. Sie erzeugten je Wert und Tag ein
       Vielfaches an Zeilen, ohne je eine eigene Aussage zu tragen -- bei einem Wert,
       dessen Stundenbar sich nicht bewegt, entsteht dieselbe Beobachtung mehrfach und
       täuscht Fallzahl vor. Gemessen an IGSB: vier identische Zeilen mit derselben
       Basis, derselben Prognose und demselben Ist-Wert.

       Ebenso draussen: 72 (3 Tage). Dafür neu 336 (2 Wochen) -- die Lücke zwischen
       einer Woche und einem Monat war die grösste im Satz. */
    private static readonly int[] DefaultHorizons = [24, 168, 336, 720, 2160, 4380, 8760];

    public int[] EffectiveHorizons =>
        Horizons.Length == 0
            ? DefaultHorizons
            : Horizons.Distinct().OrderBy(h => h).ToArray();

    /// <summary>Backfill automatisch beim ersten Start anstoßen.</summary>
    public bool AutoStartBackfill { get; set; } = false;

    /// <summary>
    /// Startzustand des Schedulers. Zur Laufzeit über die Schnittstelle
    /// umschaltbar; dieser Wert gilt nur beim Hochfahren.
    /// </summary>
    public bool SchedulerEnabled { get; set; } = true;
}
