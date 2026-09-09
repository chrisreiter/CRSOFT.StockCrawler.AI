using Ingest.Core.Enums;

namespace Ingest.Core.Models;

/// <summary>
/// Ein Paar, dessen relative Stärke gekippt ist: Die eine Kurve hat die andere
/// überholt. Wer der Kreuzung folgt, hält die obere Seite und trennt sich von
/// der unteren.
/// </summary>
/// <param name="Paargewinn">
/// Was das Paar seit der Kreuzung eingebracht hätte: Rendite der oberen Seite
/// minus Rendite der unteren. Nur so ist die Zahl vom allgemeinen Marktgang
/// unabhängig — in einer Woche, in der alles um fünf Prozent steigt, hat ein
/// Paar nichts geleistet, wenn beide Seiten fünf Prozent gestiegen sind.
/// </param>
/// <param name="HistorischeTrefferquote">
/// Anteil der FRÜHEREN Kreuzungen dieses Paares, nach denen die obere Seite
/// über die Haltedauer tatsächlich vorne blieb. Ohne diese Zahl wäre die
/// Rangliste eine Behauptung: Sie zeigt sonst nur, was schon gelaufen ist.
/// <c>null</c>, wenn es zu wenige frühere Kreuzungen gibt.
/// </param>
public sealed record CrossingOpportunity(
    string SymbolKaufen, string NameKaufen, AssetClass KlasseKaufen,
    string SymbolVerkaufen, string NameVerkaufen, AssetClass KlasseVerkaufen,
    DateTime Kreuzung, int TageSeither,
    double RenditeKaufen, double RenditeVerkaufen, double Paargewinn,
    int HistorischeKreuzungen, double? HistorischeTrefferquote, double? HistorischerMittelgewinn,
    double Rundlaufkosten)
{
    /// <summary>
    /// Der mittlere Ertrag früherer Kreuzungen, abzüglich dessen, was der
    /// Tausch kostet. Diese Zahl entscheidet — nicht die Bruttozahl daneben.
    /// </summary>
    public double? MittelgewinnNachKosten =>
        HistorischerMittelgewinn is { } g ? g - Rundlaufkosten : null;

    /// <summary>Der erzielte Paargewinn nach einem Rundlauf.</summary>
    public double PaargewinnNachKosten => Paargewinn - Rundlaufkosten;

    /// <summary>
    /// Aktie gegen Krypto — der Fall, für den die Übersicht gedacht ist. Eine
    /// Kreuzung zwischen zwei Werten derselben Klasse sagt wenig über eine
    /// Umschichtung aus; zwischen den Klassen schon.
    /// </summary>
    public bool Klassenwechsel =>
        IstKrypto(KlasseKaufen) != IstKrypto(KlasseVerkaufen);

    /// <summary>
    /// Hat sich das Paar früher bewährt? Verlangt wird beides: genug frühere
    /// Kreuzungen, um überhaupt etwas sagen zu können, und eine Trefferquote
    /// deutlich über dem Münzwurf.
    ///
    /// Die Schwelle ist bewusst 0,55 und nicht 0,50. Bei zwölf Beobachtungen
    /// liegt schon reiner Zufall in einem Drittel der Fälle über der Hälfte;
    /// wer bei 0,50 abschneidet, nennt Zufall eine Bewährung.
    /// </summary>
    /// <para>Seit der Kostenrechnung gilt eine dritte Bedingung: Der mittlere
    /// Ertrag muss den Rundlauf übersteigen. Ohne sie gilt ein Paar mit 60
    /// Prozent Trefferquote und 0,1 Prozent mittlerem Ertrag als bewährt —
    /// und kostet bei 0,3 Prozent Rundlauf im Mittel Geld. Die Trefferquote
    /// allein ist blind gegen eine schiefe Verteilung.</para>
    public bool Bewaehrt =>
        HistorischeKreuzungen >= MindestKreuzungen
        && HistorischeTrefferquote >= 0.55
        && MittelgewinnNachKosten > 0;

    public const int MindestKreuzungen = 8;

    private static bool IstKrypto(AssetClass k) => k == AssetClass.Crypto;
}

/// <summary>
/// Ein Paar, das die Übersicht bewusst NICHT bewertet — samt Grund. Stilles
/// Weglassen wäre hier besonders schädlich: Ein Kurssprung durch einen
/// Aktiensplit erzeugt einen Scheingewinn von fünfzig Prozent und stünde damit
/// zwangsläufig ganz oben in einer Rangliste nach Gewinn.
/// </summary>
public sealed record CrossingSkipped(string SymbolA, string SymbolB, string Grund);

/// <summary>Die fertige Rangliste samt allem, was zu ihrer Einordnung nötig ist.</summary>
public sealed record CrossingOverview(
    IReadOnlyList<CrossingOpportunity> Zeilen,
    IReadOnlyList<CrossingSkipped> Ausgelassen,
    int PaareGeprueft,
    int VerdraengtDurchGrenze,
    double Rundlaufkosten,
    string Kostenhinweis,
    string Intervall,
    int Tage,
    int Haltedauer,
    DateTime StandUtc,
    string Hinweis);
