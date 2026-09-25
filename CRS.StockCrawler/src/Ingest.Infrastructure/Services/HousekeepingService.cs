using System.Diagnostics;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Infrastructure.Datenbank;
using Ingest.Infrastructure.Options;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ingest.Infrastructure.Services;

/// <summary>Was eine Regel in einem Lauf bewirkt hat.</summary>
/// <param name="Verlust">Was mit diesen Zeilen verloren geht — die Kehrseite,
/// die zu jeder Löschregel gehört.</param>
public sealed record HausmeisterRegelErgebnis(
    string Schluessel, string Titel, string Tabelle, int FristTage,
    long Zeilen, double Sekunden, string Begruendung, string Verlust,
    bool Abgeschaltet, string? Fehler);

public sealed record HausmeisterLauf(
    int LaufId, DateTime GestartetUtc, DateTime? BeendetUtc, bool Probe,
    string Ausgeloest, int Regeln, long Zeilen, double? DauerSekunden, string? Note,
    IReadOnlyList<HausmeisterRegelErgebnis> Ergebnisse);

/// <summary>Was eine Tabelle heute belegt — für die Anzeige.</summary>
public sealed record TabellenGroesse(string Tabelle, long Zeilen, double Megabyte);

public sealed record HausmeisterStand(
    HausmeisterLauf? LetzterLauf,
    IReadOnlyList<HausmeisterLauf> Laeufe,
    IReadOnlyList<TabellenGroesse> Groessen,
    double SummeMegabyte,
    string Hinweis);

public interface IHousekeepingService
{
    /// <summary>
    /// Räumt auf. <paramref name="probe"/> = <c>true</c> zählt nur und löscht
    /// nichts — das ist der Weg, den die Oberfläche zuerst geht.
    /// </summary>
    Task<HausmeisterLauf> LaufeAsync(bool probe, string ausgeloest = "hand",
                                     CancellationToken ct = default);

    /// <summary>Protokoll der letzten Läufe und die heutigen Tabellengrößen.</summary>
    Task<HausmeisterStand> StandAsync(int laeufe = 10, CancellationToken ct = default);
}

