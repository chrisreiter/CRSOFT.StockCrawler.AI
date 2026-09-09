using Ingest.Core.Enums;

namespace Ingest.Core.Models;

/// <summary>
/// Eine einzelne Buchung im virtuellen Depot: an diesem Tag, zu diesem Kurs,
/// dieser Betrag. Positiv heisst einsetzen, negativ entnehmen.
/// </summary>
public sealed class InvestBuchung
{
    public int BuchungId { get; set; }

    /// <summary>manuell | streng | aktiv | halten</summary>
    public string Depot { get; set; } = "manuell";

    public int AssetId { get; set; }
    public string Symbol { get; set; } = "";
    public DateTime AmUtc { get; set; }

    /// <summary>Der Kurs, zu dem gebucht wurde.</summary>
    public decimal Kurs { get; set; }

    /// <summary>
    /// Der Zeitstempel der Bar, aus der <see cref="Kurs"/> stammt. Am Montag
    /// früh ist der jüngste Aktienkurs der vom Freitag; ohne diese Spalte
    /// hielte man ihn für tagesaktuell.
    /// </summary>
    public DateTime? KursUtc { get; set; }

    public decimal Betrag { get; set; }
    public decimal Anteile { get; set; }

    /// <summary>
    /// Was dieser Vorgang an Gebühr gekostet hat — der Betrag, nicht der Satz.
    /// Der Satz ändert sich; die bezahlte Gebühr nicht. Stünde nur der Satz in
    /// den Einstellungen, verschöbe eine spätere Änderung rückwirkend jede
    /// bereits bezahlte Gebühr.
    /// </summary>
    public decimal Gebuehr { get; set; }

    public string Waehrung { get; set; } = "EUR";
    public string? Notiz { get; set; }
}

/// <summary>Eine Bewegung auf dem Verrechnungskonto.</summary>
/// <param name="Grund">
/// <c>einzahlung</c>/<c>auszahlung</c> kommen von aussen, <c>kauf</c>,
/// <c>verkauf</c> und <c>gebuehr</c> gehören zu einer Buchung. Die Trennung
/// ist der einzige Weg, „was habe ich eingezahlt" von „was habe ich
/// umgeschichtet" zu unterscheiden — und das Erste ist der Nenner, gegen den
/// der Gewinn zu halten ist.
/// </param>
public sealed class InvestKontobewegung
{
    public int BewegungId { get; set; }
    public string Depot { get; set; } = "manuell";
    public string Waehrung { get; set; } = "EUR";
    public DateTime AmUtc { get; set; }
    public decimal Betrag { get; set; }
    public string Grund { get; set; } = "";
    public int? BuchungId { get; set; }
    public string? Notiz { get; set; }
    public string? Symbol { get; set; }
}

/// <summary>
/// Was aus allen Buchungen eines Wertes geworden ist.
///
/// <para><b>Zwei Nenner, und der Unterschied ist wichtig.</b>
/// <see cref="Einsatz"/> ist netto — eingezahlt minus entnommen — und die Zahl,
/// gegen die der heutige Stand zu halten ist. <see cref="Eingezahlt"/> ist
/// brutto und der ehrliche Nenner für einen Prozentsatz: Wer 1.000 einsetzt,
/// 500 entnimmt und heute bei 600 steht, hat nicht 20 % verloren, sondern
/// 10 % gewonnen.</para>
/// </summary>
public sealed class InvestPosition
{
    public int AssetId { get; set; }
    public string Symbol { get; set; } = "";
    public string? Name { get; set; }
    public AssetClass Klasse { get; set; }
    public string Waehrung { get; set; } = "EUR";

    public decimal Anteile { get; set; }
    public decimal Eingezahlt { get; set; }
    public decimal Entnommen { get; set; }

    /// <summary>Was die Vorgänge dieses Wertes an Gebühr gekostet haben.</summary>
    public decimal Gebuehren { get; set; }

    public int Buchungen { get; set; }
    public DateTime? SeitUtc { get; set; }

    /// <summary>
    /// Letzter bekannter Kurs — aus Tages- ODER Stundenbars, je nachdem, welche
    /// jünger ist.
    /// </summary>
    public decimal? Kurs { get; set; }
    public DateTime? KursUtc { get; set; }

    /// <summary>
    /// Woher der Kurs stammt: <c>1h</c> oder <c>1d</c>. Steht in der Antwort,
    /// weil ein Wert ohne Stundendaten zwangsläufig auf dem Tagesschluss hängt
    /// — und das soll man sehen können, statt es zu vermuten.
    /// </summary>
    public string? KursQuelle { get; set; }

