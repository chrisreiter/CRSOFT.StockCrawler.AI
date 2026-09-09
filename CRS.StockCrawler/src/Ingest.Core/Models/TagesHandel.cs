using Ingest.Core.Enums;

namespace Ingest.Core.Models;

/// <summary>
/// Wie beweglich eine Anlageklasse in einer Stunde ist — und was davon nach
/// den Kosten eines Rundlaufs übrig bleibt.
/// </summary>
/// <param name="AnteilUeberKosten">
/// Der Anteil der Stundenbars, deren Betragsbewegung den Rundlauf übersteigt.
/// <b>Das ist die Zahl, die über Day Trading entscheidet.</b> Liegt sie bei
/// einem Drittel, dann sind zwei von drei Stunden von vornherein verloren —
/// nicht, weil man die Richtung verfehlt, sondern weil selbst die richtige
/// Richtung die Gebühren nicht einspielt.
/// </param>
public sealed record Beweglichkeit(
    AssetClass Klasse, string KlasseName,
    int Bars, int Werte,
    double MittlereBewegung, double MedianBewegung,
    double AnteilUeberKosten, double AnteilUeberDoppeltenKosten);

/// <summary>Was zu dieser Stunde gehandelt wird.</summary>
public sealed record Handelsfenster(
    AssetClass Klasse, string KlasseName,
    DateTime? JuengsteBar, int StundenAlt, bool VermutlichOffen,
    string Bemerkung);

/// <summary>Ein Wert, der sich in den letzten Stunden bewegt hat.</summary>
public sealed record Stundenbewegung(
    string Symbol, string? Name, AssetClass Klasse,
    decimal Kurs, DateTime TsUtc,
    double VeraenderungStunde, double VeraenderungSechsStunden,
    double Spannweite);

/// <summary>
/// Was die Messung zum kürzesten Horizont hergibt — die einzige Zahl, die für
/// Day Trading überhaupt einschlägig ist.
/// </summary>
public sealed record IntradayGuete(
    string Quelle, int HorizontStunden,
    double? Fehlerverhaeltnis, double? Trefferquote,
    double? DriftFehlerverhaeltnis, double? DriftTrefferquote,
    bool Traegt, string Urteil);

/// <summary>Ein Beleg aus der Fachliteratur.</summary>
public sealed record Literaturstelle(
    string Titel, string Herkunft, string Auszug, string? Fundstelle, double Score);

/// <summary>Die ganze Day-Trading-Seite.</summary>
public sealed record TagesHandel(
    DateTime StandUtc,
    double Rundlaufkosten,
    string Kostenhinweis,
    string Kernaussage,
    IReadOnlyList<Handelsfenster> Fenster,
    IReadOnlyList<Beweglichkeit> Beweglichkeit,
    IReadOnlyList<Stundenbewegung> Bewegungen,
    IReadOnlyList<IntradayGuete> Guete,
    IReadOnlyList<string> Anweisungen,
    IReadOnlyList<Literaturstelle> Literatur);
