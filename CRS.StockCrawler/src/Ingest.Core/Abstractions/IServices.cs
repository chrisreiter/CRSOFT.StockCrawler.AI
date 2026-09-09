using Ingest.Core.Enums;

namespace Ingest.Core.Abstractions;

public sealed record UniverseResult(int Discovered, int Tracked, string Note);

public sealed record IngestResult(int Assets, int Ok, int Failed, int RowsWritten, TimeSpan Duration)
{
    public static IngestResult Empty => new(0, 0, 0, 0, TimeSpan.Zero);
}

public sealed record AnalysisResult(int Pairs, int Crossings, int AssetsAnalyzed, TimeSpan Duration);

public sealed record ForecastResult(int Assets, int Forecasts, TimeSpan Duration);

public sealed record ScoringResult(int Scored, int WeightsUpdated, double MeanAbsPctError, double HitRate);

/// <summary>Ermittelt, welche Instrumente es gibt, und markiert die größten N.</summary>
public interface IUniverseService
{
    Task<UniverseResult> RefreshAsync(AssetClass cls, int topN, bool autoTrack, CancellationToken ct = default);
    Task<UniverseResult> RefreshAllAsync(CancellationToken ct = default);
}

/// <summary>Holt Kursdaten und schreibt sie ins einheitliche Modell.</summary>
public interface IIngestService
{
    /// <summary>Historie über <paramref name="months"/> Monate für alle getrackten Assets.</summary>
    Task<IngestResult> BackfillAsync(int months, string intervalCode, CancellationToken ct = default);

    /// <summary>Nur das Delta seit der letzten gespeicherten Bar.</summary>
    Task<IngestResult> UpdateIncrementalAsync(string intervalCode, CancellationToken ct = default);

    Task<int> IngestOneAsync(int assetId, string intervalCode, DateTime fromUtc, DateTime toUtc,
                             CancellationToken ct = default);
}

/// <summary>Korrelationen, Vorlauf-Beziehungen und Kreuzungen.</summary>
public interface IAnalysisService
{
    Task<AnalysisResult> RecomputeAsync(string intervalCode, int windowBars, CancellationToken ct = default);
}

/// <summary>Erzeugt Prognosen für alle getrackten Assets.</summary>
public interface IForecastService
{
    Task<ForecastResult> RunAsync(CancellationToken ct = default);
    Task<ForecastResult> RunForAssetAsync(int assetId, CancellationToken ct = default);
}

/// <summary>
/// Wertet fällige Prognosen gegen die tatsächlichen Kurse aus und zieht die
/// Modellgewichte nach. Das ist die Rückkopplung, die die Prognose verbessert.
/// </summary>
public interface IScoringService
{
    Task<ScoringResult> ScoreDueAsync(CancellationToken ct = default);
}

/// <summary>
/// Stellt die jüngsten Kreuzungen als Rangliste dar: welches Paar seit dem
/// Kippen am meisten eingebracht hätte, welche Seite man dafür hält und welche
/// man abgibt.
/// </summary>
public interface ICrossingOpportunityService
{
    /// <param name="tage">Wie weit zurück eine Kreuzung liegen darf.</param>
    /// <param name="haltedauer">
    /// Über wie viele gemeinsame Bars die Bewährung früherer Kreuzungen
    /// gemessen wird. Nur gemeinsame Bars — Aktien und Krypto haben
    /// verschiedene Handelskalender, und über das rohe Zeitraster gerechnet
    /// misst man den Kalender statt den Markt.
    /// </param>
    /// <param name="maxJeSymbol">
    /// Wie oft derselbe Wert in der Rangliste vorkommen darf. Ohne diese
    /// Grenze wird die Liste von einem einzigen Absturz beherrscht: MNT-USD
    /// verlor in einem Monat 50 Prozent, und damit standen 24 der 25 besten
    /// Zeilen auf „irgendetwas gegen MNT-USD". Formal richtig, als Übersicht
    /// wertlos.
    /// </param>
    Task<Models.CrossingOverview> BuildAsync(
        string intervalCode, int tage, int haltedauer, int limit,
        bool nurKlassenwechsel, int maxJeSymbol = 3, CancellationToken ct = default);
}

