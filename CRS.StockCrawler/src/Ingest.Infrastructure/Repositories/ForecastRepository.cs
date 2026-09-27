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
              (forecast_id, actual_close, actual_return, abs_pct_error, direction_correct,
               asset_id, horizon_hours)
            SELECT @ForecastId, @ActualClose, @ActualReturn, @AbsPctError, @DirectionCorrect,
                   @AssetId, @HorizonHours
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

        /*  Zwei eingeschraenkte Lesevorgaenge statt eines Verbunds mit
            Fensterfunktion.

            Hier stand ein `WITH ranked AS (… LEFT JOIN dbo.forecast_score …
            ROW_NUMBER() OVER (PARTITION BY target_ts_utc …))`. Das ist die
            Lehrbuchform und auf dem DataCell-Backend die teuerste Variante,
            die man waehlen kann: Der Verbund materialisiert `forecast_score`
            vollstaendig (gemessen 14,1 s fuer einen einzigen Wert), und eine
            Bedingung INNERHALB eines CTE zaehlt dort nicht als Einschraenkung
            der Tabelle. Der Rueckblick im Diagramm kostete dadurch 34
            Sekunden, wovon das Diagramm selbst 211 bis 449 ms braucht.

            Beide Abfragen unten greifen jetzt auf einen Index: die erste auf
            `UX_forecast (asset_id, horizon_hours, made_at_utc)`, die zweite
            auf `IX_forecast_score_wert (asset_id, horizon_hours)` aus
            Migration 047. Je Wert und Horizont sind das einige hundert
            Zeilen, nicht 484.000.

            Die Zuordnung und das Entdoppeln geschehen in C#. Das ist nicht
            nur schneller, es ist auch die Stelle, an der man es nachlesen
            kann: „je Zielzeitpunkt die zuletzt erstellte Prognose" ist eine
            Regel ueber die Daten, keine Eigenschaft der Datenbank.            */
        var prognosen = await conn.QueryAsync<ForecastVsActual>(new CommandDefinition("""
            SELECT forecast_id AS ForecastId, made_at_utc AS MadeAtUtc,
                   target_ts_utc AS TargetTsUtc, base_close AS BaseClose,
                   predicted_close AS PredictedClose, confidence AS Confidence
              FROM dbo.forecast
             WHERE asset_id = @assetId
               AND horizon_hours = @horizonHours
               AND target_ts_utc >= @fromUtc
               AND target_ts_utc <= @toUtc
            """, new { assetId, horizonHours, fromUtc, toUtc },
            commandTimeout: 120, cancellationToken: ct));

        /*  Zu einem Zielzeitpunkt kann es mehrere Prognosen geben -- etwa eine
            aus dem Backtest und eine aus dem Livebetrieb. Es zaehlt die
            zuletzt erstellte: sie kannte den meisten Kontext.                 */
        var jeZiel = prognosen
            .GroupBy(f => f.TargetTsUtc)
            .Select(g => g.OrderByDescending(f => f.MadeAtUtc).First())
            .OrderBy(f => f.TargetTsUtc)
            .ToList();

        if (jeZiel.Count == 0) return jeZiel;

        /*  Die Bewertungen dieses Wertes und Horizonts -- ohne Zeitfenster,
            denn das kostet hier nichts und spart eine Bedingung, die der
            Index nicht traegt.

            Bewertungen, die Wert und Horizont noch nicht tragen, fehlen hier.
            Das ist der Zustand zwischen dem Einspielen von Migration 047 und
            dem Ende des Nachtragens beim Start; sichtbar wird er als
            Rueckblick ohne Treffsicherheit, nicht als Fehler. Er heilt sich
            mit dem Nachtragelauf, und der meldet, wie viele Zeilen er
            gefuellt hat.                                                      */
        var bewertung = new Dictionary<long, (decimal? Ist, double? Fehler, bool? Treffer)>();

        var rohe = await conn.QueryAsync<(long ForecastId, decimal? Ist, double? Fehler, bool? Treffer)>(
            new CommandDefinition("""
            SELECT forecast_id, actual_close, abs_pct_error, direction_correct
              FROM dbo.forecast_score
             WHERE asset_id = @assetId AND horizon_hours = @horizonHours
            """, new { assetId, horizonHours },
            commandTimeout: 120, cancellationToken: ct));

        foreach (var b in rohe) bewertung[b.ForecastId] = (b.Ist, b.Fehler, b.Treffer);

        foreach (var f in jeZiel)
        {
            if (!bewertung.TryGetValue(f.ForecastId, out var b)) continue;

            f.ActualClose = b.Ist;
            f.AbsPctError = b.Fehler;
            f.DirectionCorrect = b.Treffer;
        }

        return jeZiel;
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

    /*  Treffsicherheit: eine GROUP-BY-Abfrage ueber EINE Tabelle.

        Hier stand bis zum 27.09.2026 ein Verbund `forecast JOIN
        forecast_score`, und danach eine prozessweite Tafel, die den Verbund
        umging, indem sie beide Tabellen einmal ganz las. Beides ist weg, weil
        beides denselben Denkfehler hatte: Es liess die Datenbank bei jeder
        Frage neu herleiten, was seit dem Schreiben der Bewertung feststeht.
        Seit Migration 047 traegt `forecast_score` Wert und Horizont selbst.

        Was der Umweg gekostet hat, gemessen auf einem ruhigen Node:

          der Verbund, eingeschraenkt auf EINEN Wert (997 Treffer)   14,1 s
          dieselben 2.124 Kennungen als IN-Liste                    138,6 s
          beide Tabellen ganz lesen (1,75 Mio Zeilen)                35   s

        Der Node filtert die linke Seite korrekt und materialisiert die rechte
        vollstaendig: 2.124 mal 483.588 Paare fuer 997 Treffer. Das ist dort
        gemeldet und wird dort behoben -- aber selbst ein schneller Verbund
        waere hier der Umweg geblieben. Diese Abfrage liest jetzt einen Index
        und liefert sieben Zeilen.

        `GetAccuracyAsync` hat fuenf Aufrufer, zwei davon (`ForecastService`,
        `CombinedForecastService`) rufen sie JE WERT. Bei rund 600 verfolgten
        Werten waren das 600 mal 14 Sekunden.                                  */
    public async Task<IReadOnlyList<(int HorizonHours, int N, double Mape, double HitRate)>>
        GetAccuracyAsync(int? assetId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        /*  Der Wertefilter steht nur drin, wenn einer gemeint ist -- kein
            `(asset_id = @a OR @a IS NULL)`. Ein Sammelfilter zwingt den Planer
            zu einem Plan, der fuer beide Faelle gilt, also zum vollen
            Durchlauf; dieselbe Falle wie an vier anderen Stellen.

            `asset_id IS NOT NULL` gehoert in BEIDE Zweige: Solange das
            Nachtragen der Altzeilen laeuft, gibt es Bewertungen ohne Wert, und
            die duerfen sich nicht als eigene Gruppe in die Statistik legen.   */
        var wertefilter = assetId is not null
            ? "WHERE asset_id = @assetId"
            : "WHERE asset_id IS NOT NULL";

        var rows = await conn.QueryAsync<(int, int, double, double)>(new CommandDefinition($"""
            SELECT horizon_hours,
                   CAST(COUNT(*) AS INT)                                            AS n,
                   AVG(abs_pct_error)                                               AS mape,
                   AVG(CASE WHEN direction_correct = {d.Wahr} THEN 1.0 ELSE 0.0 END) AS hit_rate
              FROM dbo.forecast_score
             {wertefilter}
             GROUP BY horizon_hours
             ORDER BY horizon_hours
            """, new { assetId }, commandTimeout: 60, cancellationToken: ct));

        return rows.ToList();
    }

    /*  Wert und Horizont an den Altzeilen nachtragen.

        Migration 047 legt die Spalten an, fuellt sie aber nicht: Ein
        Migrationsskript, das 484.000 Zeilen ueber einen Verbund nachzieht,
        scheitert auf diesem Backend an genau dem Verbund, dessentwegen die
        Spalten ueberhaupt entstehen. Also hier, und zwar so, wie die
        Anwendung ohnehin schreibt -- Massenkopie in eine Stufe, dann ein
        MERGE.

        Gelesen wird ungepuffert. Auf dem DataCell-Backend kostet jede
        ZURUECKGEGEBENE Zeile rund 15 Mikrosekunden Kernzeit (gemessen: der
        Aufwand haengt an der Zeilenzahl, nicht an der Spaltenzahl und nicht
        an den gescannten Zeilen), 1,26 Millionen Prognosen also gut zwanzig
        Sekunden. Das ist der Preis fuer EINEN Lauf, der danach nie wieder
        anfaellt -- gegen 14 Sekunden je Wert und Aufruf.

        Der Lauf meldet, wie viele Zeilen er gefuellt hat. Eine Migration, die
        still nichts tut, sieht aus wie eine, die alles getan hat.             */
    public async Task<int> NachtragenAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var offen = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CAST(COUNT(*) AS INT) FROM dbo.forecast_score WHERE asset_id IS NULL",
            commandTimeout: 120, cancellationToken: ct));

        if (offen == 0) return 0;

        /*  Erst die offenen Kennungen, dann die Prognosen daruebergelegt. Die
            kleinere Menge gehoert gehalten, die groessere durchgereicht.      */
        var offeneIds = new HashSet<long>(offen);

        await foreach (var id in conn.QueryUnbufferedAsync<long>(
                           "SELECT forecast_id FROM dbo.forecast_score WHERE asset_id IS NULL",
                           commandTimeout: 300).WithCancellation(ct))
            offeneIds.Add(id);

        var tabelle = new DataTable();
        tabelle.Columns.Add("forecast_id", typeof(long));
        tabelle.Columns.Add("asset_id", typeof(int));
        tabelle.Columns.Add("horizon_hours", typeof(int));

        await foreach (var f in conn.QueryUnbufferedAsync<(long ForecastId, int AssetId, int HorizonHours)>(
                           "SELECT forecast_id, asset_id, horizon_hours FROM dbo.forecast",
                           commandTimeout: 300).WithCancellation(ct))
        {
            if (!offeneIds.Contains(f.ForecastId)) continue;
            tabelle.Rows.Add(f.ForecastId, f.AssetId, f.HorizonHours);
        }

        if (tabelle.Rows.Count == 0) return 0;

        // Eindeutiger Name je Aufruf -- siehe SqlDialekt.EindeutigerTempName.
        var stufe = SqlDialekt.EindeutigerTempName("fsnach");

        await conn.ExecuteAsync(new CommandDefinition($"""
            {d.CreateTemp(stufe)} (
              forecast_id BIGINT NOT NULL PRIMARY KEY,
              asset_id INT NOT NULL,
              horizon_hours INT NOT NULL);
            """, cancellationToken: ct));

        try
        {
            await Massenkopie.SchreibeAsync(conn, tabelle, d.Temp(stufe), 300, ct);

            return await conn.ExecuteAsync(new CommandDefinition($"""
                MERGE INTO dbo.forecast_score {d.MergeSperre} AS t
                USING (SELECT forecast_id, asset_id, horizon_hours FROM {d.Temp(stufe)}) AS s
                   ON t.forecast_id = s.forecast_id
                WHEN MATCHED THEN UPDATE SET
                      asset_id = s.asset_id, horizon_hours = s.horizon_hours;
                """, commandTimeout: 600, cancellationToken: ct));
        }
        finally
        {
            /* Die Stufe muss in jedem Fall weg: Auf dem DataCell-Backend sind
               temporaere Tabellen global, eine liegengebliebene traefe den
               naechsten Lauf. */
            await conn.ExecuteAsync(new CommandDefinition(
                $"""DROP TABLE {d.Temp(stufe)}""", cancellationToken: CancellationToken.None));
        }
    }
}
