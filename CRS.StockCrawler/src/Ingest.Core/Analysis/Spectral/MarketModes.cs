namespace Ingest.Core.Analysis.Spectral;

/// <summary>Wie stark ein einzelner Wert an einer gemeinsamen Schwingung hängt.</summary>
/// <param name="Loading">
/// Betrag der Ladung, 0 bis 1. Wie stark dieser Wert von der Mode getragen wird.
/// </param>
/// <param name="PhaseDegrees">Lage relativ zur Mode, −180 bis +180.</param>
/// <param name="LeadBars">
/// Die Phase in Bars umgerechnet. Positiv heißt: Dieser Wert läuft der
/// gemeinsamen Schwingung voraus. <b>Genau hier liegt der Prognosewert</b> —
/// ein Wert, der auf einer Zeitskala systematisch vorausläuft, verrät, was den
/// nachlaufenden bevorsteht.
/// </param>
public sealed record ModeMember(
    int AssetId,
    string Symbol,
    double Loading,
    double PhaseDegrees,
    double LeadBars);

/// <summary>Eine gemeinsame Schwingung des Marktes auf einer Zeitskala.</summary>
/// <param name="ExplainedShare">
/// Anteil der gesamten Marktbewegung auf dieser Zeitskala, den eine einzige
/// Schwingung erklärt. Bei völlig unabhängigen Werten liegt er nahe 1/N, bei
/// einem Markt, der auf dieser Skala im Gleichschritt läuft, nahe 1.
/// </param>
/// <param name="SurrogateShare">
/// Was dieselbe Rechnung an Ersatzreihen liefert, die jedem Wert sein eigenes
/// Spektrum lassen, aber jeden Zusammenhang zwischen den Werten zerstören. Das
/// ist die Latte.
/// </param>
/// <param name="ZScore">
/// Wie viele Streuungen der gemessene Anteil über dem Ersatz liegt. <b>Diese
/// Zahl entscheidet</b>, ob eine gemeinsame Schwingung existiert oder ob man
/// die Farbe der einzelnen Spektren wiedergefunden hat.
/// </param>
public sealed record MarketMode(
    double PeriodBars,
    double ExplainedShare,
    double SurrogateShare,
    double SurrogateSd,
    double ZScore,
    bool Significant,
    IReadOnlyList<ModeMember> Members,

    /// <summary>Rang der Mode: 1 ist die stärkste, 2 die zweitstärkste.</summary>
    int Rank = 1,

    /// <summary>
    /// Wie weit die Werte innerhalb dieser Mode zeitlich auseinanderliegen —
    /// die mit der Ladung gewichtete Streuung der Vorläufe, in Bars.
    ///
    /// <b>Die entscheidende Kennzahl für die Prognose.</b> Eine Mode, in der
    /// alle gleichzeitig laufen, hat eine Spreizung nahe null und sagt
    /// zwischen den Werten nichts voraus — sie beschreibt nur, dass sich der
    /// Markt gemeinsam bewegt. Erst eine Mode mit deutlicher Spreizung enthält
    /// Vorläufer und Nachzügler, und nur die sind nutzbar.
    /// </summary>
    double PhaseSpreadBars = 0);

public sealed record MarketModeResult(
    int Assets,
    int Points,
    int Segments,
    int Surrogates,
    IReadOnlyList<MarketMode> Modes,
    string Note,

    /// <summary>
    /// Die Schwelle, ab der eine Frequenz als bedeutsam gilt — gewonnen aus den
    /// <b>Höchstwerten</b> der Ersatzläufe über alle Frequenzen hinweg.
    /// </summary>
    double Threshold = 0,

    /// <summary>Freiheitsgrade der Schätzung: Abschnitte × geglättete Frequenzen.</summary>
    int DegreesOfFreedom = 0);

