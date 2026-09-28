using System.Collections.Concurrent;
using Ingest.Core.Models;

namespace Ingest.Infrastructure.Datenbank;

/// <summary>
/// Kursbars im Arbeitsspeicher, je Wert und Intervall.
///
/// <para><b>Warum es ihn gibt.</b> Das EventMesh-DataCell-Backend zahlt je
/// Zeile, die es über einen Index findet und aus seinen Zell-Tabellen
/// zusammensetzt. Gemessen am 27.09.2026: 250 Kursbars mit acht Spalten
/// kosten 208 ms, also 832 µs je Zeile; derselbe Node liefert bei einem
/// sequentiellen Massenlesevorgang 3,5 µs je Zeile. Ein Diagramm über zwölf
/// Monate braucht genau diese 250 Zeilen — und derselbe Aufruf kostet auf dem
/// SQL Server 21 ms für die ganze Antwort.</para>
///
/// <para>Das ist von aussen nicht zu beheben: Die Rekonstruktion je Treffer
/// sitzt im Store. Was von aussen geht, ist es nicht jedes Mal zu bezahlen.
/// Kursbars sind dafür der ideale Fall — sie ändern sich nur, wenn ein Lauf
/// sie schreibt, und dann weiss die Anwendung es genau.</para>
///
/// <para><b>Der Speicher ist gedeckelt, und das ist keine Formalie.</b> Die
/// Auflage lautet, nie mehr Arbeitsspeicher zu belegen als der SQL Server, den
/// diese Anwendung ersetzt (gemessen 2,10 GB gegen 0,44 GB des Node). Gehalten
/// wird deshalb nach Anzahl Bars, nicht nach „Anzahl Werte" — ein Wert mit
/// zehn Jahren Stundendaten ist tausendmal so gross wie einer mit einem Jahr
/// Tagesdaten, und eine Obergrenze, die das nicht unterscheidet, ist keine.
/// Verdrängt wird der am längsten nicht gelesene Eintrag.</para>
///
/// <para><b>Was NICHT hineingehört.</b> Nur Kursbars. Sie sind unveränderlich,
/// sobald geschrieben, und ihre einzige Quelle ist <c>UpsertAsync</c> — es gibt
/// also genau eine Stelle, an der verworfen werden muss. Für Prognosen oder
/// Bewertungen gilt das nicht; die werden laufend fortgeschrieben, und ein
/// Zwischenspeicher darüber wäre eine zweite Wahrheit mit unklarem Ablauf.</para>
/// </summary>
public sealed class Kursspeicher
{
    /// <summary>
    /// Ein gehaltener Abschnitt: alle Bars eines Wertes und Intervalls ab
    /// <see cref="AbUtc"/> bis zum Ende der Reihe, aufsteigend nach Zeit.
    /// </summary>
    private sealed class Abschnitt
    {
        public required DateTime AbUtc { get; init; }
        public required PriceBar[] Bars { get; init; }
        public long Zugriff;
    }

    private readonly ConcurrentDictionary<(int Wert, string Intervall), Abschnitt> _abschnitte = new();
    private readonly int _budget;
    private long _uhr;

    /*  Zaehler, und sie sind kein Beiwerk.

        Ein Zwischenspeicher verdeckt das Problem, das ihn noetig macht. Wer
        spaeter wissen will, ob das Backend besser geworden ist, braucht die
        Fehlschlagquote: Sie sagt, wie oft trotz allem gelesen werden musste.
        Ohne diese Zahl bleibt der Speicher fuer immer drin, weil niemand
        belegen kann, dass er entbehrlich geworden ist -- und genau das steht
        als Auflage in der Dokumentation.                                       */
    private long _treffer;
    private long _fehlschlaege;
    private long _ergaenzungen;

    public long Treffer => Interlocked.Read(ref _treffer);
    public long Fehlschlaege => Interlocked.Read(ref _fehlschlaege);
    public long Ergaenzungen => Interlocked.Read(ref _ergaenzungen);

    /// <param name="budgetBars">
    /// Obergrenze in Bars. 1,5 Millionen entsprechen bei dieser Datenform rund
    /// 300 MB. Das deckt die Tagesreihen aller verfolgten Werte ueber zwei
    /// Jahre (318.240 Bars) UND die Stundenreihen ueber drei Monate (rund 1,4
    /// Millionen) — und liegt noch immer bei einem Siebtel dessen, was der
    /// abgeloeste SQL Server allein fuer sich beanspruchte (2,10 GB).
    /// </param>
    public Kursspeicher(int budgetBars = 2_500_000) => _budget = budgetBars;

    public int GehalteneBars { get; private set; }

    public int GehalteneReihen => _abschnitte.Count;