/// <summary>
/// Bestand des Nutzers und die daraus folgenden Tauschvorschläge: Welche
/// gehaltene Position ist von einer anderen Kurve überholt worden, und was
/// hätte der Tausch seit dem Kippen eingebracht?
/// </summary>
public interface IPortfolioService
{
    Task<IReadOnlyList<Models.Holding>> ListAsync(CancellationToken ct = default);

    /// <summary>Legt an oder überschreibt — eine Zeile je Wert.</summary>
    Task<Models.Holding?> UpsertAsync(string symbol, decimal kapital, string waehrung,
                                      decimal? einstand, DateTime? gekauftUtc, string? notiz,
                                      CancellationToken ct = default);

    Task<bool> DeleteAsync(int assetId, CancellationToken ct = default);

    Task<Models.SwapUebersicht> SwapsAsync(string intervalCode, int tage, int haltedauer,
                                           int jeBestand, bool nurBewaehrt,
                                           CancellationToken ct = default);
}

/// <summary>
/// Das virtuelle Depot der Kursansicht: Was aus einem heute eingesetzten
/// Betrag geworden wäre.
///
/// <para>Getrennt von <see cref="IPortfolioService"/>, obwohl beide „Geld in
/// einem Wert" abbilden. Der Bestand beantwortet „womit stecke ich wo drin"
/// und hält je Wert eine überschreibbare Zeile. Hier ist die Frage „wie hat
/// sich das entwickelt, seit ich es eingesetzt habe" — und die lässt sich aus
/// einer überschreibbaren Zeile nicht mehr beantworten, sobald jemand den
/// Betrag einmal angepasst hat.</para>
/// </summary>
public interface IInvestService
{
    /// <param name="depot">manuell | streng | aktiv | halten.</param>
    /// <param name="assetIds">
    /// Die gerade gewählten Werte. Sie erscheinen auch ohne Position, damit man
    /// überhaupt etwas eintragen kann. Werte MIT Position kommen immer dazu,
    /// auch wenn sie nicht gewählt sind — eine Position, die aus der Übersicht
    /// verschwindet, weil jemand die Auswahl geändert hat, wäre verlorenes Geld.
    /// </param>
    Task<Models.InvestUebersicht> UebersichtAsync(string depot, IReadOnlyList<int>? assetIds,
                                                  CancellationToken ct = default);

    /// <summary>
    /// Setzt den Stand eines Wertes auf <paramref name="sollBetrag"/> und bucht
    /// die Differenz zum jüngsten Kurs. Ein Soll von null löst die Position auf.
    /// Der Betrag geht vom Verrechnungskonto ab, die Gebühr zusätzlich.
    /// </summary>
    Task<Models.InvestBuchungErgebnis?> SetzeAsync(string depot, string symbol,
                                                   decimal sollBetrag, string waehrung,
                                                   string? notiz,
                                                   CancellationToken ct = default);

    Task<IReadOnlyList<Models.InvestBuchung>> BuchungenAsync(string depot, int assetId,
                                                             CancellationToken ct = default);

    /// <summary>Der Verlauf des Vermögens je Tag — eine Reihe je Währung.</summary>
    Task<IReadOnlyList<Models.InvestVerlauf>> VerlaufAsync(string depot,
                                                           CancellationToken ct = default);

    /// <summary>
    /// Entfernt alle Buchungen eines Wertes samt ihrer Kassenspur. Die
    /// Simulation, nicht der Kurs.
    /// </summary>
    Task<int> LoescheAsync(string depot, int assetId, CancellationToken ct = default);

    /// <summary>Verrechnungskonten je Währung — Stand, Gebührensatz, Herkunft.</summary>
    Task<IReadOnlyList<Models.InvestKonto>> KontenAsync(string depot,
                                                        CancellationToken ct = default);

