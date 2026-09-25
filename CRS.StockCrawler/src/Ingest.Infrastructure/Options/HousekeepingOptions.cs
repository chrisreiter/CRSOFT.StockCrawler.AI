namespace Ingest.Infrastructure.Options;

/// <summary>
/// Die Aufbewahrungsfristen des Hausmeisters, in Tagen. <b>0 schaltet eine
/// Regel ab</b> — sie läuft dann nicht und erscheint im Protokoll als
/// „abgeschaltet“, nicht als „nichts gefunden“.
///
/// <para><b>Wie die Fristen zustande kommen.</b> Jede ist aus dem gemessenen
/// Bestand vom 25.09.2026 abgeleitet und in
/// <c>docs/HAUSMEISTER.md</c> einzeln begründet. Die Regel dahinter ist
/// überall dieselbe: Eine Zeile darf weg, wenn <i>keine</i> Ansicht und
/// <i>keine</i> Messung sie mehr liest — nicht, wenn sie bloß alt ist.</para>
///
/// <para><b>Was keine Einstellung anfassen kann:</b> Kurse, Werte, Depot,
/// Benutzer, Säulengewichte, Modellgewichte und jede Prognose, deren
/// Zielzeitpunkt noch aussteht. Diese Tabellen kommen in keiner Regel vor.</para>
/// </summary>
public sealed class HousekeepingOptions
{
    /// <summary>Läuft der Hausmeister im Tageslauf mit?</summary>
    public bool Aktiv { get; set; } = true;

    /// <summary>
    /// Läuft er dort als <b>Probelauf</b> (zählt nur) oder löscht er wirklich?
    /// Voreinstellung: löschen — ein Hausmeister, der nur zusieht, räumt nicht
    /// auf. Der Knopf in der Oberfläche macht dagegen zuerst einen Probelauf.
    /// </summary>
    public bool ImTageslaufLoeschen { get; set; } = true;

    /// <summary>
    /// Zeitbudget je Lauf. Läuft es ab, bricht der Hausmeister nach der
    /// laufenden Regel ab und merkt sich das im Protokoll; der nächste Tag
    /// macht weiter. Besser ein halb aufgeräumter Bestand als ein Tageslauf,
    /// der eine Stunde blockiert.
    /// </summary>
    public int BudgetMinuten { get; set; } = 20;

    /// <summary>
    /// Wie viele Zeilen je Schritt gelöscht werden. Ein einziges DELETE über
    /// sechs Millionen Zeilen sperrt die Tabelle und bläht das Protokoll;
    /// gemessen sind Blöcke dieser Größe der beste Kompromiss.
    /// </summary>
    public int Blockgroesse { get; set; } = 25_000;

    // ------------------------------------------------------------ Fristen --

    /// <summary>Überholte Kurvendiskussions-Läufe (Ereignisse und Verknüpfungen).
    /// Der jüngste Lauf je Intervall und Glättungsart bleibt IMMER.</summary>
    public int KurvenLaeufeTage { get; set; } = 14;

    /// <summary>Teilmodell-Anteile einer Prognose (<c>forecast_component</c>).</summary>
    public int PrognoseKomponentenTage { get; set; } = 30;

    /// <summary>Dauerhaft unbewertbare Prognosen, gerechnet ab Zielzeitpunkt.</summary>
    public int UnbewertbareTage { get; set; } = 30;

    /// <summary>Bewertete Prognosen samt ihren Bewertungen.</summary>
    public int BewertetePrognosenTage { get; set; } = 365;

    /// <summary>Die Rückrechnung (<c>forecast_track</c>).</summary>
    public int RueckrechnungTage { get; set; } = 1825;

    /// <summary>Kreuzungen (<c>crossing</c>), gerechnet ab dem Zeitpunkt der Kreuzung.</summary>
    public int KreuzungenTage { get; set; } = 730;

    /// <summary>Nachrichtenartikel samt Abschnitten und Vektoren.</summary>
    public int NachrichtenTage { get; set; } = 365;

    /// <summary>Das Laufprotokoll (<c>ingest_run</c>).</summary>
    public int LaufprotokollTage { get; set; } = 90;

    /// <summary>Antworten des Agenten — <b>gemerkte bleiben</b>.</summary>
    public int ReasoningProtokollTage { get; set; } = 90;

    /// <summary>Autopilot-Läufe und ihre Entscheidungen.</summary>
    public int AutopilotProtokollTage { get; set; } = 365;

    /// <summary>Das eigene Protokoll des Hausmeisters.</summary>
    public int EigenesProtokollTage { get; set; } = 365;
}