    /// <summary>
    /// Der Ausschnitt [von, bis], falls der gehaltene Abschnitt ihn vollständig
    /// abdeckt — sonst <c>null</c>.
    ///
    /// <para>Entscheidend ist <c>AbUtc &lt;= von</c>: Ein Abschnitt, der erst
    /// später beginnt, darf NICHT als Treffer gelten. Sonst zeigte ein
    /// Diagramm, das zwei Jahre anfordert, stillschweigend eines — und genau
    /// diese Art von Fehler fällt niemandem auf, weil die Linie ja da ist.</para>
    /// </summary>
    public IReadOnlyList<PriceBar>? Hole(int wert, string intervall, DateTime von, DateTime bis)
    {
        if (!_abschnitte.TryGetValue((wert, intervall), out var a))
        {
            Interlocked.Increment(ref _fehlschlaege);
            return null;
        }

        if (a.AbUtc > von)
        {
            Interlocked.Increment(ref _fehlschlaege);
            return null;
        }

        Interlocked.Increment(ref _treffer);
        a.Zugriff = Interlocked.Increment(ref _uhr);

        // Binäre Suche statt Where(): der Abschnitt ist sortiert, und bei
        // zehntausenden Bars ist der Unterschied messbar.
        var start = UntereGrenze(a.Bars, von);
        var ende = ObereGrenze(a.Bars, bis);

        if (ende <= start) return Array.Empty<PriceBar>();

        var ausschnitt = new PriceBar[ende - start];
        Array.Copy(a.Bars, start, ausschnitt, 0, ausschnitt.Length);
        return ausschnitt;
    }

    /// <summary>
    /// Einen geladenen Abschnitt aufnehmen. <paramref name="abUtc"/> ist der
    /// Zeitpunkt, ab dem die Reihe VOLLSTÄNDIG ist — also die untere Grenze der
    /// Abfrage, die sie geliefert hat, nicht der erste vorhandene Bar.
    /// </summary>
    public void Lege(int wert, string intervall, DateTime abUtc, PriceBar[] bars)
    {
        var neu = new Abschnitt
        {
            AbUtc = abUtc,
            Bars = bars,
            Zugriff = Interlocked.Increment(ref _uhr),
        };

        _abschnitte.AddOrUpdate((wert, intervall), neu, (_, _) => neu);
        NeuZaehlen();
        Aufraeumen();
    }

    /// <summary>
    /// Die eben geschriebenen Bars in den gehaltenen Abschnitt einarbeiten,
    /// statt ihn wegzuwerfen.
    ///
    /// <para><b>Warum das den Unterschied macht.</b> Der Tageslauf schreibt für
    /// alle verfolgten Werte; mit <see cref="Verwerfe"/> war danach der gesamte
    /// Kursspeicher leer, und die unmittelbar folgenden Läufe — Analyse,
    /// Prognose, Autopilot — haben dieselben Bars einzeln wieder aus der
    /// Datenbank geholt. Gemessen am 28.09.2026 kostet allein das erneute Laden
    /// von 318.590 Tagesbars <b>92 Sekunden</b>, und zwar direkt nachdem die
    /// Anwendung diese Zeilen selbst geschrieben hat. Sie kennt sie also
    /// bereits; sie ein zweites Mal zu bezahlen ist reine Verschwendung.</para>
    ///
    /// <para><b>Warum das sicher ist.</b> Ein Upsert schreibt genau die Bars,
    /// die hier hereingereicht werden — es gibt keine dritte Stelle, die an der
    /// Reihe rührt. Bei gleichem Zeitstempel gewinnt der NEUE Bar; das ist
    /// dieselbe Regel, die der Upsert in der Datenbank anwendet, und sie ist der
    /// Grund, warum ein Nachladen derselben Tage (der Inkrementlauf holt die
    /// letzten fünf Tage erneut) korrigierte Kurse durchreicht statt sie zu
    /// verdoppeln.</para>
    ///
    /// <para><b>Was bewusst NICHT passiert.</b> Bars vor <c>AbUtc</c> werden
    /// verworfen, nicht vorangestellt. <c>AbUtc</c> ist die Zusage „ab hier ist
    /// die Reihe vollständig"; sie nach unten zu verschieben, weil zufällig ein
    /// älterer Bar geschrieben wurde, wäre eine Zusage ohne Deckung — der
    /// Bereich dazwischen ist nie geladen worden. Ein Leser, der weiter zurück
    /// fragt, bekommt so korrekt einen Fehlschlag statt einer Reihe mit Loch.</para>
    ///
    /// <para>Wird die Reihe nicht gehalten, passiert nichts: Was nicht im
    /// Speicher steht, muss auch nicht fortgeschrieben werden.</para>
    /// </summary>
    public void Ergaenze(int wert, string intervall, IReadOnlyList<PriceBar>? geschrieben)
    {
        if (geschrieben is null || geschrieben.Count == 0) return;
        if (!_abschnitte.TryGetValue((wert, intervall), out var alt)) return;

        var neue = geschrieben.Where(b => b.TsUtc >= alt.AbUtc)
                              .OrderBy(b => b.TsUtc)
                              .ToArray();
        if (neue.Length == 0) return;

        var verschmolzen = Verschmelze(alt.Bars, neue);

        var ersetzt = new Abschnitt
        {
            AbUtc = alt.AbUtc,
            Bars = verschmolzen,
            Zugriff = Interlocked.Increment(ref _uhr),
        };

        _abschnitte[(wert, intervall)] = ersetzt;
        Interlocked.Increment(ref _ergaenzungen);
        NeuZaehlen();
        Aufraeumen();
    }

