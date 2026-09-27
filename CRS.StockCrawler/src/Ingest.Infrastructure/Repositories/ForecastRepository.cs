using System.Data;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Models;
using Ingest.Infrastructure.Datenbank;

namespace Ingest.Infrastructure.Repositories;

public sealed class ForecastRepository : IForecastRepository
{
    private readonly ISqlConnectionFactory _factory;
    private SqlDialekt d => _factory.Dialekt;

    public ForecastRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<long> InsertAsync(Forecast f, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // Wiederholter Lauf zur selben Minute soll die alte Prognose ersetzen,
            // nicht am eindeutigen Index scheitern.
            var id = await conn.ExecuteScalarAsync<long?>(new CommandDefinition("""
                SELECT forecast_id FROM dbo.forecast
                 WHERE asset_id = @AssetId AND horizon_hours = @HorizonHours AND made_at_utc = @MadeAtUtc
                """, f, tx, cancellationToken: ct));

            if (id is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM dbo.forecast_component WHERE forecast_id = @id",
                    new { id }, tx, cancellationToken: ct));

                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE dbo.forecast
                       SET target_ts_utc = @TargetTsUtc, base_close = @BaseClose,
                           predicted_close = @PredictedClose, predicted_return = @PredictedReturn,
                           confidence = @Confidence, model_version = @ModelVersion
                     WHERE forecast_id = @id
                    """, new
                {
                    id,
                    f.TargetTsUtc,
                    f.BaseClose,
                    f.PredictedClose,
                    f.PredictedReturn,
                    f.Confidence,
                    f.ModelVersion
                }, tx, cancellationToken: ct));
            }
            else
            {
                id = await conn.ExecuteScalarAsync<long>(new CommandDefinition($"""
                    INSERT INTO dbo.forecast
                      (asset_id, horizon_hours, made_at_utc, target_ts_utc, base_close,
                       predicted_close, predicted_return, confidence, model_version,
                       combined_close, combined_return, pillar_mix)
                    {d.RueckgabeVor("forecast_id")} VALUES
                      (@AssetId, @HorizonHours, @MadeAtUtc, @TargetTsUtc, @BaseClose,
                       @PredictedClose, @PredictedReturn, @Confidence, @ModelVersion,
                       @CombinedClose, @CombinedReturn, @PillarMix) {d.RueckgabeNach("forecast_id")}
                    """, f, tx, cancellationToken: ct));
            }

            foreach (var c in f.Components)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO dbo.forecast_component (forecast_id, model_name, predicted_return, weight)
                    VALUES (@id, @ModelName, @PredictedReturn, @Weight)
                    """, new { id, c.ModelName, c.PredictedReturn, c.Weight }, tx, cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
            return id!.Value;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<Forecast>> GetDueAsync(DateTime nowUtc, int maxRows, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<Forecast>(new CommandDefinition(
            d.Aufruf("dbo.get_due_forecasts", "now_utc", "max_rows"),
            new { now_utc = nowUtc, max_rows = maxRows },
            cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>
    /// Stellt Prognosen zurück, die nie bewertbar werden. Erkennbar sind sie
    /// eindeutig: Existiert eine Bar NACH dem Zielzeitpunkt, ist die Reihe
    /// darüber hinweg fortgeschritten; ist die letzte Bar davor trotzdem nicht
    /// neuer als die Prognose, lag zwischen beiden keine Handelszeit — und es
    /// wird auch keine mehr kommen.
    /// </summary>
    public async Task<int> MarkUnscoreableAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            d.Aufruf("dbo.mark_unscoreable_forecasts"),
            commandTimeout: 300, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ForecastComponent>> GetComponentsAsync(
        IEnumerable<long> forecastIds, CancellationToken ct = default)
    {
        var ids = forecastIds.Distinct().ToArray();
        if (ids.Length == 0) return [];

        await using var conn = await _factory.OpenAsync(ct);

        /* In Blöcken, nicht in einem Zug.

           Der Scheduler wertet bis zu 5000 fällige Prognosen auf einmal aus,
           und Dapper macht aus der Schlüsselliste einen Parameter je Element.
           SQL Server nimmt 2100 an — der Lauf brach also ab, sobald sich mehr
           als rund zweitausend Prognosen angesammelt hatten. Also genau dann,
           wenn er gebraucht wurde. */
        var all = new List<ForecastComponent>(ids.Length * 5);

        foreach (var chunk in SqlBatching.Chunks(ids))
        {
            var rows = await conn.QueryAsync<ForecastComponent>(new CommandDefinition($"""
                SELECT forecast_id AS ForecastId, model_name AS ModelName,
                       predicted_return AS PredictedReturn, weight AS Weight
                  FROM dbo.forecast_component
                 WHERE {d.In("forecast_id", "ids")}
                """, new { ids = chunk }, cancellationToken: ct));

            all.AddRange(rows);
        }

        return all;
    }

    /// <summary>
    /// Bewertet die gemischte Zahl — getrennt von der ersten Säule.
    ///
    /// <para>Eigene Tabelle statt einer Kennzeichnungsspalte: Eine gemeinsame Tabelle wäre
    /// kürzer gewesen und hätte jede Auswertung um ein <c>WHERE</c> verlängert, das man
    /// vergessen kann — und wer es vergisst, mischt zwei Messreihen und merkt es nicht.</para>
    /// </summary>
    public async Task ScoreCombinedAsync(
        IEnumerable<(long Id, decimal Ist, double IstRendite, double Fehler, bool? Richtung)> scores,
        CancellationToken ct = default)
    {
        var list = scores.ToList();
        if (list.Count == 0) return;

        await using var conn = await _factory.OpenAsync(ct);

        foreach (var s in list)
        {
            await conn.ExecuteAsync(new CommandDefinition($"""
                MERGE INTO dbo.forecast_score_combined {d.MergeSperre} AS t
                USING (SELECT @Id AS forecast_id) AS q ON t.forecast_id = q.forecast_id
                WHEN MATCHED THEN UPDATE SET
                     actual_close = @Ist, actual_return = @IstRendite,
                     abs_pct_error = @Fehler, direction_correct = @Richtung,
                     scored_at_utc = {d.Jetzt}
                WHEN NOT MATCHED THEN
                     INSERT (forecast_id, actual_close, actual_return, abs_pct_error,
                             direction_correct)
                     VALUES (@Id, @Ist, @IstRendite, @Fehler, @Richtung);
                """,
                /* Ein anonymes Objekt, kein Tupel.

                   Dapper nimmt ValueTuples als ERGEBNIS (positionsweise zugeordnet),
                   weist sie als PARAMETER aber ausdruecklich zurueck: "ValueTuple should
                   not be used for parameters -- the language-level names are not
                   available to use as parameter names". Die Namen, die im C#-Quelltext
                   stehen, gibt es zur Laufzeit nicht. */
                new { s.Id, s.Ist, s.IstRendite, s.Fehler, s.Richtung },
                cancellationToken: ct));
        }
    }

    public async Task ScoreAsync(IEnumerable<ForecastScore> scores, CancellationToken ct = default)
    {
        var list = scores.ToList();
        if (list.Count == 0) return;

        await using var conn = await _factory.OpenAsync(ct);

        // Bereits bewertete Prognosen dürfen nicht doppelt einfließen.
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO dbo.forecast_score
              (forecast_id, actual_close, actual_return, abs_pct_error, direction_correct)
            SELECT @ForecastId, @ActualClose, @ActualReturn, @AbsPctError, @DirectionCorrect
             WHERE NOT EXISTS (SELECT 1 FROM dbo.forecast_score WHERE forecast_id = @ForecastId)
            """, list, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Forecast>> GetLatestAsync(int assetId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        /*  Ohne CTE und ohne Fensterfunktion: lesen, dann in C# auswählen.

            Hier stand ein `WITH ranked AS (… ROW_NUMBER() OVER (PARTITION BY
            horizon_hours ORDER BY made_at_utc DESC) … WHERE asset_id =
            @assetId)` — die Lehrbuchform für „die jüngste je Horizont".

            Das EventMesh-DataCell-Backend zählt eine Bedingung INNERHALB eines
            CTE nicht als Einschränkung der Tabelle. Es wies die Abfrage am
            27.09.2026 deshalb ab: „Komplexe Query über die grosse Tabelle
            'forecast' (1.264.033 Zeilen) ohne einschränkendes WHERE" — obwohl
            genau dort ein `WHERE asset_id = @assetId` steht. In der Oberfläche
            erschien das als 500 beim Einblenden der Prognose.

            Ein Wert hat über alle Horizonte hinweg einige hundert Prognosen.
            Die zu lesen und die jüngste je Horizont hier zu wählen, kostet
            nichts — und die Bedingung steht dabei dort, wo jede Datenbank sie
            sieht.                                                             */
        var rows = await conn.QueryAsync<Forecast>(new CommandDefinition("""
            SELECT forecast_id AS ForecastId, asset_id AS AssetId,
                   horizon_hours AS HorizonHours, made_at_utc AS MadeAtUtc,
                   target_ts_utc AS TargetTsUtc, base_close AS BaseClose,
                   predicted_close AS PredictedClose, predicted_return AS PredictedReturn,
                   confidence AS Confidence, model_version AS ModelVersion
              FROM dbo.forecast
             WHERE asset_id = @assetId
            """, new { assetId }, commandTimeout: 60, cancellationToken: ct));

        return rows
            .GroupBy(f => f.HorizonHours)
            .Select(g => g.OrderByDescending(f => f.MadeAtUtc).First())
            .OrderBy(f => f.HorizonHours)
            .ToList();
    }

    public async Task<IReadOnlyList<ForecastVsActual>> GetHistoryAsync(
        int assetId, int horizonHours, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        /* Zu einem Zielzeitpunkt kann es mehrere Prognosen geben — etwa eine
           aus dem Backtest und eine aus dem Livebetrieb. Es zählt die zuletzt
           erstellte: sie kannte den meisten Kontext. */
        var rows = await conn.QueryAsync<ForecastVsActual>(new CommandDefinition("""
            WITH ranked AS (
              SELECT f.forecast_id, f.made_at_utc, f.target_ts_utc, f.base_close,
                     f.predicted_close, f.confidence,
                     s.actual_close, s.abs_pct_error, s.direction_correct,
                     ROW_NUMBER() OVER (PARTITION BY f.target_ts_utc
                                        ORDER BY f.made_at_utc DESC) AS rn
                FROM dbo.forecast f
                LEFT JOIN dbo.forecast_score s ON s.forecast_id = f.forecast_id
               WHERE f.asset_id = @assetId AND f.horizon_hours = @horizonHours
                 AND f.target_ts_utc >= @fromUtc AND f.target_ts_utc <= @toUtc
            )
            SELECT forecast_id AS ForecastId, made_at_utc AS MadeAtUtc,
                   target_ts_utc AS TargetTsUtc, base_close AS BaseClose,
                   predicted_close AS PredictedClose, confidence AS Confidence,
                   actual_close AS ActualClose, abs_pct_error AS AbsPctError,
                   direction_correct AS DirectionCorrect
              FROM ranked WHERE rn = 1
             ORDER BY target_ts_utc
            """, new { assetId, horizonHours, fromUtc, toUtc },
            commandTimeout: 120, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<int>> GetAvailableHorizonsAsync(
        int assetId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<int>(new CommandDefinition(
            "SELECT DISTINCT horizon_hours FROM dbo.forecast WHERE asset_id = @assetId ORDER BY horizon_hours",
            new { assetId }, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<ModelWeight>> GetWeightsAsync(
        int assetId, int horizonHours, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<ModelWeight>(new CommandDefinition("""
            SELECT asset_id AS AssetId, horizon_hours AS HorizonHours, model_name AS ModelName,
                   weight AS Weight, n_obs AS NObs, mean_abs_pct_err AS MeanAbsPctErr,
                   hit_rate AS HitRate, updated_utc AS UpdatedUtc
              FROM dbo.model_weight
             WHERE asset_id = @assetId AND horizon_hours = @horizonHours
            """, new { assetId, horizonHours }, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task UpsertWeightsAsync(IEnumerable<ModelWeight> weights, CancellationToken ct = default)
    {
        var list = weights.ToList();
        if (list.Count == 0) return;

        await using var conn = await _factory.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition($"""
            MERGE INTO dbo.model_weight {d.MergeSperre} AS t
            USING (SELECT @AssetId AS asset_id, @HorizonHours AS horizon_hours,
                          @ModelName AS model_name) AS s
               ON t.asset_id = s.asset_id AND t.horizon_hours = s.horizon_hours
              AND t.model_name = s.model_name
            WHEN MATCHED THEN UPDATE SET
                  weight = @Weight, n_obs = @NObs, mean_abs_pct_err = @MeanAbsPctErr,
                  hit_rate = @HitRate, updated_utc = {d.Jetzt}
            WHEN NOT MATCHED THEN
              INSERT (asset_id, horizon_hours, model_name, weight, n_obs, mean_abs_pct_err, hit_rate)
              VALUES (@AssetId, @HorizonHours, @ModelName, @Weight, @NObs, @MeanAbsPctErr, @HitRate);
            """, list, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<(int HorizonHours, int N, double Mape, double HitRate)>>
        GetAccuracyAsync(int? assetId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        /*  Der Wertefilter steht nur drin, wenn einer gemeint ist.

            Hier stand `WHERE (f.asset_id = @assetId OR @assetId IS NULL)` —
            derselbe Sammelfilter wie an drei anderen Stellen, die ich heute
            schon aufgelöst habe. Er zwingt den Planer zu einem Plan, der für
            beide Fälle gilt, also zum vollen Durchlauf über den Verbund von
            1,26 Millionen Prognosen mit 484.000 Bewertungen.

            Das EventMesh-DataCell-Backend wies ihn am 27.09.2026 rundheraus
            ab („Komplexe Query über die grosse Tabelle 'forecast' … ohne
            einschränkendes WHERE"), und die Kursansicht lieferte 500, sobald
            jemand die Prognose einblendete. Mit dem Wert im WHERE bleibt von
            1,26 Millionen Zeilen eine Handvoll übrig.

            Die Reihenfolge im FROM ist dabei nicht gleichgültig: Stand
            `forecast_score` vorn, sah der Node `forecast` als unbeschränkt
            verbundene Tabelle und wies weiter ab, obwohl die Bedingung im
            WHERE stand. Die eingeschränkte Tabelle gehört nach vorn — was
            ohnehin die richtige Leserichtung ist: erst die Prognosen dieses
            Wertes, dann ihre Bewertungen.                                     */
        var wertefilter = assetId is not null ? "WHERE f.asset_id = @assetId" : "";

        var rows = await conn.QueryAsync<(int, int, double, double)>(new CommandDefinition($"""
            SELECT f.horizon_hours,
                   CAST(COUNT(*) AS INT)                                              AS n,
                   AVG(s.abs_pct_error)                                  AS mape,
                   AVG(CASE WHEN s.direction_correct = {d.Wahr} THEN 1.0 ELSE 0.0 END) AS hit_rate
              FROM dbo.forecast f
              JOIN dbo.forecast_score s ON s.forecast_id = f.forecast_id
             {wertefilter}
             GROUP BY f.horizon_hours
             ORDER BY f.horizon_hours
            """, new { assetId }, commandTimeout: 60, cancellationToken: ct));

        return rows.ToList();
    }
}
