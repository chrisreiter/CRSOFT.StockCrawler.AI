namespace Ingest.Core.Models;

/// <summary>
/// Ein Bestandteil der Bewertung eines Wertes.
/// </summary>
/// <param name="Rendite">Was dieser Bestandteil an Rendite erwartet.</param>
/// <param name="Verdienst">
/// Was er nachweislich taugt, zwischen 0 und 1 — aus einer Messung, nie aus
/// einer Einstellung. Dieselbe Regel wie bei der Säulenmischung: Der Regler
/// bestimmt, wie viel von etwas Brauchbarem einfliesst; ob etwas brauchbar ist,
/// entscheidet die Messung.
/// </param>
public sealed record AutopilotBeitrag(string Name, double Rendite, double Verdienst,
                                      double Anteil, string Warum);

/// <summary>
/// Ein Wert, wie der Autopilot ihn sieht.
/// </summary>
/// <param name="TrefferquoteRoh">
/// Die rohe gemessene Richtungstrefferquote dieses Wertes — <b>nur aus
/// Live-Prognosen</b>. Rückdatierte Prognosen aus dem Aufrollen der
/// Vergangenheit (<c>model_version</c> endet auf <c>-bt</c>) bleiben draussen:
/// Sie wurden über bereits bekannte Kurse gerechnet und belegen nichts. Mit
/// ihnen zusammen lag die mittlere Trefferquote bei 0,571, ohne sie bei 0,474.
/// </param>
/// <param name="Trefferquote">
/// Dieselbe Quote, <b>zum Bestandsmittel hin geschrumpft</b>.
///
/// <para>Der Grund ist gemessen: Bei rund vier bewerteten Live-Prognosen je
/// Wert liegt die Streuung der Quoten über die Werte bei 0,285, während reines
/// Würfeln schon 0,250 erzeugte. Die Unterschiede zwischen den Werten sind
/// also weit überwiegend Rauschen. Wer aus 571 Werten den mit der höchsten
/// Quote auswählt, wählt den glücklichsten — nicht den besten. Der
/// Schrumpfungsfaktor wird aus der Zerlegung der Streuung selbst bestimmt und
/// nicht geraten; er verschwindet von allein, sobald die Fallzahl wächst.</para>
/// </param>
/// <param name="Bewegung">Mittlerer Betrag der Tagesrendite über 90 Tage.</param>
/// <param name="Verdienst">
/// Was die Bewertung dieses Wertes nachweislich taugt, zwischen 0 und 1.
/// Derzeit über den ganzen Bestand <b>null</b>: Die Trefferquote der
/// Live-Prognosen liegt unter dem Nullpunkt von 0,523.
/// </param>
/// <param name="Grundlage">
/// Worauf die Punktzahl beruht — auf gemessenem Verdienst oder, solange es
/// keinen gibt, auf der blossen Erwartung des Modells. Der Unterschied gehört
/// in die Anzeige und nicht in eine Fussnote.
/// </param>
/// <param name="Erwartungswert">
/// <c>(2p − 1)·E|r| − Rundlauf</c>. Die einzige Zahl, die sagt, ob ein Geschäft
/// sich lohnen KANN. Über den gesamten Bestand ist sie derzeit fast überall
/// negativ, und das ist ein Befund und kein Fehler.
/// </param>
public sealed record AutopilotAnwaerter(
    int AssetId, string Symbol, string? Name, string Klasse,
    decimal? Kurs, DateTime? KursUtc,
    double Punktzahl,
    IReadOnlyList<AutopilotBeitrag> Beitraege,
    double? TrefferquoteRoh,
    double? Trefferquote, int Bewertet,
    double Bewegung,
    double? Erwartungswert,
    double NoetigeTrefferquote,
    double Verdienst,
    string Grundlage,
    decimal Gehalten,
    string Lage);

/// <summary>
/// Die Rangfolge samt der Kennzahlen, aus denen sie entsteht.
///
/// <para><b>Die Zahlen werden gemessen, nicht aufgeschrieben.</b> Ein
/// Hinweistext mit fest eingetippten Werten steht irgendwann neben einer
/// Tabelle, die etwas anderes zeigt — und wer beide liest, glaubt keiner
/// mehr.</para>
/// </summary>
/// <param name="Bestandsmittel">
/// Die Richtungstrefferquote über den ganzen verfolgten Bestand, nur aus
/// Live-Prognosen. Der Wert, zu dem geschrumpft wird.
/// </param>
/// <param name="Streuung">
/// Die beobachtete Streuung der Quoten über die Werte.
/// </param>
/// <param name="StreuungZufall">
/// Wieviel Streuung allein aus der endlichen Fallzahl folgt. Liegt sie nahe an
/// <paramref name="Streuung"/>, sind die Unterschiede zwischen den Werten
/// Rauschen — und eine Auswahl nach der höchsten Quote wählt den
/// glücklichsten, nicht den besten.
/// </param>
public sealed record AutopilotRangfolge(
    IReadOnlyList<AutopilotAnwaerter> Anwaerter,
    int Geprueft, int Messbar, int Lohnend,
    double Bestandsmittel, int Faelle,
    double Streuung, double StreuungZufall,
    double NoetigeTrefferquote, double MittlereBewegung,
    string Hinweis);

