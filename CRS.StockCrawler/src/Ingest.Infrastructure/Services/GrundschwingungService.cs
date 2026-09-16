using System.Data;
using System.Diagnostics;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Analysis.Spectral;
using Ingest.Core.Models;
using Ingest.Infrastructure.Datenbank;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Der Lauf über alle Werte, die Ablage und die Ansichten des Katalogs.
///
/// <para><b>Was gespeichert wird und was nicht.</b> Gespeichert werden die
/// Akkorde (je Wert und Epoche), die Klassen und die Paare — alles, was die
/// Oberfläche zeigt. Die rohen Spektralspitzen gehen in <c>freq_pattern</c>,
/// damit ein Zweifler die Akkordbildung nachrechnen kann. Nicht gespeichert
/// wird die Skizze: Sie ist eine Funktion der Klassenperioden und wird beim
/// Lesen gebildet.</para>
///
/// <para><b>Nur der jüngste Lauf bleibt.</b> Ein neuer Lauf löscht die Zeilen
/// des alten — der Katalog ist ein Stand, keine Geschichte. Die Geschichte der
/// Läufe (wann, wie viele Klassen) bleibt in <c>freq_run</c>.</para>
/// </summary>
public sealed class GrundschwingungService(
    ISqlConnectionFactory factory,
    IAssetRepository assets,
    IPriceBarRepository bars,
    ILogger<GrundschwingungService> log) : IGrundschwingungService
{
    private SqlDialekt d => factory.Dialekt;

    private const int EpochBars = 1024;
    private const int MindestBars = EpochBars + 40;

    // ================================================================ Lauf ==

    public async Task<GrundschwingungLauf> LaufeAsync(string interval = "1d", CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var verfolgt = await assets.GetTrackedAsync(ct);
        var ids = verfolgt.Select(a => a.AssetId).ToArray();
        var namen = verfolgt.ToDictionary(a => a.AssetId, a => a.Symbol);

        /*  Alle Kurse in einem Zug je Block, nicht je Wert: 546 Werte à einer
            Fahrt zur Datenbank wären bei 3,9 Millionen Bars der langsamste Teil
            des Laufs. Die Rechnung selbst kostet gemessen 0,2 s je Wert.       */
        var bis = DateTime.UtcNow; var von = bis.AddYears(-25);
        var muster = new List<FreqPattern>();
        var mitDaten = 0;

        /*  Epochen auf einem KALENDERRASTER, nicht je Wert vom Reihenanfang.

            Der erste Lauf zaehlte die Epochen je Wert von seinem eigenen ersten
            Bar aus. Damit endete keine Epoche zweier Werte am selben Tag, und
            "dieselbe Klasse in derselben Epoche" traf praktisch nie zu: 311
            Klassen, zwei Paare. Gleichzeitigkeit muss aus dem Kalender kommen.
            Jede Epoche endet deshalb an einem Ankertag -- heute, vor einem Jahr,
            vor zwei Jahren ... -- und umfasst die 1024 Bars davor. Bei Aktien
            sind das rund vier Jahre, bei Krypto knapp drei; das Fenster ist in
            Bars gleich, in Kalenderzeit nicht. Das steht in der Doku.          */
        var anker = Enumerable.Range(0, 30).Select(k => bis.Date.AddDays(-365 * k)).ToList();

        foreach (var block in SqlBatching.Chunks(ids, 100))
        {
            var reihen = await bars.GetManyAsync(block, interval, von, bis, ct);
            foreach (var (id, liste) in reihen)
            {
                if (liste.Count < MindestBars) continue;
                mitDaten++;
                var closes = liste.Select(b => (double)b.Close).ToArray();
                var stamps = liste.Select(b => b.TsUtc).ToArray();

                foreach (var a in anker)
                {
                    // Index des letzten Bars am oder vor dem Anker.
                    var ende = Array.BinarySearch(stamps, a);
                    if (ende < 0) ende = ~ende - 1;
                    if (ende + 1 < EpochBars) break;   // weiter zurueck wird es nur kuerzer

                    var fenster = closes[(ende + 1 - EpochBars)..(ende + 1)];
                    var stempel = stamps[(ende + 1 - EpochBars)..(ende + 1)];
                    foreach (var m in FrequencyPatterns.Extract(id, namen[id], fenster, stempel, EpochBars, EpochBars))
                        muster.Add(m with { EpochTo = a });
                }
            }
            ct.ThrowIfCancellationRequested();
        }

        var akkorde = Grundschwingungen.Akkorde(muster);
        var katalog = Grundschwingungen.Katalog(akkorde);
        var paareJeKlasse = katalog.ToDictionary(k => k.Nr, k => Grundschwingungen.Paare(k));

        // ------------------------------------------------------- Ablage ----
        await using var conn = await factory.OpenAsync(ct);

        var runId = await conn.ExecuteScalarAsync<int>(new CommandDefinition($"""
            INSERT INTO dbo.freq_run (interval_code, started_utc) {d.RueckgabeVor("run_id")} VALUES (@interval, @started) {d.RueckgabeNach("run_id")}
            """, new { interval, started = bis }, cancellationToken: ct));

        /*  Den vorigen Lauf dieses Intervalls raeumen -- erst jetzt, nachdem der
            neue gerechnet ist. Bricht die Rechnung ab, steht der alte Katalog
            weiter da, statt dass die Seite leer bleibt.                       */
        await conn.ExecuteAsync(new CommandDefinition($"""
            DELETE FROM dbo.freq_match  WHERE interval_code = @interval AND (run_id IS NULL OR run_id <> @runId);
            DELETE FROM dbo.freq_akkord WHERE run_id IN (SELECT run_id FROM dbo.freq_run WHERE interval_code = @interval AND run_id <> @runId);
            DELETE FROM dbo.freq_class  WHERE run_id IN (SELECT run_id FROM dbo.freq_run WHERE interval_code = @interval AND run_id <> @runId);
            DELETE FROM dbo.freq_pattern WHERE interval_code = @interval;
            """, new { interval, runId }, commandTimeout: 300, cancellationToken: ct));

        // Klassen einzeln (wenige hundert), Akkorde und Muster per Massenkopie.
        var classIds = new Dictionary<int, int>();
        foreach (var k in katalog)
        {
            var paare = paareJeKlasse[k.Nr];
            var cid = await conn.ExecuteScalarAsync<int>(new CommandDefinition($"""
                INSERT INTO dbo.freq_class
                    (run_id, nr, stimmen, periode1, periode2, periode3, amp2, amp3, harmonik,
                     akkorde, werte, epochen, prominenz, stabilitaet, paare, paare_bestaendig)
                {d.RueckgabeVor("class_id")}
                VALUES (@runId, @nr, @stimmen, @p1, @p2, @p3, @a2, @a3, @harmonik,
                        @akkorde, @werte, @epochen, @prominenz, @stabilitaet, @paare, @pb)
                {d.RueckgabeNach("class_id")}
                """, new
                {
                    runId, nr = k.Nr, stimmen = (short)k.Perioden.Length,
                    p1 = k.Perioden[0], p2 = At(k.Perioden, 1), p3 = At(k.Perioden, 2),
                    a2 = At(k.Amplituden, 1), a3 = At(k.Amplituden, 2),
                    harmonik = k.Harmonik, akkorde = k.Akkorde, werte = k.Werte, epochen = k.Epochen,
                    prominenz = k.MittlereProminenz, stabilitaet = k.MittlereStabilitaet,
                    paare = paare.Count, pb = paare.Count(p => p.Bestaendig)
                }, cancellationToken: ct));
            classIds[k.Nr] = cid;
        }

        var klasseVonAkkord = new Dictionary<(int, DateTime), int>();
        foreach (var k in katalog)
            foreach (var m in k.Mitglieder)
                klasseVonAkkord[(m.AssetId, m.EpochTo)] = classIds[k.Nr];

        var ta = new DataTable();
        foreach (var (n, t) in new (string, Type)[] {
            ("run_id", typeof(int)), ("class_id", typeof(int)), ("asset_id", typeof(int)),
            ("epoch_from_utc", typeof(DateTime)), ("epoch_to_utc", typeof(DateTime)),
            ("periode1", typeof(double)), ("periode2", typeof(double)), ("periode3", typeof(double)),
            ("amp2", typeof(double)), ("amp3", typeof(double)),
            ("phase1", typeof(double)), ("phase2", typeof(double)), ("phase3", typeof(double)),
            ("prominenz", typeof(double)), ("stabilitaet", typeof(double)) })
            ta.Columns.Add(n, t);
        foreach (var a in akkorde)
        {
            object cid = klasseVonAkkord.TryGetValue((a.AssetId, a.EpochTo), out var c) ? c : DBNull.Value;
            ta.Rows.Add(runId, cid, a.AssetId, a.EpochFrom, a.EpochTo,
                a.Perioden[0], Db(At(a.Perioden, 1)), Db(At(a.Perioden, 2)),
                Db(At(a.Amplituden, 1)), Db(At(a.Amplituden, 2)),
                a.Phasen[0], Db(At(a.Phasen, 1)), Db(At(a.Phasen, 2)),
                a.Prominenz, a.Stabilitaet);
        }
        await Massenkopie.SchreibeAsync(conn, ta, "dbo.freq_akkord", 300, ct);

        var tm = new DataTable();
        foreach (var (n, t) in new (string, Type)[] {
            ("run_id", typeof(int)), ("asset_id", typeof(int)), ("interval_code", typeof(string)),
            ("epoch_from_utc", typeof(DateTime)), ("epoch_to_utc", typeof(DateTime)),
            ("period_bars", typeof(double)), ("prominence", typeof(double)), ("stability", typeof(double)),
            ("phase_deg", typeof(double)), ("amplitude", typeof(double)) })
            tm.Columns.Add(n, t);
        foreach (var p in muster)
            tm.Rows.Add(runId, p.AssetId, interval, p.EpochFrom, p.EpochTo, p.PeriodBars, p.Prominence,
                        p.Stability, Db(p.PhaseDeg), Db(p.Amplitude));
        await Massenkopie.SchreibeAsync(conn, tm, "dbo.freq_pattern", 300, ct);

        var tp = new DataTable();
        foreach (var (n, t) in new (string, Type)[] {
            ("run_id", typeof(int)), ("class_id", typeof(int)), ("interval_code", typeof(string)),
            ("epoch_to_utc", typeof(DateTime)), ("asset_a", typeof(int)), ("asset_b", typeof(int)),
            ("period_a", typeof(double)), ("period_b", typeof(double)), ("period_delta", typeof(double)),
            ("phase_delta_deg", typeof(double)), ("lag_bars", typeof(double)), ("score", typeof(double)),
            ("epochs", typeof(int)), ("lag_sd", typeof(double)), ("bestaendig", typeof(bool)) })
            tp.Columns.Add(n, t);
        var paareGesamt = 0; var bestaendigGesamt = 0;
        foreach (var k in katalog)
        {
            var letzte = k.Mitglieder.Max(m => m.EpochTo);
            foreach (var p in paareJeKlasse[k.Nr])
            {
                paareGesamt++; if (p.Bestaendig) bestaendigGesamt++;
                tp.Rows.Add(runId, classIds[k.Nr], interval, letzte, p.AssetA, p.AssetB,
                    k.Perioden[0], k.Perioden[0], 0.0,
                    p.MittlererVersatzBars / k.Perioden[0] * 360.0, p.MittlererVersatzBars,
                    (double)p.GemeinsameEpochen, p.GemeinsameEpochen, p.VersatzStreuung, p.Bestaendig);
            }
        }
        await Massenkopie.SchreibeAsync(conn, tp, "dbo.freq_match", 300, ct);

        var epochen = muster.Select(m => m.EpochTo).Distinct().Count();
        var note = $"{mitDaten} Werte mit mindestens {MindestBars} Bars, Epoche {EpochBars} Bars bis zu Jahresankern, "
                 + $"Toleranz 12 %, Klassen ab 3 Akkorden, Paare ab 3 gemeinsamen Epochen.";

        await conn.ExecuteAsync(new CommandDefinition($"""
            UPDATE dbo.freq_run SET finished_utc = {d.Jetzt}, werte = @werte, epochen = @epochen,
                   muster = @muster, akkorde = @akkorde, klassen = @klassen, paare = @paare,
                   paare_bestaendig = @pb, dauer_s = @dauer, note = @note
             WHERE run_id = @runId
            """, new
            {
                runId, werte = mitDaten, epochen, muster = muster.Count, akkorde = akkorde.Count,
                klassen = katalog.Count, paare = paareGesamt, pb = bestaendigGesamt,
                dauer = Math.Round(sw.Elapsed.TotalSeconds, 1), note
            }, cancellationToken: ct));

        log.LogInformation("Grundschwingungen: {Werte} Werte, {Muster} Muster, {Akkorde} Akkorde, "
                         + "{Klassen} Klassen, {Paare} Paare ({Bestaendig} bestaendig) in {S:F1} s",
                         mitDaten, muster.Count, akkorde.Count, katalog.Count, paareGesamt,
                         bestaendigGesamt, sw.Elapsed.TotalSeconds);

        return (await LetzterLaufAsync(conn, interval, ct))!;
    }

    // ============================================================ Ansichten ==

    public async Task<GrundschwingungUebersicht> UebersichtAsync(string interval = "1d", CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var lauf = await LetzterLaufAsync(conn, interval, ct);
        if (lauf is null)
            return new GrundschwingungUebersicht(null, [],
                "Noch kein Lauf. „Jetzt rechnen“ geht über alle verfolgten Werte mit mindestens "
                + "1.064 Tagesbars und dauert gemessen rund zwei Minuten.");

        var klassen = (await conn.QueryAsync<KlasseRoh>(new CommandDefinition("""
            SELECT class_id AS ClassId, nr AS Nr, CAST(stimmen AS INT) AS Stimmen,
                   periode1 AS Periode1, periode2 AS Periode2, periode3 AS Periode3,
                   amp2 AS Amp2, amp3 AS Amp3, harmonik AS Harmonik,
                   akkorde AS Akkorde, werte AS Werte, epochen AS Epochen,
                   prominenz AS Prominenz, stabilitaet AS Stabilitaet,
                   paare AS Paare, paare_bestaendig AS PaareBestaendig
              FROM dbo.freq_class WHERE run_id = @runId ORDER BY nr
            """, new { runId = lauf.RunId }, cancellationToken: ct))).Select(Klasse).ToList();

        return new GrundschwingungUebersicht(lauf, klassen,
            "Eine Klasse ist ein Akkord aus ein bis drei Grundperioden, den mehrere Werte in "
            + "mehreren Epochen tragen. Dass zwei Werte in derselben Klasse stehen, ist bei "
            + $"{lauf.Werte} Werten und {lauf.Klassen} Klassen für sich genommen Zufall. "
            + "Gezählt werden deshalb Paare, die die Klasse in mindestens drei Epochen teilen — "
            + $"und davon gelten {lauf.PaareBestaendig} von {lauf.Paare} als beständig: "
            + "Versatz über die Epochen derselbe und ungleich null. Nur das wäre ein Vorlauf.");
    }

    public async Task<GrundschwingungKlasseDetail?> KlasseAsync(int classId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var roh = await conn.QuerySingleOrDefaultAsync<KlasseRoh>(new CommandDefinition("""
            SELECT class_id AS ClassId, nr AS Nr, CAST(stimmen AS INT) AS Stimmen,
                   periode1 AS Periode1, periode2 AS Periode2, periode3 AS Periode3,
                   amp2 AS Amp2, amp3 AS Amp3, harmonik AS Harmonik,
                   akkorde AS Akkorde, werte AS Werte, epochen AS Epochen,
                   prominenz AS Prominenz, stabilitaet AS Stabilitaet,
                   paare AS Paare, paare_bestaendig AS PaareBestaendig
              FROM dbo.freq_class WHERE class_id = @classId
            """, new { classId }, cancellationToken: ct));
        if (roh is null) return null;

        var juengste = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(epoch_to_utc) FROM dbo.freq_akkord WHERE run_id = (SELECT run_id FROM dbo.freq_class WHERE class_id = @classId)",
            new { classId }, cancellationToken: ct));

        var mitglieder = (await conn.QueryAsync<MitgliedRoh>(new CommandDefinition($"""
            SELECT k.asset_id AS AssetId, a.symbol AS Symbol, a."name" AS Name,
                   CAST(COUNT(*) AS INT) AS Epochen,
                   MAX(k.epoch_to_utc) AS LetzteEpocheUtc,
                   AVG(k.prominenz) AS Prominenz
              FROM dbo.freq_akkord k
              JOIN dbo.asset a ON a.asset_id = k.asset_id
             WHERE k.class_id = @classId
             GROUP BY k.asset_id, a.symbol, a."name"
             ORDER BY COUNT(*) DESC, MAX(k.epoch_to_utc) DESC, a.symbol
            """, new { classId }, cancellationToken: ct))).ToList();

        // Die Phase der jüngsten Epoche je Mitglied, eine Abfrage für alle.
        var phasen = (await conn.QueryAsync<(int AssetId, DateTime EpochTo, double Phase1)>(new CommandDefinition(
            "SELECT asset_id, epoch_to_utc, phase1 FROM dbo.freq_akkord WHERE class_id = @classId",
            new { classId }, cancellationToken: ct)))
            .GroupBy(x => x.AssetId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.EpochTo).First().Phase1);

        var paare = (await conn.QueryAsync<PaarRoh>(new CommandDefinition("""
            SELECT m.asset_a AS AssetA, sa.symbol AS SymbolA, m.asset_b AS AssetB, sb.symbol AS SymbolB,
                   m.epochs AS Epochen, m.lag_bars AS VersatzBars, m.lag_sd AS VersatzStreuung,
                   m.bestaendig AS Bestaendig
              FROM dbo.freq_match m
              JOIN dbo.asset sa ON sa.asset_id = m.asset_a
              JOIN dbo.asset sb ON sb.asset_id = m.asset_b
             WHERE m.class_id = @classId
             ORDER BY m.bestaendig DESC, m.epochs DESC, m.lag_sd
            """, new { classId }, cancellationToken: ct))).ToList();

        var klasse = Klasse(roh);
        return new GrundschwingungKlasseDetail(
            klasse,
            mitglieder.Select(m => new GrundschwingungMitglied(
                m.AssetId, m.Symbol, m.Name, m.Epochen, m.LetzteEpocheUtc,
                phasen.TryGetValue(m.AssetId, out var ph) ? Math.Round(ph, 1) : double.NaN,
                Math.Round(m.Prominenz, 2),
                juengste.HasValue && m.LetzteEpocheUtc >= juengste.Value.AddDays(-400))).ToList(),
            paare.Select(p => new GrundschwingungPaar(
                p.AssetA, p.SymbolA, p.AssetB, p.SymbolB, p.Epochen ?? 0,
                p.VersatzBars ?? 0, p.VersatzStreuung ?? 0, p.Bestaendig ?? false)).ToList(),
            klasse.Stimmen == 1
                ? $"Reine Schwingung von {klasse.Perioden[0]:0.#} Bars. „Aktuell“ heisst: der Wert trug sie in einer der letzten Epochen."
                : $"Grundton {klasse.Perioden[0]:0.#} Bars mit {(klasse.Stimmen == 2 ? "einer Nebenstimme" : "zwei Nebenstimmen")} — {klasse.Harmonik}. "
                  + "Der Versatz eines Paares ist der Phasenunterschied der Grundtöne in Bars; positiv heisst, der zweite Wert liegt zurück.");
    }

    public async Task<GrundschwingungWert?> WertAsync(int assetId, CancellationToken ct = default)
    {
        var asset = await assets.GetAsync(assetId, ct);
        if (asset is null) return null;

        await using var conn = await factory.OpenAsync(ct);

        var akkord = await conn.QuerySingleOrDefaultAsync<AkkordRoh>(new CommandDefinition("""
            SELECT k.class_id AS ClassId, k.epoch_to_utc AS EpochTo,
                   k.periode1 AS Periode1, k.periode2 AS Periode2, k.periode3 AS Periode3,
                   k.amp2 AS Amp2, k.amp3 AS Amp3, k.phase1 AS Phase1, k.phase2 AS Phase2, k.phase3 AS Phase3
              FROM dbo.freq_akkord k
             WHERE k.asset_id = @assetId
             ORDER BY k.epoch_to_utc DESC OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY
            """, new { assetId }, cancellationToken: ct));

        GrundschwingungKlasse? klasse = null;
        var partner = new List<GrundschwingungPaar>();
        if (akkord?.ClassId is { } cid)
        {
            var detail = await KlasseAsync(cid, ct);
            klasse = detail?.Klasse;
            partner = detail?.Paare.Where(p => p.AssetA == assetId || p.AssetB == assetId).ToList() ?? [];
        }

        // Prognosebeitrag samt Rückhalt, frisch aus den Tagesschlüssen.
        var bis = DateTime.UtcNow; var von = bis.AddYears(-25);
        var reihe = await bars.GetAsync(assetId, "1d", von, bis, ct);
        var lage = GrundschwingungenPrognose.Rechne(reihe.Select(b => (double)b.Close).ToList());

        double[] perioden = akkord is null ? (lage?.Perioden ?? []) : Perioden(akkord.Periode1, akkord.Periode2, akkord.Periode3);
        double[] amps = akkord is null ? (lage?.Amplituden ?? []) : Perioden(1, akkord.Amp2, akkord.Amp3);
        double[] phasen = akkord is null ? (lage?.Phasen ?? []) : Perioden(akkord.Phase1, akkord.Phase2, akkord.Phase3);

        return new GrundschwingungWert(
            assetId, asset.Symbol, akkord?.EpochTo,
            perioden, amps, phasen,
            perioden.Length > 0 ? Grundschwingungen.Harmonik(perioden) : null,
            klasse, partner,
            lage?.Skill, lage?.RenditeNach(5), lage?.RenditeNach(20), lage?.RenditeNach(40));
    }

    // ================================================================ Hilfen ==

    private static async Task<GrundschwingungLauf?> LetzterLaufAsync(
        System.Data.Common.DbConnection conn, string interval, CancellationToken ct)
        => await conn.QuerySingleOrDefaultAsync<GrundschwingungLauf>(new CommandDefinition("""
            SELECT run_id AS RunId, interval_code AS Interval, started_utc AS GestartetUtc,
                   finished_utc AS BeendetUtc, werte AS Werte, epochen AS Epochen, muster AS Muster,
                   akkorde AS Akkorde, klassen AS Klassen, paare AS Paare,
                   paare_bestaendig AS PaareBestaendig, dauer_s AS DauerSekunden, note AS Note
              FROM dbo.freq_run
             WHERE interval_code = @interval AND finished_utc IS NOT NULL
             ORDER BY run_id DESC OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY
            """, new { interval }, cancellationToken: ct));

    private static GrundschwingungKlasse Klasse(KlasseRoh r)
    {
        var perioden = Perioden(r.Periode1, r.Periode2, r.Periode3);
        var amps = Perioden(1, r.Amp2, r.Amp3);
        return new GrundschwingungKlasse(
            r.ClassId, r.Nr, r.Stimmen, perioden, amps, r.Harmonik,
            r.Akkorde, r.Werte, r.Epochen, Math.Round(r.Prominenz, 2), Math.Round(r.Stabilitaet, 3),
            r.Paare, r.PaareBestaendig, Grundschwingungen.Skizze(perioden, amps));
    }

    private static double[] Perioden(double p1, double? p2, double? p3)
        => p3.HasValue ? [p1, p2!.Value, p3.Value] : p2.HasValue ? [p1, p2.Value] : [p1];

    private static double? At(double[] a, int i) => i < a.Length ? a[i] : null;
    private static object Db(double? v) => v.HasValue && !double.IsNaN(v.Value) ? v.Value : DBNull.Value;
    private static object Db(double v) => double.IsNaN(v) ? DBNull.Value : v;

    /*  stimmen ist in SQL Server TINYINT (byte), in Postgres smallint (short);
        Dapper verlangt fuer den Konstruktor den exakten Typ. Deshalb im SQL auf
        INT gecastet -- die Uebersicht antwortete sonst gegen SQL Server mit 500,
        waehrend der Lauf selbst laengst fertig war.                            */
    private sealed record KlasseRoh(int ClassId, int Nr, int Stimmen, double Periode1, double? Periode2,
        double? Periode3, double? Amp2, double? Amp3, string Harmonik, int Akkorde, int Werte, int Epochen,
        double Prominenz, double Stabilitaet, int Paare, int PaareBestaendig);
    private sealed record MitgliedRoh(int AssetId, string Symbol, string? Name, int Epochen,
        DateTime LetzteEpocheUtc, double Prominenz);
    private sealed record PaarRoh(int AssetA, string SymbolA, int AssetB, string SymbolB,
        int? Epochen, double? VersatzBars, double? VersatzStreuung, bool? Bestaendig);
    private sealed record AkkordRoh(int? ClassId, DateTime EpochTo, double Periode1, double? Periode2,
        double? Periode3, double? Amp2, double? Amp3, double Phase1, double? Phase2, double? Phase3);
}
