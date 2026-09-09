using Dapper;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Infrastructure.Services;

/// <summary>Wie gut ein einzelner Wert vorausgesagt wurde.</summary>
public sealed record Prognoseguete(
    int AssetId, string Symbol, string? Name, AssetClass Klasse,
    int Bewertet,
    double MittlererFehlerPct,
    double TrefferquotePct,
    double StillstandFehlerPct,
    double Fehlerverhaeltnis,
    DateTime ErsteBewertung,
    DateTime LetzteBewertung)
{
    /// <summary>
    /// Schlägt die Prognose die Annahme „der Kurs bleibt, wo er ist"?
    ///
    /// <para>Das ist die einzige Frage, die zählt. Ein mittlerer Fehler von 1,2 % klingt gut
    /// und ist wertlos, wenn Stillhalten 0,9 % gekostet hätte. Kleiner als 1 heißt: besser
    /// als nichts tun.</para>
    /// </summary>
    public bool Traegt => Fehlerverhaeltnis < 1.0;
}

/// <summary>Eine einzelne bewertete Prognose — Prognose gegen Ist.</summary>
public sealed record Prognosevergleich(
    DateTime GestelltUtc, DateTime ZielUtc, DateTime BewertetUtc,
    int HorizonHours,
    decimal Basis, decimal Prognose, decimal Ist,
    double AbsFehlerPct, bool? RichtungKorrekt)
{
    /// <summary>Das Delta, das der neue Bar gegenüber der Prognose zeigt — mit Vorzeichen.</summary>
    public double DeltaPct => Prognose == 0 ? 0
        : Math.Round((double)((Ist - Prognose) / Prognose) * 100, 3);

    /// <summary>Was der Stillstand gekostet hätte: der Kurs bewegte sich um so viel.</summary>
    public double BewegungPct => Basis == 0 ? 0
        : Math.Round(Math.Abs((double)((Ist - Basis) / Basis)) * 100, 3);
}

