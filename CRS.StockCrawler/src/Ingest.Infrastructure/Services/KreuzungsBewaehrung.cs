using System.Data;
using System.Data.Common;
using Dapper;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Was frühere Kreuzungen eines Paares eingebracht hätten.
///
/// <para>Diese Rechnung ist die einzige Rechtfertigung dafür, überhaupt eine
/// Rangliste nach Gewinn anzuzeigen. Ohne sie zeigt die Liste nur, was schon
/// gelaufen ist, und wer oben einsteigt, kauft nach der Bewegung. Deshalb
/// steht sie an einer Stelle und wird von der Übersicht wie von der
/// Tausch-Seite benutzt — zwei Umsetzungen desselben Maßes wären zwei
/// Gelegenheiten, es unterschiedlich falsch zu machen.</para>
/// </summary>
internal static class KreuzungsBewaehrung
{
    internal sealed class Zeile
    {
        public int A { get; set; }
        public int B { get; set; }
        public int N { get; set; }
        public double Trefferquote { get; set; }
        public double Mittelgewinn { get; set; }
    }

    /// <summary>
    /// Mindestzahl früherer Kreuzungen, ab der eine Trefferquote überhaupt
    /// genannt wird. Bei weniger sagt sie nichts: Bei drei Beobachtungen ist
    /// „zwei von drei" 67 Prozent und trotzdem Rauschen.
    /// </summary>
    internal const int MindestKreuzungen = 8;

    /// <summary>
    /// <paramref name="paare"/> sind die Paare in der Reihenfolge, in der sie
    /// in <c>dbo.crossing</c> stehen (asset_id_a, asset_id_b) — nicht in der
    /// Reihenfolge Kaufen/Verkaufen. Wer das vertauscht, findet nichts.
    /// </summary>
    internal static async Task<Dictionary<(int, int), Zeile>> LadenAsync(
        DbConnection conn, IEnumerable<(int A, int B)> paare,
        string intervalCode, int tage, int haltedauer, CancellationToken ct)
    {
        var liste = paare.Distinct().ToList();
        if (liste.Count == 0) return [];

        var tabelle = new DataTable();
        tabelle.Columns.Add("a", typeof(int));
        tabelle.Columns.Add("b", typeof(int));
        foreach (var (a, b) in liste) tabelle.Rows.Add(a, b);

        var p = new DynamicParameters();
        p.Add("@interval", intervalCode);
        p.Add("@tage", tage);
        p.Add("@halte", haltedauer);
        p.Add("@paare", tabelle.AsTableValuedParameter("dbo.IntPairList"));

        var rows = await conn.QueryAsync<Zeile>(new CommandDefinition(
            Sql, p, commandTimeout: 180, cancellationToken: ct));

        return rows.ToDictionary(r => (r.A, r.B));
    }

    /// <summary>
    /// Gemessen wird über <c>@halte</c> GEMEINSAME Bars, nicht über
    /// Kalendertage: Aktien und Krypto handeln an verschiedenen Tagen, und wer
    /// über das rohe Zeitraster rechnet, misst den Kalender statt den Markt.
    ///
    /// <para><c>c.ts_utc &lt; @seit</c> ist die zweite entscheidende Zeile.
    /// Ohne sie ginge die gerade angezeigte Kreuzung in ihre eigene Bewährung
    /// ein — das Maß benotete sich selbst.</para>
    /// </summary>
    private const string Sql = """
        SET NOCOUNT ON;
        DECLARE @seit DATETIME2(0) = DATEADD(DAY, -@tage, SYSUTCDATETIME());

        WITH reihe AS (
          SELECT p.a, p.b, pa.ts_utc,
                 CAST(pa.[close] AS FLOAT) AS ca, CAST(pb.[close] AS FLOAT) AS cb,
                 ROW_NUMBER() OVER (PARTITION BY p.a, p.b ORDER BY pa.ts_utc) AS rn
            FROM @paare p
            JOIN dbo.price_bar pa ON pa.asset_id = p.a AND pa.interval_code = @interval
                                  AND pa.[close] > 0
            JOIN dbo.price_bar pb ON pb.asset_id = p.b AND pb.interval_code = @interval
                                  AND pb.ts_utc = pa.ts_utc AND pb.[close] > 0
        ),
        mess AS (
          SELECT c.asset_id_a AS a, c.asset_id_b AS b,
                 CASE WHEN c.direction = 1
                      THEN (r1.ca / r0.ca - r1.cb / r0.cb)
                      ELSE (r1.cb / r0.cb - r1.ca / r0.ca)
                 END AS gewinn
            FROM dbo.crossing c
            JOIN @paare p ON p.a = c.asset_id_a AND p.b = c.asset_id_b
            JOIN reihe r0 ON r0.a = c.asset_id_a AND r0.b = c.asset_id_b AND r0.ts_utc = c.ts_utc
            JOIN reihe r1 ON r1.a = c.asset_id_a AND r1.b = c.asset_id_b AND r1.rn = r0.rn + @halte
           WHERE c.interval_code = @interval AND c.ts_utc < @seit
        )
        SELECT a AS A, b AS B, COUNT(*) AS N,
               AVG(CASE WHEN gewinn > 0 THEN 1.0 ELSE 0.0 END) AS Trefferquote,
               AVG(gewinn) AS Mittelgewinn
          FROM mess
         GROUP BY a, b;
        """;
}