/// <summary>
/// Gemeinsame Marktschwingungen: Welche Zeitskalen treiben viele Kurse
/// gleichzeitig, und wer läuft dabei voran?
///
/// <b>Warum das etwas anderes ist als die Analyse einer einzelnen Reihe.</b>
/// Das Spektrum eines einzelnen Kurses beantwortet die Frage, welche
/// Periodenlängen in <i>ihm</i> stecken. Das ist die schwächere Frage, und sie
/// führt bei Kursen selten weiter — die Energie verteilt sich glatt über die
/// Frequenzen, weil eine einzelne Reihe von allem etwas enthält.
///
/// Die stärkere Frage lautet: Gibt es Frequenzen, bei denen <i>viele</i> Kurse
/// dasselbe tun? Eine gemeinsame Ursache — Zinsentscheidung, Quartalsrhythmus,
/// Liquiditätszyklus — bewegt nicht einen Wert, sondern hunderte, und zwar mit
/// festen Phasenbeziehungen untereinander. Im einzelnen Chart ist das kaum zu
/// sehen, weil es dort unter allem anderen liegt. Über das gesamte Universum
/// hinweg ist es das <b>Einzige</b>, was sich konstruktiv überlagert.
///
/// <b>Wie das rechnerisch geht.</b> Zu jeder Frequenz wird die
/// Kreuzspektralmatrix aufgestellt: eine hermitesche N×N-Matrix, deren Eintrag
/// (i,j) beschreibt, wie stark und mit welcher Phasenverschiebung die Werte i
/// und j bei dieser Frequenz zusammenhängen. Ihr größter Eigenwert sagt, wie
/// viel der gesamten Bewegung auf dieser Zeitskala <i>eine einzige</i>
/// Schwingung erklärt. Der zugehörige Eigenvektor ist komplex — sein Betrag je
/// Wert ist die Ladung, sein Winkel die Phasenlage.
///
/// Damit ist die Phasenverschiebung nicht ein Störfaktor, den man wegrechnen
/// müsste, sondern das Ergebnis: Ein Wert mit +3 Bars Vorlauf auf der
/// 60-Tage-Skala sagt der Mode voraus, und die Mode sagt den nachlaufenden
/// Werten voraus.
///
/// <b>Die Latte, ohne die das nichts wert wäre.</b> Auch bei völlig
/// unabhängigen Reihen ist der größte Eigenwert nicht 1/N — Zufall erzeugt
/// Struktur, und bei roten Spektren mehr als bei weißen. Deshalb wird dieselbe
/// Rechnung an Ersatzreihen wiederholt, die jedem Wert sein <i>eigenes</i>
/// Spektrum unverändert lassen und nur die Phasen würfeln. Damit bleibt jede
/// Reihe für sich exakt so rot wie vorher, aber jeder Zusammenhang zwischen den
/// Reihen ist zerstört. Was darüber hinausragt, ist gemeinsame Bewegung — und
/// nur das.
/// </summary>
public static class MarketModes
{
    /// <summary>
    /// Zerlegt ein ganzes Universum. <paramref name="data"/> hält je Wert eine
    /// Reihe gleicher Länge auf einem gemeinsamen Zeitraster.
    /// </summary>
    /// <param name="surrogates">
    /// Wie viele Ersatzdurchläufe. Null schaltet die Prüfung ab — dann steht
    /// zwar eine Zahl da, aber ohne jede Aussage darüber, ob sie etwas bedeutet.
    /// </param>
    public static MarketModeResult Analyze(
        IReadOnlyList<(int AssetId, string Symbol, double[] Series)> data,
        int segment = 256,
        double overlap = 0.5,
        double minPeriod = 5,
        double maxPeriod = 250,
        int surrogates = 60,
        int topMembers = 15,
        int smoothBins = 3,
        int modes = 1,
        int seed = 20260821)
    {
        var n = data.Count;
        if (n < 4) return Empty("Mindestens vier Werte nötig");

        var raw = data[0].Series.Length;
        foreach (var d in data)
            if (d.Series.Length != raw) return Empty("Reihen ungleich lang");

        /* Die Reihen werden auf die größte Zweierpotenz gekürzt, die hineinpasst.

           Das klingt nach Verschwendung und ist der Preis für einen korrekten
           Vergleich. Die Ersatzreihen entstehen, indem jede Reihe transformiert,
           in der Phase gewürfelt und zurücktransformiert wird — und das erhält
           ihr Leistungsspektrum nur dann EXAKT, wenn nichts mit Nullen
           aufgefüllt wurde. Andernfalls verteilt die Rücktransformation Signal
           in den aufgefüllten Bereich, das Abschneiden wirft es weg, und die
           Ersatzreihe ist weniger langgedächtnis als das Original.

           Die Folge wäre eine zu niedrige Latte: Die Ersatzverteilung fiele zu
           schwach aus, und die Auswertung erklärte reihenweise Zufall für
           bedeutsam. Genau das ist beim ersten Versuch passiert — zwanzig
           voneinander unabhängige rote Reihen ergaben eine „signifikante“
           gemeinsame Schwingung. */
        var len = 1;
        while (len * 2 <= raw) len *= 2;

        if (len < raw)
        {
            var trimmed = new List<(int AssetId, string Symbol, double[] Series)>(data.Count);
            foreach (var d in data) trimmed.Add((d.AssetId, d.Symbol, d.Series[^len..]));
            data = trimmed;
        }

        segment = Math.Min(segment, len / 3);
        if (segment < 32) return Empty($"Zu wenige gemeinsame Zeitpunkte ({len})");

        var padded = Fft.NextPow2(segment * 2);
        var half = padded / 2;

        var starts = new List<int>();
        var step = Math.Max(1, (int)(segment * (1 - Math.Clamp(overlap, 0, 0.95))));
        for (var s = 0; s + segment <= len; s += step) starts.Add(s);

        /* Freiheitsgrade. Der entscheidende Punkt dieser ganzen Rechnung.

           Die Kreuzspektralmatrix hat N Zeilen und wird aus Mittelungen
           geschätzt. Sind es weniger Mittelungen als Werte, hat sie nicht vollen
           Rang — der größte Eigenwert saugt dann zwangsläufig einen viel zu
           großen Anteil auf, und zwar rein rechnerisch, ohne dass irgendetwas
           gemeinsam schwingen müsste.

           Genau daran ist der erste Versuch gescheitert: 20 Werte, aber nur
           7 Abschnitte. Der ausgewiesene Anteil lag bei 2,5 und 9,8 — Werte über
           eins, was für einen normierten Anteil unmöglich ist und das
           Rangdefizit unmittelbar verrät. Zufällige und echte gemeinsame
           Schwingungen kamen auf identische Zahlen.

           Freiheitsgrade entstehen aus zwei Quellen: der Zahl der Abschnitte und
           der Zahl benachbarter Frequenzen, über die geglättet wird. Die zweite
           kostet Frequenzauflösung und ist der übliche Weg, wenn viele Reihen
           gleichzeitig betrachtet werden. */
        var dof = starts.Count * (2 * Math.Max(0, smoothBins) + 1);

        if (starts.Count < 3)
            return Empty("Zu wenige Abschnitte für eine Kreuzspektralschätzung");

        if (dof < 2 * n)
            return Empty($"Zu wenige Freiheitsgrade: {starts.Count} Abschnitte × "
                       + $"{2 * Math.Max(0, smoothBins) + 1} Frequenzen = {dof}, "
                       + $"nötig sind mindestens {2 * n} bei {n} Werten. "
                       + "Abhilfe: kürzere Teilfenster, mehr Überlappung, stärkere "
                       + "Frequenzglättung oder weniger Werte.");

        var win = Spectrum.Hann(segment);

        // Frequenzstützstellen im gesuchten Band.
        var bins = new List<int>();
        for (var k = 1; k < half; k++)
        {
            var period = (double)padded / k;
            if (period >= minPeriod && period <= maxPeriod) bins.Add(k);
        }

        if (bins.Count == 0) return Empty("Band leer");

        var spectra = Transform(data, starts, segment, padded, win);
        var real = Solve(data, spectra, bins, padded, topMembers, smoothBins, half, modes);

        if (surrogates <= 0)
            return new MarketModeResult(n, len, starts.Count, 0, real,
                $"Ohne Ersatzprüfung — die Anteile sind nicht einzuordnen. "
                + $"Ausgewertet auf {len} von {raw} gemeinsamen Zeitpunkten.",
                0, dof);

        /* Alle Ersatzläufe werden vollständig behalten, nicht nur Summe und
           Quadratsumme. Der Grund steht weiter unten: Für die Schwelle wird je
           Lauf sein Höchstwert über ALLE Frequenzen gebraucht, und der lässt
           sich aus Summen nicht rekonstruieren. */
        var draws = new double[surrogates][];

        /* Die Ersatzläufe sind voneinander unabhängig und tragen den Löwenanteil
           der Rechenzeit — bei sechzig Durchläufen das Sechzigfache des echten.
           Nebeneinander gerechnet wird daraus der Bruchteil, den die Kerne
           hergeben.

           Jeder Durchlauf bekommt seinen EIGENEN Zufallsgenerator mit einem aus
           der Laufnummer abgeleiteten Startwert. Ein geteilter Generator wäre
           nicht nur unsicher im Nebeneinander, er machte das Ergebnis auch von
           der Reihenfolge abhängig — und damit von Lauf zu Lauf anders. So
           bleibt es bei gleichem Startwert reproduzierbar, egal wie viele Kerne
           die Maschine hat.

           Zwei Kerne bleiben frei: Der Rechner bedient nebenher noch die
           Oberfläche und die Datenbank. */
        var parallelism = Math.Max(1, Environment.ProcessorCount - 2);

        Parallel.For(0, surrogates, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, r =>
        {
            var rnd = new Random(seed + r * 7919);

            var shuffled = Surrogate(data, rnd);
            var sp = Transform(shuffled, starts, segment, padded, win);
            var draw = Solve(shuffled, sp, bins, padded, 0, smoothBins, half, modes);

            var row = new double[real.Count];
            for (var i = 0; i < real.Count; i++) row[i] = draw[i].ExplainedShare;

            draws[r] = row;
        });

        // Mittelwert und Streuung je Frequenz.
        var mean = new double[real.Count];
        var sd = new double[real.Count];

        for (var i = 0; i < real.Count; i++)
        {
            double m = 0;
            for (var r = 0; r < surrogates; r++) m += draws[r][i];
            m /= surrogates;

            double v = 0;
            for (var r = 0; r < surrogates; r++) v += (draws[r][i] - m) * (draws[r][i] - m);

            mean[i] = m;
            sd[i] = Math.Sqrt(v / Math.Max(1, surrogates - 1));
        }

        /* Die Schwelle kommt aus den HÖCHSTWERTEN der Ersatzläufe, nicht aus
           einer festen Zahl von Streuungen.

           Hier werden zweihundert Frequenzen gleichzeitig geprüft. Bei einer
           festen Schwelle von drei Streuungen rutscht allein durch diese Zahl
           in praktisch jedem Durchlauf irgendeine Frequenz hindurch — belegt:
           zwanzig nachweislich unabhängige Reihen lieferten zwei „bedeutsame“
           gemeinsame Schwingungen, und zwar sowohl im roten als auch im weißen
           Fall.

           Die richtige Frage lautet nicht „ist DIESE Frequenz auffällig“, sondern
           „ist die auffälligste Frequenz auffälliger, als der Zufall über
           zweihundert Versuche hergibt". Genau das misst die Verteilung der
           Höchstwerte. */
        var maxima = new double[surrogates];

        for (var r = 0; r < surrogates; r++)
        {
            double best = double.NegativeInfinity;

            for (var i = 0; i < real.Count; i++)
            {
                if (sd[i] <= 1e-12) continue;
                var z = (draws[r][i] - mean[i]) / sd[i];
                if (z > best) best = z;
            }

            maxima[r] = double.IsNegativeInfinity(best) ? 0 : best;
        }

        Array.Sort(maxima);

        // 95. Perzentil der Höchstwerte.
        var idx = Math.Min(surrogates - 1, (int)Math.Ceiling(0.95 * surrogates) - 1);
        var threshold = maxima[Math.Max(0, idx)];

        var outp = new List<MarketMode>(real.Count);

        for (var i = 0; i < real.Count; i++)
        {
            var z = sd[i] > 1e-12 ? (real[i].ExplainedShare - mean[i]) / sd[i] : 0;

            outp.Add(real[i] with
            {
                SurrogateShare = Math.Round(mean[i], 5),
                SurrogateSd = Math.Round(sd[i], 5),
                ZScore = Math.Round(z, 2),
                Significant = z > threshold
            });
        }

        return new MarketModeResult(n, len, starts.Count, surrogates,
            outp.OrderByDescending(m => m.ZScore).ToList(),
            $"Schwelle {threshold:F2} — das 95. Perzentil der Höchstwerte aus "
            + $"{surrogates} Ersatzläufen über {real.Count} Frequenz-Moden-Paare. "
            + $"Ausgewertet auf {len} von {raw} gemeinsamen Zeitpunkten.",
            Math.Round(threshold, 2), dof);
    }