    /// <summary>Steht dieser Wert gerade in der Auswahl der Kursansicht?</summary>
    public bool Gewaehlt { get; set; }

    public decimal Einsatz => Eingezahlt - Entnommen;

    /// <summary>
    /// Anteile mal heutiger Kurs. <c>null</c>, solange kein Kurs vorliegt — und
    /// ebenso, solange gar nichts gebucht ist.
    ///
    /// <para>Die zweite Bedingung ist keine Formsache: Ein Wert ohne Buchung
    /// zeigte sonst „0,00" in Stand und Gewinn und läse sich wie eine Position,
    /// die alles verloren hat. Eine aufgelöste Position dagegen steht
    /// berechtigt bei null — dort sind die Anteile weg, aber der Ertrag der
    /// Rundreise steht noch im Gewinn.</para>
    /// </summary>
    public decimal? Stand =>
        Buchungen > 0 && Kurs is > 0 ? Math.Round(Anteile * Kurs.Value, 2) : null;

    public decimal? Gewinn => Stand is { } s ? s - Einsatz : null;

    /// <summary>
    /// Gewinn bezogen auf das jemals Eingezahlte. Das ist <b>keine</b>
    /// Zeitrendite: Wer nachlegt, verschiebt den Nenner, ohne dass sich die
    /// Leistung des Wertes geändert hätte. Als „was habe ich verdient" richtig,
    /// als Vergleichsmass zwischen zwei Positionen mit verschieden vielen
    /// Nachkäufen irreführend.
    /// </summary>
    public double? RenditePct =>
        Gewinn is { } g && Eingezahlt > 0 ? (double)(g / Eingezahlt) * 100 : null;

    /// <summary>
    /// Der noch investierte Betrag geteilt durch die gehaltenen Anteile — der
    /// Kurs, bei dem die Position auf null stünde.
    ///
    /// <para><b>Das ist nicht der Einstandskurs</b>, und die Spalte hiess
    /// zuerst so. Nach einer Entnahme fällt die Zahl, weil weniger Geld
    /// drinsteckt, nicht weil jemand billiger gekauft hätte: Bei NVDA sprang
    /// sie von 200,85 auf 191,34, nachdem 3.391 entnommen waren — zu 191,34
    /// hat nie ein Kauf stattgefunden. Ein Einstandskurs ändert sich beim
    /// Verkauf gar nicht; diese Zahl schon, und sie soll es auch, denn sie
    /// beantwortet „ab welchem Kurs bin ich im Plus".</para>
    ///
    /// <c>null</c> bei leerem oder rechnerisch negativem Bestand.
    /// </summary>
    public decimal? EinsatzJeAnteil =>
        Anteile > 0 && Einsatz > 0 ? Math.Round(Einsatz / Anteile, 8) : null;
}

/// <summary>
/// Alle Positionen einer Währung samt ihrer Summen.
///
/// Getrennt je Währung, weil dieses System keine Devisenreihen führt: Der
/// Kursanstieg eines Wertes vermehrt den Einsatz in jeder Zähleinheit
/// gleichermassen, die Bewegung des Wechselkurses fehlt. Eine Summe über EUR-
/// und USD-Positionen hinweg wäre eine Zahl, die niemand nachrechnen kann.
/// </summary>
/// <param name="Kontostand">Was gerade nicht im Kurs steckt.</param>
/// <param name="Eingezahlt">Was von aussen auf das Konto kam.</param>
/// <param name="Ausgezahlt">Was wieder abgehoben wurde.</param>
/// <param name="Gebuehren">Summe aller bezahlten Gebühren.</param>
/// <param name="Vermoegen">Kontostand plus Kurswert aller Positionen.</param>
/// <param name="Gewinn">
/// <paramref name="Vermoegen"/> minus dem netto Eingezahlten. Das ist der
/// eigentliche Ertrag: Gebühren sind darin enthalten, weil sie das Konto
/// bereits verlassen haben. Nur diese Zahl darf „Gewinn" heissen — der
/// Kursgewinn einer einzelnen Position sagt nichts über das Ganze, solange
/// nicht feststeht, was das Hin und Her gekostet hat.
/// </param>
public sealed record InvestWaehrungsblock(
    string Waehrung,
    int Positionen,
    decimal Kontostand,
    decimal Eingezahlt,
    decimal Ausgezahlt,
    decimal Gebuehren,
    decimal GebuehrPct,
    decimal Stand,
    decimal Vermoegen,
    decimal Gewinn,
    double? RenditePct);

