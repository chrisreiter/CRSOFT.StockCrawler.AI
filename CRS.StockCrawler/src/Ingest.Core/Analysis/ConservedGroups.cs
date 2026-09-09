namespace Ingest.Core.Analysis;

/// <summary>Ein Mitglied einer geschlossenen Gruppe.</summary>
/// <param name="Side">
/// Seite der Zirkularbewegung <i>innerhalb</i> der Gruppe. Werte mit
/// entgegengesetztem Vorzeichen sitzen auf entgegengesetzten Seiten der Wippe:
/// Gewinnt die eine Seite Umsatzanteil, verliert die andere.
/// </param>
/// <param name="SwingWeight">
/// Wie stark dieser Wert an der Zirkularbewegung teilnimmt.
/// </param>
public sealed record GroupMember(int AssetId, string Symbol, int Side, double SwingWeight);

/// <summary>Eine Gruppe, deren gemeinsame Umsatzsumme annähernd konstant bleibt.</summary>
/// <param name="Closure">
/// Wie geschlossen die Gruppe ist. Eins bedeutet: Die Summe schwankt genauso
/// stark, wie es unabhängige Werte hergäben — kein Ausgleich. Deutlich unter
/// eins bedeutet: Was der eine verliert, gewinnt der andere.
/// </param>
/// <param name="OutOfSampleClosure">
/// Dasselbe Maß in einem Zeitraum, den die Suche nie gesehen hat.
/// <b>Nur diese Zahl zählt.</b> Eine Gruppe, die sich nur dort ausgleicht, wo
/// sie gefunden wurde, ist eine Anpassung an die Vergangenheit.
/// </param>
/// <param name="SwingShare">
/// Anteil der verbliebenen Bewegung, den die Zirkularbewegung innerhalb der
/// Gruppe erklärt.
/// </param>
public sealed record ConservedGroup(
    int Rank,
    int Size,
    double Closure,
    double OutOfSampleClosure,
    double NullClosure,
    double SwingShare,
    bool Holds,
    IReadOnlyList<GroupMember> Members);