public interface IPrognosegueteService
{
    Task<IReadOnlyList<Prognoseguete>> RanglisteAsync(
        DateTime von, int? horizonHours, int mindestens, int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<Prognosevergleich>> VerlaufAsync(
        string symbol, DateTime von, int? horizonHours, int limit,
        CancellationToken ct = default);

    /// <summary>Alle Horizonte auf einmal — je Horizont Bilanz und beste Treffer.</summary>
    Task<IReadOnlyList<Horizontbilanz>> UebersichtAsync(
        DateTime von, IReadOnlyList<int> horizonte, int mindestens, int proHorizont,
        CancellationToken ct = default);

    /// <summary>Wird es besser? Je Zieltag der Stand über alle Werte.</summary>
    Task<IReadOnlyList<Lerntag>> LernkurveAsync(
        DateTime von, int horizonHours, CancellationToken ct = default);

    /// <summary>Bringt das Mischen über alle Säulen etwas gegenüber der ersten allein?</summary>
    Task<IReadOnlyList<Mischvergleich>> MischvergleichAsync(
        DateTime von, CancellationToken ct = default);
}

/// <summary>Säule 1 gegen die Mischung — je Horizont.</summary>
/// <param name="Verglichen">
/// Nur Prognosen, für die BEIDE Bewertungen vorliegen. Alles andere verglich zwei
/// verschiedene Mengen und sähe nach einem Unterschied aus, der keiner ist.
/// </param>
public sealed record Mischvergleich(
    int HorizonHours, int Verglichen,
    double FehlerSaeule1Pct, double FehlerMischungPct,
    double RichtungSaeule1Pct, double RichtungMischungPct,
    int MischungBesser);

/// <summary>Ein Zieltag der Lernkurve.</summary>
/// <param name="MedianVerhaeltnis">
/// Der Median des Fehlerverhältnisses über alle Werte dieses Tages. <b>Median, nicht
/// Mittelwert</b> — ein einzelner sterbender Kleinstwert mit dreistelligem Verhältnis
/// verschiebt den Mittelwert beliebig und sagt nichts über den Rest.
/// </param>
public sealed record Lerntag(
    DateTime Tag, int Werte, double MedianVerhaeltnis,
    int Traegt, double MittlereTrefferquotePct);

/// <summary>Was ein einzelner Horizont seit dem Startdatum geleistet hat.</summary>
/// <param name="Werte">Wie viele Werte überhaupt genug Beobachtungen haben.</param>
/// <param name="Traegt">Wie viele davon den Stillstand schlagen.</param>
/// <param name="MedianVerhaeltnis">
/// Der Median über alle Werte. <b>Die einzige Zahl dieser Auswertung, die bei kleinen
/// Fallzahlen etwas aussagt</b> — die Spitze der Rangliste ist bei drei Beobachtungen je
/// Wert der obere Rand des Rauschens, der Median nicht.
/// </param>
/// <param name="Beste">Die besten Treffer, zur Ansicht — nicht als Beleg.</param>
public sealed record Horizontbilanz(
    int HorizonHours, string Label,
    int Werte, int Traegt, double MedianVerhaeltnis,
    int KleinsteFallzahl, int GroessteFallzahl,
    IReadOnlyList<Prognoseguete> Beste);

/// <summary>
/// Wertet aus, wie gut die <b>abgegebenen</b> Prognosen eingetroffen sind.
///
/// <para><b>Nur Live-Prognosen.</b> Gerechnet wird ausschließlich über <c>dbo.forecast</c> —
/// also über das, was vor dem Zielzeitpunkt wirklich abgegeben und danach gegen den
/// eingetroffenen Bar gerechnet wurde. Der Walk-Forward in <c>forecast_track</c> bleibt
/// draußen: Er wurde nachträglich über bekannte Kurse gerechnet und belegt deshalb nichts.
/// Die Trennung ist der ganze Sinn dieser Auswertung.</para>
///
/// <para><b>Die Rangliste ordnet nach dem Fehlerverhältnis, nicht nach dem Fehler.</b> Ein
/// mittlerer Fehler von 1,2 % sagt für sich genommen nichts: Wenn sich der Kurs in dieser
/// Zeit ohnehin nur um 0,9 % bewegt hat, war Stillhalten besser. Verglichen wird deshalb
/// gegen genau diese Bewegung — <c>|Ist − Basis| / Basis</c>, den Fehler der Annahme „es
/// ändert sich nichts". Unter 1 heißt besser als nichts tun; darüber schlechter. Nach dem
/// rohen Fehler zu sortieren würde zuverlässig die ruhigsten Werte nach oben spülen, nicht
/// die am besten getroffenen.</para>
/// </summary>
public sealed class PrognosegueteService : IPrognosegueteService
{
    private readonly ISqlConnectionFactory _factory;

