using Ingest.Core.Enums;

namespace Ingest.Core.Models;

/// <summary>
/// Was ein einzelner Wert über den betrachteten Zeitraum abgeworfen hat.
/// </summary>
/// <param name="RenditeProJahr">
/// Die geometrische Jahresrendite (CAGR). <b>Vergangenheit.</b> Sie steht hier,
/// weil sie messbar ist — nicht, weil sie sich fortsetzt.
/// </param>
/// <param name="SchwankungProJahr">
/// Die auf ein Jahr hochgerechnete Streuung der Tagesrenditen. Ohne sie ist die
/// Rendite nicht einzuordnen: 40 Prozent im Jahr bei 90 Prozent Schwankung sind
/// etwas anderes als 8 Prozent bei 12.
/// </param>
/// <param name="GroessterRueckgang">
/// Der tiefste Einbruch vom bisherigen Höchststand. Die Zahl, die darüber
/// entscheidet, ob jemand eine Anlage durchhält — und deshalb wichtiger als
/// die Schwankung.
/// </param>
/// <param name="AnteilPositiverJahre">
/// Anteil der gleitenden Ein-Jahres-Fenster, die im Plus endeten. Beantwortet
/// „wie oft hat sich ein Jahr Halten gelohnt" direkt, ohne Umweg über
/// Verteilungsannahmen.
/// </param>
public sealed record LangfristWert(
    string Symbol, string? Name, AssetClass Klasse,
    int Handelstage, double Jahre,
    double RenditeProJahr, double SchwankungProJahr, double GroessterRueckgang,
    double AnteilPositiverJahre, double SchlechtestesJahr, double BestesJahr)
{
    /// <summary>
    /// Rendite je Einheit Schwankung. Kein Sharpe-Verhältnis — der risikofreie
    /// Zins fehlt bewusst, weil er hier nirgends erhoben wird und ein
    /// geschätzter Zins die Zahl nur scheingenau machte.
    /// </summary>
    public double RenditeJeSchwankung =>
        SchwankungProJahr > 0 ? RenditeProJahr / SchwankungProJahr : 0;

    /// <summary>
    /// Rendite je Einheit tiefsten Einbruchs. Für jemanden, der eine Anlage
    /// aussitzen muss, ist das die ehrlichere Kennzahl als die Schwankung.
    /// </summary>
    public double RenditeJeRueckgang =>
        GroessterRueckgang > 0 ? RenditeProJahr / GroessterRueckgang : 0;
}

/// <summary>
/// Eine Kombination mehrerer Werte, gleich gewichtet und täglich neu
/// ausgeglichen.
/// </summary>
/// <param name="MittlereKorrelation">
/// Die mittlere paarweise Korrelation der Mitglieder. Sie ist der ganze Zweck
/// der Streuung: Bei 0,9 hat man einen Wert in fünf Verpackungen, bei 0,2 fünf
/// Werte.
/// </param>
/// <param name="ImSperrbereich">
/// <c>true</c>, wenn die Zahlen aus dem Zeitraum stammen, den die Auswahl NICHT
/// gesehen hat. Nur diese Zahlen sagen etwas.
/// </param>
public sealed record LangfristKorb(
    string Name, string Begruendung,
    IReadOnlyList<string> Mitglieder,
    int Handelstage, double Jahre,
    double RenditeProJahr, double SchwankungProJahr, double GroessterRueckgang,
    double AnteilPositiverJahre, double MittlereKorrelation,
    double HoechsteKorrelation, double HoechsteKorrelationBeiAuswahl,
    bool ImSperrbereich)
{
    /// <summary>
    /// Wie stark sich die engste Bindung im Korb verschoben hat, seit die
    /// Auswahl getroffen wurde.
    ///
    /// <para><b>Das ist die ehrlichste Zahl dieser Seite.</b> XAUT-USD und
    /// PAXG-USD sind beide goldgedeckt. In der Auswahlhälfte korrelierten sie
    /// mit 0,838 — unter jeder vernünftigen Schwelle. Im Sperrbereich sind es
    /// 0,984. Die Streuung, auf die sich die Auswahl stützte, gab es zum
    /// Zeitpunkt der Messung nicht mehr.</para>
    ///
    /// <para>Das ist kein Rechenfehler, sondern die bekannte Schwäche jeder
    /// korrelationsbasierten Streuung: Korrelationen laufen gegen eins, wenn
    /// es darauf ankommt. Wer eine Kombination nach vergangenen Korrelationen
    /// zusammenstellt, streut gegen die Vergangenheit.</para>
    /// </summary>
    public double Korrelationsdrift => HoechsteKorrelation - HoechsteKorrelationBeiAuswahl;

    /* Die HÖCHSTE Paarkorrelation ist das Maß, nicht die mittlere.

       Die erste Fassung suchte nach der mittleren -- und wählte HWM,
       XAUT-USD und PAXG-USD. Die beiden letzten sind goldgedeckte Marken und
       laufen praktisch identisch; die mittlere Korrelation blieb trotzdem bei
       0,39, weil HWM sie herunterzog. Ein Korb aus drei Teilen, von denen zwei
       dasselbe sind, ist ein Korb aus zwei Teilen. */

    public double RenditeJeSchwankung =>
        SchwankungProJahr > 0 ? RenditeProJahr / SchwankungProJahr : 0;

    public double RenditeJeRueckgang =>
        GroessterRueckgang > 0 ? RenditeProJahr / GroessterRueckgang : 0;
}

/// <summary>Die ganze Langfrist-Seite.</summary>
public sealed record LangfristUebersicht(
    DateTime StandUtc,
    DateTime VonUtc, DateTime BisUtc, DateTime SperrbereichAbUtc,
    int WerteGeprueft,
    string Kernaussage,
    string Verzerrungshinweis,
    IReadOnlyList<LangfristWert> Einzelwerte,
    IReadOnlyList<LangfristKorb> Koerbe,
    IReadOnlyList<string> Anweisungen);