/// <summary>
/// Sucht Gruppen von Werten, deren gemeinsame Umsatzsumme annähernd konstant
/// bleibt — und bestimmt darin die Zirkularbewegung.
///
/// <b>Der Gedanke.</b> Bleibt der Gesamtmarkt in Summe ungefähr gleich und
/// verschieben sich die Umsätze nur innerhalb, dann müsste es Teilmengen geben,
/// die für sich genommen geschlossen sind. Innerhalb einer solchen Gruppe ist
/// die Zuordnung von Verlierern zu Gewinnern keine Annahme mehr, sondern folgt
/// aus der Sache: Was dort verschwindet, muss dort wieder auftauchen.
///
/// <b>Zwei Schritte, zwei verschiedene Fragen.</b>
///
/// <list type="number">
/// <item><b>Welche Werte bilden eine geschlossene Gruppe?</b> Gesucht ist eine
/// Teilmenge, deren <i>Summe</i> möglichst wenig schwankt. Die Gewichte sind
/// dabei alle positiv — eine Summe ist eine Summe. Das ist ein
/// Auswahlproblem und wird schrittweise gelöst: vom gegenläufigsten Paar
/// ausgehend wird jeweils der Wert aufgenommen, der die Schwankung der Summe am
/// stärksten senkt.</item>
/// <item><b>Wie zirkuliert es darin?</b> Erst jetzt sind gemischte Vorzeichen
/// richtig: Der kleinste Eigenvektor <i>innerhalb der gefundenen Gruppe</i>
/// zeigt, wer gegen wen läuft.</item>
/// </list>
///
/// <b>Warum nicht gleich der kleinste Eigenvektor über alles.</b> Das war der
/// erste Ansatz und beantwortet eine andere Frage. Ein Eigenvektor darf
/// Vorzeichen mischen, und die am besten ausgleichende Kombination ist deshalb
/// fast immer die <i>Differenz zweier nahezu gleicher</i> Werte — bei zwei
/// Papieren desselben Konzerns etwa. Das hebt sich hervorragend auf und ist
/// keine geschlossene Gruppe, sondern ein Zwillingspaar.
///
/// <b>Die zweite Falle: Standardisierung.</b> Ohne sie fände das Verfahren
/// nicht Werte, die einander ausgleichen, sondern Werte, die sich schlicht kaum
/// bewegen — vier illiquide Papiere haben eine sehr konstante Summe und sagen
/// nichts aus. Nach der Standardisierung kann eine niedrige Summenschwankung
/// nur noch durch tatsächliche Gegenläufigkeit entstehen.
/// </summary>
public static class ConservedGroups
{
    /// <summary>
    /// Findet geschlossene Gruppen.
    /// </summary>
    /// <param name="shares">Je Wert der Anteil am Gesamtumsatz über ein gemeinsames Raster.</param>
    /// <param name="splitAt">
    /// Trennstelle zwischen Such- und Prüfzeitraum. Die Gruppen entstehen
    /// ausschließlich aus dem ersten Teil.
    /// </param>
    public static List<ConservedGroup> Find(
        IReadOnlyList<(int AssetId, string Symbol, double[] Share)> shares,
        int splitAt,
        int groups = 3,
        int maxSize = 12,
        int nullDraws = 300,
        int seed = 20260821)
    {
        var result = new List<ConservedGroup>();

        var n = shares.Count;
        if (n < 6) return result;

        var len = shares[0].Share.Length;
        if (splitAt < 129 || len - splitAt < 129) return result;

        /* Gerechnet wird auf den Änderungen, nicht auf den Ständen. „Die Summe
           bleibt konstant" heißt „die Summe der Änderungen ist null“ — und
           Änderungen sind frei von Niveau und Trend, an denen sich ein
           Ausgleich sonst nur scheinbar zeigt. */
        var diffs = new double[n][];

        for (var i = 0; i < n; i++)
        {
            var s = shares[i].Share;
            var d = new double[len - 1];

            for (var t = 1; t < len; t++) d[t - 1] = s[t] - s[t - 1];

            diffs[i] = d;
        }

        var cut = splitAt - 1;

        var train = Standardize(diffs, 0, cut);
        var test = Standardize(diffs, cut, len - 1);

        if (train is null || test is null) return result;

        var corr = Correlation(train, n);

        var rnd = new Random(seed);
        var used = new HashSet<int>();

        for (var g = 0; g < groups; g++)
        {
            var set = Grow(train, corr, n, used, maxSize);
            if (set.Count < 3) break;

            foreach (var i in set) used.Add(i);

            var inSample = Closure(train, set);
            var outSample = Closure(test, set);

            /* Der Nulltest: gleich viele, aber zufällig gezogene Werte. Ohne
               diesen Bezug ist eine Geschlossenheit von 0,7 weder gut noch
               schlecht, sondern nur eine Zahl. */
            double nullSum = 0;

            for (var draw = 0; draw < nullDraws; draw++)
            {
                var pick = new HashSet<int>();
                while (pick.Count < set.Count) pick.Add(rnd.Next(n));

                nullSum += Closure(test, pick.ToList());
            }

            var nullMean = nullSum / nullDraws;

            var (swing, swingShare) = Circulation(train, set);

            var members = new List<GroupMember>(set.Count);

            for (var k = 0; k < set.Count; k++)
            {
                var i = set[k];

                members.Add(new GroupMember(
                    shares[i].AssetId, shares[i].Symbol,
                    Math.Sign(swing[k]), Math.Round(Math.Abs(swing[k]), 4)));
            }

            result.Add(new ConservedGroup(
                g + 1, set.Count,
                Math.Round(inSample, 4),
                Math.Round(outSample, 4),
                Math.Round(nullMean, 4),
                Math.Round(swingShare, 4),

                // Sie hält, wenn sie sich auch im Prüfzeitraum deutlich besser
                // ausgleicht als eine beliebige Gruppe gleicher Größe.
                outSample < nullMean * 0.85,

                members.OrderByDescending(m => m.SwingWeight).ToList()));
        }

        return result;
    }

    /// <summary>
    /// Baut eine Gruppe schrittweise auf: vom gegenläufigsten Paar ausgehend
    /// jeweils der Wert, der die Schwankung der Summe am stärksten senkt.
    ///
    /// Bewusst schrittweise und nicht vollständig durchsucht: Bei zweihundert
    /// Werten gibt es mehr Teilmengen als Atome in der Milchstraße. Das
    /// schrittweise Vorgehen findet nicht garantiert das Beste, aber es findet
    /// verlässlich Gutes — und ob das Gefundene etwas taugt, entscheidet
    /// ohnehin der Prüfzeitraum und nicht die Suchgüte.
    /// </summary>
    private static List<int> Grow(double[][] data, double[,] corr, int n,
                                  HashSet<int> excluded, int maxSize)
    {
        var best = (a: -1, b: -1, c: double.MaxValue);

        for (var i = 0; i < n; i++)
        {
            if (excluded.Contains(i)) continue;

            for (var j = i + 1; j < n; j++)
            {
                if (excluded.Contains(j)) continue;
                if (corr[i, j] < best.c) best = (i, j, corr[i, j]);
            }
        }

        if (best.a < 0) return [];

        var set = new List<int> { best.a, best.b };
        var current = Closure(data, set);

        while (set.Count < maxSize)
        {
            var pick = -1;
            var pickVal = current;

            for (var i = 0; i < n; i++)
            {
                if (excluded.Contains(i) || set.Contains(i)) continue;

                set.Add(i);
                var v = Closure(data, set);
                set.RemoveAt(set.Count - 1);

                if (v < pickVal) { pickVal = v; pick = i; }
            }

            // Kein Wert verbessert die Geschlossenheit mehr -- die Gruppe steht.
            if (pick < 0) break;

            set.Add(pick);
            current = pickVal;
        }

        return set;
    }