    /// <summary>
    /// Setzt Kontostand und/oder Gebührensatz. Beides ist einzeln übergebbar;
    /// <paramref name="sollStand"/> ist wie beim Positionsfeld das SOLL, gebucht
    /// wird die Differenz.
    /// </summary>
    Task<Models.InvestKonto?> KontoSetzeAsync(string depot, string waehrung,
                                               decimal? sollStand, decimal? gebuehrPct,
                                               CancellationToken ct = default);

    /// <summary>
    /// Jeder Vorgang eines Depots in einer Liste — Ein- und Auszahlungen wie
    /// Käufe und Verkäufe, mit dem Kontostand nach jedem Schritt.
    /// </summary>
    Task<Models.InvestJournal> JournalAsync(string depot, int grenze = 500,
                                             CancellationToken ct = default);

    /// <summary>Das Kassenjournal einer Währung.</summary>
    Task<IReadOnlyList<Models.InvestKontobewegung>> KontobewegungenAsync(
        string depot, string waehrung, int grenze = 200, CancellationToken ct = default);

    /// <summary>
    /// Alle Depots und beide Währungen zusammen, umgerechnet über EURUSD=X mit
    /// dem Kurs des jeweiligen Tages.
    /// </summary>
    Task<Models.InvestGesamt> GesamtAsync(string zielWaehrung, CancellationToken ct = default);

    /// <summary>
    /// Eine Kurve je Depot — nebeneinander, nicht summiert. Hier dürfen die
    /// Vergleichsläufe mit: Was in einer Summe eine Doppelzählung wäre, ist als
    /// eigene Linie die Aussage.
    /// </summary>
    Task<IReadOnlyList<Models.InvestDepotreihe>> DepotverlaufAsync(
        string zielWaehrung, CancellationToken ct = default);
}

/// <summary>
/// Der Autopilot: sucht selbst Werte aus, kauft und verkauft.
///
/// <para>Vier Depots teilen sich denselben Buchungsapparat wie das manuelle:
/// <c>streng</c> handelt nur bei positivem Erwartungswert, <c>aktiv</c> hält
/// immer die bestbewerteten N, <c>halten</c> ist die Grundlinie aus
/// Kaufen-und-Halten. Ohne die Grundlinie ist keine der beiden Strategien zu
/// beurteilen — eine Rendite ohne Massstab ist eine Zahl, kein Ergebnis.</para>
/// </summary>
public interface IAutopilotService
{
    /// <summary>
    /// Bewertet jeden verfolgten Wert, ohne zu handeln — dieselbe Rechnung, die
    /// der Lauf benutzt.
    /// </summary>
    Task<IReadOnlyList<Models.AutopilotAnwaerter>> RangfolgeAsync(
        string depot, int grenze = 40, CancellationToken ct = default);

    /// <summary>Dieselbe Rechnung samt der Kennzahlen, aus denen sie entsteht.</summary>
    Task<Models.AutopilotRangfolge> KennzahlenAsync(
        string depot, int grenze = 40, CancellationToken ct = default);

    Task<IReadOnlyList<Models.AutopilotEinstellung>> EinstellungenAsync(
        CancellationToken ct = default);

    Task<Models.AutopilotEinstellung?> EinstellungSetzeAsync(
        string depot, bool? aktiv, int? werte, decimal? maxAnteil, decimal? hysterese,
        string? takt, string? waehrung, bool? nemotron, decimal? startkapital,
        bool? zaehlt, CancellationToken ct = default);

    /// <summary>
    /// Stellt eine Strategie auf Anfang und legt das Startbudget aufs Konto.
    /// </summary>
    Task<Models.AutopilotEinstellung?> ZuruecksetzenAsync(string depot,
                                                          CancellationToken ct = default);

    /// <param name="erzwingen">Übergeht den eingestellten Takt.</param>
    Task<Models.AutopilotLauf> LaufeAsync(string depot, bool erzwingen = false,
                                           CancellationToken ct = default);

    /// <summary>Jede eingeschaltete Strategie einmal.</summary>
    Task<IReadOnlyList<Models.AutopilotLauf>> LaufeAlleAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Models.AutopilotLauf>> LaeufeAsync(string? depot, int grenze = 20,
                                                           CancellationToken ct = default);

