using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Bestand des Nutzers und die daraus folgenden Tauschvorschläge.
///
/// <para><b>Die Frage, die diese Seite beantwortet.</b> „Ich stecke mit
/// soundsoviel Kapital in diesem Wert — gegen welchen sollte ich tauschen?"
/// Gesucht wird deshalb nicht die stärkste Kurve überhaupt, sondern die
/// Gegenseite einer Kreuzung, bei der der GEHALTENE Wert die untere Seite
/// geworden ist.</para>
///
/// <para><b>Was hier bewusst nicht steht.</b> Kein Vorschlag behauptet, was
/// kommen wird. Jede Zahl ist Vergangenheit: was der Tausch seit dem Kippen
/// eingebracht hätte, und wie oft frühere Kreuzungen desselben Paares
/// gehalten haben. Die zweite Zahl ist die wichtigere, und sie fällt in
/// diesem Datenbestand meist nahe an den Münzwurf — das steht so in der
/// Antwort und nicht im Kleingedruckten.</para>
/// </summary>
public sealed class PortfolioService(ISqlConnectionFactory factory) : IPortfolioService
{
    /// <inheritdoc cref="CrossingOpportunityService"/>
    private const double SprungAktie = 0.40;
    private const double SprungKrypto = 0.90;

    // ------------------------------------------------------------- Bestand --