    /// <summary>
    /// Geschlossenheit: Streuung der Summe im Verhältnis zu dem, was
    /// unabhängige Werte hergäben.
    ///
    /// Bei unabhängigen, standardisierten Reihen wächst die Streuung der Summe
    /// mit der Wurzel aus ihrer Zahl. Geteilt durch genau diese Wurzel ergibt
    /// sich eins für Unabhängigkeit — und alles darunter ist Ausgleich.
    /// </summary>
    private static double Closure(double[][] data, IReadOnlyList<int> set)
    {
        if (set.Count < 2) return 1;

        var len = data[0].Length;
        double sumSq = 0;

        for (var t = 0; t < len; t++)
        {
            double s = 0;
            foreach (var i in set) s += data[i][t];

            sumSq += s * s;
        }

        return Math.Sqrt(sumSq / len) / Math.Sqrt(set.Count);
    }

    /// <summary>
    /// Die Zirkularbewegung innerhalb einer Gruppe: der kleinste Eigenvektor
    /// der Korrelationsmatrix, auf die Gruppe beschränkt.
    ///
    /// Hier sind gemischte Vorzeichen genau richtig — sie sind die Antwort.
    /// Der zweite Rückgabewert sagt, wie viel der Bewegung innerhalb der Gruppe
    /// diese eine Wippe erklärt.
    /// </summary>
    private static (double[] Weights, double Share) Circulation(double[][] data, IReadOnlyList<int> set)
    {
        var m = set.Count;
        var c = new double[m, m];

        var len = data[0].Length;

        for (var a = 0; a < m; a++)
        {
            for (var b = a; b < m; b++)
            {
                double s = 0;
                for (var t = 0; t < len; t++) s += data[set[a]][t] * data[set[b]][t];

                c[a, b] = s / len;
                c[b, a] = c[a, b];
            }
        }

        var (values, vectors) = SymmetricEigen.Decompose(c, m);

        /* Die Spur einer Korrelationsmatrix ist die Zahl ihrer Zeilen. Der
           Anteil der stärksten Gegenbewegung ist deshalb der größte Eigenwert
           geteilt durch m -- nicht der kleinste: Der kleinste beschreibt, was
           sich aufhebt, der größte, was sich gemeinsam bewegt. Für die Frage
           „wie stark ist die Wippe“ zählt der zweitgrößte, denn der größte ist
           der gemeinsame Anteil. */
        var swing = m >= 2 ? vectors[m - 2] : vectors[m - 1];
        var share = m >= 2 ? values[m - 2] / m : 0;

        return (swing, share);
    }

    private static double[,] Correlation(double[][] data, int n)
    {
        var c = new double[n, n];
        var len = data[0].Length;

        for (var i = 0; i < n; i++)
        {
            for (var j = i; j < n; j++)
            {
                double s = 0;
                for (var t = 0; t < len; t++) s += data[i][t] * data[j][t];

                c[i, j] = s / len;
                c[j, i] = c[i, j];
            }
        }

        return c;
    }

    private static double[][]? Standardize(double[][] diffs, int from, int to)
    {
        var n = diffs.Length;
        var len = to - from;

        if (len < 64) return null;

        var outp = new double[n][];

        for (var i = 0; i < n; i++)
        {
            var x = new double[len];
            Array.Copy(diffs[i], from, x, 0, len);

            double mean = 0;
            foreach (var v in x) mean += v;
            mean /= len;

            double var2 = 0;
            for (var t = 0; t < len; t++)
            {
                x[t] -= mean;
                var2 += x[t] * x[t];
            }

            var sd = Math.Sqrt(var2 / len);

            // Eine Reihe ohne Bewegung trüge nichts bei und teilte durch null.
            if (sd < 1e-15) return null;

            for (var t = 0; t < len; t++) x[t] /= sd;

            outp[i] = x;
        }

        return outp;
    }
}
