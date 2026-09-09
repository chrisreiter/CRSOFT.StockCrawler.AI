namespace Ingest.Core.Analysis;

/// <summary>
/// Was ein Tausch kostet, bevor er etwas einbringt.
///
/// <para><b>Warum das eine eigene Größe ist und kein Nachgedanke.</b> Ein
/// Signal, dessen durchschnittlicher Ertrag unter dem Rundlauf liegt, ist
/// nicht schwach — es ist wertlos, und zwar unabhängig von seiner
/// Trefferquote. Eine Rangliste, die das nicht verrechnet, empfiehlt
/// zuverlässig Geschäfte, die im Mittel Geld kosten.</para>
///
/// <para>Gemessen an den Zahlen dieses Systems ist das kein Randfall: Von den
/// Kreuzungspaaren mit genug Vorgeschichte liegen die mittleren Erträge
/// überwiegend zwischen −3 und +5 Prozent. Ein Rundlauf von 0,3 Prozent
/// streicht davon nicht viel, aber er streicht genau die Zeilen, die knapp
/// über null liegen und ohne ihn nach einem Fund aussähen.</para>
/// </summary>
/// <param name="GebuehrJeSeite">
/// Gebühr des Nehmers je Ausführung. 0,1 Prozent ist der übliche Satz für
/// Privatkunden auf großen Kryptobörsen; wer Rückvergütung als Steller
/// bekommt, zahlt weniger, aber darauf darf sich eine Voreinstellung nicht
/// verlassen.
/// </param>
/// <param name="SchlupfJeSeite">
/// Der angezeigte Kurs ist die Spitze des Orderbuchs, nicht der eigene
/// Abschluss. 0,05 Prozent ist zurückhaltend gerechnet — bei dünn gehandelten
/// Werten ist es ein Vielfaches.
/// </param>
public sealed record Handelskosten(double GebuehrJeSeite = 0.0010, double SchlupfJeSeite = 0.0005)
{
    /// <summary>
    /// Ein Tausch hat ZWEI Beine: das eine verkaufen, das andere kaufen.
    /// Gebühr und Schlupf fallen deshalb doppelt an. Wer nur einmal rechnet,
    /// halbiert die Schwelle und findet doppelt so viele Gelegenheiten, die
    /// keine sind.
    /// </summary>
    public double Rundlauf => 2 * (GebuehrJeSeite + SchlupfJeSeite);

    /// <summary>0,3 Prozent je Tausch.</summary>
    public static Handelskosten Standard { get; } = new();

    /// <summary>
    /// Was von einem Ertrag nach dem Rundlauf übrig bleibt. Negativ zu werden
    /// ist der Normalfall und keine Ausnahme.
    /// </summary>
    public double NachKosten(double bruttoertrag) => bruttoertrag - Rundlauf;

    /// <summary>
    /// Der Erwartungswert eines Geschäfts nach Kosten.
    ///
    /// <para><c>(2p − 1) · E|r| − Rundlauf</c>. Bei einer Trefferquote p
    /// gewinnt man in p Fällen die typische Bewegung und verliert sie in
    /// (1 − p) Fällen; der Nettoanteil ist (2p − 1). Davon geht der Rundlauf
    /// ab, und zwar immer, nicht nur bei Fehlschlägen.</para>
    ///
    /// <para><b>Warum das nicht dasselbe ist wie „Trefferquote über der
    /// Schwelle und Bewegung über den Kosten".</b> Genau diese beiden
    /// Bedingungen waren der erste Entwurf, und sie haben eine Reihe als
    /// tragfähig ausgewiesen, die Geld verliert: 52,4 Prozent Trefferquote bei
    /// 0,47 Prozent mittlerer Bewegung ergibt einen Bruttovorsprung von 0,023
    /// Prozent — gegen 0,3 Prozent Kosten. Der Vorsprung wächst mit
    /// (2p − 1), also bei 52,4 Prozent mit dem Faktor 0,048. Er muss die
    /// Kosten um ein Vielfaches überschreiten, nicht knapp erreichen.</para>
    /// </summary>
    public double Erwartungswert(double trefferquote, double mittlereBewegung) =>
        (2 * trefferquote - 1) * mittlereBewegung - Rundlauf;

    /// <summary>
    /// Welche Trefferquote nötig wäre, damit sich ein Geschäft bei dieser
    /// typischen Bewegung überhaupt lohnt. Die Zahl ist ernüchternd und
    /// gehört deshalb in die Anzeige: Bei 0,47 Prozent Bewegung und 0,3
    /// Prozent Kosten sind es 81,9 Prozent.
    /// </summary>
    public double NoetigeTrefferquote(double mittlereBewegung) =>
        mittlereBewegung <= 0 ? 1 : 0.5 * (Rundlauf / mittlereBewegung + 1);

    public string Beschreibung =>
        $"{Rundlauf * 100:0.##} % je Tausch — zwei Beine à {GebuehrJeSeite * 100:0.##} % Gebühr "
        + $"und {SchlupfJeSeite * 100:0.##} % Schlupf";
}