    Task<IReadOnlyList<Models.AutopilotEntscheidung>> BeschluesseAsync(
        int laufId, int grenze = 200, CancellationToken ct = default);

    /// <summary>Die Strategien gegeneinander und gegen die Grundlinie.</summary>
    Task<Models.AutopilotVergleich> VergleichAsync(CancellationToken ct = default);
}

/// <summary>
/// Die Sprachdateien der Oberfläche.
///
/// <para>Der deutsche Text ist der Schlüssel; <c>loc.res.de.xml</c> ist damit
/// die Identität und zugleich der Katalog dessen, was übersetzbar ist.</para>
/// </summary>
public interface ILocService
{
    /// <summary>Die gefundenen Sprachen, Katalogsprache zuerst.</summary>
    IReadOnlyList<Models.Sprache> Sprachen();

    /// <summary>Eine Sprachdatei, oder <c>null</c>, wenn es sie nicht gibt.</summary>
    Models.Sprachdatei? Lade(string code);

    /// <summary>Liest das Sprachverzeichnis neu ein; liefert die Anzahl.</summary>
    int Auffrischen();

    /// <summary>
    /// In welcher der vorhandenen Sprachen ein Text geschrieben ist —
    /// <c>null</c>, wenn es dafür zu wenig Anhalt gibt.
    ///
    /// <para><b>Warum das nicht das Sprachmodell entscheiden darf.</b>
    /// Gemessen an <c>qwen3-vl:4b</c>: Bei der Anweisung „antworte auf
    /// Englisch, es sei denn, die Frage ist erkennbar in einer anderen
    /// Sprache“ und einer deutschen Frage kam die Antwort auf Englisch. Ein
    /// Modell wägt zwei widersprüchliche Anweisungen nicht ab, es folgt der
    /// deutlicheren. Wird die Sprache dagegen VORHER bestimmt, bekommt es
    /// genau eine Anweisung — und die befolgt es nachweislich, in beide
    /// Richtungen.</para>
    /// </summary>
    string? Erkenne(string? text);
}

/// <summary>
/// Vorankündigungen: was demnächst neu an den Markt kommt.
///
/// <para>Die Sammlung ist die vollständige Kohorte aus Ankündigungen, die
/// CLAUDE.md für eine ehrliche Auswertung von Neuzugängen verlangt — der
/// eigene Bestand taugt dafür nicht, weil dort ein Wert erst auftaucht,
/// nachdem er gestiegen ist.</para>
/// </summary>
public interface INeuzugangService
{
    /// <summary>Holt alle Quellen ab. Jede in ihrem eigenen <c>try</c>.</summary>
    Task<IReadOnlyList<Models.NeuzugangLauf>> SammleAsync(CancellationToken ct = default);

    Task<Models.NeuzugangUebersicht> UebersichtAsync(CancellationToken ct = default);
}

/// <summary>
/// Die Day-Trading-Seite: Trägt der Markt heute überhaupt einen Handel
/// innerhalb des Tages — nach Gebühren?
/// </summary>
public interface IDayTradingService
{
    /// <param name="stunden">Wie weit zurück die Beweglichkeit gemessen wird.</param>
    Task<Models.TagesHandel> BuildAsync(int stunden, CancellationToken ct = default);
}

/// <summary>
/// Langfristiger Vermögensaufbau: Einzelwerte und Kombinationen mit
/// Jahresrendite, Schwankung und tiefstem Einbruch.
/// </summary>
public interface ILangfristService
{
    /// <param name="jahre">Betrachteter Zeitraum.</param>
    /// <param name="minTage">
    /// Mindestzahl an Tagesbars, damit ein Wert überhaupt mitgerechnet wird.
    /// Ein junger Kryptowert mit vier Monaten Historie hat keine
    /// Jahresrendite — er hat vier Monate.
    /// </param>
    Task<Models.LangfristUebersicht> BuildAsync(
        int jahre, int minTage, int limit, CancellationToken ct = default);
}