    public PrognosegueteService(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Prognoseguete>> RanglisteAsync(
        DateTime von, int? horizonHours, int mindestens, int limit,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        /* base_close = 0 wird ausgeschlossen, nicht abgefangen: Eine Division dagegen
           liefert entweder einen Fehler oder eine Zahl, die alles verzerrt. Solche Zeilen
           stammen von toten Werten -- dieselbe Falle wie bei den Bot-Auslösern, wo ein
           Kurs von 0,0000 dreistellige Prozentzahlen erzeugte.

           JE WERT UND ZIELBAR NUR EINE BEOBACHTUNG.

           Prognosen entstehen stündlich, der Zielbar aber nicht. Bei einem Anleihen-ETF
           wie IGSB steht der Kurs zwischen den Stunden still: Vier aufeinanderfolgende
           Läufe sahen dieselbe Basis (52,110), stellten dieselbe Prognose (52,136) und
           trafen denselben Ist-Wert (52,140). Als vier Beobachtungen gezählt sah das nach
           n = 12 und 100 % Richtungstreffern aus, wo drei Beobachtungen und ein Zufall
           standen -- dieselbe Scheingenauigkeit wie bei überlappenden Zeitfenstern, nur
           noch krasser, weil die Zeilen buchstäblich identisch sind.

           Gewählt wird je Wert und Zieltag die ZULETZT gestellte Prognose: Sie hatte die
           meisten Informationen, und sie ist dieselbe, die die Oberfläche als jüngste
           anzeigt. */
        var zeilen = await conn.QueryAsync<Prognoseguete>(new CommandDefinition(
            """
            WITH je_bar AS (
                SELECT  f.asset_id, f.base_close, f.target_ts_utc,
                        s.actual_close, s.abs_pct_error, s.direction_correct,
                        ROW_NUMBER() OVER (
                            PARTITION BY f.asset_id, f.horizon_hours,
                                         CONVERT(date, f.target_ts_utc)
                            ORDER BY f.made_at_utc DESC) AS rn
                  FROM  dbo.forecast f
                  JOIN  dbo.forecast_score s ON s.forecast_id = f.forecast_id
                 WHERE  f.target_ts_utc >= @von
                   AND  f.base_close > 0
                   AND  (@h IS NULL OR f.horizon_hours = @h)
            )
            SELECT  a.asset_id                                   AS AssetId,
                    a.symbol                                     AS Symbol,
                    a.name                                       AS Name,
                    a.asset_class                                AS Klasse,
                    COUNT(*)                                     AS Bewertet,
                    CAST(ROUND(AVG(f.abs_pct_error) * 100, 4) AS float) AS MittlererFehlerPct,
                    ROUND(AVG(CAST(ISNULL(f.direction_correct, 0) AS float)) * 100, 2)
                                                                 AS TrefferquotePct,
                    CAST(ROUND(AVG(ABS((f.actual_close - f.base_close) / f.base_close)) * 100, 4)
                         AS float)                               AS StillstandFehlerPct,

                    /* CAST auf float, nicht nur ROUND: SQL liefert den Quotienten zweier
                       decimal-Werte als decimal zurueck, und Dapper ordnet das keinem
                       double zu -- die Abfrage scheitert dann erst zur Laufzeit. */
                    CAST(ROUND(
                        CASE WHEN AVG(ABS((f.actual_close - f.base_close) / f.base_close)) > 0
                             THEN AVG(f.abs_pct_error)
                                  / AVG(ABS((f.actual_close - f.base_close) / f.base_close))
                             ELSE 999 END, 4) AS float)          AS Fehlerverhaeltnis,
                    MIN(f.target_ts_utc)                         AS ErsteBewertung,
                    MAX(f.target_ts_utc)                         AS LetzteBewertung
              FROM  je_bar f
              JOIN  dbo.asset a ON a.asset_id = f.asset_id
             WHERE  f.rn = 1
             GROUP  BY a.asset_id, a.symbol, a.name, a.asset_class
            HAVING  COUNT(*) >= @mindestens
             ORDER  BY Fehlerverhaeltnis ASC
            OFFSET  0 ROWS FETCH NEXT @limit ROWS ONLY
            """,
            new { von, h = horizonHours, mindestens = Math.Max(1, mindestens),
                  limit = Math.Clamp(limit, 1, 500) },
            cancellationToken: ct));

        return zeilen.ToList();
    }

    /// <summary>
    /// Je Horizont eine eigene Auswertung.
    ///
    /// <para>Nacheinander, nicht nebenläufig: Es sind sieben Abfragen gegen dieselbe
    /// Datenbank, und sieben gleichzeitige Aggregationen über dieselben Tabellen kommen
    /// nicht schneller an — sie konkurrieren nur um dieselben Seiten.</para>
    ///
    /// <para>Die Horizonte kommen vom Aufrufer, nicht aus einer festen Liste hier: Sie
    /// stehen in der Konfiguration, und zwei Stellen mit derselben Liste laufen
    /// auseinander, sobald jemand eine davon ändert.</para>
    /// </summary>
    public async Task<IReadOnlyList<Horizontbilanz>> UebersichtAsync(
        DateTime von, IReadOnlyList<int> horizonte, int mindestens, int proHorizont,
        CancellationToken ct = default)
    {
        var bilanzen = new List<Horizontbilanz>();

        foreach (var h in horizonte)
        {
            ct.ThrowIfCancellationRequested();

            // Voll auswerten, um Median und Anteil zu bekommen; gezeigt wird nur die Spitze.
            var alle = await RanglisteAsync(von, h, mindestens, 500, ct);

            if (alle.Count == 0)
            {
                bilanzen.Add(new Horizontbilanz(h, Label(h), 0, 0, 0, 0, 0, []));
                continue;
            }

            var sortiert = alle.Select(x => x.Fehlerverhaeltnis).OrderBy(x => x).ToList();

            bilanzen.Add(new Horizontbilanz(
                h, Label(h),
                alle.Count,
                alle.Count(x => x.Traegt),
                Math.Round(sortiert[sortiert.Count / 2], 3),
                alle.Min(x => x.Bewertet),
                alle.Max(x => x.Bewertet),
                alle.Take(Math.Clamp(proHorizont, 1, 50)).ToList()));
        }

        return bilanzen;
    }

    /// <summary>
    /// Der Verlauf je Zieltag — die einzige Form, in der sich „wird es besser" prüfen
    /// lässt.
    ///
    /// <para><b>Warum das nicht schon aus den Gewichten folgt.</b> Dass die Rückkopplung
    /// läuft, ist an <c>model_weight</c> ablesbar: Die Gewichte bewegen sich und folgen
    /// der Trefferquote. Ob die Prognose dadurch <i>besser</i> wird, steht damit nicht
    /// fest — ein Verfahren kann fleissig lernen und trotzdem auf der Stelle treten, weil
    /// das Signal nicht da ist. Nur diese Kurve entscheidet das, und sie braucht Zeit.</para>
    ///
    /// <para>Dieselbe Entdopplung wie in der Rangliste: je Wert und Zieltag eine
    /// Beobachtung. Ohne sie zählen ruhige Werte mehrfach, und der Verlauf zeigte die
    /// Zahl der Stunden statt die Güte.</para>
    /// </summary>
    public async Task<IReadOnlyList<Lerntag>> LernkurveAsync(
        DateTime von, int horizonHours, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var zeilen = await conn.QueryAsync<Lerntag>(new CommandDefinition(
            """
            WITH je_bar AS (
                SELECT  f.asset_id,
                        CONVERT(date, f.target_ts_utc) AS tag,
                        s.abs_pct_error,
                        s.direction_correct,
                        ABS((s.actual_close - f.base_close) / f.base_close) AS stillstand,
                        ROW_NUMBER() OVER (
                            PARTITION BY f.asset_id, CONVERT(date, f.target_ts_utc)
                            ORDER BY f.made_at_utc DESC) AS rn
                  FROM  dbo.forecast f
                  JOIN  dbo.forecast_score s ON s.forecast_id = f.forecast_id
                 WHERE  f.target_ts_utc >= @von
                   AND  f.horizon_hours = @h
                   AND  f.base_close > 0
            ),
            je_wert AS (
                SELECT  tag, asset_id,
                        CASE WHEN stillstand > 0 THEN abs_pct_error / stillstand ELSE NULL END
                            AS verhaeltnis,
                        CAST(ISNULL(direction_correct, 0) AS float) AS richtig
                  FROM  je_bar WHERE rn = 1
            ),
            /* PERCENTILE_CONT ist eine FENSTERfunktion, keine Aggregation: Sie liefert
               je Zeile denselben Wert und lässt sich nicht in MIN() schachteln -- SQL
               antwortet dann mit „Windowed functions cannot be used in the context of
               another windowed function or aggregate". Median und Aggregat werden
               deshalb getrennt gerechnet und danach verbunden. */
            median AS (
                SELECT DISTINCT tag,
                       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY verhaeltnis)
                           OVER (PARTITION BY tag) AS wert
                  FROM je_wert
                 WHERE verhaeltnis IS NOT NULL
            ),
            summe AS (
                SELECT tag,
                       COUNT(*) AS werte,
                       SUM(CASE WHEN verhaeltnis < 1 THEN 1 ELSE 0 END) AS traegt,
                       AVG(richtig) * 100 AS quote
                  FROM je_wert
                 WHERE verhaeltnis IS NOT NULL
                 GROUP BY tag
            )
            SELECT  CAST(s.tag AS datetime)                  AS Tag,
                    s.werte                                  AS Werte,
                    CAST(m.wert AS float)                    AS MedianVerhaeltnis,
                    s.traegt                                 AS Traegt,
                    CAST(ROUND(s.quote, 2) AS float)         AS MittlereTrefferquotePct
              FROM  summe s
              JOIN  median m ON m.tag = s.tag
             ORDER  BY s.tag
            """,
            new { von, h = horizonHours }, cancellationToken: ct));

        return zeilen.ToList();
    }

    /// <summary>
    /// Vergleicht beide Wege auf denselben Prognosen.
    ///
    /// <para><b>Nur wo beide bewertet sind.</b> Prognosen aus der Zeit vor der
    /// Säulenmischung haben keine gemischte Zahl. Sie mitzuzählen hiesse, zwei
    /// verschiedene Mengen zu vergleichen — der Unterschied wäre dann der zwischen den
    /// Zeiträumen, nicht der zwischen den Verfahren.</para>
    ///
    /// <para>Entdoppelt wie überall: je Wert und Zieltag eine Beobachtung.</para>
    /// </summary>
    public async Task<IReadOnlyList<Mischvergleich>> MischvergleichAsync(
        DateTime von, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var zeilen = await conn.QueryAsync<Mischvergleich>(new CommandDefinition(
            """
            WITH je_bar AS (
                SELECT f.horizon_hours,
                       s.abs_pct_error       AS f1,
                       m.abs_pct_error       AS fm,
                       CAST(ISNULL(s.direction_correct, 0) AS float) AS r1,
                       CAST(ISNULL(m.direction_correct, 0) AS float) AS rm,
                       ROW_NUMBER() OVER (
                           PARTITION BY f.asset_id, f.horizon_hours,
                                        CONVERT(date, f.target_ts_utc)
                           ORDER BY f.made_at_utc DESC) AS rn
                  FROM dbo.forecast f
                  JOIN dbo.forecast_score s          ON s.forecast_id = f.forecast_id
                  JOIN dbo.forecast_score_combined m ON m.forecast_id = f.forecast_id
                 WHERE f.target_ts_utc >= @von
            )
            SELECT horizon_hours                                        AS HorizonHours,
                   COUNT(*)                                             AS Verglichen,
                   CAST(ROUND(AVG(f1) * 100, 4) AS float)               AS FehlerSaeule1Pct,
                   CAST(ROUND(AVG(fm) * 100, 4) AS float)               AS FehlerMischungPct,
                   CAST(ROUND(AVG(r1) * 100, 2) AS float)               AS RichtungSaeule1Pct,
                   CAST(ROUND(AVG(rm) * 100, 2) AS float)               AS RichtungMischungPct,
                   SUM(CASE WHEN fm < f1 THEN 1 ELSE 0 END)             AS MischungBesser
              FROM je_bar
             WHERE rn = 1
             GROUP BY horizon_hours
             ORDER BY horizon_hours
            """, new { von }, cancellationToken: ct));

        return zeilen.ToList();
    }

    private static string Label(int h) => h switch
    {
        24 => "1 Tag",
        168 => "1 Woche",
        336 => "2 Wochen",
        720 => "1 Monat",
        2160 => "3 Monate",
        4380 => "6 Monate",
        8760 => "1 Jahr",
        _ => h < 24 ? $"{h} h" : $"{h / 24} Tage"
    };

    public async Task<IReadOnlyList<Prognosevergleich>> VerlaufAsync(
        string symbol, DateTime von, int? horizonHours, int limit,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var zeilen = await conn.QueryAsync<Prognosevergleich>(new CommandDefinition(
            """
            SELECT  f.made_at_utc      AS GestelltUtc,
                    f.target_ts_utc    AS ZielUtc,
                    s.scored_at_utc    AS BewertetUtc,
                    f.horizon_hours    AS HorizonHours,
                    f.base_close       AS Basis,
                    f.predicted_close  AS Prognose,
                    s.actual_close     AS Ist,
                    ROUND(s.abs_pct_error * 100, 4) AS AbsFehlerPct,
                    s.direction_correct AS RichtungKorrekt
              FROM  dbo.forecast f
              JOIN  dbo.forecast_score s ON s.forecast_id = f.forecast_id
              JOIN  dbo.asset a          ON a.asset_id    = f.asset_id
             WHERE  a.symbol = @symbol
               AND  f.target_ts_utc >= @von
               AND  (@h IS NULL OR f.horizon_hours = @h)
             ORDER  BY f.target_ts_utc DESC
            OFFSET  0 ROWS FETCH NEXT @limit ROWS ONLY
            """,
            new { symbol, von, h = horizonHours, limit = Math.Clamp(limit, 1, 500) },
            cancellationToken: ct));

        return zeilen.ToList();
    }
}