/// <summary>
/// Der Hausmeister: löscht, was keine Ansicht und keine Messung mehr liest.
///
/// <para><b>Die Regel hinter den Regeln.</b> Eine Zeile darf weg, wenn sie
/// nachweislich niemand mehr liest — nicht, wenn sie alt ist. Deshalb steht in
/// jeder Regel unten, <i>wer</i> sie las und <i>was verloren geht</i>. Der
/// Bestand ist in diesem Projekt die Beweislage; wer ihn kürzt, kürzt sie mit.</para>
///
/// <para><b>Was nie angetastet wird</b>, unabhängig von jeder Einstellung:
/// Kurse (<c>price_bar</c>), Werte, Depot und Buchungen, Benutzer und
/// Sitzungen, Säulengewichte, die gelernten Modellgewichte
/// (<c>model_weight</c> — dort steckt alles, was die erste Säule gelernt hat)
/// und <b>jede Prognose, deren Zielzeitpunkt noch aussteht</b>. Diese Tabellen
/// kommen in keiner Regel vor; das ist der Schutz, nicht eine Einstellung.</para>
///
/// <para><b>Probelauf zuerst.</b> Ein Löschlauf, den man nicht vorher sehen
/// kann, ist ein Vertrauensakt. Der Probelauf zählt mit denselben Bedingungen,
/// mit denen später gelöscht wird — nicht mit ähnlichen.</para>
///
/// <para><b>Blockweise und mit Zeitbudget.</b> Ein DELETE über sechs Millionen
/// Zeilen sperrt die Tabelle und bläht das Protokoll; währenddessen steht der
/// stündliche Kursabruf. Gelöscht wird in Blöcken, und wenn das Budget abläuft,
/// bricht der Lauf sauber ab — der nächste Tag macht weiter.</para>
/// </summary>
public sealed class HousekeepingService(
    ISqlConnectionFactory factory,
    IKnowledgeService knowledge,
    IReasoningLogService reasoningLog,
    IOptions<HousekeepingOptions> optionen,
    ILogger<HousekeepingService> log) : IHousekeepingService
{
    private SqlDialekt d => factory.Dialekt;
    private HousekeepingOptions O => optionen.Value;

    /// <summary>Ein Löschschritt: eine Tabelle, eine Bedingung, ein Schlüssel.</summary>
    private sealed record Schritt(string Tabelle, string Schluessel, string Bedingung);

    private sealed record Regel(
        string Schluessel, string Titel, string Tabelle, int FristTage,
        string Begruendung, string Verlust, IReadOnlyList<Schritt> Schritte,
        Func<bool, CancellationToken, Task<long>>? Eigen = null);

    // ================================================================ Lauf ==

    public async Task<HausmeisterLauf> LaufeAsync(bool probe, string ausgeloest = "hand",
                                                  CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var budget = TimeSpan.FromMinutes(Math.Clamp(O.BudgetMinuten, 1, 240));
        var regeln = Regeln();

        await using var conn = await factory.OpenAsync(ct);

        var laufId = await conn.ExecuteScalarAsync<int>(new CommandDefinition($"""
            INSERT INTO dbo.housekeeping_lauf (probe, ausgeloest) {d.RueckgabeVor("lauf_id")}
            VALUES (@probe, @ausgeloest) {d.RueckgabeNach("lauf_id")}
            """, new { probe, ausgeloest }, cancellationToken: ct));

        var ergebnisse = new List<HausmeisterRegelErgebnis>();
        long gesamt = 0;
        string? note = null;

        foreach (var r in regeln)
        {
            if (sw.Elapsed > budget)
            {
                note = $"Zeitbudget von {budget.TotalMinutes:0} Minuten erreicht — "
                     + $"{regeln.Count - ergebnisse.Count} Regeln stehen für den nächsten Lauf aus.";
                break;
            }

            var rsw = Stopwatch.StartNew();
            long zeilen = 0;
            string? fehler = null;

            if (r.FristTage <= 0)
            {
                ergebnisse.Add(new HausmeisterRegelErgebnis(
                    r.Schluessel, r.Titel, r.Tabelle, 0, 0, 0, r.Begruendung, r.Verlust, true, null));
                continue;
            }

            try
            {
                zeilen = r.Eigen is not null
                    ? await r.Eigen(probe, ct)
                    : await SchritteAusfuehrenAsync(conn, r, probe, sw, budget, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                fehler = "abgebrochen (Zeitbudget)";
            }
            catch (Exception ex)
            {
                /*  Eine Regel, die scheitert, darf die uebrigen nicht mitreissen:
                    Sie betreffen verschiedene Tabellen und haben nichts
                    miteinander zu tun. Der Fehler steht im Protokoll.         */
                fehler = ex.Message.Length > 380 ? ex.Message[..380] : ex.Message;
                log.LogWarning(ex, "Hausmeister-Regel {Regel} übersprungen", r.Schluessel);
            }

            gesamt += zeilen;
            ergebnisse.Add(new HausmeisterRegelErgebnis(
                r.Schluessel, r.Titel, r.Tabelle, r.FristTage, zeilen,
                Math.Round(rsw.Elapsed.TotalSeconds, 1), r.Begruendung, r.Verlust, false, fehler));

            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO dbo.housekeeping_regel (lauf_id, schluessel, tabelle, frist_tage, zeilen, dauer_s, fehler)
                VALUES (@laufId, @schluessel, @tabelle, @frist, @zeilen, @dauer, @fehler)
                """, new
                {
                    laufId, schluessel = r.Schluessel, tabelle = r.Tabelle, frist = r.FristTage,
                    zeilen, dauer = Math.Round(rsw.Elapsed.TotalSeconds, 1), fehler
                }, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition($"""
            UPDATE dbo.housekeeping_lauf
               SET beendet_utc = {d.Jetzt}, regeln = @regeln, zeilen = @zeilen,
                   dauer_s = @dauer, note = @note
             WHERE lauf_id = @laufId
            """, new
            {
                laufId, regeln = ergebnisse.Count(e => !e.Abgeschaltet), zeilen = gesamt,
                dauer = Math.Round(sw.Elapsed.TotalSeconds, 1), note
            }, cancellationToken: ct));

        log.LogInformation("Hausmeister{Probe}: {Zeilen} Zeilen über {Regeln} Regeln in {S:F0} s",
            probe ? " (Probelauf)" : "", gesamt, ergebnisse.Count(e => !e.Abgeschaltet), sw.Elapsed.TotalSeconds);

        return new HausmeisterLauf(laufId, DateTime.UtcNow - sw.Elapsed, DateTime.UtcNow, probe,
            ausgeloest, ergebnisse.Count(e => !e.Abgeschaltet), gesamt,
            Math.Round(sw.Elapsed.TotalSeconds, 1), note, ergebnisse);
    }

    /// <summary>
    /// Zählt (Probe) oder löscht blockweise. Die Schritte einer Regel laufen in
    /// der angegebenen Reihenfolge — abhängige Tabellen zuerst, sonst steht der
    /// Fremdschlüssel im Weg.
    /// </summary>
    private async Task<long> SchritteAusfuehrenAsync(
        System.Data.Common.DbConnection conn, Regel r, bool probe,
        Stopwatch sw, TimeSpan budget, CancellationToken ct)
    {
        long gesamt = 0;

        foreach (var s in r.Schritte)
        {
            if (probe)
            {
                gesamt += await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    $"SELECT COUNT_BIG(*) FROM {s.Tabelle} WHERE {s.Bedingung}",
                    new { tage = r.FristTage }, commandTimeout: 600, cancellationToken: ct));
                continue;
            }

            while (true)
            {
                if (sw.Elapsed > budget) throw new OperationCanceledException();

                var weg = await conn.ExecuteAsync(new CommandDefinition(
                    d.LoescheBlock(s.Tabelle, s.Schluessel, s.Bedingung, "block"),
                    new { tage = r.FristTage, block = Math.Clamp(O.Blockgroesse, 1000, 200_000) },
                    commandTimeout: 600, cancellationToken: ct));

                gesamt += weg;
                if (weg == 0) break;
            }
        }

        return gesamt;
    }

    // ============================================================== Regeln ==

    /// <summary>
    /// Die Regeln in der Reihenfolge, in der sie laufen: die grössten und
    /// eindeutigsten zuerst, damit ein abgelaufenes Zeitbudget den grössten
    /// Posten nicht Tag für Tag verschiebt.
    /// </summary>
    private List<Regel> Regeln()
    {
        // COUNT_BIG gibt es in Postgres nicht; dort ist COUNT ohnehin bigint.
        var jetzt = d.Jetzt;
        string Aelter(string spalte) => $"{spalte} < {d.PlusTage("-@tage", jetzt)}";

        return
        [
            new("kurvenlaeufe", "Überholte Kurvendiskussions-Läufe", "curve_event, curve_link",
                O.KurvenLaeufeTage,
                "Die Kurvendiskussion liest ausschliesslich den jüngsten Lauf je Intervall und "
              + "Glättungsart (MAX(run_id)). Gemessen am 25.09.2026 lagen acht überholte Läufe "
              + "mit zusammen rund 1,5 Millionen Zeilen im Bestand, die nichts mehr liest.",
                "Die Möglichkeit, einen älteren Lauf mit einem neueren zu vergleichen. Der "
              + "jüngste Lauf je Intervall und Glättungsart bleibt immer stehen.",
                [
                    new("dbo.curve_link", "link_id", UeberholteLaeufe()),
                    new("dbo.curve_event", "event_id", UeberholteLaeufe()),
                ]),

            new("rueckrechnung", "Rückrechnung (Walk-Forward)", "forecast_track",
                O.RueckrechnungTage,
                "Die Rückrechnung wurde nachträglich über bereits bekannte Kurse gerechnet und "
              + "belegt deshalb keine Prognosegüte — sie ist aus jeder Auswertung ausgeschlossen. "
              + "Sie dient allein der Anzeige eines durchgehenden Verlaufs in der Kursansicht. "
              + "9,3 Millionen Zeilen, 956 MB — der grösste Posten des Bestands.",
                "Der gezeichnete Verlauf reicht dann nur noch so weit zurück wie die Frist. Neu "
              + "rechnen lässt er sich jederzeit.",
                [new("dbo.forecast_track", "track_id", Aelter("made_at_utc"))]),

            new("prognose_komponenten", "Teilmodell-Anteile alter Prognosen", "forecast_component",
                O.PrognoseKomponentenTage,
                "Die Anteile sagen, welches der fünf Teilmodelle was beigetragen hat. Sie werden "
              + "in der Prognose-Ansicht zur aktuellen Schätzung gezeigt; sobald eine Prognose "
              + "bewertet ist, hat das Ensemble seine Gewichte daraus längst angepasst. "
              + "6,4 Millionen Zeilen, 317 MB.",
                "Für Prognosen jenseits der Frist lässt sich nicht mehr aufschlüsseln, welches "
              + "Teilmodell sie getragen hat. Die Prognose selbst und ihre Bewertung bleiben.",
                [new("dbo.forecast_component", "component_id",
                     $"forecast_id IN (SELECT forecast_id FROM dbo.forecast WHERE {Aelter("made_at_utc")})")]),

            new("unbewertbar", "Dauerhaft unbewertbare Prognosen", "forecast",
                O.UnbewertbareTage,
                "Ihr Zielzeitpunkt fällt in ein geschlossenes Marktfenster — dort kann nie eine "
              + "Bar erscheinen, sie sind als unbewertbar markiert. Gemessen 42.659 Stück; sie "
              + "stehen am Kopf der Bewertungswarteschlange und verdrängen bewertbare Prognosen.",
                "Nichts Messbares: Eine Prognose, die nie bewertet werden kann, trägt zu keiner "
              + "Auswertung bei.",
                [
                    new("dbo.forecast_component", "component_id",
                        $"forecast_id IN (SELECT forecast_id FROM dbo.forecast WHERE unscoreable_utc IS NOT NULL AND {Aelter("target_ts_utc")})"),
                    new("dbo.forecast", "forecast_id",
                        $"unscoreable_utc IS NOT NULL AND {Aelter("target_ts_utc")}"),
                ]),

            new("kreuzungen", "Alte Kreuzungen", "crossing",
                O.KreuzungenTage,
                "Die Kreuzungs-Bewährung misst die Trefferquote früherer Kreuzungen über die "
              + "vorhandene Historie; zwei Jahre sind der Zeitraum, über den die Ranglisten "
              + "rechnen. 7,2 Millionen Zeilen, 431 MB.",
                "Kreuzungen jenseits der Frist zählen nicht mehr in die Bewährung. Bei einer "
              + "Neuberechnung der Analyse entstehen sie aus den Kursen neu.",
                [new("dbo.crossing", "crossing_id", Aelter("ts_utc"))]),

            new("bewertete_prognosen", "Bewertete Prognosen samt Bewertung", "forecast",
                O.BewertetePrognosenTage,
                "Bewertete Prognosen tragen die Lernkurve und die Güte-Ranglisten. Ein Jahr "
              + "deckt jede Aussage ab, die diese Ansichten treffen; darüber hinaus ist jede "
              + "einzelne Zeile entbehrlich. OFFENE Prognosen — Zielzeitpunkt in der Zukunft — "
              + "sind ausgenommen, unabhängig von ihrem Alter.",
                "Die Lernkurve reicht nur noch so weit zurück wie die Frist. Das Gelernte selbst "
              + "steckt in den Modellgewichten und bleibt unberührt.",
                [
                    new("dbo.forecast_component", "component_id",
                        $"forecast_id IN (SELECT forecast_id FROM dbo.forecast f WHERE {Aelter("f.made_at_utc")} AND f.target_ts_utc < {jetzt})"),
                    new("dbo.forecast_score_combined", "forecast_id",
                        $"forecast_id IN (SELECT forecast_id FROM dbo.forecast f WHERE {Aelter("f.made_at_utc")} AND f.target_ts_utc < {jetzt})"),
                    new("dbo.forecast_score", "forecast_id",
                        $"forecast_id IN (SELECT forecast_id FROM dbo.forecast f WHERE {Aelter("f.made_at_utc")} AND f.target_ts_utc < {jetzt})"),
                    new("dbo.forecast", "forecast_id",
                        $"{Aelter("made_at_utc")} AND target_ts_utc < {jetzt}"),
                ]),

            new("autopilot_protokoll", "Autopilot-Protokoll", "autopilot_lauf, autopilot_entscheidung",
                O.AutopilotProtokollTage,
                "Je Lauf und geprüftem Wert eine Zeile — bei 600 Werten und wöchentlichem Takt "
              + "rund 31.000 Zeilen im Jahr. Die Buchungen und der Vermögensverlauf des Depots "
              + "stehen woanders und bleiben.",
                "Die Begründung einzelner Kauf- und Verkaufsentscheidungen jenseits der Frist. "
              + "Was tatsächlich gebucht wurde, bleibt im Depot nachvollziehbar.",
                [
                    new("dbo.autopilot_entscheidung", "entscheidung_id",
                        $"lauf_id IN (SELECT lauf_id FROM dbo.autopilot_lauf WHERE {Aelter("gestartet_utc")})"),
                    new("dbo.autopilot_lauf", "lauf_id", Aelter("gestartet_utc")),
                ]),

            new("laufprotokoll", "Laufprotokoll der Abrufe", "ingest_run",
                O.LaufprotokollTage,
                "Eine Zeile je Kursabruf, Analyse- und Prognoselauf. Für die Frage „läuft der "
              + "Zeitplan?" + "“" + " reichen die letzten Wochen.",
                "Die Geschichte der Läufe jenseits der Frist.",
                [new("dbo.ingest_run", "run_id", Aelter("started_utc"))]),

            new("eigenes_protokoll", "Eigenes Protokoll", "housekeeping_lauf, housekeeping_regel",
                O.EigenesProtokollTage,
                "Der Hausmeister räumt auch hinter sich auf.",
                "Ältere Aufräumläufe sind nicht mehr nachvollziehbar.",
                [
                    new("dbo.housekeeping_regel", "regel_id",
                        $"lauf_id IN (SELECT lauf_id FROM dbo.housekeeping_lauf WHERE {Aelter("gestartet_utc")})"),
                    new("dbo.housekeeping_lauf", "lauf_id", Aelter("gestartet_utc")),
                ]),

            /*  Zwei Regeln mit eigenem Weg: Nachrichten haengen an Qdrant, und
                das Antwortprotokoll kennt seine gemerkten Zeilen selbst.      */
            new("nachrichten", "Alte Nachrichtenartikel", "knowledge_source, knowledge_chunk",
                O.NachrichtenTage,
                "Gemessen trägt die Nachrichtenstimmung nichts zur Prognose bei (Korrelation "
              + "0,04 gegen eine Zufallsschwelle von 0,08); sie hängt mit heute zusammen, nicht "
              + "mit morgen. Was zählt, sind die jüngsten Meldungen. Rund 15.000 Artikel im "
              + "Monat, mit ihren Abschnitten der grösste Textposten.",
                "Ältere Meldungen verschwinden aus der Suche und aus dem Journal. Die Feeds "
              + "selbst bleiben; nur die abgelegten Artikel werden gelöscht — samt ihrer "
              + "Vektoren in Qdrant, damit dort keine verwaisten Punkte zurückbleiben.",
                [],
                Eigen: NachrichtenAsync),

            new("reasoning_protokoll", "Antworten des Agenten", "reasoning_log",
                O.ReasoningProtokollTage,
                "Jede Antwort wird abgelegt, damit sie nachprüfbar ist. Als GEMERKT markierte "
              + "Antworten bleiben unabhängig vom Alter — sie wurden ausdrücklich behalten.",
                "Nicht gemerkte Antworten jenseits der Frist samt ihrer Werkzeugspur.",
                [],
                Eigen: async (probe, ct) => probe
                    ? await OffeneReasoningAsync(ct)
                    : await reasoningLog.AufraeumenAsync(O.ReasoningProtokollTage, ct)),
        ];

        /*  Ueberholt ist ein Lauf, der weder der juengste seiner Art ist noch
            in die Frist faellt. „Seiner Art" heisst: je Intervall UND je
            Glaettung (kausal/zentriert) -- die Oberflaeche zeigt beide.      */
        string UeberholteLaeufe() =>
            $"""
             run_id NOT IN (
                 SELECT MAX(r2.run_id) FROM dbo.curve_run r2
                  WHERE r2.finished_utc IS NOT NULL
                  GROUP BY r2.interval_code, r2.causal)
             AND run_id IN (
                 SELECT r3.run_id FROM dbo.curve_run r3
                  WHERE r3.started_utc < {d.PlusTage("-@tage", d.Jetzt)})
             """;
    }

    /// <summary>Wie viele Antworten der Agent jenseits der Frist stehen hat, ohne sie zu löschen.</summary>
    private async Task<long> OffeneReasoningAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition($"""
            SELECT COUNT(*) FROM dbo.reasoning_log
             WHERE asked_utc < {d.PlusTage("-@tage", d.Jetzt)} AND gemerkt = {d.Falsch}
            """, new { tage = O.ReasoningProtokollTage }, cancellationToken: ct));
    }

    /// <summary>
    /// Alte Nachrichtenartikel — über den Wissensdienst, nicht per DELETE.
    ///
    /// <para>Ein Artikel besteht aus einer Zeile in <c>knowledge_source</c>,
    /// seinen Abschnitten in <c>knowledge_chunk</c> und seinen Vektoren in
    /// Qdrant. Wer nur die Datenbank aufräumt, hinterlässt verwaiste Vektoren —
    /// die erzeugen keinen Fehler, sondern <i>weniger Treffer</i>: Die Suche
    /// findet den Punkt, schlägt den Text nach, findet nichts und lässt das
    /// Ergebnis still verschwinden. Deshalb geht der Weg über
    /// <c>IKnowledgeService.DeleteAsync</c>, das beide Seiten räumt.</para>
    /// </summary>
    private async Task<long> NachrichtenAsync(bool probe, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);

        var ids = (await conn.QueryAsync<int>(new CommandDefinition($"""
            SELECT source_id FROM dbo.knowledge_source
             WHERE pillar = 'semantic' AND kind = 'article'
               AND COALESCE(published_utc, added_utc) < {d.PlusTage("-@tage", d.Jetzt)}
            """, new { tage = O.NachrichtenTage }, commandTimeout: 300, cancellationToken: ct))).ToList();

        if (probe || ids.Count == 0) return ids.Count;

        long weg = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try { await knowledge.DeleteAsync(id, ct); weg++; }
            catch (Exception ex)
            {
                // Ein Artikel, der sich nicht löschen lässt, hält die übrigen nicht auf.
                log.LogDebug(ex, "Artikel {Id} nicht gelöscht", id);
            }
        }
        return weg;
    }

    // =============================================================== Stand ==

    public async Task<HausmeisterStand> StandAsync(int laeufe = 10, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var kopf = (await conn.QueryAsync<(int LaufId, DateTime Gestartet, DateTime? Beendet, bool Probe,
                                           string Ausgeloest, int Regeln, long Zeilen, double? Dauer, string? Note)>(
            new CommandDefinition("""
                SELECT lauf_id, gestartet_utc, beendet_utc, probe, ausgeloest, regeln, zeilen, dauer_s, note
                  FROM dbo.housekeeping_lauf
                 ORDER BY lauf_id DESC OFFSET 0 ROWS FETCH NEXT (@n) ROWS ONLY
                """, new { n = Math.Clamp(laeufe, 1, 100) }, cancellationToken: ct))).ToList();

        var regeln = kopf.Count == 0
            ? []
            : (await conn.QueryAsync<(int LaufId, string Schluessel, string Tabelle, int Frist, long Zeilen, double? Dauer, string? Fehler)>(
                new CommandDefinition($"""
                    SELECT lauf_id, schluessel, tabelle, frist_tage, zeilen, dauer_s, fehler
                      FROM dbo.housekeeping_regel
                     WHERE {d.In("lauf_id", "ids")}
                     ORDER BY regel_id
                    """, new { ids = kopf.Select(k => k.LaufId).ToArray() }, cancellationToken: ct))).ToList();

        var titel = Regeln().ToDictionary(r => r.Schluessel);

        var laeufeListe = kopf.Select(k => new HausmeisterLauf(
            k.LaufId, k.Gestartet, k.Beendet, k.Probe, k.Ausgeloest, k.Regeln, k.Zeilen, k.Dauer, k.Note,
            regeln.Where(r => r.LaufId == k.LaufId).Select(r => new HausmeisterRegelErgebnis(
                r.Schluessel,
                titel.TryGetValue(r.Schluessel, out var t) ? t.Titel : r.Schluessel,
                r.Tabelle, r.Frist, r.Zeilen, r.Dauer ?? 0,
                t?.Begruendung ?? "", t?.Verlust ?? "", false, r.Fehler)).ToList())).ToList();

        var groessen = await GroessenAsync(conn, ct);

        return new HausmeisterStand(
            laeufeListe.FirstOrDefault(), laeufeListe, groessen,
            Math.Round(groessen.Sum(g => g.Megabyte), 1),
            "Der Probelauf zählt mit denselben Bedingungen, mit denen später gelöscht wird. "
          + "Kurse, Werte, Depot, Benutzer, Säulen- und Modellgewichte kommen in keiner Regel "
          + "vor — sie sind nicht eine Einstellung, sondern gar nicht erst gefährdet.");
    }

    /// <summary>
    /// Die zehn grössten Tabellen. Die Abfrage ist die einzige Stelle mit
    /// echtem Systemkatalog-SQL — sie lässt sich nicht portabel schreiben, und
    /// eine Zahl aus dem Katalog ist besser als eine geschätzte.
    /// </summary>
    private async Task<IReadOnlyList<TabellenGroesse>> GroessenAsync(
        System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        try
        {
            var sql = d is SqlServerDialekt
                ? """
                  SELECT TOP 12 t.name AS Tabelle, MAX(p.rows) AS Zeilen,
                         CAST(SUM(a.total_pages) * 8.0 / 1024 AS FLOAT) AS Megabyte
                    FROM sys.tables t
                    JOIN sys.indexes i ON i.object_id = t.object_id
                    JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id = i.index_id
                    JOIN sys.allocation_units a ON a.container_id = p.partition_id
                   WHERE i.index_id IN (0, 1)
                   GROUP BY t.name
                   ORDER BY SUM(a.total_pages) DESC
                  """
                : """
                  SELECT c.relname AS "Tabelle",
                         CAST(c.reltuples AS bigint) AS "Zeilen",
                         pg_total_relation_size(c.oid) / 1048576.0 AS "Megabyte"
                    FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relkind = 'r' AND n.nspname IN ('dbo', 'public')
                   ORDER BY pg_total_relation_size(c.oid) DESC
                   LIMIT 12
                  """;

            var rows = await conn.QueryAsync<TabellenGroesse>(
                new CommandDefinition(sql, commandTimeout: 120, cancellationToken: ct));

            return rows.Select(r => r with { Megabyte = Math.Round(r.Megabyte, 1) }).ToList();
        }
        catch (Exception ex)
        {
            // Fehlt das Recht auf den Katalog, ist das kein Grund, die Seite scheitern zu lassen.
            log.LogDebug(ex, "Tabellengrößen nicht verfügbar");
            return [];
        }
    }
}
