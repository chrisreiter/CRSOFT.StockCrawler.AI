namespace Ingest.Core.Analysis;

/// <summary>
/// Eigenwerte und Eigenvektoren symmetrischer Matrizen nach Jacobi.
///
/// Für die hier auftretenden Größen — einige Dutzend bis wenige hundert Zeilen —
/// ist das Verfahren schnell genug und zugleich das numerisch gutmütigste: Es
/// arbeitet ausschließlich mit Drehungen, erhält die Symmetrie in jedem Schritt
/// und braucht weder Startwerte noch Abbruchheuristiken.
///
/// Gebraucht wird es an beiden Enden des Spektrums. Die Modenanalyse nimmt den
/// <b>größten</b> Eigenwert — die Richtung, in der sich alles gemeinsam bewegt.
/// Die Suche nach geschlossenen Gruppen nimmt den <b>kleinsten</b> — die
/// Richtung, in der sich alles gegenseitig aufhebt. Dieselbe Rechnung, die
/// entgegengesetzte Frage.
/// </summary>
public static class SymmetricEigen
{
    /// <summary>
    /// Zerlegt eine symmetrische Matrix. Die Rückgabe ist nach Eigenwert
    /// <b>aufsteigend</b> sortiert — der kleinste zuerst.
    /// </summary>
    public static (double[] Values, double[][] Vectors) Decompose(double[,] a, int n)
    {
        var m = (double[,])a.Clone();
        var v = new double[n, n];

        for (var i = 0; i < n; i++) v[i, i] = 1;

        for (var sweep = 0; sweep < 100; sweep++)
        {
            // Wie weit ist die Matrix noch von der Diagonalgestalt entfernt?
            double off = 0;
            for (var p = 0; p < n; p++)
                for (var q = p + 1; q < n; q++)
                    off += m[p, q] * m[p, q];

            if (off < 1e-22) break;

            for (var p = 0; p < n - 1; p++)
            {
                for (var q = p + 1; q < n; q++)
                {
                    if (Math.Abs(m[p, q]) < 1e-18) continue;

                    var theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                    var t = theta == 0
                        ? 1
                        : Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));

                    var c = 1 / Math.Sqrt(t * t + 1);
                    var s = t * c;

                    for (var k = 0; k < n; k++)
                    {
                        var mkp = m[k, p];
                        var mkq = m[k, q];
                        m[k, p] = c * mkp - s * mkq;
                        m[k, q] = s * mkp + c * mkq;
                    }

                    for (var k = 0; k < n; k++)
                    {
                        var mpk = m[p, k];
                        var mqk = m[q, k];
                        m[p, k] = c * mpk - s * mqk;
                        m[q, k] = s * mpk + c * mqk;
                    }

                    for (var k = 0; k < n; k++)
                    {
                        var vkp = v[k, p];
                        var vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
            }
        }

        var raw = new double[n];
        for (var i = 0; i < n; i++) raw[i] = m[i, i];

        var order = Enumerable.Range(0, n).OrderBy(i => raw[i]).ToArray();

        var values = new double[n];
        var vectors = new double[n][];

        for (var k = 0; k < n; k++)
        {
            values[k] = raw[order[k]];

            var col = new double[n];
            for (var i = 0; i < n; i++) col[i] = v[i, order[k]];

            vectors[k] = col;
        }

        return (values, vectors);
    }
}