    /// <summary>Fourier-Koeffizienten je Wert und Abschnitt.</summary>
    private static (double[][] Re, double[][] Im)[] Transform(
        IReadOnlyList<(int AssetId, string Symbol, double[] Series)> data,
        List<int> starts, int segment, int padded, double[] win)
    {
        var result = new (double[][], double[][])[data.Count];

        for (var a = 0; a < data.Count; a++)
        {
            var re = new double[starts.Count][];
            var im = new double[starts.Count][];

            for (var s = 0; s < starts.Count; s++)
            {
                var r = new double[padded];
                var i = new double[padded];

                var off = starts[s];

                double mean = 0;
                for (var t = 0; t < segment; t++) mean += data[a].Series[off + t];
                mean /= segment;

                for (var t = 0; t < segment; t++)
                    r[t] = (data[a].Series[off + t] - mean) * win[t];

                Fft.Forward(r, i);

                re[s] = r;
                im[s] = i;
            }

            result[a] = (re, im);
        }

        return result;
    }

    /// <summary>
    /// Baut je Frequenz die Kreuzspektralmatrix und holt ihren führenden
    /// Eigenvektor.
    ///
    /// <b>Wo die Rechenzeit hingeht — und warum sie es nicht muss.</b> Der
    /// erste Entwurf baute für jede Frequenz die Matrix aus den Rohdaten neu
    /// auf, einschließlich aller Nachbarfrequenzen des Glättungsfensters. Da
    /// benachbarte Fenster einander weitgehend überdecken, wurde derselbe
    /// Kreuzterm ein Dutzend Mal gerechnet. Bei 160 Werten, 100 Frequenzen und
    /// dreizehn Durchläufen sind das rund siebzehn Milliarden Rechenschritte —
    /// Minuten statt Sekunden.
    ///
    /// Jetzt werden die Kreuzterme <b>einmal je Frequenz</b> bestimmt und
    /// abgelegt; die Glättung summiert danach nur noch fertige Matrizen. Der
    /// Speicherbedarf dafür ist bei 160 Werten und 100 Frequenzen etwa
    /// vierzig Megabyte — ein guter Tausch gegen den Faktor zehn an Zeit.
    /// </summary>
    private static List<MarketMode> Solve(
        IReadOnlyList<(int AssetId, string Symbol, double[] Series)> data,
        (double[][] Re, double[][] Im)[] spectra,
        List<int> bins, int padded, int topMembers, int smoothBins, int half, int modes)
    {
        var n = data.Count;
        var segments = spectra[0].Re.Length;

        /* Alle Frequenzen, die irgendein Glättungsfenster berührt — nicht nur
           die ausgewerteten. Sonst fehlt am Rand der Bänder genau das, was
           hineingemittelt werden soll. */
        var need = new SortedSet<int>();

        foreach (var k in bins)
        {
            var wide = Math.Max(smoothBins, (int)Math.Round(k * 0.08));

            for (var b = Math.Max(1, k - wide); b <= Math.Min(half - 1, k + wide); b++)
                need.Add(b);
        }

        var slot = new Dictionary<int, int>(need.Count);
        var idx = 0;
        foreach (var b in need) slot[b] = idx++;

        var cnt = need.Count;

        // Nur die obere Dreiecksmatrix je Frequenz -- der Rest folgt aus der Symmetrie.
        var pairs = n * (n + 1) / 2;
        var accRe = new double[cnt * pairs];
        var accIm = new double[cnt * pairs];

        foreach (var b in need)
        {
            var off = slot[b] * pairs;
            var pos = 0;

            for (var i = 0; i < n; i++)
            {
                for (var j = i; j < n; j++, pos++)
                {
                    double cr = 0, ci = 0;

                    for (var t = 0; t < segments; t++)
                    {
                        var xr = spectra[i].Re[t][b];
                        var xi = spectra[i].Im[t][b];
                        var yr = spectra[j].Re[t][b];
                        var yi = spectra[j].Im[t][b];

                        // x · konjugiert(y)
                        cr += xr * yr + xi * yi;
                        ci += xi * yr - xr * yi;
                    }

                    accRe[off + pos] = cr;
                    accIm[off + pos] = ci;
                }
            }
        }

        var sRe = new double[n, n];
        var sIm = new double[n, n];

        var diag = new double[n];

        var vRe = new double[n];
        var vIm = new double[n];
        var wRe = new double[n];
        var wIm = new double[n];

        var outp = new List<MarketMode>(bins.Count * Math.Max(1, modes));

        foreach (var k in bins)
        {
            /* Die Glättungsbreite wächst mit der Frequenz.

               Eine feste Zahl von Stützstellen bedeutet bei niedrigen
               Frequenzen einen riesigen Periodenbereich: Bei k=8 umfasst ±3
               Stützstellen die Perioden 44 bis 93 — eine 60-Bar-Schwingung wird
               darin mit allem verrührt, was in der Nähe liegt, und verschwindet.
               Proportional geglättet ist der Periodenbereich überall gleich
               breit. */
            var wide = Math.Max(smoothBins, (int)Math.Round(k * 0.08));
            var lo = Math.Max(1, k - wide);
            var hi = Math.Min(half - 1, k + wide);

            var count = (hi - lo + 1) * segments;

            Array.Clear(sRe);
            Array.Clear(sIm);

            for (var b = lo; b <= hi; b++)
            {
                var off = slot[b] * pairs;
                var pos = 0;

                for (var i = 0; i < n; i++)
                {
                    for (var j = i; j < n; j++, pos++)
                    {
                        sRe[i, j] += accRe[off + pos];
                        sIm[i, j] += accIm[off + pos];
                    }
                }
            }

            for (var i = 0; i < n; i++)
            {
                for (var j = i; j < n; j++)
                {
                    sRe[i, j] /= count;
                    sIm[i, j] /= count;

                    sRe[j, i] = sRe[i, j];
                    sIm[j, i] = -sIm[i, j];
                }
            }

            /* Erst ALLE Diagonalwerte sichern, dann normieren.

               Wird zeilenweise normiert und dabei aus der bereits veränderten
               Matrix gelesen, bekommt jede Zeile einen anderen Bezug: Für die
               zweite Zeile steht in der ersten Diagonale längst eine Eins statt
               des ursprünglichen Wertes. Das Ergebnis ist nicht mehr
               hermitesch, seine Eigenwerte sind Unsinn, und der ausgewiesene
               Anteil übersteigt eins. */
            for (var i = 0; i < n; i++)
                diag[i] = Math.Sqrt(Math.Max(1e-300, sRe[i, i]));

            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                {
                    sRe[i, j] /= diag[i] * diag[j];
                    sIm[i, j] /= diag[i] * diag[j];
                }
            }

            var period = (double)padded / k;

            /* Mehrere Moden je Frequenz, gewonnen über Deflation: Ist der
               führende Eigenvektor bestimmt, wird sein Beitrag aus der Matrix
               herausgenommen, und der nächste Durchlauf findet den
               zweitstärksten.

               Der Grund dafür ist der wichtigste Befund dieser Auswertung: Die
               erste Mode ist der Marktfaktor -- alles bewegt sich gleichzeitig,
               die Phasen liegen bei null, und damit sagt sie zwischen den
               Werten nichts voraus. Was prognostisch taugt, muss danach kommen:
               Rotation zwischen Branchen, Umschichtung zwischen Klassen. */
            for (var rank = 1; rank <= Math.Max(1, modes); rank++)
            {
                var lambda = LeadingEigen(sRe, sIm, n, vRe, vIm, wRe, wIm);

                // Die Spur ist nach der Normierung genau n.
                var share = Math.Clamp(lambda / n, 0, 1);

                List<ModeMember> members = [];
                double spread = 0;

                if (topMembers > 0)
                {
                    /* Bezugspunkt ist der Schwerpunkt der Mode, nicht ihr
                       stärkster Wert. Der stärkste wechselt von Frequenz zu
                       Frequenz, und damit wechselte der Nullpunkt -- ein
                       Vorlauf von +3 Bars bedeutete dann bei jeder Frequenz
                       etwas anderes. Gemittelt wird über die Zeiger, nicht über
                       die Winkel: Der arithmetische Mittelwert von 350 und
                       10 Grad wäre 180 statt 0. */
                    double cx = 0, cy = 0;

                    for (var i = 0; i < n; i++)
                    {
                        var mg = Math.Sqrt(vRe[i] * vRe[i] + vIm[i] * vIm[i]);
                        if (mg < 1e-12) continue;

                        cx += vRe[i];
                        cy += vIm[i];
                    }

                    var a0 = Math.Atan2(cy, cx);

                    for (var i = 0; i < n; i++)
                    {
                        var mag = Math.Sqrt(vRe[i] * vRe[i] + vIm[i] * vIm[i]);
                        if (mag < 1e-12) continue;

                        var ang = Math.Atan2(vIm[i], vRe[i]) - a0;
                        while (ang > Math.PI) ang -= 2 * Math.PI;
                        while (ang < -Math.PI) ang += 2 * Math.PI;

                        members.Add(new ModeMember(
                            data[i].AssetId, data[i].Symbol,
                            Math.Round(mag, 4),
                            Math.Round(ang * 180 / Math.PI, 1),
                            Math.Round(ang / (2 * Math.PI) * period, 2)));
                    }

                    /* Die Spreizung über ALLE Werte, bevor die Liste gekürzt
                       wird -- sonst beschriebe sie nur die Auswahl. Gewichtet
                       mit der Ladung, weil ein Wert, der kaum an der Mode
                       hängt, auch keine aussagekräftige Phase hat. */
                    double wsum = 0, wmean = 0;
                    foreach (var m in members) { wsum += m.Loading; wmean += m.Loading * m.LeadBars; }

                    if (wsum > 0)
                    {
                        wmean /= wsum;

                        double v = 0;
                        foreach (var m in members) v += m.Loading * (m.LeadBars - wmean) * (m.LeadBars - wmean);

                        spread = Math.Sqrt(v / wsum);
                    }

                    members = members.OrderByDescending(m => m.Loading).Take(topMembers).ToList();
                }

                outp.Add(new MarketMode(
                    Math.Round(period, 2), Math.Round(share, 5), 0, 0, 0, false, members,
                    rank, Math.Round(spread, 3)));

                /* Deflation: den Beitrag dieser Mode aus der Matrix nehmen.
                   Bei einer hermiteschen Matrix ist das S := S − λ·v·v*. */
                if (rank < modes)
                {
                    for (var i = 0; i < n; i++)
                    {
                        for (var j = 0; j < n; j++)
                        {
                            var pr = vRe[i] * vRe[j] + vIm[i] * vIm[j];
                            var pi = vIm[i] * vRe[j] - vRe[i] * vIm[j];

                            sRe[i, j] -= lambda * pr;
                            sIm[i, j] -= lambda * pi;
                        }
                    }
                }
            }
        }