/// <summary>Einstellungen einer Strategie.</summary>
/// <param name="Takt">
/// 1T, 1W, 1M, 3M, 6M oder 1J. Bewertet und protokolliert wird trotzdem jeden
/// Tag; der Takt bestimmt allein, wann gehandelt werden darf.
/// </param>
/// <param name="Hysterese">
/// Wieviel Vorsprung ein Anwärter braucht, um einen gehaltenen Wert zu
/// verdrängen. Ohne sie tauscht ein Rangwechsel um einen Platz täglich hin und
/// her und zahlt jedes Mal den Rundlauf.
/// </param>
/// <param name="Zaehlt">
/// Ob diese Strategie ins Gesamtvermögen eingeht. Genau eine tut es.
///
/// <para>Die drei Strategien sind Gegenrechnungen auf dasselbe Geld, keine drei
/// Geldtöpfe. Sie alle zu summieren meldete 3.422,79 EUR, wo 2.000 USD richtig
/// waren — einmal das manuelle Depot und einmal der Autopilot.</para>
/// </param>
/// <param name="Startkapital">
/// Der Betrag, auf den ein Zurücksetzen zurückführt — ein SOLL-Wert.
///
/// <para>Bewusst nicht auf dem Konto abgelegt: Der Kontostand ist die Summe
/// aller Bewegungen und ändert sich mit jedem Kauf. Läge beides in derselben
/// Spalte, wüsste man nach dem ersten Geschäft nicht mehr, womit die Strategie
/// angetreten ist — und genau das ist die Zahl, gegen die ihr Ergebnis zu
/// halten ist.</para>
/// </param>
public sealed record AutopilotEinstellung(
    string Depot, bool Aktiv, int Werte, decimal MaxAnteil, decimal Hysterese,
    string Takt, string Waehrung, bool Nemotron, decimal Startkapital,
    bool Zaehlt, DateTime UpdatedUtc)
{
    /// <summary>Der Takt in Tagen.</summary>
    public int TaktTage => Takt switch
    {
        "1W" => 7,
        "1M" => 30,
        "3M" => 90,
        "6M" => 180,
        "1J" => 365,
        _ => 1
    };

    public string TaktName => Takt switch
    {
        "1W" => "wöchentlich",
        "1M" => "monatlich",
        "3M" => "alle drei Monate",
        "6M" => "halbjährlich",
        "1J" => "jährlich",
        _ => "täglich"
    };
}

/// <summary>Ein Beschluss über einen einzelnen Wert.</summary>
/// <param name="Urteil">
/// Nemotrons Votum. <c>null</c> heisst „kein Urteil eingeholt oder Modell nicht
/// erreichbar“ — ausdrücklich etwas anderes als Zustimmung.
/// </param>
public sealed record AutopilotEntscheidung(
    int EntscheidungId, int LaufId, string Depot, int AssetId, string Symbol,
    int Rang, double Punktzahl,
    double? Trefferquote, double? Bewegung, double? Erwartungswert,
    string Beschluss, string? Grund, decimal? Betrag,
    bool? Urteil, string? UrteilText, bool Ausgefuehrt, DateTime AmUtc);

/// <summary>Ein Lauf einer Strategie.</summary>
public sealed record AutopilotLauf(
    int LaufId, string Depot, DateTime GestartetUtc, DateTime? BeendetUtc,
    int Geprueft, int Geschaefte, decimal Gebuehren, decimal? Vermoegen,
    bool Handelstag, string? Notiz)
{
    public IReadOnlyList<AutopilotEntscheidung> Beschluesse { get; init; } = [];
}

/// <summary>
/// Wie die Strategien gegeneinander stehen.
///
/// <para>Die Grundlinie <c>halten</c> zahlt DIESELBEN Gebühren wie die anderen.
/// Eine kostenfreie Grundlinie wäre unschlagbar und damit als Vergleich
/// wertlos.</para>
/// </summary>
public sealed record AutopilotVergleichszeile(
    string Depot, string Was, decimal Eingezahlt, decimal Vermoegen, decimal Gewinn,
    double? RenditePct, decimal Gebuehren, int Geschaefte, int Positionen,
    double? VorsprungPct);

public sealed record AutopilotVergleich(
    IReadOnlyList<AutopilotVergleichszeile> Zeilen, DateTime StandUtc, string Hinweis);