    /*  Zwei sortierte Folgen zu einer sortierten ohne Dubletten.

        Bei gleichem Zeitstempel gewinnt der neue Bar. Das ist nicht Geschmack:
        Der Inkrementlauf laedt die letzten Tage bewusst erneut, weil ein
        Anbieter Kurse nachtraeglich korrigiert (Splits, verspaetete
        Schlusskurse). Gaebe der alte den Ausschlag, haette der Speicher genau
        die Korrektur nicht, wegen der nachgeladen wurde.                        */
    private static PriceBar[] Verschmelze(PriceBar[] alt, PriceBar[] neu)
    {
        var ziel = new List<PriceBar>(alt.Length + neu.Length);
        int i = 0, j = 0;

        while (i < alt.Length && j < neu.Length)
        {
            var c = alt[i].TsUtc.CompareTo(neu[j].TsUtc);
            if (c < 0) ziel.Add(alt[i++]);
            else if (c > 0) ziel.Add(neu[j++]);
            else { ziel.Add(neu[j++]); i++; }   // gleicher Zeitpunkt: der neue gilt
        }

        while (i < alt.Length) ziel.Add(alt[i++]);
        while (j < neu.Length) ziel.Add(neu[j++]);

        return ziel.ToArray();
    }

    /// <summary>
    /// Verwerfen, sobald für diesen Wert und dieses Intervall geschrieben wurde.
    /// Ein Aufruf für eine nicht gehaltene Reihe ist billig und erlaubt.
    ///
    /// <para>Bleibt für die Fälle, in denen die Anwendung NICHT weiss, was
    /// geschrieben wurde — Löschläufe des Hausmeisters etwa. Wer die
    /// geschriebenen Bars zur Hand hat, nimmt <see cref="Ergaenze"/>.</para>
    /// </summary>
    public void Verwerfe(int wert, string intervall)
    {
        if (_abschnitte.TryRemove((wert, intervall), out _)) NeuZaehlen();
    }

    public void VerwerfeAlles()
    {
        _abschnitte.Clear();
        GehalteneBars = 0;
    }

    private void NeuZaehlen()
        => GehalteneBars = _abschnitte.Values.Sum(a => a.Bars.Length);

    /*  Verdrängung nach Alter des letzten Zugriffs.

        Bewusst schlicht: Die Zahl der Reihen liegt im niedrigen Tausenderbereich,
        ein Durchlauf darüber kostet nichts, und er passiert nur, wenn das Budget
        wirklich überschritten ist. Eine echte LRU-Liste mit Verkettung wäre
        schneller und hätte an dieser Stelle keinen messbaren Vorteil — dafür
        Sperren, die man falsch nehmen kann.                                     */
    private void Aufraeumen()
    {
        if (GehalteneBars <= _budget) return;

        foreach (var eintrag in _abschnitte.OrderBy(e => e.Value.Zugriff))
        {
            if (GehalteneBars <= _budget) break;
            if (_abschnitte.TryRemove(eintrag.Key, out var weg))
                GehalteneBars -= weg.Bars.Length;
        }
    }

    private static int UntereGrenze(PriceBar[] bars, DateTime von)
    {
        int lo = 0, hi = bars.Length;
        while (lo < hi)
        {
            var m = (lo + hi) / 2;
            if (bars[m].TsUtc < von) lo = m + 1; else hi = m;
        }
        return lo;
    }

    private static int ObereGrenze(PriceBar[] bars, DateTime bis)
    {
        int lo = 0, hi = bars.Length;
        while (lo < hi)
        {
            var m = (lo + hi) / 2;
            if (bars[m].TsUtc <= bis) lo = m + 1; else hi = m;
        }
        return lo;
    }
}
