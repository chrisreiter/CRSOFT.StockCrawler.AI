namespace Ingest.Core.Enums;

/// <summary>
/// Die beiden Auflösungen, die wir speichern. Der String landet 1:1 in
/// price_bar.interval_code, deshalb hier zentral gekapselt.
/// </summary>
public static class BarInterval
{
    public const string Hourly = "1h";
    public const string Daily  = "1d";

    public static readonly string[] All = [Hourly, Daily];

    /// <summary>Dauer einer Bar – für Horizont- und Lag-Rechnungen.</summary>
    public static TimeSpan Duration(string code) => code switch
    {
        Hourly => TimeSpan.FromHours(1),
        Daily  => TimeSpan.FromDays(1),
        _      => throw new ArgumentOutOfRangeException(nameof(code), code, "Unbekanntes Intervall")
    };

    public static bool IsValid(string code) => code is Hourly or Daily;
}
