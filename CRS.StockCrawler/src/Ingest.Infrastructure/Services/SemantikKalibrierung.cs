using Dapper;
using Ingest.Core.Analysis;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>Was die Kalibrierung einer Säule für einen Horizont ergeben hat.</summary>
public sealed record Kalibrierergebnis(
    int HorizonHours, int NObs, double Korrelation, double Faktor,
    double Schwelle, double Skill, string Befund);

public interface ISemantikKalibrierung
{
    Task<IReadOnlyList<Kalibrierergebnis>> LaufeAsync(
        IReadOnlyList<int> horizonte, int tageZurueck, CancellationToken ct = default);
}

/// <summary>
/// Misst, ob Nachrichtenstimmung einen Kurs vorhersagt — und mit welchem Faktor.
///
/// <para><b>Warum das nicht weggelassen werden darf.</b> Aus Text lässt sich mühelos eine
/// Zahl zwischen −1 und +1 gewinnen. Sie in eine Prognose zu geben, ist genau ein Schritt
/// weiter — und genau der Schritt, der Signal erfindet, wo keines ist. Zwischen „die
/// Nachrichten sind positiv" und „der Kurs steigt um x Prozent" steht ein Faktor, den
/// niemand raten darf.</para>
///
/// <para><b>Wie gemessen wird.</b> Für jeden Tag im Archiv und jeden Wert wird die Stimmung
/// aus den Meldungen <i>dieses Tages</i> berechnet und gegen die <i>danach</i> eingetroffene
/// Rendite über den Horizont gehalten. Die Steigung der Regression ist der Faktor, die
/// Korrelation der Rückhalt.</para>
///
/// <para><b>Nur Meldungen bis zum Stichtag.</b> Das klingt selbstverständlich und ist die
/// häufigste Art, sich Lookahead einzubauen: Wer die Stimmung eines Tages aus dem gesamten
/// Archiv berechnet, benutzt Meldungen, die es zum Zeitpunkt der Prognose nicht gab, und
/// bekommt einen prächtigen Zusammenhang, der im Betrieb verschwindet.</para>
///
/// <para><b>Die Schwelle.</b> Eine Korrelation muss die Zufallsschwelle überschreiten, sonst
/// ist sie keine. Angesetzt wird 2/√n — bei 648 Beobachtungen sind das 0,079, und genau
/// dort lag die GDELT-Messung mit 0,0419 darunter. Wer diese Latte reisst, bekommt Skill
/// null und trägt nichts bei, egal wie deutlich die Stimmung ist.</para>
/// </summary>
public sealed class SemantikKalibrierung : ISemantikKalibrierung
{
    private readonly ISqlConnectionFactory _factory;
    private readonly ILogger<SemantikKalibrierung> _log;