        return outp;
    }

    /// <summary>
    /// Größter Eigenwert und zugehöriger Eigenvektor einer hermiteschen Matrix,
    /// über Potenziteration.
    ///
    /// Nur das führende Paar wird gebraucht, und dafür ist die Potenziteration
    /// das richtige Werkzeug: Ein vollständiges Jacobi-Verfahren über die reelle
    /// Einbettung wäre eine 2N×2N-Zerlegung je Frequenz — bei hundert Werten und
    /// zweihundert Frequenzen mal einundzwanzig Durchläufen ist der Unterschied
    /// der zwischen Sekunden und Stunden.
    /// </summary>
    private static double LeadingEigen(
        double[,] aRe, double[,] aIm, int n,
        double[] vRe, double[] vIm, double[] tRe, double[] tIm)
    {
        // Startvektor mit gleichen Anteilen: neutral gegenüber jedem Wert.
        for (var i = 0; i < n; i++) { vRe[i] = 1.0 / Math.Sqrt(n); vIm[i] = 0; }

        double lambda = 0;

        for (var it = 0; it < 200; it++)
        {
            for (var i = 0; i < n; i++)
            {
                double r = 0, m = 0;

                for (var j = 0; j < n; j++)
                {
                    r += aRe[i, j] * vRe[j] - aIm[i, j] * vIm[j];
                    m += aRe[i, j] * vIm[j] + aIm[i, j] * vRe[j];
                }

                tRe[i] = r;
                tIm[i] = m;
            }

            double norm = 0;
            for (var i = 0; i < n; i++) norm += tRe[i] * tRe[i] + tIm[i] * tIm[i];
            norm = Math.Sqrt(norm);

            if (norm < 1e-300) return 0;

            for (var i = 0; i < n; i++) { vRe[i] = tRe[i] / norm; vIm[i] = tIm[i] / norm; }

            // Rayleigh-Quotient; bei hermitescher Matrix ist er reell.
            double lam = 0;
            for (var i = 0; i < n; i++)
            {
                double r = 0, m = 0;

                for (var j = 0; j < n; j++)
                {
                    r += aRe[i, j] * vRe[j] - aIm[i, j] * vIm[j];
                    m += aRe[i, j] * vIm[j] + aIm[i, j] * vRe[j];
                }

                lam += vRe[i] * r + vIm[i] * m;
            }

            if (it > 4 && Math.Abs(lam - lambda) < 1e-10 * Math.Max(1, Math.Abs(lam)))
            {
                lambda = lam;
                break;
            }

            lambda = lam;
        }

        return lambda;
    }

    /// <summary>
    /// Ersatzreihen mit gewürfelter Phase.
    ///
    /// Jede Reihe wird transformiert, die Phase jeder Frequenz zufällig gedreht
    /// und zurücktransformiert. Das Leistungsspektrum jeder einzelnen Reihe
    /// bleibt dabei <b>exakt</b> erhalten — die Reihe ist hinterher genauso rot
    /// wie vorher, hat dieselbe Autokorrelation und dasselbe Langzeitgedächtnis.
    /// Zerstört wird ausschließlich der Zusammenhang zwischen den Reihen.
    ///
    /// Damit prüft der Vergleich genau die richtige Frage: nicht ob die Reihen
    /// Struktur haben — die haben sie —, sondern ob sie <i>dieselbe</i> haben.
    /// </summary>
    private static List<(int AssetId, string Symbol, double[] Series)> Surrogate(
        IReadOnlyList<(int AssetId, string Symbol, double[] Series)> data, Random rnd)
    {
        var outp = new List<(int, string, double[])>(data.Count);

        foreach (var (id, sym, series) in data)
        {
            var n = series.Length;

            // Ohne Zweierpotenz waere hier aufgefuellt worden -- siehe Analyze.
            var re = (double[])series.Clone();
            var im = new double[n];

            Fft.Forward(re, im);

            /* Die Symmetrie muss erhalten bleiben, sonst ist das Ergebnis
               komplex und die Rücktransformation liefert Unsinn. Deshalb wird
               für jede Frequenz und ihre gespiegelte dieselbe Drehung mit
               entgegengesetztem Vorzeichen angesetzt. */
            for (var k = 1; k < n / 2; k++)
            {
                var phi = rnd.NextDouble() * 2 * Math.PI;
                var c = Math.Cos(phi);
                var s = Math.Sin(phi);

                var r = re[k] * c - im[k] * s;
                var m = re[k] * s + im[k] * c;

                re[k] = r; im[k] = m;
                re[n - k] = r; im[n - k] = -m;
            }

            Fft.Forward(re, im, inverse: true);

            outp.Add((id, sym, re));
        }

        return outp;
    }

    private static MarketModeResult Empty(string note) =>
        new(0, 0, 0, 0, [], note, 0, 0);
}