/// <summary>
/// Ein Zeitpunkt im Verlauf des virtuellen Vermögens.
///
/// <para><b>Nicht mehr ein Tag.</b> Die Kurve lief zuerst auf Tagesschlüssen —
/// und ein Depot, das heute eröffnet wurde, hatte damit genau EINEN Punkt und
/// zeichnete keine Linie. Das widersprach dem, was daneben stand: Der Wert
/// bewegt sich mit jeder Stundenbar. Für die jüngsten Tage stehen deshalb
/// Stundenpunkte, davor Tagespunkte.</para>
/// </summary>
/// <param name="Positionen">Anteile zu diesem Zeitpunkt, mit dem letzten Kurs bewertet.</param>
/// <param name="Konto">Der Kontostand zu diesem Zeitpunkt.</param>
/// <param name="Vermoegen">
/// Beides zusammen. <b>Ohne den Kontostand wäre die Kurve falsch:</b> Nach
/// einem Verkauf steckt das Geld nicht mehr im Kurs, sondern im Konto — eine
/// Kurve, die nur die Positionen zeigt, stellte den Verkauf als Absturz dar.
/// </param>
/// <param name="Eingezahlt">Was bis zu diesem Tag netto von aussen kam.</param>
public sealed record InvestVerlaufPunkt(DateTime Zeit, decimal Positionen,
                                        decimal Konto, decimal Vermoegen,
                                        decimal Eingezahlt);

/// <summary>Der Verlauf einer Währung samt der Werte, die er umfasst.</summary>
public sealed record InvestVerlauf(
    string Waehrung,
    IReadOnlyList<InvestVerlaufPunkt> Punkte,
    IReadOnlyList<string> Werte,
    string Hinweis);

/// <summary>
/// Was die Übersicht zurückgibt.
///
/// <para><b>Eine flache Liste, Summen daneben.</b> <paramref name="Positionen"/>
/// enthält die gewählten Werte UND alle mit Buchungen — auch die ohne, denn in
/// eine Zeile, die es nicht gibt, kann man nichts eintragen. Die Summen stehen
/// getrennt in <paramref name="Waehrungen"/> und umfassen nur, was tatsächlich
/// gebucht ist.</para>
/// </summary>
public sealed record InvestUebersicht(
    IReadOnlyList<InvestPosition> Positionen,
    IReadOnlyList<InvestWaehrungsblock> Waehrungen,
    DateTime StandUtc,
    string Hinweis);

/// <summary>Das Ergebnis einer Anpassung — was tatsächlich gebucht wurde.</summary>
public sealed record InvestBuchungErgebnis(
    string Symbol, decimal Vorher, decimal Nachher, decimal Betrag,
    decimal Gebuehr, decimal Kontostand,
    decimal Kurs, DateTime? KursUtc, string Waehrung, string Meldung)
{
    /// <summary>Wurde tatsächlich gebucht, oder war es nur ein Hinweis?</summary>
    public bool Gebucht => Betrag != 0;
}

/// <summary>
/// Der Wertverlauf eines einzelnen Depots, in eine gemeinsame Zähleinheit
/// gebracht.
/// </summary>
/// <param name="Indexiert">
/// Derselbe Verlauf, auf 100 am ersten Tag bezogen.
///
/// <para>Beides mitzuliefern statt nur der Beträge: Depots mit verschiedenem
/// Budget lassen sich in Euro nicht vergleichen — ein Depot mit 20.000 sieht
/// immer besser aus als eines mit 1.000, auch wenn es schlechter läuft. Die
/// Beträge beantworten „wieviel habe ich", die Indexierung „was war die bessere
/// Entscheidung". Das sind zwei Fragen, und die Anzeige lässt zwischen ihnen
/// umschalten.</para>
/// </param>
public sealed record InvestDepotreihe(
    string Depot, bool Zaehlt, string Waehrung,
    IReadOnlyList<DateTime> Zeiten,
    IReadOnlyList<decimal> Werte,
    IReadOnlyList<double> Indexiert);