    public SemantikKalibrierung(ISqlConnectionFactory factory, ILogger<SemantikKalibrierung> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task<IReadOnlyList<Kalibrierergebnis>> LaufeAsync(
        IReadOnlyList<int> horizonte, int tageZurueck, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var werte = (await conn.QueryAsync<(int AssetId, string Symbol, string? Name)>(
            new CommandDefinition(
                "SELECT asset_id, symbol, name FROM dbo.asset WHERE is_tracked = 1",
                cancellationToken: ct))).ToList();

        var index = BaueIndex(werte);

        /* Alle Meldungen des Zeitraums mit ihrem Zeitstempel. Der Stichtag entsteht
           daraus, nicht umgekehrt -- so kann keine Meldung in einen Tag geraten, an dem
           es sie noch nicht gab. */
        var meldungen = (await conn.QueryAsync<Meldung>(new CommandDefinition(
            """
            SELECT c.content AS Text, ISNULL(c.occurred_utc, s.published_utc) AS AlsUtc
              FROM dbo.knowledge_chunk c
              JOIN dbo.knowledge_source s ON s.source_id = c.source_id
             WHERE s.pillar = 'semantic'
               AND ISNULL(c.occurred_utc, s.published_utc) >= DATEADD(day, -@tage, SYSUTCDATETIME())
               AND c.content IS NOT NULL
            """, new { tage = tageZurueck }, commandTimeout: 300, cancellationToken: ct))).ToList();

        if (meldungen.Count == 0)
        {
            _log.LogWarning("Semantik-Kalibrierung: keine Meldungen im Zeitraum");
            return [];
        }

        // Je Tag und Wert die Stimmung — aus den Meldungen BIS zu diesem Tag.
        var stimmungen = new Dictionary<(DateTime Tag, int AssetId), double>();

        var tage = meldungen.Select(m => m.AlsUtc.Date).Distinct().OrderBy(d => d).ToList();

        foreach (var tag in tage)
        {
            /* Ein Tagesfenster: Meldungen dieses Tages. Nicht kumulativ -- die
               Halbwertszeit im Betrieb ist ein Tag, und die Kalibrierung muss dasselbe
               Signal messen, das später benutzt wird. */
            var desTages = meldungen.Where(m => m.AlsUtc.Date == tag).ToList();

            var jeWert = Zuordnen(desTages, index);

            foreach (var (assetId, liste) in jeWert)
            {
                var s = Nachrichtenstimmung.Fasse(liste, tag.AddDays(1));
                if (s.Treffer > 0) stimmungen[(tag, assetId)] = s.Wert;
            }
        }

        _log.LogInformation("Semantik-Kalibrierung: {N} Stimmungswerte über {T} Tage",
                            stimmungen.Count, tage.Count);

        var ergebnisse = new List<Kalibrierergebnis>();

        foreach (var h in horizonte)
        {
            ct.ThrowIfCancellationRequested();

            /* Die eingetroffene Rendite je Wert und Stichtag. Bewusst über price_bar und
               nicht über forecast_score: Hier wird die Säule gegen die WIRKLICHKEIT
               gemessen, nicht gegen eine andere Prognose. */
            var renditen = (await conn.QueryAsync<Rendite>(new CommandDefinition(
                """
                WITH b AS (
                    SELECT asset_id, ts_utc, CAST([close] AS float) AS c,
                           ROW_NUMBER() OVER (PARTITION BY asset_id ORDER BY ts_utc) AS rn
                      FROM dbo.price_bar
                     WHERE interval_code = '1d'
                       AND ts_utc >= DATEADD(day, -@tage - 40, SYSUTCDATETIME())
                       AND [close] > 0
                )
                SELECT b.asset_id AS AssetId, CAST(b.ts_utc AS date) AS Tag,
                       LOG(z.c / b.c) AS LogRendite
                  FROM b
                  JOIN b z ON z.asset_id = b.asset_id AND z.rn = b.rn + @schritte
                 WHERE b.ts_utc >= DATEADD(day, -@tage, SYSUTCDATETIME())
                """,
                new { tage = tageZurueck, schritte = Math.Max(1, h / 24) },
                commandTimeout: 300, cancellationToken: ct))).ToList();

            var paare = new List<(double S, double R)>();

            foreach (var r in renditen)
                if (stimmungen.TryGetValue((r.Tag, r.AssetId), out var s))
                    paare.Add((s, r.LogRendite));

            if (paare.Count < 30)
            {
                ergebnisse.Add(new Kalibrierergebnis(
                    h, paare.Count, 0, 0, 0, 0,
                    $"Nur {paare.Count} Paare — zu wenige. Verdienst null; die Säule trägt "
                    + "nichts bei, bis genug Meldungen mit Kurshistorie zusammenkommen."));
                continue;
            }

            var (korr, steigung) = Regression(paare);

            /* 2/√n als Zufallsschwelle. Nicht streng im Sinne einer Testtheorie, aber
               die Grössenordnung stimmt und sie ist ohne Tabelle nachrechenbar. */
            var schwelle = 2.0 / Math.Sqrt(paare.Count);

            var traegt = Math.Abs(korr) > schwelle;

            /* Skill wächst erst OBERHALB der Schwelle. Sonst bekäme eine Korrelation von
               0,05 bei grosser Fallzahl bereits Gewicht, obwohl sie Rauschen ist. */
            var skill = traegt
                ? Math.Clamp((Math.Abs(korr) - schwelle) / 0.10, 0, 1)
                : 0;

            ergebnisse.Add(new Kalibrierergebnis(
                h, paare.Count, Math.Round(korr, 4), traegt ? Math.Round(steigung, 6) : 0,
                Math.Round(schwelle, 4), Math.Round(skill, 4),
                traegt
                    ? $"Korrelation {korr:F4} über {paare.Count} Paare, Schwelle {schwelle:F4} "
                      + $"— trägt. Faktor {steigung:F6}."
                    : $"Korrelation {korr:F4} über {paare.Count} Paare liegt unter der "
                      + $"Zufallsschwelle {schwelle:F4} — kein Rückhalt, Verdienst null."));
        }

        await SchreibeAsync(conn, ergebnisse, ct);

        return ergebnisse;
    }

    private async Task SchreibeAsync(Microsoft.Data.SqlClient.SqlConnection conn,
                                     IReadOnlyList<Kalibrierergebnis> e, CancellationToken ct)
    {
        foreach (var r in e)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                MERGE dbo.pillar_skill WITH (HOLDLOCK) AS t
                USING (SELECT 'semantic' AS pillar, @h AS horizon_hours, 0 AS asset_id) AS q
                   ON t.pillar = q.pillar AND t.horizon_hours = q.horizon_hours
                  AND t.asset_id = q.asset_id
                WHEN MATCHED THEN UPDATE SET
                     skill = @skill, n_obs = @n, detail = @detail,
                     measured_utc = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                     INSERT (pillar, horizon_hours, asset_id, skill, n_obs, detail)
                     VALUES ('semantic', @h, 0, @skill, @n, @detail);
                """,
                new
                {
                    h = r.HorizonHours,
                    skill = r.Skill,
                    n = r.NObs,
                    /* Der Faktor reist im Detailfeld mit -- dort, wo ihn der
                       Beitragsdienst sucht. */
                    detail = $"faktor={r.Faktor.ToString(System.Globalization.CultureInfo.InvariantCulture)};"
                           + $"korr={r.Korrelation};schwelle={r.Schwelle};{r.Befund}"
                }, cancellationToken: ct));
        }
    }

    /// <summary>Korrelation und Steigung in einem Durchgang.</summary>
    private static (double Korrelation, double Steigung) Regression(List<(double S, double R)> p)
    {
        var n = p.Count;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;

        foreach (var (x, y) in p)
        {
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
        }

        var zaehler = n * sxy - sx * sy;
        var nennerX = n * sxx - sx * sx;
        var nennerY = n * syy - sy * sy;

        if (nennerX <= 1e-12 || nennerY <= 1e-12) return (0, 0);

        return (zaehler / Math.Sqrt(nennerX * nennerY), zaehler / nennerX);
    }

    // -------------------------------------------------------------- Zuordnung --

    private static Dictionary<string, List<int>> BaueIndex(
        List<(int AssetId, string Symbol, string? Name)> werte)
    {
        var index = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        void Merke(string k, int id)
        {
            if (k.Length < 3) return;
            if (!index.TryGetValue(k, out var l)) index[k] = l = [];
            if (!l.Contains(id)) l.Add(id);
        }

        foreach (var (id, symbol, name) in werte)
        {
            Merke(symbol, id);

            var kurz = symbol.Split('-', '.')[0];
            if (kurz.Length >= 3) Merke(kurz, id);

            var teile = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (teile.Length > 0 && teile[0].Length >= 4)
                Merke(teile[0].Trim(',', '.'), id);
        }

        return index;
    }

    private static Dictionary<int, List<(string, DateTime)>> Zuordnen(
        List<Meldung> meldungen, Dictionary<string, List<int>> index)
    {
        var jeWert = new Dictionary<int, List<(string, DateTime)>>();

        foreach (var m in meldungen)
        {
            if (string.IsNullOrWhiteSpace(m.Text)) continue;

            var gesehen = new HashSet<int>();

            foreach (var token in m.Text.Split(
                         [' ', '\n', '\r', '\t', ',', ';', ':', '(', ')', '"', '\''],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (!index.TryGetValue(token.Trim('.', '-'), out var ids)) continue;

                foreach (var id in ids)
                {
                    if (!gesehen.Add(id)) continue;

                    if (!jeWert.TryGetValue(id, out var l)) jeWert[id] = l = [];
                    if (l.Count < 60) l.Add((m.Text, m.AlsUtc));
                }
            }
        }

        return jeWert;
    }

    private sealed class Meldung
    {
        public string? Text { get; set; }
        public DateTime AlsUtc { get; set; }
    }

    private sealed class Rendite
    {
        public int AssetId { get; set; }
        public DateTime Tag { get; set; }
        public double LogRendite { get; set; }
    }
}
