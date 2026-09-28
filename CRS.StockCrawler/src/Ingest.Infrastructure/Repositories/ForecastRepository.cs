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

    /*  Optional, wie beim Kursspeicher: Faellt er weg, geht jede Abfrage an
        die Datenbank. Ein Zwischenspeicher, ohne den die Anwendung nicht mehr
        laeuft, waere keiner mehr.                                           */
    private readonly Prognosespeicher? _speicher;

    public ForecastRepository(ISqlConnectionFactory factory, Prognosespeicher? speicher = null)
    {
        _factory = factory;
        _speicher = speicher;
    }

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

            /*  Geschrieben heisst verworfen. Das ist die einzige Stelle, an
                der eine Prognose entsteht -- deshalb steht die Verwerfung
                hier und nicht in einer Frist.                               */
            _speicher?.Verwerfe(f.AssetId);

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
                     scored_at_utc = @Jetzt
                WHEN NOT MATCHED THEN
                     INSERT (forecast_id, actual_close, actual_return, abs_pct_error,
                             direction_correct, scored_at_utc)
                     VALUES (@Id, @Ist, @IstRendite, @Fehler, @Richtung, @Jetzt);
                """,
                /* Ein anonymes Objekt, kein Tupel.

                   Dapper nimmt ValueTuples als ERGEBNIS (positionsweise zugeordnet),
                   weist sie als PARAMETER aber ausdruecklich zurueck: "ValueTuple should
                   not be used for parameters -- the language-level names are not
                   available to use as parameter names". Die Namen, die im C#-Quelltext
                   stehen, gibt es zur Laufzeit nicht. */
                new { s.Id, s.Ist, s.IstRendite, s.Fehler, s.Richtung, Jetzt = DateTime.UtcNow },
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
               asset_id, horizon_hours, scored_at_utc)
            SELECT @ForecastId, @ActualClose, @ActualReturn, @AbsPctError, @DirectionCorrect,
                   @AssetId, @HorizonHours, @ScoredAtUtc
             WHERE NOT EXISTS (SELECT 1 FROM dbo.forecast_score WHERE forecast_id = @ForecastId)
            """, list, cancellationToken: ct));

        /*  Eine neue Bewertung aendert die Treffsicherheit des Wertes UND den
            Durchschnitt ueber alle -- `Verwerfe` raeumt beides.             */
        foreach (var wert in list.Select(s => s.AssetId).Distinct())
            _speicher?.Verwerfe(wert);
    }

    /*  EINE Abfrage ueber alle Prognosen des Wertes, dann die Auswahl in C#.

        Diese Stelle hat heute drei Fassungen gesehen, und die Reihenfolge ist
        lehrreich:

        1. `ROW_NUMBER() OVER (PARTITION BY horizon_hours ...)` -- vom
           DataCell-Backend abgewiesen („komplexe Query ohne einschraenkendes
           WHERE"), obwohl die Bedingung im CTE stand.
        2. Alles lesen, in C# auswaehlen -- lief, kostete aber ueber Npgsql
           gemessen 466 bis 489 ms fuer 2.392 Zeilen.
        3. Je Horizont eine Indexsuche mit `FETCH NEXT 1`. Ueber psql gemessen
           3,5 bis 11,6 ms je Abfrage, also rechnerisch besser. Ueber den
           TREIBER DER ANWENDUNG gemessen: **1.426 bis 1.947 ms** fuer
           dieselben neun Zeilen -- dreimal schlechter als Fassung 2.

        Also zurueck zu Fassung 2. Und die Lehre dazu steht schon in
        CLAUDE.md, ich bin trotzdem hineingelaufen: Wer ein fremdes Backend
        misst, misst mit dem Treiber der Anwendung. `psql` schickt einfache
        Abfragen mit Literalen; Npgsql schickt Parse/Bind/Execute. Neun kleine
        Abfragen sind dort neunmal dieser Weg, und auf diesem Node kostet das
        mehr als eine grosse Abfrage mit dreitausend Zeilen.

        Die 466 ms bleiben trotzdem zu viel fuer eine Kursansicht. Deshalb
        liegt davor der `Prognosespeicher` -- dieselbe Ueberlegung wie beim
        Kursspeicher: Die Menge aendert sich nur, wenn ein Lauf sie schreibt,
        und dann weiss die Anwendung es genau.                                */
    public async Task<IReadOnlyList<Forecast>> GetLatestAsync(int assetId, CancellationToken ct = default)
    {
        var gehalten = _speicher?.Neueste(assetId);
        if (gehalten is not null) return gehalten;

        await using var conn = await _factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<Forecast>(new CommandDefinition("""
            SELECT forecast_id AS ForecastId, asset_id AS AssetId,
                   horizon_hours AS HorizonHours, made_at_utc AS MadeAtUtc,
                   target_ts_utc AS TargetTsUtc, base_close AS BaseClose,
                   predicted_close AS PredictedClose, predicted_return AS PredictedReturn,
                   confidence AS Confidence, model_version AS ModelVersion
              FROM dbo.forecast
             WHERE asset_id = @assetId
            """, new { assetId }, commandTimeout: 60, cancellationToken: ct));

        var neueste = rows
            .GroupBy(f => f.HorizonHours)
            .Select(g => g.OrderByDescending(f => f.MadeAtUtc).First())
            .OrderBy(f => f.HorizonHours)
            .ToList();

        _speicher?.LegeNeueste(assetId, neueste);
        return neueste;
    }

    public async Task<IReadOnlyList<ForecastVsActual>> GetHistoryAsync(
        int assetId, int horizonHours, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default)
    {
        /*  Gehalten wird OHNE Zeitfenster, geschnitten wird in C#.

            Das Fenster einer Kursansicht wandert mit jedem Tag; der Bestand je
            Wert und Horizont nicht. Wer nach Fenster schluesselte, haette bei
            jedem Aufruf einen neuen Schluessel und nie einen Treffer.        */
        var gehalten = _speicher?.Verlauf(assetId, horizonHours);

        if (gehalten is null)
        {
            gehalten = await LadeVerlaufAsync(assetId, horizonHours, ct);
            _speicher?.LegeVerlauf(assetId, horizonHours, gehalten);
        }

        return gehalten
            .Where(f => f.TargetTsUtc >= fromUtc && f.TargetTsUtc <= toUtc)
            .ToList();
    }

    /*  Zwei eingeschraenkte Lesevorgaenge statt eines Verbunds mit
        Fensterfunktion.

        Hier stand ein `WITH ranked AS (… LEFT JOIN dbo.forecast_score …
        ROW_NUMBER() OVER (PARTITION BY target_ts_utc …))`. Das ist die
        Lehrbuchform und auf dem DataCell-Backend die teuerste Variante, die
        man waehlen kann: Der Verbund materialisiert `forecast_score`
        vollstaendig (gemessen 14,1 s fuer einen einzigen Wert), und eine
        Bedingung INNERHALB eines CTE zaehlt dort nicht als Einschraenkung der
        Tabelle. Der Rueckblick kostete dadurch 34 Sekunden.

        Beide Abfragen unten greifen auf einen Index: die erste auf
        `UX_forecast (asset_id, horizon_hours, made_at_utc)`, die zweite auf
        `IX_forecast_score_wert (asset_id, horizon_hours)` aus Migration 047.
        Gemessen ueber Npgsql: 177 bis 182 ms und 51 bis 70 ms.

        Die Zuordnung und das Entdoppeln geschehen in C#. Das ist nicht nur
        schneller, es ist auch die Stelle, an der man es nachlesen kann: „je
        Zielzeitpunkt die zuletzt erstellte Prognose" ist eine Regel ueber die
        Daten, keine Eigenschaft der Datenbank.                               */
    private async Task<ForecastVsActual[]> LadeVerlaufAsync(
        int assetId, int horizonHours, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var prognosen = await conn.QueryAsync<ForecastVsActual>(new CommandDefinition("""
            SELECT forecast_id AS ForecastId, made_at_utc AS MadeAtUtc,
                   target_ts_utc AS TargetTsUtc, base_close AS BaseClose,
                   predicted_close AS PredictedClose, confidence AS Confidence
              FROM dbo.forecast
             WHERE asset_id = @assetId AND horizon_hours = @horizonHours
            """, new { assetId, horizonHours },
            commandTimeout: 120, cancellationToken: ct));

        /*  Zu einem Zielzeitpunkt kann es mehrere Prognosen geben -- etwa eine
            aus dem Backtest und eine aus dem Livebetrieb. Es zaehlt die
            zuletzt erstellte: sie kannte den meisten Kontext.                */
        var jeZiel = prognosen
            .GroupBy(f => f.TargetTsUtc)
            .Select(g => g.OrderByDescending(f => f.MadeAtUtc).First())
            .OrderBy(f => f.TargetTsUtc)
            .ToArray();

        if (jeZiel.Length == 0) return jeZiel;

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

    /*  Treffsicherheit: Zeilen holen, in C# zusammenfassen.

        Das sieht nach dem Gegenteil dessen aus, was in CLAUDE.md steht
        („Filter gehoeren in die Abfrage, nicht dahinter"), und ist es nicht:
        Der FILTER bleibt in der Abfrage. Nur die AGGREGATION wandert heraus,
        und dafuer gibt es einen gemessenen Grund.

        Das EventMesh-DataCell-Backend nutzt die Einschraenkung nicht mehr,
        sobald darueber eine Aggregation sitzt. Gemessen am 27.09.2026 auf
        einer frisch angelegten 50.000-Zeilen-Tabelle mit Index auf der
        gefilterten Spalte, drei verschiedene Werte gegen den
        Ergebnisspeicher:

          SELECT count(*)              WHERE gruppe = 51      1,754 ms
          SELECT gruppe, count(*)      WHERE gruppe = 51
                                       GROUP BY gruppe      105,605 ms
          SELECT count(*), avg(wert)   WHERE gruppe = 51     73,552 ms

        Faktor 60 bis 80, nur weil ein GROUP BY oder ein zweites Aggregat
        dazukommt. Auf `forecast_score` mit 483.588 Zeilen wurden daraus
        **3.912 ms** -- und zwar fuer ein Ergebnis von sieben Zeilen. Diese
        eine Abfrage steckte in jeder Kursansicht mit eingeblendeter Prognose
        und machte den Unterschied zwischen 33 ms (SQL Server) und 3.855 ms.

        Roh gelesen sind es je Wert einige hundert bis zweitausend Zeilen ueber
        `IX_forecast_score_wert (asset_id, horizon_hours)`. Die kosten auf
        demselben Node einstellige Millisekunden -- und das erst, seit der
        Draht nicht mehr einen Systemaufruf je Zeile macht. Vor dieser
        Korrektur waere die Umgehung langsamer gewesen als das Problem.

        Es IST eine Umgehung. Der Befund ist beim Backend gemeldet; faellt er
        dort, gehoert das Aggregat zurueck in die Abfrage. Bis dahin steht
        hier die Rechnung, die jede Datenbank sonst selbst macht.              */
    public async Task<IReadOnlyList<(int HorizonHours, int N, double Mape, double HitRate)>>
        GetAccuracyAsync(int? assetId, CancellationToken ct = default)
    {
        var gehalten = _speicher?.Guete(assetId);
        if (gehalten is not null) return gehalten;

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

        var posten = new Dictionary<int, Gueteposten>();

        /*  GEPUFFERT lesen, nicht ungepuffert -- und das ist die Umkehrung
            dessen, was hier bis eben stand.

            Ich hatte `QueryUnbufferedAsync` gewaehlt, um den Speicher flach zu
            halten: „die kleinere Menge gehoert gehalten, die groessere
            durchgereicht". Das klingt vernuenftig und war hier um Faktor 30
            falsch. Gemessen am 27.09.2026 ueber Npgsql gegen denselben Node,
            dieselbe Abfrage, 993 Zeilen:

              rohes ADO.NET                        93 bis 151 ms
              Dapper QueryAsync (gepuffert)         68 bis  85 ms
              Dapper QueryUnbufferedAsync        2.108 bis 2.151 ms

            Der ungepufferte Weg kostet je Zeile eine eigene asynchrone
            Fortsetzung; bei knapp tausend Zeilen sind das zwei Sekunden reiner
            Verwaltungsaufwand. In der Kursansicht war das die gesamte
            gemessene Zeit der Treffsicherheit -- 2.087 ms von 2.100 ms.

            Die Lehre ist unangenehm und gehoert aufgeschrieben: Ich habe den
            Aufwand einer Optimierung geschaetzt statt gemessen, und zwar
            ausgerechnet in einer Sitzung, in der ich genau das mehrfach
            angemahnt habe. Knapp tausend Zeilen passen in jeden Speicher;
            „sparsam" war hier kein Argument, sondern eine Angewohnheit.      */
        var zeilen = await conn.QueryAsync<(int Horizont, double? Fehler, bool? Treffer)>(
            new CommandDefinition($"""
                SELECT horizon_hours, abs_pct_error, direction_correct
                  FROM dbo.forecast_score
                 {wertefilter}
                """, new { assetId }, commandTimeout: 300, cancellationToken: ct));

        foreach (var z in zeilen)
        {
            if (z.Fehler is null || z.Treffer is null) continue;

            if (!posten.TryGetValue(z.Horizont, out var p))
                posten[z.Horizont] = p = new Gueteposten();

            p.N++;
            p.SummeFehler += z.Fehler.Value;
            if (z.Treffer.Value) p.Treffer++;
        }

        var ergebnis = posten
            .Where(e => e.Value.N > 0)
            .OrderBy(e => e.Key)
            .Select(e => (e.Key, e.Value.N,
                          e.Value.SummeFehler / e.Value.N,
                          (double)e.Value.Treffer / e.Value.N))
            .ToList();

        _speicher?.LegeGuete(assetId, ergebnis);
        return ergebnis;
    }

    /// <summary>
    /// Summen statt Mittelwerte — der Mittelwert von Mittelwerten ist nicht
    /// der Mittelwert, und ueber Werte hinweg wird hier zusammengefasst.
    /// </summary>
    private sealed class Gueteposten
    {
        public int N;
        public double SummeFehler;
        public int Treffer;
    }

    /*  Wert und Horizont an den Altzeilen nachtragen.

        Migration 047 legt die Spalten an, fuellt sie aber nicht: Ein
        Migrationsskript, das 484.000 Zeilen ueber einen Verbund nachzieht,
        scheitert auf diesem Backend an genau dem Verbund, dessentwegen die
        Spalten ueberhaupt entstehen.

        In BLOECKEN, und das ist die Lehre aus zwei Fehlschlaegen: Der erste
        Entwurf baute eine einzige Massenkopie ueber alle 483.588 Zeilen. Die
        lief minutenlang, und in dieser Zeit genuegte ein Neustart des Node
        (der Kollege deployte gerade), um den ganzen Lauf zu verlieren -- ohne
        Teilergebnis, denn geschrieben wurde erst am Ende. Ein Lauf, der nur
        ganz oder gar nicht gelingt, gelingt bei einer halben Stunde Dauer
        irgendwann gar nicht mehr.

        Jetzt schreibt jeder Block fuer sich. Bricht der Lauf ab, ist alles
        bis zum letzten Block getan, und der naechste Start macht dort weiter
        -- die Auswahl ist `asset_id IS NULL`, also selbstheilend.             */
    private const int NachtragBlock = 50_000;

    public async Task<int> NachtragenAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var offen = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CAST(COUNT(*) AS INT) FROM dbo.forecast_score WHERE asset_id IS NULL",
            commandTimeout: 300, cancellationToken: ct));

        if (offen == 0) return 0;

        /*  Erst die offenen Kennungen, dann die Prognosen daruebergelegt. Die
            kleinere Menge gehoert gehalten, die groessere durchgereicht.

            Beide Lesevorgaenge gepuffert und NACHEINANDER: Zwei offene Leser
            auf einer Verbindung gehen bei Npgsql nicht, und der zweite Lauf
            ist ohnehin der grosse.                                            */
        var offeneIds = new HashSet<long>(
            await conn.QueryAsync<long>(new CommandDefinition(
                "SELECT forecast_id FROM dbo.forecast_score WHERE asset_id IS NULL",
                commandTimeout: 300, cancellationToken: ct)));

        var tabelle = NeueNachtragstabelle();
        var geschrieben = 0;

        /*  Gepuffert, nicht ungepuffert: Dapper zahlt je Zeile eine eigene
            asynchrone Fortsetzung, gemessen Faktor 30 gegenueber dem
            gepufferten Weg (993 Zeilen: 2.151 ms gegen 68 ms). Bei 1,26
            Millionen Zeilen waeren das Minuten reiner Verwaltungsaufwand.
            Die Liste selbst kostet rund 40 MB und lebt nur waehrend dieses
            einen Laufs.                                                     */
        var prognosen = await conn.QueryAsync<(long ForecastId, int AssetId, int HorizonHours)>(
            new CommandDefinition(
                "SELECT forecast_id, asset_id, horizon_hours FROM dbo.forecast",
                commandTimeout: 600, cancellationToken: ct));

        foreach (var f in prognosen)
        {
            if (!offeneIds.Contains(f.ForecastId)) continue;

            tabelle.Rows.Add(f.ForecastId, f.AssetId, f.HorizonHours);

            if (tabelle.Rows.Count < NachtragBlock) continue;

            geschrieben += await BlockSchreibenAsync(conn, tabelle, ct);
            tabelle = NeueNachtragstabelle();
        }

        if (tabelle.Rows.Count > 0)
            geschrieben += await BlockSchreibenAsync(conn, tabelle, ct);

        return geschrieben;
    }

    private static DataTable NeueNachtragstabelle()
    {
        var t = new DataTable();
        t.Columns.Add("forecast_id", typeof(long));
        t.Columns.Add("asset_id", typeof(int));
        t.Columns.Add("horizon_hours", typeof(int));
        return t;
    }

    private async Task<int> BlockSchreibenAsync(
        System.Data.Common.DbConnection conn, DataTable tabelle, CancellationToken ct)
    {
        // Eindeutiger Name je Block -- siehe SqlDialekt.EindeutigerTempName.
        var stufe = SqlDialekt.EindeutigerTempName("fsnach");

        await conn.ExecuteAsync(new CommandDefinition($"""
            {d.CreateTemp(stufe)} (
              forecast_id BIGINT NOT NULL PRIMARY KEY,
              asset_id INT NOT NULL,
              horizon_hours INT NOT NULL);
            """, commandTimeout: 120, cancellationToken: ct));

        try
        {
            await Massenkopie.SchreibeAsync(conn, tabelle, d.Temp(stufe), 600, ct);

            return await conn.ExecuteAsync(new CommandDefinition($"""
                MERGE INTO dbo.forecast_score {d.MergeSperre} AS t
                USING (SELECT forecast_id, asset_id, horizon_hours FROM {d.Temp(stufe)}) AS s
                   ON t.forecast_id = s.forecast_id
                WHEN MATCHED THEN UPDATE SET
                      asset_id = s.asset_id, horizon_hours = s.horizon_hours;
                """, commandTimeout: 900, cancellationToken: ct));
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
