using Ingest.Core.Enums;

namespace Ingest.Core.Models;

/// <summary>Eine Position im Bestand des Nutzers.</summary>
public sealed class Holding
{
    public int HoldingId { get; set; }
    public int AssetId { get; set; }
    public string Symbol { get; set; } = "";
    public string? Name { get; set; }
    public AssetClass Klasse { get; set; }

    /// <summary>Eingesetztes Kapital in <see cref="Waehrung"/>.</summary>
    public decimal Kapital { get; set; }
    public string Waehrung { get; set; } = "USD";

    /// <summary>Einstandskurs, falls bekannt. Ohne ihn bleibt der Buchstand offen.</summary>
    public decimal? Einstand { get; set; }
    public DateTime? GekauftUtc { get; set; }
    public string? Notiz { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Letzter bekannter Kurs — für den Buchstand.</summary>
    public decimal? Kurs { get; set; }
    public DateTime? KursUtc { get; set; }

    /// <summary>
    /// Was aus dem Kapital seit dem Einstand geworden wäre. <c>null</c>, solange
    /// kein Einstandskurs eingetragen ist — geraten wird hier nichts.
    /// </summary>
    public decimal? Buchstand =>
        Einstand is > 0 && Kurs is > 0 ? Math.Round(Kapital * (Kurs.Value / Einstand.Value), 2) : null;
}

/// <summary>
/// Ein Tauschvorschlag für eine gehaltene Position: Die Kurve, die man hält,
/// ist von einer anderen überholt worden.
/// </summary>
/// <param name="PaargewinnSeither">
/// Was der Tausch seit der Kreuzung eingebracht HÄTTE — Rendite des Ziels minus
/// Rendite des Bestands. Vergangenheit, keine Vorhersage.
/// </param>
/// <param name="WaereGeworden">
/// Das eingesetzte Kapital, fortgeschrieben mit <paramref name="PaargewinnSeither"/>.
/// Die Zahl beantwortet „was wäre gewesen", nicht „was wird sein". Sie steht
/// hier, weil ein Prozentsatz allein bei kleinen Beträgen zu Fehlschlüssen
/// verleitet: 40 Prozent auf 200 Euro sind achtzig Euro.
/// </param>
public sealed record SwapVorschlag(
    string SymbolBestand, string SymbolZiel, string NameZiel, AssetClass KlasseZiel,
    DateTime Kreuzung, int TageSeither,
    double RenditeBestand, double RenditeZiel, double PaargewinnSeither,
    decimal Kapital, decimal WaereGeworden,
    int HistorischeKreuzungen, double? HistorischeTrefferquote, double? HistorischerMittelgewinn,
    double Rundlaufkosten, bool Bewaehrt)
{
    /// <summary>
    /// Der mittlere Ertrag früherer Kreuzungen abzüglich der Kosten eines
    /// Tauschs. Ist er negativ, kostet dem Signal zu folgen im Mittel Geld —
    /// auch bei hoher Trefferquote.
    /// </summary>
    public double? MittelgewinnNachKosten =>
        HistorischerMittelgewinn is { } g ? g - Rundlaufkosten : null;

    /// <summary>Was nach Gebühren und Schlupf übrig bliebe.</summary>
    public decimal WaereGewordenNachKosten =>
        Math.Round(Kapital * (decimal)(1 + PaargewinnSeither - Rundlaufkosten), 2);
}

/// <summary>Alle Tauschvorschläge zu einer Bestandsposition.</summary>
public sealed record SwapFuerPosition(
    string Symbol, string Name, AssetClass Klasse, decimal Kapital, string Waehrung,
    IReadOnlyList<SwapVorschlag> Vorschlaege, string Lage);

/// <summary>Das Ergebnis der ganzen Seite.</summary>
public sealed record SwapUebersicht(
    IReadOnlyList<SwapFuerPosition> Positionen,
    decimal KapitalGesamt,
    int VorschlaegeGesamt,
    int DavonBewaehrt,
    double Rundlaufkosten,
    string Kostenhinweis,
    string Intervall,
    int Tage,
    int Haltedauer,
    DateTime StandUtc,
    string Hinweis);
