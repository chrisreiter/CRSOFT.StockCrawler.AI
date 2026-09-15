namespace Ingest.Core.Models;

/// <summary>Ein abgeschlossener Lauf über alle verfolgten Werte.</summary>
public sealed record GrundschwingungLauf(
    int RunId, string Interval, DateTime GestartetUtc, DateTime? BeendetUtc,
    int Werte, int Epochen, int Muster, int Akkorde, int Klassen,
    int Paare, int PaareBestaendig, double? DauerSekunden, string? Note);

/// <summary>Eine Klasse des Katalogs, wie sie die Oberfläche zeigt.</summary>
/// <param name="Skizze">Die Wellenform über zwei Grundperioden, auf ±1 normiert.</param>
public sealed record GrundschwingungKlasse(
    int ClassId, int Nr, int Stimmen,
    double[] Perioden, double[] Amplituden, string Harmonik,
    int Akkorde, int Werte, int Epochen, double Prominenz, double Stabilitaet,
    int Paare, int PaareBestaendig, double[] Skizze);

/// <summary>Ein Wert innerhalb einer Klasse: wie oft und zuletzt wann.</summary>
public sealed record GrundschwingungMitglied(
    int AssetId, string Symbol, string? Name, int Epochen,
    DateTime LetzteEpocheUtc, double LetztePhaseDeg, double Prominenz, bool Aktuell);

/// <summary>Zwei Werte, die eine Klasse in mehreren Epochen teilen.</summary>
public sealed record GrundschwingungPaar(
    int AssetA, string SymbolA, int AssetB, string SymbolB,
    int Epochen, double VersatzBars, double VersatzStreuung, bool Bestaendig);

public sealed record GrundschwingungKlasseDetail(
    GrundschwingungKlasse Klasse,
    IReadOnlyList<GrundschwingungMitglied> Mitglieder,
    IReadOnlyList<GrundschwingungPaar> Paare,
    string Hinweis);

public sealed record GrundschwingungUebersicht(
    GrundschwingungLauf? Lauf,
    IReadOnlyList<GrundschwingungKlasse> Klassen,
    string Hinweis);

/// <summary>Was ein einzelner Wert an Grundschwingungen trägt — für Reasoning und Prognose.</summary>
public sealed record GrundschwingungWert(
    int AssetId, string Symbol,
    DateTime? LetzteEpocheUtc,
    double[] Perioden, double[] Amplituden, double[] Phasen, string? Harmonik,
    GrundschwingungKlasse? Klasse,
    IReadOnlyList<GrundschwingungPaar> Partner,
    double? Rueckhalt, double? RenditeNach5, double? RenditeNach20, double? RenditeNach40);