/// <summary>
/// Eine Zeile im Journal eines Depots.
/// </summary>
/// <param name="Kasse">
/// Was der Vorgang mit dem Konto gemacht hat — negativ beim Kauf, positiv beim
/// Verkauf, in beiden Fällen einschliesslich der Gebühr. Die Spalte steht neben
/// <paramref name="Betrag"/>, weil die beiden verschiedene Dinge sind: Der
/// Betrag ist das, was in den Kurs ging; die Kasse ist das, was das Konto
/// gekostet hat. Ihr Unterschied IST die Gebühr.
/// </param>
/// <param name="KontostandDanach">
/// Der Kontostand nach diesem Vorgang. Ohne ihn ist ein Journal eine Liste von
/// Ereignissen; mit ihm ist es eine Rechnung, die man nachrechnen kann.
/// </param>
public sealed record InvestJournalzeile(
    DateTime AmUtc, string Depot, string Art, string? Symbol, string? Name,
    decimal? Kurs, DateTime? KursUtc, decimal? Anteile,
    decimal Betrag, decimal Gebuehr, decimal Kasse, decimal KontostandDanach,
    string Waehrung, string? Notiz);

/// <summary>
/// Alle Vorgänge eines Depots, jüngste zuerst.
///
/// <para><c>Depot</c> darf auch <c>autopilot</c> sein: dann stehen die
/// Vorgänge aller drei Strategien in einer Liste, jede Zeile mit ihrer
/// Strategie. Der Nutzer sieht den Autopiloten als EINE Sache — drei Fenster
/// nacheinander zu öffnen, um seine Geschäfte zu überblicken, wäre eine
/// Trennung, die nur im Datenmodell existiert.</para>
///
/// <para>Der laufende Kontostand wird dabei <b>je Strategie</b> geführt, nicht
/// über alle zusammen. Ein gemeinsamer Stand über drei Gegenrechnungen wäre
/// dieselbe Doppelzählung wie beim Gesamtvermögen.</para>
/// </summary>
public sealed record InvestJournal(
    string Depot,
    IReadOnlyList<InvestJournalzeile> Zeilen,
    int Geschaefte, int Kaeufe, int Verkaeufe,
    IReadOnlyList<InvestJournalsumme> Summen,
    bool Vollstaendig,
    string Hinweis);

/// <summary>
/// Die Summen EINER Strategie.
///
/// <para>Getrennt je Strategie und nicht als eine Zahl: Ein gemeinsamer
/// Kontostand über <c>streng</c>, <c>aktiv</c> und <c>halten</c> wäre dieselbe
/// Doppelzählung wie beim Gesamtvermögen — drei Gegenrechnungen auf dasselbe
/// Geld, addiert. Die erste Fassung meldete „Konto 1.000,08“, wo dreimal
/// 1.000 eingesetzt und zweimal fast alles investiert war.</para>
/// </summary>
public sealed record InvestJournalsumme(
    string Depot, int Geschaefte, decimal Eingezahlt, decimal Ausgezahlt,
    decimal Gebuehren, decimal Kontostand, string Waehrung);

/// <summary>Ein Depot in der Aufstellung.</summary>
/// <param name="Zaehlt">
/// Ob dieses Depot in die Summe eingeht.
///
/// <para><c>false</c> bei den Vergleichsläufen des Autopiloten: <c>streng</c>,
/// <c>aktiv</c> und <c>halten</c> sind drei Antworten auf dieselbe Frage — was
/// aus denselben 1.000 geworden wäre, wenn man so oder so vorgegangen wäre.
/// Sie zu addieren zählte dasselbe Geld dreimal. Sie stehen deshalb weiter in
/// der Aufstellung, aber nur eine geht in die Summe ein.</para>
/// </param>
public sealed record InvestGesamtDepot(
    string Depot, decimal Vermoegen, decimal Eingezahlt, decimal Gewinn,
    double? RenditePct, int Werte, bool Zaehlt);

/// <summary>
/// Alles zusammen — jedes Depot, beide Währungen, eine Zahl und eine Kurve.
/// </summary>
/// <param name="Kurs">
/// Der verwendete EURUSD-Kurs. Er steht in der Antwort, weil eine umgerechnete
/// Zahl ohne den Kurs, mit dem sie entstand, nicht nachprüfbar ist.
/// </param>
public sealed record InvestGesamt(
    string Waehrung,
    decimal Vermoegen,
    decimal Eingezahlt,
    decimal Gewinn,
    double? RenditePct,
    decimal? Kurs,
    DateTime? KursUtc,
    IReadOnlyList<InvestGesamtDepot> Depots,
    IReadOnlyList<InvestVerlaufPunkt> Punkte,
    string Hinweis);

/// <summary>Das Verrechnungskonto einer Währung.</summary>
public sealed record InvestKonto(
    string Waehrung, decimal Stand, decimal GebuehrPct,
    decimal Eingezahlt, decimal Ausgezahlt, decimal Gebuehren,
    int Bewegungen, DateTime? SeitUtc);