    public async Task<IReadOnlyList<Holding>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<Holding>(new CommandDefinition("""
            SELECT h.holding_id AS HoldingId, h.asset_id AS AssetId,
                   a.symbol AS Symbol, a.name AS Name, a.asset_class AS Klasse,
                   h.kapital AS Kapital, h.waehrung AS Waehrung,
                   h.einstand AS Einstand, h.gekauft_utc AS GekauftUtc,
                   h.notiz AS Notiz, h.updated_utc AS UpdatedUtc,
                   k.[close] AS Kurs, k.ts_utc AS KursUtc
              FROM dbo.holding h
              JOIN dbo.asset a ON a.asset_id = h.asset_id
              OUTER APPLY (SELECT TOP 1 p.[close], p.ts_utc
                             FROM dbo.price_bar p
                            WHERE p.asset_id = h.asset_id AND p.interval_code = '1d'
                            ORDER BY p.ts_utc DESC) k
             ORDER BY h.kapital DESC
            """, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<Holding?> UpsertAsync(string symbol, decimal kapital, string waehrung,
                                            decimal? einstand, DateTime? gekauftUtc, string? notiz,
                                            CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol) || kapital <= 0) return null;

        await using var conn = await factory.OpenAsync(ct);

        var id = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT asset_id FROM dbo.asset WHERE symbol = @s",
            new { s = symbol.Trim() }, cancellationToken: ct));

        if (id is null) return null;

        await conn.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.holding WITH (HOLDLOCK) AS t
            USING (SELECT @id AS asset_id) AS s ON t.asset_id = s.asset_id
            WHEN MATCHED THEN UPDATE SET
                 kapital = @kapital, waehrung = @waehrung, einstand = @einstand,
                 gekauft_utc = @gekauft, notiz = @notiz, updated_utc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                 INSERT (asset_id, kapital, waehrung, einstand, gekauft_utc, notiz)
                 VALUES (@id, @kapital, @waehrung, @einstand, @gekauft, @notiz);
            """,
            new { id, kapital, waehrung, einstand, gekauft = gekauftUtc, notiz },
            cancellationToken: ct));

        return (await ListAsync(ct)).FirstOrDefault(h => h.AssetId == id);
    }

    public async Task<bool> DeleteAsync(int assetId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.holding WHERE asset_id = @id",
            new { id = assetId }, cancellationToken: ct)) > 0;
    }

    // ----------------------------------------------------------- Tausch ------

    public async Task<SwapUebersicht> SwapsAsync(string intervalCode, int tage, int haltedauer,
                                                 int jeBestand, bool nurBewaehrt,
                                                 CancellationToken ct = default)
    {
        tage = Math.Clamp(tage, 1, 365);
        haltedauer = Math.Clamp(haltedauer, 1, 250);
        jeBestand = Math.Clamp(jeBestand, 1, 20);

        var kosten = Handelskosten.Standard;
        var bestand = await ListAsync(ct);
        var kapitalGesamt = bestand.Sum(h => h.Kapital);

        if (bestand.Count == 0)
            return new SwapUebersicht([], 0, 0, 0, kosten.Rundlauf, kosten.Beschreibung,
                intervalCode, tage, haltedauer, DateTime.UtcNow,
                "Noch kein Bestand eingetragen. Ohne Positionen gibt es nichts zu tauschen.");

        await using var conn = await factory.OpenAsync(ct);

        var p = new DynamicParameters();
        p.Add("@interval", intervalCode);
        p.Add("@tage", tage);
        p.Add("@sprung_aktie", SprungAktie);
        p.Add("@sprung_krypto", SprungKrypto);

        var roh = (await conn.QueryAsync<RohTausch>(new CommandDefinition(
            TauschSql, p, commandTimeout: 180, cancellationToken: ct))).ToList();

        var bewaehrung = await KreuzungsBewaehrung.LadenAsync(
            conn, roh.Select(r => (r.AssetIdA, r.AssetIdB)), intervalCode, tage, haltedauer, ct);

        var jeAsset = roh.GroupBy(r => r.BestandId).ToDictionary(g => g.Key, g => g.ToList());
        var positionen = new List<SwapFuerPosition>(bestand.Count);
        var gesamt = 0;
        var bewaehrt = 0;

        foreach (var h in bestand)
        {
            var kandidaten = jeAsset.GetValueOrDefault(h.AssetId, []);

            var vorschlaege = kandidaten
                .OrderByDescending(r => r.Paargewinn)
                .Select(r =>
                {
                    bewaehrung.TryGetValue((r.AssetIdA, r.AssetIdB), out var b);
                    var genug = b is { N: >= KreuzungsBewaehrung.MindestKreuzungen };

                    return new SwapVorschlag(
                        SymbolBestand: h.Symbol,
                        SymbolZiel: r.SymbolZiel,
                        NameZiel: r.NameZiel ?? "",
                        KlasseZiel: (AssetClass)r.KlasseZiel,
                        Kreuzung: r.TsUtc,
                        TageSeither: (int)Math.Round((DateTime.UtcNow - r.TsUtc).TotalDays),
                        RenditeBestand: r.RenditeBestand,
                        RenditeZiel: r.RenditeZiel,
                        PaargewinnSeither: r.Paargewinn,
                        Kapital: h.Kapital,
                        WaereGeworden: Math.Round(h.Kapital * (decimal)(1 + r.Paargewinn), 2),
                        HistorischeKreuzungen: b?.N ?? 0,
                        HistorischeTrefferquote: genug ? b!.Trefferquote : null,
                        HistorischerMittelgewinn: genug ? b!.Mittelgewinn : null,
                        Rundlaufkosten: kosten.Rundlauf,
                        /* Drei Bedingungen, nicht zwei: genug Vorgeschichte, eine
                           Trefferquote über dem Münzwurf UND ein mittlerer Ertrag
                           über den Kosten. Die dritte fehlte zuerst, und ohne sie
                           galten Paare als bewährt, die im Mittel Geld kosten. */
                        Bewaehrt: genug && b!.Trefferquote >= 0.55
                                  && b.Mittelgewinn > kosten.Rundlauf);
                })
                .Where(v => !nurBewaehrt || v.Bewaehrt)
                .Take(jeBestand)
                .ToList();

            gesamt += vorschlaege.Count;
            bewaehrt += vorschlaege.Count(v => v.Bewaehrt);

            positionen.Add(new SwapFuerPosition(
                h.Symbol, h.Name ?? "", h.Klasse, h.Kapital, h.Waehrung, vorschlaege,
                Lage(vorschlaege, tage)));
        }

        return new SwapUebersicht(
            positionen, kapitalGesamt, gesamt, bewaehrt,
            kosten.Rundlauf, kosten.Beschreibung,
            intervalCode, tage, haltedauer, DateTime.UtcNow,
            Hinweis(gesamt, bewaehrt, haltedauer, kosten));
    }

    /// <summary>Ein Satz je Position — was für sie gefunden wurde.</summary>
    private static string Lage(List<SwapVorschlag> v, int tage) => v.Count switch
    {
        0 => $"Keine Kreuzung in den letzten {tage} Tagen, bei der dieser Wert die untere Seite "
             + "geworden wäre. Das ist kein Halte-Signal — es heißt nur, dass hier nichts kippte.",
        _ when v.All(x => !x.Bewaehrt) =>
            $"{v.Count} Kreuzung(en) gefunden, aber keine mit Rückhalt: Bei den früheren "
            + "Kreuzungen dieser Paare blieb die obere Seite nicht häufiger vorne als der Zufall.",
        _ => $"{v.Count(x => x.Bewaehrt)} von {v.Count} Vorschlägen haben Rückhalt aus früheren "
             + "Kreuzungen desselben Paares."
    };

    private static string Hinweis(int gesamt, int bewaehrt, int haltedauer,
                                 Handelskosten kosten) => gesamt switch
    {
        0 => "Zu keiner Position wurde eine Kreuzung gefunden, bei der sie die untere Seite "
             + "geworden wäre.",
        _ when bewaehrt == 0 =>
            "**Kein Vorschlag hat Rückhalt.** Alle Zahlen unten sind Vergangenheit: was der "
            + "Tausch seit dem Kippen eingebracht hätte. Frühere Kreuzungen derselben Paare "
            + "haben nicht häufiger gehalten als ein Münzwurf — was hier steht, ist eine "
            + "Beobachtung, keine Handlungsanweisung.",
        _ => $"{bewaehrt} von {gesamt} Vorschlägen haben Rückhalt: Bei den früheren Kreuzungen "
             + $"desselben Paares blieb die obere Seite über {haltedauer} gemeinsame Bars in mehr "
             + $"als 55 Prozent der Fälle vorne, und der mittlere Ertrag lag über den Kosten "
             + $"({kosten.Beschreibung}). Alles andere unten ist Vergangenheit ohne Beleg."
    };

    // ------------------------------------------------------------ Abfrage ----

    /// <summary>
    /// Kreuzungen von der Seite des gehaltenen Wertes aus.
    ///
    /// <para>Entscheidend ist, dass je Paar die JÜNGSTE Kreuzung genommen und
    /// erst danach geprüft wird, ob der gehaltene Wert die untere Seite ist.
    /// Andersherum — erst filtern, dann die jüngste nehmen — bekäme man ein
    /// Verkaufssignal, das eine spätere Gegenkreuzung längst aufgehoben
    /// hat.</para>
    /// </summary>
    private const string TauschSql = """
        SET NOCOUNT ON;
        DECLARE @seit DATETIME2(0) = DATEADD(DAY, -@tage, SYSUTCDATETIME());

        SELECT asset_id INTO #bestand FROM dbo.holding;

        SELECT x.asset_id_a, x.asset_id_b, x.ts_utc, x.direction
          INTO #letzte
          FROM (SELECT c.asset_id_a, c.asset_id_b, c.ts_utc, c.direction,
                       ROW_NUMBER() OVER (PARTITION BY c.asset_id_a, c.asset_id_b
                                          ORDER BY c.ts_utc DESC) AS rn
                  FROM dbo.crossing c
                 WHERE c.interval_code = @interval AND c.ts_utc >= @seit
                   AND (EXISTS (SELECT 1 FROM #bestand b WHERE b.asset_id = c.asset_id_a)
                     OR EXISTS (SELECT 1 FROM #bestand b WHERE b.asset_id = c.asset_id_b))) x
         WHERE x.rn = 1;

        /* Die untere Seite ist die gehaltene — sonst gibt es nichts zu tauschen. */
        SELECT l.asset_id_a, l.asset_id_b, l.ts_utc,
               CASE WHEN l.direction = 1 THEN l.asset_id_b ELSE l.asset_id_a END AS bestand_id,
               CASE WHEN l.direction = 1 THEN l.asset_id_a ELSE l.asset_id_b END AS ziel_id
          INTO #kandidat
          FROM #letzte l
         WHERE EXISTS (SELECT 1 FROM #bestand b
                        WHERE b.asset_id = CASE WHEN l.direction = 1
                                                THEN l.asset_id_b ELSE l.asset_id_a END);

        /* Der jüngste Zeitpunkt, zu dem BEIDE gehandelt haben. */
        SELECT k.bestand_id, k.ziel_id, MAX(pa.ts_utc) AS ts_jetzt
          INTO #jetzt
          FROM #kandidat k
          JOIN dbo.price_bar pa ON pa.asset_id = k.bestand_id AND pa.interval_code = @interval
                                AND pa.ts_utc >= DATEADD(DAY, -15, SYSUTCDATETIME())
          JOIN dbo.price_bar pb ON pb.asset_id = k.ziel_id AND pb.interval_code = @interval
                                AND pb.ts_utc = pa.ts_utc
         GROUP BY k.bestand_id, k.ziel_id;

        SELECT s.asset_id, MAX(ABS(LOG(s.c / s.vor))) AS max_sprung
          INTO #sprung
          FROM (SELECT p.asset_id, p.[close] AS c,
                       LAG(p.[close]) OVER (PARTITION BY p.asset_id ORDER BY p.ts_utc) AS vor
                  FROM dbo.price_bar p
                  JOIN (SELECT bestand_id AS id FROM #kandidat
                        UNION SELECT ziel_id FROM #kandidat) w ON w.id = p.asset_id
                 WHERE p.interval_code = @interval
                   AND p.ts_utc >= DATEADD(DAY, -(@tage + 5), SYSUTCDATETIME())
                   AND p.[close] > 0) s
         WHERE s.vor > 0
         GROUP BY s.asset_id;

        SELECT k.asset_id_a AS AssetIdA, k.asset_id_b AS AssetIdB,
               k.bestand_id AS BestandId, k.ziel_id AS ZielId,
               az.symbol AS SymbolZiel, az.name AS NameZiel, az.asset_class AS KlasseZiel,
               k.ts_utc AS TsUtc,
               CAST(nb.[close] AS FLOAT) / kb.[close] - 1 AS RenditeBestand,
               CAST(nz.[close] AS FLOAT) / kz.[close] - 1 AS RenditeZiel,
               CAST(nz.[close] AS FLOAT) / kz.[close] - CAST(nb.[close] AS FLOAT) / kb.[close]
                 AS Paargewinn
          FROM #kandidat k
          JOIN #jetzt j ON j.bestand_id = k.bestand_id AND j.ziel_id = k.ziel_id
          JOIN dbo.asset ab ON ab.asset_id = k.bestand_id
          JOIN dbo.asset az ON az.asset_id = k.ziel_id
          JOIN dbo.price_bar kb ON kb.asset_id = k.bestand_id AND kb.interval_code = @interval
                                AND kb.ts_utc = k.ts_utc AND kb.[close] > 0
          JOIN dbo.price_bar kz ON kz.asset_id = k.ziel_id AND kz.interval_code = @interval
                                AND kz.ts_utc = k.ts_utc AND kz.[close] > 0
          JOIN dbo.price_bar nb ON nb.asset_id = k.bestand_id AND nb.interval_code = @interval
                                AND nb.ts_utc = j.ts_jetzt AND nb.[close] > 0
          JOIN dbo.price_bar nz ON nz.asset_id = k.ziel_id AND nz.interval_code = @interval
                                AND nz.ts_utc = j.ts_jetzt AND nz.[close] > 0
          LEFT JOIN #sprung sb ON sb.asset_id = k.bestand_id
          LEFT JOIN #sprung sz ON sz.asset_id = k.ziel_id
         WHERE ISNULL(sb.max_sprung, 0)
                 < CASE WHEN ab.asset_class = 2 THEN @sprung_krypto ELSE @sprung_aktie END
           AND ISNULL(sz.max_sprung, 0)
                 < CASE WHEN az.asset_class = 2 THEN @sprung_krypto ELSE @sprung_aktie END;
        """;

    private sealed class RohTausch
    {
        public int AssetIdA { get; set; }
        public int AssetIdB { get; set; }
        public int BestandId { get; set; }
        public int ZielId { get; set; }
        public string SymbolZiel { get; set; } = "";
        public string? NameZiel { get; set; }
        public byte KlasseZiel { get; set; }
        public DateTime TsUtc { get; set; }
        public double RenditeBestand { get; set; }
        public double RenditeZiel { get; set; }
        public double Paargewinn { get; set; }
    }
}
