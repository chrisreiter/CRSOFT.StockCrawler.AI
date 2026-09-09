using System.Data;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Models;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Das virtuelle Depot: Verrechnungskonto, Positionen, Gebühren.
///
/// <para><b>Gebucht wird die Differenz, nicht der Betrag.</b> Das Feld in der
/// Oberfläche trägt den Stand, den die Position ab jetzt haben SOLL. Wer 1.000
/// stehen hat und 1.500 einträgt, kauft für 500 nach; wer 0 einträgt, löst auf.
/// Dieselbe Regel gilt für den Kontostand. Die Alternative — ein Feld „wieviel
/// dazu“ — verlangt vom Nutzer die Subtraktion, die die Anwendung selbst machen
/// kann, und erzeugt bei jedem versehentlichen zweiten Klick eine echte zweite
/// Buchung.</para>
///
/// <para><b>Das Geld geht vom Konto ab und kommt dorthin zurück.</b> Beim Kauf
/// verlässt der Betrag PLUS die Gebühr das Konto, beim Verkauf kommt der Betrag
/// MINUS die Gebühr zurück. In einer Zeile: <c>konto -= betrag + gebuehr</c>,
/// wobei <c>betrag</c> sein Vorzeichen trägt und <c>gebuehr</c> nie negativ ist.
/// Getrennte Zweige für Kauf und Verkauf wären zwei Stellen, an denen ein
/// Vorzeichen falsch stehen kann.</para>
///
/// <para><b>Was hier bewusst nicht steht.</b> Keine Steuer, kein Schlupf über
/// die eingestellte Gebühr hinaus, keine Stückelung. Die Seite beantwortet „wie
/// hat sich der Kurs auf mein Geld ausgewirkt, wenn Reibung anfällt“ — nicht
/// „was hätte ich nach Abrechnung übrig“.</para>
/// </summary>
public sealed class InvestService(ISqlConnectionFactory factory) : IInvestService
{
    /// <summary>
    /// Wie weit der Verlauf höchstens zurückreicht. Buchungen entstehen nur
    /// tagesaktuell, ein längerer Zeitraum kann also gar nicht auflaufen —
    /// die Grenze ist ein Riegel gegen eine von Hand veränderte Tabelle, nicht
    /// gegen den Normalbetrieb.
    /// </summary>
    private const int MaxTage = 2000;

    /// <summary>
    /// Wie viele der jüngsten Tage in <b>Stundenauflösung</b> gezeichnet werden.
    ///
    /// <para>Ohne sie hat ein heute eröffnetes Depot genau einen Punkt und
    /// zeichnet keine Linie — obwohl sich sein Wert mit jeder Stundenbar
    /// bewegt. Drei Tage decken ein Wochenende ab und kosten rund 72 Punkte;
    /// ein ganzes Jahr in Stundenauflösung wären etwa 6.000 je Wert, und die
    /// braucht eine Vermögenskurve nicht.</para>
    /// </summary>
    private const int FeinTage = 3;

    private static readonly string[] Waehrungen = ["EUR", "USD"];

    // ------------------------------------------------------------ Übersicht --

    public async Task<InvestUebersicht> UebersichtAsync(string depot,
                                                        IReadOnlyList<int>? assetIds,
                                                        CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        /*  Zwei Mengen in einer Abfrage: was gebucht ist, und was gerade
            gewählt ist. Ein Wert mit Position MUSS erscheinen, auch wenn er
            nicht gewählt ist — eine Position, die aus der Übersicht fällt,
            weil jemand die Auswahl geändert hat, sieht aus wie verlorenes
            Geld. Umgekehrt muss ein gewählter Wert OHNE Position erscheinen,
            denn in eine Zeile, die es nicht gibt, trägt man nichts ein.       */
        var ids = assetIds is { Count: > 0 } ? assetIds.ToArray() : [];

        var rows = await conn.QueryAsync<InvestPosition>(new CommandDefinition("""
            WITH gebucht AS (
              SELECT b.asset_id, MIN(b.waehrung) AS waehrung,
                     SUM(b.anteile)                                        AS anteile,
                     SUM(CASE WHEN b.betrag > 0 THEN  b.betrag ELSE 0 END) AS eingezahlt,
                     SUM(CASE WHEN b.betrag < 0 THEN -b.betrag ELSE 0 END) AS entnommen,
                     SUM(b.gebuehr)                                        AS gebuehren,
                     COUNT(*)                                              AS buchungen,
                     MIN(b.am_utc)                                         AS seit_utc
                FROM dbo.invest_buchung b
               WHERE b.depot = @depot
               GROUP BY b.asset_id
            ),
            gewaehlt AS (
              SELECT TRY_CAST(value AS INT) AS asset_id
                FROM STRING_SPLIT(@ids, ',')
               WHERE value <> ''
            ),
            beteiligt AS (
              SELECT asset_id FROM gebucht
              UNION
              SELECT asset_id FROM gewaehlt WHERE asset_id IS NOT NULL
            )
            SELECT a.asset_id                       AS AssetId,
                   a.symbol                         AS Symbol,
                   a.name                           AS Name,
                   a.asset_class                    AS Klasse,
                   ISNULL(g.waehrung, '')           AS Waehrung,
                   ISNULL(g.anteile, 0)             AS Anteile,
                   ISNULL(g.eingezahlt, 0)          AS Eingezahlt,
                   ISNULL(g.entnommen, 0)           AS Entnommen,
                   ISNULL(g.gebuehren, 0)           AS Gebuehren,
                   ISNULL(g.buchungen, 0)           AS Buchungen,
                   g.seit_utc                       AS SeitUtc,
                   k.schluss                        AS Kurs,
                   k.ts_utc                         AS KursUtc,
                   k.interval_code                  AS KursQuelle,
                   CASE WHEN w.asset_id IS NULL THEN CAST(0 AS BIT)
                        ELSE CAST(1 AS BIT) END     AS Gewaehlt
              FROM beteiligt t
              JOIN dbo.asset a ON a.asset_id = t.asset_id
              LEFT JOIN gebucht g  ON g.asset_id = t.asset_id
              LEFT JOIN gewaehlt w ON w.asset_id = t.asset_id
              /*  Der JUENGSTE bekannte Kurs, nicht der letzte Tagesschluss.
                  Gemessen am 26.08.2026: Tagesbars bis 25.08. 00:00,
                  Stundenbars bis 26.08. 14:00 -- der angezeigte Depotwert war
                  38 Stunden alt und bewegte sich zwischen zwei Tageslaeufen
                  gar nicht, obwohl stuendlich frische Kurse hereinkamen.     */
              OUTER APPLY dbo.letzter_kurs(t.asset_id) k
             ORDER BY CASE WHEN ISNULL(g.buchungen, 0) > 0 THEN 0 ELSE 1 END,
                      a.symbol
            """,
            new { ids = string.Join(',', ids), depot }, cancellationToken: ct));

        var liste = rows.ToList();
        var konten = await KontenAsync(conn, depot, ct);

        var bloecke = konten.Select(k =>
        {
            var eigene = liste.Where(p => p.Buchungen > 0 && p.Waehrung == k.Waehrung).ToList();
            var stand = eigene.Sum(p => p.Stand ?? 0);
            var vermoegen = k.Stand + stand;
            var netto = k.Eingezahlt - k.Ausgezahlt;
            var gewinn = vermoegen - netto;

            return new InvestWaehrungsblock(
                k.Waehrung, eigene.Count, k.Stand, k.Eingezahlt, k.Ausgezahlt,
                k.Gebuehren, k.GebuehrPct, stand, vermoegen, gewinn,
                k.Eingezahlt > 0 ? (double)(gewinn / k.Eingezahlt) * 100 : null);
        })
        /*  Währungen ohne jede Spur bleiben draussen. Eine Karte „USD 0,00“ neben
            einem laufenden EUR-Konto sieht aus wie ein Bestand und ist keiner.  */
        .Where(b => b.Positionen > 0 || b.Kontostand != 0 || b.Eingezahlt != 0)
        .OrderByDescending(b => b.Vermoegen)
        .ToList();

        return new InvestUebersicht(liste, bloecke, DateTime.UtcNow, Hinweis(bloecke));
    }

    private static string Hinweis(IReadOnlyList<InvestWaehrungsblock> bloecke)
    {
        if (bloecke.Count == 0)
            return "Noch nichts eingesetzt. Erst einen Kontostand setzen — davon gehen "
                 + "die Investitionen ab, und Verkäufe buchen wieder darauf zurück.";

        /*  Hier stand einmal, dieses System führe keine Devisenreihen. Das war
            falsch und ungeprüft: `EURUSD=X` liegt mit rund 5.900 Tagesbars
            verfolgt im Bestand, dazu GBP, CHF und JPY. Deshalb gibt es das
            Gesamtvermögen über beide Währungen — je Währung getrennt bleibt es
            trotzdem, weil eine umgerechnete Zahl eine Annahme mehr enthält als
            eine ungerechnete.                                                 */
        var mehr = bloecke.Count > 1
            ? " Je Währung getrennt. Das Band ganz oben rechnet beide über "
            + "EURUSD=X zusammen."
            : "";

        /*  NICHT mehr „zum jüngsten Tagesschluss": Bewertet wird auf der
            jüngsten bekannten Bar, und das ist meist eine Stundenbar. Der alte
            Satz stand hier, als es tatsächlich nur Tagesschlüsse waren — und
            er hätte den Fehler überlebt, wenn ihn niemand mitkorrigiert hätte. */
        return "Bewertet zum jüngsten bekannten Kurs — Stundenbar, wo es eine gibt, "
             + "sonst Tagesschluss. Die Kursbewegung ist abgebildet, die des "
             + "Wechselkurses nur in der Gesamtsumme." + mehr;
    }

    // ---------------------------------------------------------------- Konto --

    public async Task<IReadOnlyList<InvestKonto>> KontenAsync(string depot,
                                                              CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await KontenAsync(conn, depot, ct);
    }

    private static async Task<List<InvestKonto>> KontenAsync(IDbConnection conn, string depot,
                                                             CancellationToken ct)
    {
        /*  Der Stand ist die SUMME der Bewegungen, keine gespeicherte Zahl.
            Damit kann er gar nicht von seiner eigenen Geschichte abweichen —
            derselbe Grundsatz wie bei den Anteilen einer Position.            */
        var rows = await conn.QueryAsync<InvestKonto>(new CommandDefinition("""
            SELECT k.waehrung              AS Waehrung,
                   ISNULL(b.stand, 0)      AS Stand,
                   k.gebuehr_pct           AS GebuehrPct,
                   ISNULL(b.eingezahlt, 0) AS Eingezahlt,
                   ISNULL(b.ausgezahlt, 0) AS Ausgezahlt,
                   ISNULL(b.gebuehren, 0)  AS Gebuehren,
                   ISNULL(b.bewegungen, 0) AS Bewegungen,
                   b.seit_utc              AS SeitUtc
              FROM dbo.invest_konto k
              LEFT JOIN (
                SELECT waehrung,
                       SUM(betrag)                                                 AS stand,
                       SUM(CASE WHEN grund = 'einzahlung' THEN betrag  ELSE 0 END) AS eingezahlt,
                       SUM(CASE WHEN grund = 'auszahlung' THEN -betrag ELSE 0 END) AS ausgezahlt,
                       SUM(CASE WHEN grund = 'gebuehr'    THEN -betrag ELSE 0 END) AS gebuehren,
                       COUNT(*)                                                    AS bewegungen,
                       MIN(am_utc)                                                 AS seit_utc
                  FROM dbo.invest_kontobewegung
                 WHERE depot = @depot
                 GROUP BY waehrung) b ON b.waehrung = k.waehrung
             WHERE k.depot = @depot
             ORDER BY k.waehrung
            """, new { depot }, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<InvestKonto?> KontoSetzeAsync(string depot, string waehrung,
                                                    decimal? sollStand, decimal? gebuehrPct,
                                                    CancellationToken ct = default)
    {
        var w = (waehrung ?? "").Trim().ToUpperInvariant();
        if (!Waehrungen.Contains(w)) return null;

        await using var conn = await factory.OpenAsync(ct);

        if (gebuehrPct is { } pct)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.invest_konto
                   SET gebuehr_pct = @pct, updated_utc = SYSUTCDATETIME()
                 WHERE waehrung = @w AND depot = @depot
                """, new { w, depot, pct = Math.Clamp(pct, 0m, 10m) }, cancellationToken: ct));
        }

        if (sollStand is { } soll)
        {
            var jetzt = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
                "SELECT ISNULL(SUM(betrag), 0) FROM dbo.invest_kontobewegung "
              + "WHERE waehrung = @w AND depot = @depot",
                new { w, depot }, cancellationToken: ct));

            var delta = Math.Round(soll, 2) - jetzt;

            /*  Unter einem Cent ist keine Bewegung, sondern das Bestätigen
                eines unveränderten Feldes.                                    */
            if (Math.Abs(delta) >= 0.01m)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO dbo.invest_kontobewegung (depot, waehrung, betrag, grund)
                    VALUES (@depot, @w, @betrag, @grund)
                    """,
                    new { depot, w, betrag = delta,
                          grund = delta > 0 ? "einzahlung" : "auszahlung" },
                    cancellationToken: ct));
            }
        }

        return (await KontenAsync(conn, depot, ct)).FirstOrDefault(k => k.Waehrung == w);
    }

    public async Task<IReadOnlyList<InvestKontobewegung>> KontobewegungenAsync(
        string depot, string waehrung, int grenze = 200, CancellationToken ct = default)
    {
        var w = (waehrung ?? "").Trim().ToUpperInvariant();

        await using var conn = await factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<InvestKontobewegung>(new CommandDefinition("""
            SELECT TOP (@grenze)
                   m.bewegung_id AS BewegungId, m.waehrung AS Waehrung, m.am_utc AS AmUtc,
                   m.betrag AS Betrag, m.grund AS Grund, m.buchung_id AS BuchungId,
                   m.notiz AS Notiz, a.symbol AS Symbol
              FROM dbo.invest_kontobewegung m
              LEFT JOIN dbo.invest_buchung b ON b.buchung_id = m.buchung_id
              LEFT JOIN dbo.asset a ON a.asset_id = b.asset_id
             WHERE m.waehrung = @w AND m.depot = @depot
             ORDER BY m.am_utc DESC, m.bewegung_id DESC
            """, new { w, depot, grenze = Math.Clamp(grenze, 1, 2000) },
            cancellationToken: ct));

        return rows.ToList();
    }

    // --------------------------------------------------------------- Buchen --

    public async Task<InvestBuchungErgebnis?> SetzeAsync(string depot, string symbol,
                                                         decimal sollBetrag, string waehrung,
                                                         string? notiz,
                                                         CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        await using var conn = await factory.OpenAsync(ct);

        var assetId = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT asset_id FROM dbo.asset WHERE symbol = @s",
            new { s = symbol.Trim() }, cancellationToken: ct));

        if (assetId is null) return null;

        var sym = symbol.Trim();

        /*  DIESELBE Quelle wie die Anzeige. Liefen die beiden auseinander,
            zeigte die Tabelle einen Preis und die Buchung benutzte einen
            anderen -- und die Zahlen liessen sich nicht mehr gegeneinander
            pruefen.                                                          */
        var kurs = await conn.QuerySingleOrDefaultAsync<Tagesschluss>(
            new CommandDefinition(
                "SELECT schluss AS Schluss, ts_utc AS TsUtc FROM dbo.letzter_kurs(@id)",
                new { id = assetId.Value }, cancellationToken: ct));

        /*  Ohne Kurs kein Einstand. Zu raten, was ein Wert heute kostet, wäre
            genau die Sorte stille Erfindung, die diese Anwendung an anderer
            Stelle mühsam ausgebaut hat.                                      */
        if (kurs.Schluss <= 0)
            return Nichts(sym, waehrung,
                $"Für {sym} liegt kein Tagesschluss vor — dafür lässt sich nichts buchen.");

        var close = kurs.Schluss;
        var kursUtc = kurs.TsUtc;

        /*  ISNULL um die Summe: Ohne Zeilen liefert SUM ein NULL, und das lässt
            sich nicht auf ein `decimal` legen.                                */
        var stand = await conn.QuerySingleOrDefaultAsync<Bestand>(
            new CommandDefinition("""
                SELECT ISNULL(SUM(anteile), 0) AS Anteile, MIN(waehrung) AS Waehrung
                  FROM dbo.invest_buchung
                 WHERE asset_id = @id AND depot = @depot
                """, new { id = assetId.Value, depot }, cancellationToken: ct));

        var anteileVorher = stand.Anteile;

        /*  Eine Position trägt EINE Währung. Sie mitten im Lauf zu wechseln
            hiesse, Beträge in zwei Zähleinheiten zu addieren — das Ergebnis
            wäre eine Zahl ohne Einheit, und niemand sähe es der Spalte an.    */
        if (!string.IsNullOrEmpty(stand.Waehrung) &&
            !string.Equals(stand.Waehrung, waehrung, StringComparison.OrdinalIgnoreCase))
        {
            return Nichts(sym, stand.Waehrung!,
                $"{sym} läuft in {stand.Waehrung}. Ein Wechsel der Währung würde Beträge "
              + "in zwei Zähleinheiten addieren — erst auflösen, dann neu einsetzen.");
        }

        var w = (stand.Waehrung ?? waehrung).Trim().ToUpperInvariant();
        var konto = (await KontenAsync(conn, depot, ct)).FirstOrDefault(k => k.Waehrung == w);

        if (konto is null)
            return Nichts(sym, w, $"Für {w} gibt es kein Konto.");

        var standVorher = Math.Round(anteileVorher * close, 2);
        var soll = Math.Round(Math.Max(0, sollBetrag), 2);
        var betrag = soll - standVorher;

        /*  Unter einem Cent ist keine Buchung, sondern das Bestätigen eines
            unveränderten Feldes. Ohne diese Schwelle füllte jeder zweite Klick
            das Journal mit Zeilen, die nichts aussagen.                       */
        if (Math.Abs(betrag) < 0.01m)
            return new InvestBuchungErgebnis(sym, standVorher, standVorher, 0, 0,
                konto.Stand, close, kursUtc, w, "Unverändert — nichts gebucht.");

        var gebuehr = Math.Round(Math.Abs(betrag) * konto.GebuehrPct / 100m, 2);

        /*  Kaufen kann nur, wer das Geld hat. Ein stillschweigend negativer
            Kontostand machte das Vermögen zu einer Zahl, die nichts mehr
            bedeutet — und die Meldung soll sagen, WIEVIEL fehlt, nicht bloss
            dass etwas fehlt.                                                  */
        var abfluss = betrag + gebuehr;

        if (abfluss > konto.Stand)
        {
            var fehlt = abfluss - konto.Stand;

            return Nichts(sym, w,
                $"Auf dem {w}-Konto stehen {konto.Stand:N2}. Für {betrag:N2} zuzüglich "
              + $"{gebuehr:N2} Gebühr fehlen {fehlt:N2} — erst den Kontostand erhöhen.");
        }

        /*  Beim Auflösen zählen die Anteile, nicht der Betrag. Rundung auf zwei
            Nachkommastellen liesse sonst einen Splitter von Bruchteilen eines
            Cents stehen, und die Position verschwände nie ganz aus der Liste.  */
        var anteile = soll == 0 ? -anteileVorher : Math.Round(betrag / close, 10);

        /*  Buchung und Kassenbewegungen gehören zusammen oder gar nicht. Bricht
            etwas dazwischen ab, stünde sonst eine Position ohne Gegenbuchung da
            — Geld aus dem Nichts.                                             */
        Oeffne(conn);
        using var tx = conn.BeginTransaction();

        var buchungId = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO dbo.invest_buchung
                   (depot, asset_id, kurs, kurs_utc, betrag, anteile, gebuehr, waehrung, notiz)
            OUTPUT INSERTED.buchung_id
            VALUES (@depot, @id, @kurs, @kursUtc, @betrag, @anteile, @gebuehr, @waehrung, @notiz)
            """,
            new { depot, id = assetId.Value, kurs = close, kursUtc, betrag, anteile, gebuehr,
                  waehrung = w, notiz },
            tx, cancellationToken: ct));

        // Das Gegenstück auf dem Konto: Kauf zieht ab, Verkauf legt zurück.
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO dbo.invest_kontobewegung (depot, waehrung, betrag, grund, buchung_id)
            VALUES (@depot, @w, @betrag, @grund, @bid)
            """,
            new { depot, w, betrag = -betrag, grund = betrag > 0 ? "kauf" : "verkauf",
                  bid = buchungId },
            tx, cancellationToken: ct));

        /*  Die Gebühr als EIGENE Bewegung, nicht in den Kaufbetrag gerechnet.
            Sonst liesse sich im Kassenjournal nicht mehr sehen, was die Reibung
            gekostet hat — und genau dafür ist der Satz da.                     */
        if (gebuehr > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO dbo.invest_kontobewegung (depot, waehrung, betrag, grund, buchung_id)
                VALUES (@depot, @w, @betrag, 'gebuehr', @bid)
                """,
                new { depot, w, betrag = -gebuehr, bid = buchungId },
                tx, cancellationToken: ct));
        }

        tx.Commit();

        var neuerStand = konto.Stand - abfluss;

        var was = soll == 0 ? "aufgelöst"
                : betrag > 0 ? $"{betrag:N2} {w} eingesetzt"
                : $"{-betrag:N2} {w} zurückgebucht";

        var gebuehrText = gebuehr > 0 ? $", Gebühr {gebuehr:N2}" : "";

        return new InvestBuchungErgebnis(sym, standVorher, soll, betrag, gebuehr, neuerStand,
            close, kursUtc, w,
            $"{sym}: {was} zu {close:N4}{gebuehrText}. Konto {neuerStand:N2} {w}.");
    }

    private static InvestBuchungErgebnis Nichts(string sym, string w, string meldung) =>
        new(sym, 0, 0, 0, 0, 0, 0, null, w, meldung);

    /*  NICHT als `Tagesschluss?` abfragen. Dapper liefert für einen NULLBAREN
        record struct kommentarlos `null` zurück, statt die Spalten zu füllen —
        kein Fehler, keine Meldung. Sichtbar wurde es an der Meldung „für NVDA
        liegt kein Tagesschluss vor“, während die Zeile daneben den Kurs zeigte.
        Beim Bestand wäre es schlimmer gewesen und wäre nicht aufgefallen: Eine
        bestehende Position hätte immer als leer gegolten, jede Anpassung also
        verdoppelt statt angepasst. Ein `default(T)` mit einer Prüfung auf null
        ist hier der verlässlichere Weg.

        Die Felder heissen ausserdem NICHT `Close`: Als Spaltenalias ist `Close`
        in T-SQL ein reserviertes Wort (`CLOSE cursor`), und `[close] AS Close`
        scheitert mit „Incorrect syntax near the keyword 'Close'“ — zur Laufzeit,
        nicht beim Übersetzen, und die Meldung nennt den Alias, nicht die
        Abfrage.                                                               */
    private readonly record struct Tagesschluss(decimal Schluss, DateTime TsUtc);

    private readonly record struct Bestand(decimal Anteile, string? Waehrung);

    public async Task<IReadOnlyList<InvestBuchung>> BuchungenAsync(string depot, int assetId,
                                                                   CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var rows = await conn.QueryAsync<InvestBuchung>(new CommandDefinition("""
            SELECT b.buchung_id AS BuchungId, b.asset_id AS AssetId, a.symbol AS Symbol,
                   b.am_utc AS AmUtc, b.kurs AS Kurs, b.kurs_utc AS KursUtc,
                   b.betrag AS Betrag, b.anteile AS Anteile, b.gebuehr AS Gebuehr,
                   b.waehrung AS Waehrung, b.notiz AS Notiz
              FROM dbo.invest_buchung b
              JOIN dbo.asset a ON a.asset_id = b.asset_id
             WHERE b.asset_id = @id AND b.depot = @depot
             ORDER BY b.am_utc DESC, b.buchung_id DESC
            """, new { id = assetId, depot }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>
    /// Verwirft die Simulation eines Wertes — Buchungen UND ihre Kassenspur.
    ///
    /// <para>Die Kassenbewegungen mitzulöschen ist keine Bequemlichkeit, sondern
    /// Notwendigkeit: Bliebe die Spur stehen, während die Position verschwindet,
    /// fehlte dem Konto der eingesetzte Betrag für immer, ohne dass etwas dafür
    /// da wäre. Das Geld wäre stillschweigend vernichtet.</para>
    /// </summary>
    public async Task<int> LoescheAsync(string depot, int assetId,
                                        CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        Oeffne(conn);
        using var tx = conn.BeginTransaction();

        // Erst die Bewegungen — danach ist die Verbindung zu ihnen weg.
        await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM dbo.invest_kontobewegung
             WHERE buchung_id IN (SELECT buchung_id FROM dbo.invest_buchung
                                   WHERE asset_id = @id AND depot = @depot)
            """, new { id = assetId, depot }, tx, cancellationToken: ct));

        var n = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.invest_buchung WHERE asset_id = @id AND depot = @depot",
            new { id = assetId, depot }, tx, cancellationToken: ct));

        tx.Commit();
        return n;
    }

    /// <summary>
    /// Öffnet die Verbindung, falls sie es noch nicht ist. <c>BeginTransaction</c>
    /// verlangt eine offene Verbindung; die Fabrik liefert sie zwar offen, aber
    /// darauf zu bauen macht diesen Code von einer Zusicherung abhängig, die
    /// nirgends steht.
    /// </summary>
    private static void Oeffne(IDbConnection conn)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
    }

    // -------------------------------------------------------------- Journal --

    /// <summary>
    /// Jeder Vorgang eines Depots, der Geld bewegt hat — Ein- und Auszahlungen
    /// wie Käufe und Verkäufe, in einer Liste.
    ///
    /// <para><b>Warum beides zusammen.</b> Es gibt bereits ein Journal je Wert
    /// und eines je Kassenwährung. Beide beantworten Teilfragen. Wer prüfen
    /// will, ob der Kontostand stimmt, muss den ganzen Weg sehen: eingezahlt,
    /// gekauft, Gebühr, verkauft — in der Reihenfolge, in der es passiert ist.
    /// Aus zwei getrennten Listen lässt sich das nur mühsam
    /// zusammenrechnen.</para>
    ///
    /// <para><b>Der Kontostand steht in jeder Zeile.</b> Ohne ihn ist ein
    /// Journal eine Liste von Ereignissen; mit ihm ist es eine Rechnung, die
    /// man nachrechnen kann — und genau darum geht es bei einem Journal.</para>
    /// </summary>
    public async Task<InvestJournal> JournalAsync(string depot, int grenze = 500,
                                                   CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        /*  `autopilot` fasst die drei Strategien zusammen. Der Nutzer sieht
            den Autopiloten als EINE Sache; drei Fenster nacheinander zu
            oeffnen, um seine Geschaefte zu ueberblicken, waere eine Trennung,
            die nur im Datenmodell existiert.                                  */
        var depots = depot == "autopilot"
            ? new[] { "streng", "aktiv", "halten", "invers" }
            : [depot];

        /*  Zwei Quellen, eine Liste. Die Kaeufe und Verkaeufe kommen aus
            `invest_buchung` -- dort steht die Gebuehr bereits je Vorgang, so
            dass sie nicht aus zwei Kassenbewegungen zusammengesucht werden
            muss. Ein- und Auszahlungen kommen aus der Kasse, denn zu ihnen
            gibt es keine Buchung.                                             */
        var zeilen = (await conn.QueryAsync<Rohzeile>(new CommandDefinition("""
            SELECT b.am_utc                                    AS AmUtc,
                   b.depot                                     AS Depot,
                   CASE WHEN b.betrag >= 0 THEN 'kauf'
                        ELSE 'verkauf' END                     AS Art,
                   a.symbol                                    AS Symbol,
                   a.name                                      AS Name,
                   b.kurs                                      AS Kurs,
                   b.kurs_utc                                  AS KursUtc,
                   b.anteile                                   AS Anteile,
                   b.betrag                                    AS Betrag,
                   b.gebuehr                                   AS Gebuehr,
                   b.waehrung                                  AS Waehrung,
                   b.notiz                                     AS Notiz
              FROM dbo.invest_buchung b
              JOIN dbo.asset a ON a.asset_id = b.asset_id
             WHERE b.depot IN @depots

            UNION ALL

            SELECT m.am_utc, m.depot, m.grund, NULL, NULL, NULL, NULL, NULL,
                   m.betrag, 0, m.waehrung, m.notiz
              FROM dbo.invest_kontobewegung m
             WHERE m.depot IN @depots
               AND m.grund IN ('einzahlung', 'auszahlung')

             ORDER BY AmUtc, Art
            """, new { depots }, cancellationToken: ct))).ToList();

        /*  Der laufende Kontostand wird VORWAERTS gerechnet und danach
            umgedreht. Rueckwaerts zu rechnen waere derselbe Betrag mit
            umgekehrtem Vorzeichen -- und genau dort schleicht sich ein
            Vorzeichenfehler ein, den niemand sieht, weil das Ergebnis
            plausibel bleibt.

            Die Formel ist dieselbe wie beim Buchen: `konto -= betrag +
            gebuehr`, wobei der Betrag sein Vorzeichen traegt. Eine Einzahlung
            hat keine Gebuehr und geht mit `+betrag` ein.                      */
        /*  Der laufende Stand wird JE STRATEGIE gefuehrt.

            Ein gemeinsamer Stand ueber `streng`, `aktiv` und `halten` waere
            dieselbe Doppelzaehlung wie beim Gesamtvermoegen: drei
            Gegenrechnungen auf dasselbe Geld, addiert. Die Spalte zeigt
            deshalb den Stand DIESER Strategie nach diesem Vorgang.           */
        var stand = new Dictionary<string, decimal>();
        var fertig = new List<InvestJournalzeile>(zeilen.Count);

        foreach (var z in zeilen)
        {
            var kasse = z.Art is "einzahlung" or "auszahlung"
                ? z.Betrag
                : -(z.Betrag + z.Gebuehr);

            var neu2 = stand.GetValueOrDefault(z.Depot) + kasse;
            stand[z.Depot] = neu2;

            fertig.Add(new InvestJournalzeile(
                z.AmUtc, z.Depot, z.Art, z.Symbol, z.Name, z.Kurs, z.KursUtc, z.Anteile,
                z.Betrag, z.Gebuehr, Math.Round(kasse, 2), Math.Round(neu2, 2),
                z.Waehrung, z.Notiz));
        }

        fertig.Reverse();   // Juengste zuerst, wie auf einem Kontoauszug.

        var deckel = Math.Clamp(grenze, 1, 5000);
        var gezeigt = fertig.Take(deckel).ToList();

        var kaeufe = fertig.Count(z => z.Art == "kauf");
        var verkaeufe = fertig.Count(z => z.Art == "verkauf");

        var summen = fertig
            .GroupBy(z => z.Depot)
            .Select(g => new InvestJournalsumme(
                g.Key,
                g.Count(z => z.Art is "kauf" or "verkauf"),
                g.Where(z => z.Art == "einzahlung").Sum(z => z.Betrag),
                g.Where(z => z.Art == "auszahlung").Sum(z => -z.Betrag),
                g.Sum(z => z.Gebuehr),
                Math.Round(stand.GetValueOrDefault(g.Key), 2),
                g.First().Waehrung))
            .OrderBy(x => x.Depot)
            .ToList();

        return new InvestJournal(depot, gezeigt,
            kaeufe + verkaeufe, kaeufe, verkaeufe,
            summen,
            fertig.Count <= deckel,
            fertig.Count == 0
                ? "Noch kein Vorgang in diesem Depot."
                : "Jüngste zuerst. „Betrag“ ist, was in den Kurs ging; „Kasse“ ist, "
                + "was das Konto gekostet hat — ihr Unterschied ist die Gebühr. Der "
                + "Kontostand gilt jeweils NACH dem Vorgang und je Strategie: Ein "
                + "gemeinsamer Stand über drei Gegenrechnungen wäre dieselbe "
                + "Doppelzählung wie beim Gesamtvermögen."
                + (fertig.Count > deckel
                    ? $" Von {fertig.Count} Vorgängen sind die jüngsten {deckel} gezeigt."
                    : ""));
    }

    private sealed record Rohzeile(DateTime AmUtc, string Depot, string Art,
                                   string? Symbol, string? Name,
                                   decimal? Kurs, DateTime? KursUtc, decimal? Anteile,
                                   decimal Betrag, decimal Gebuehr, string Waehrung,
                                   string? Notiz);

    // -------------------------------------------------------------- Verlauf --

    /// <summary>
    /// Der Verlauf eines Depots, Tag für Tag — Positionen UND Konto.
    ///
    /// <para><b>Ohne den Kontostand wäre die Kurve falsch.</b> Nach einem
    /// Verkauf steckt das Geld nicht mehr im Kurs, sondern im Konto; eine Kurve
    /// nur über die Positionen stellte den Verkauf als Absturz dar. Genau
    /// deshalb ist auch das Konto ein Journal und keine gespeicherte Zahl —
    /// aus einer Zahl liesse sich der gestrige Stand nicht zurückrechnen.</para>
    ///
    /// <para><b>Fortgeschrieben, nicht geschnitten.</b> Ein Depot aus Aktie und
    /// Krypto hat samstags nur für die Krypto einen frischen Kurs. Die Regel
    /// dieses Projekts — „über mehrere Werte nur auf gemeinsamen
    /// Handelszeitpunkten“ — gilt hier ausdrücklich NICHT: Sie schützt
    /// Messungen von Zusammenhängen davor, den Kalender statt den Markt zu
    /// messen. Dies ist keine Messung, sondern eine Addition. Die Aktie ist am
    /// Samstag nicht wertlos, sie wird nur nicht gehandelt.</para>
    ///
    /// <para>Was aus dieser Reihe NICHT werden darf, ist eine Statistik über
    /// Tagesrenditen: Die fortgeschriebenen Tage sind keine Beobachtungen, und
    /// eine Schwankungsbreite darüber wäre systematisch zu klein.</para>
    /// </summary>
    public async Task<IReadOnlyList<InvestVerlauf>> VerlaufAsync(string depot,
                                                                 CancellationToken ct = default)
    {
        var reihen = await ReihenAsync(depot, ct);

        return reihen
            .Select(r => new InvestVerlauf(r.Waehrung, r.Punkte, r.Werte,
                "Vermögen ist Konto plus Kurswert — ohne das Konto sähe jeder Verkauf wie "
              + "ein Absturz aus. Kurse werden über Tage ohne Handel fortgeschrieben; eine "
              + "Aktie ist samstags nicht wertlos. Diese Reihe ist eine Addition, keine "
              + "Messung: Eine Schwankungsbreite darüber wäre zu klein, weil die "
              + "fortgeschriebenen Tage keine Beobachtungen sind."))
            .OrderByDescending(v => v.Punkte.Count > 0 ? v.Punkte[^1].Vermoegen : 0)
            .ToList();
    }

    /// <summary>Eine Reihe je Depot und Währung.</summary>
    private sealed record Depotreihe(string Depot, string Waehrung,
                                     List<InvestVerlaufPunkt> Punkte, List<string> Werte);

    /// <summary>
    /// Der gemeinsame Kern: baut je Depot und Währung eine Tagesreihe.
    /// <paramref name="depot"/> <c>null</c> heisst „alle“.
    /// </summary>
    private async Task<List<Depotreihe>> ReihenAsync(string? depot, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);

        var buchungen = (await conn.QueryAsync<InvestBuchung>(new CommandDefinition("""
            SELECT b.depot AS Depot, b.asset_id AS AssetId, a.symbol AS Symbol,
                   b.am_utc AS AmUtc, b.betrag AS Betrag, b.anteile AS Anteile,
                   b.gebuehr AS Gebuehr, b.waehrung AS Waehrung, b.kurs AS Kurs
              FROM dbo.invest_buchung b
              JOIN dbo.asset a ON a.asset_id = b.asset_id
             WHERE (@depot IS NULL OR b.depot = @depot)
             ORDER BY b.am_utc
            """, new { depot }, cancellationToken: ct))).ToList();

        var bewegungen = (await conn.QueryAsync<InvestKontobewegung>(new CommandDefinition("""
            SELECT depot AS Depot, waehrung AS Waehrung, am_utc AS AmUtc,
                   betrag AS Betrag, grund AS Grund
              FROM dbo.invest_kontobewegung
             WHERE (@depot IS NULL OR depot = @depot)
             ORDER BY am_utc
            """, new { depot }, cancellationToken: ct))).ToList();

        if (buchungen.Count == 0 && bewegungen.Count == 0) return [];

        var ersterTag = buchungen.Select(b => b.AmUtc)
            .Concat(bewegungen.Select(m => m.AmUtc))
            .Min().Date;

        var heute = DateTime.UtcNow.Date;

        if ((heute - ersterTag).TotalDays > MaxTage)
            ersterTag = heute.AddDays(-MaxTage);

        var kurseJeWert = await KurseAsync(conn,
            buchungen.Select(b => b.AssetId).Distinct().ToArray(), ersterTag, ct);

        var ergebnis = new List<Depotreihe>();
        var jetzt = DateTime.UtcNow;

        var gruppen = buchungen.Select(b => (b.Depot, b.Waehrung))
            .Concat(bewegungen.Select(m => (m.Depot, m.Waehrung)))
            .Distinct();

        foreach (var (d, waehrung) in gruppen)
        {
            var reihe = buchungen.Where(b => b.Depot == d && b.Waehrung == waehrung)
                                 .OrderBy(b => b.AmUtc).ToList();

            var kasse = bewegungen.Where(m => m.Depot == d && m.Waehrung == waehrung)
                                  .OrderBy(m => m.AmUtc).ToList();

            /*  Der EXAKTE Zeitpunkt des ersten Vorgangs, nicht sein Datum.

                Mit `.Date` begann die Reihe um Mitternacht -- also Stunden
                bevor irgendetwas eingezahlt war -- und ihr erster Punkt stand
                bei null. Die Kurve fing damit unten an und sprang bei der
                Einzahlung senkrecht nach oben: Sie behauptete einen Verlust,
                den es nie gab. Genau der Fehler, den ich beim Zeichnen der
                Depotlinien vermieden hatte und hier wieder eingebaut habe.   */
            var start = new[]
            {
                reihe.Count > 0 ? reihe[0].AmUtc : DateTime.MaxValue,
                kasse.Count > 0 ? kasse[0].AmUtc : DateTime.MaxValue
            }.Min();

            if (start > jetzt) continue;

            /*  Die Zeitachse dieser Reihe.

                Grob, wo es nur Tagesschluesse gibt; fein, wo Stundenbars
                vorliegen. Die Stundenzeitpunkte kommen aus den Kursen selbst
                und nicht aus einer erzeugten Folge -- eine Stunde ohne Bar
                waere sonst ein Punkt, der die Fortschreibung wiederholt und
                die Kurve waagerecht ausfranst.

                Der letzte Punkt ist IMMER jetzt: Sonst endete die Kurve auf
                der letzten vollen Stunde, waehrend die Tabelle daneben den
                aktuellen Stand zeigt.                                        */
            var feinAb = jetzt.Date.AddDays(-FeinTage);

            var zeitpunkte = new List<DateTime>();

            for (var tag = start.Date; tag < feinAb; tag = tag.AddDays(1))
                zeitpunkte.Add(tag.AddDays(1).AddSeconds(-1));   // Tagesende

            var feine = kurseJeWert
                .Where(kv => reihe.Any(b => b.AssetId == kv.Key))
                .SelectMany(kv => kv.Value)
                .Select(x => x.Zeit)
                .Where(t => t >= feinAb && t >= start && t <= jetzt)
                .Distinct()
                .OrderBy(t => t);

            zeitpunkte.AddRange(feine);
            zeitpunkte.Add(jetzt);

            /*  Ein Depot ohne Positionen (nur Kasse) hat keine Kurse und damit
                keine feinen Zeitpunkte. Damit es trotzdem eine Linie bekommt,
                wird die Kassenspur selbst zum Zeitpunkt.                     */
            foreach (var m in kasse) if (m.AmUtc >= start) zeitpunkte.Add(m.AmUtc);

            zeitpunkte = zeitpunkte.Distinct().OrderBy(t => t).ToList();

            var punkte = new List<InvestVerlaufPunkt>(zeitpunkte.Count);
            var anteile = new Dictionary<int, decimal>();
            var konto = 0m;
            var eingezahlt = 0m;
            var i = 0;
            var j = 0;

            foreach (var zeit in zeitpunkte)
            {
                // Alle Vorgänge bis zu diesem Zeitpunkt einarbeiten.
                while (i < reihe.Count && reihe[i].AmUtc <= zeit)
                {
                    var b = reihe[i++];
                    anteile[b.AssetId] = anteile.GetValueOrDefault(b.AssetId) + b.Anteile;
                }

                while (j < kasse.Count && kasse[j].AmUtc <= zeit)
                {
                    var m = kasse[j++];
                    konto += m.Betrag;

                    // Nur was von aussen kam, zählt als Einzahlung.
                    if (m.Grund is "einzahlung" or "auszahlung") eingezahlt += m.Betrag;
                }

                var positionen = 0m;

                foreach (var (assetId, stueck) in anteile)
                {
                    if (stueck == 0) continue;
                    if (!kurseJeWert.TryGetValue(assetId, out var kurse)) continue;

                    // Letzter Kurs an oder vor diesem Zeitpunkt — die Fortschreibung.
                    if (LetzterKurs(kurse, zeit) is { } c) positionen += stueck * c;
                }

                positionen = Math.Round(positionen, 2);

                punkte.Add(new InvestVerlaufPunkt(zeit, positionen, Math.Round(konto, 2),
                                                  Math.Round(positionen + konto, 2),
                                                  Math.Round(eingezahlt, 2)));
            }

            ergebnis.Add(new Depotreihe(d, waehrung, punkte,
                reihe.Select(b => b.Symbol).Distinct().OrderBy(x => x).ToList()));
        }

        return ergebnis;
    }

    /// <summary>
    /// Tagesschlüsse der genannten Werte ab <paramref name="von"/>, je Wert nach
    /// Tag sortiert.
    ///
    /// <para>Der Vorlauf von dreissig Tagen ist kein Puffer aus Bequemlichkeit:
    /// Ohne ihn hätte ein Wert, der am ersten Tag keine Bar hat, nichts zum
    /// Fortschreiben, und der Verlauf begänne mit einer Lücke — die wie ein
    /// Vermögen von null aussähe.</para>
    /// </summary>
    private static async Task<Dictionary<int, List<(DateTime Zeit, decimal Schluss)>>> KurseAsync(
        IDbConnection conn, int[] assetIds, DateTime von, CancellationToken ct)
    {
        if (assetIds.Length == 0) return [];

        /*  BEIDE Aufloesungen. Die Tagesbars tragen den Verlauf ueber lange
            Zeitraeume, die Stundenbars die juengsten Tage -- ohne sie hat ein
            heute eroeffnetes Depot genau einen Punkt und zeichnet keine Linie,
            obwohl sich sein Wert stuendlich bewegt.

            Die Stundenbars nur fuer das feine Fenster: Ein Jahr in
            Stundenaufloesung waeren rund 6.000 Punkte je Wert, und die
            Vermoegenskurve braucht sie nicht.                                 */
        var bars = (await conn.QueryAsync<Kurspunkt>(new CommandDefinition("""
            SELECT asset_id AS AssetId, ts_utc AS TsUtc, [close] AS Schluss
              FROM dbo.price_bar
             WHERE asset_id IN @ids
               AND [close] > 0
               AND (   (interval_code = '1d' AND ts_utc >= @von)
                    OR (interval_code = '1h' AND ts_utc >= @fein))
             ORDER BY asset_id, ts_utc
            """,
            new { ids = assetIds, von = von.AddDays(-30),
                  fein = DateTime.UtcNow.Date.AddDays(-FeinTage) },
            cancellationToken: ct))).ToList();

        var reihen = bars
            .GroupBy(b => b.AssetId)
            .ToDictionary(g => g.Key,
                          g => g.Select(x => (Zeit: x.TsUtc, Schluss: x.Schluss))
                                .OrderBy(x => x.Zeit).ToList());

        /*  Den letzten Punkt auf den JUENGSTEN bekannten Kurs heben.

            Fuer vergangene Tage ist der Tagesschluss richtig. Fuer HEUTE gibt
            es noch keinen -- der Tageslauf holt ihn erst um 02:20 UTC. Ohne
            diesen Zusatz endete die Kurve auf dem Stand von vorgestern,
            waehrend die Tabelle daneben den frischen Stundenkurs zeigt. Zwei
            Zahlen fuer dasselbe, und man sucht den Fehler in der falschen.   */
        var frisch = await conn.QueryAsync<Kurspunkt>(new CommandDefinition("""
            SELECT a.asset_id AS AssetId, k.ts_utc AS TsUtc, k.schluss AS Schluss
              FROM dbo.asset a
              OUTER APPLY dbo.letzter_kurs(a.asset_id) k
             WHERE a.asset_id IN @ids AND k.schluss IS NOT NULL
            """, new { ids = assetIds }, cancellationToken: ct));

        foreach (var f in frisch)
        {
            if (!reihen.TryGetValue(f.AssetId, out var reihe))
            {
                reihen[f.AssetId] = [(f.TsUtc, f.Schluss)];
                continue;
            }

            // Nur anhaengen, wenn wirklich neuer -- sonst steht er schon drin.
            if (reihe.Count == 0 || reihe[^1].Zeit < f.TsUtc) reihe.Add((f.TsUtc, f.Schluss));
        }

        return reihen;
    }

    // -------------------------------------------------------- Gesamtvermögen --

    /// <summary>
    /// Alles zusammen: jedes Depot, beide Währungen, eine Zahl und eine Kurve.
    ///
    /// <para><b>Umgerechnet mit dem Kurs JENES Tages, nicht dem von heute.</b>
    /// Ein historischer Verlauf zum heutigen Wechselkurs behauptet, das Geld
    /// hätte damals anders gestanden, als es dastand — der Fehler ist am Ende
    /// der Reihe null und wächst nach hinten, sieht also aus wie ein Trend.</para>
    ///
    /// <para>Dass das überhaupt geht, liegt an <c>EURUSD=X</c>: rund 5.900
    /// Tagesbars, verfolgt. In dieser Datei stand einmal das Gegenteil, und es
    /// war ungeprüft.</para>
    /// </summary>
    public async Task<InvestGesamt> GesamtAsync(string zielWaehrung,
                                                CancellationToken ct = default)
    {
        var ziel = (zielWaehrung ?? "EUR").Trim().ToUpperInvariant();
        if (!Waehrungen.Contains(ziel)) ziel = "EUR";

        var reihen = await ReihenAsync(null, ct);

        await using var conn = await factory.OpenAsync(ct);

        /*  Welche Depots ueberhaupt in die Summe gehoeren.

            `streng`, `aktiv` und `halten` sind drei Antworten auf dieselbe
            Frage -- was aus denselben 1.000 geworden waere, wenn man so oder so
            vorgegangen waere. Sie zu addieren zaehlt dasselbe Geld dreimal; das
            Band meldete so 3.422,79 EUR, wo 2.000 USD richtig waren. Genau eine
            Strategie traegt `zaehlt = 1`; die anderen bleiben in der
            Aufstellung und im Vergleich, aber draussen aus der Summe.        */
        var ausgenommen = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT depot FROM dbo.autopilot_einstellung WHERE zaehlt = 0",
            cancellationToken: ct))).ToHashSet();

        var fx = await conn.QuerySingleOrDefaultAsync<Tagesschluss>(new CommandDefinition("""
            SELECT TOP 1 p.[close] AS Schluss, p.ts_utc AS TsUtc
              FROM dbo.price_bar p
              JOIN dbo.asset a ON a.asset_id = p.asset_id
             WHERE a.symbol = 'EURUSD=X' AND p.interval_code = '1d'
             ORDER BY p.ts_utc DESC
            """, cancellationToken: ct));

        var fxReihe = await conn.QueryAsync<Kurspunkt>(new CommandDefinition("""
            SELECT p.asset_id AS AssetId, p.ts_utc AS TsUtc, p.[close] AS Schluss
              FROM dbo.price_bar p
              JOIN dbo.asset a ON a.asset_id = p.asset_id
             WHERE a.symbol = 'EURUSD=X' AND p.interval_code = '1d'
             ORDER BY p.ts_utc
            """, cancellationToken: ct));

        var fxTage = fxReihe.Select(x => (Tag: x.TsUtc.Date, x.Schluss)).ToList();

        if (reihen.Count == 0)
        {
            return new InvestGesamt(ziel, 0, 0, 0, null,
                fx.Schluss > 0 ? fx.Schluss : null, fx.Schluss > 0 ? fx.TsUtc : null,
                [], [], KeinBestandHinweis());
        }

        /*  EURUSD=X notiert Dollar je Euro. Nach EUR wird deshalb geteilt, nach
            USD multipliziert — und wer die Richtung verwechselt, bekommt bei
            einem Kurs nahe 1 eine Zahl, die immer noch plausibel aussieht. Genau
            deshalb steht die Umrechnung an EINER Stelle.                       */
        decimal Um(decimal betrag, string von, DateTime tag)
        {
            if (von == ziel) return betrag;

            var kurs = LetzterKurs(fxTage, tag) ?? (fx.Schluss > 0 ? fx.Schluss : 0m);
            if (kurs <= 0) return betrag;   // ohne Kurs lieber ungerechnet als geraten

            return ziel == "EUR" ? betrag / kurs : betrag * kurs;
        }

        var heute = DateTime.UtcNow.Date;

        /*  Die gemeinsame Zeitachse ist die Vereinigung der Zeitpunkte aller
            beitragenden Reihen -- nicht eine erzeugte Tagesfolge. Die Reihen
            sind in den juengsten Tagen stuendlich aufgeloest; eine Tagesfolge
            darueberzulegen warfe genau die Punkte weg, die die Bewegung
            zeigen.                                                           */
        var beitragend = reihen.Where(r => !ausgenommen.Contains(r.Depot)).ToList();

        if (beitragend.Count == 0)
        {
            return new InvestGesamt(ziel, 0, 0, 0, null,
                fx.Schluss > 0 ? fx.Schluss : null, fx.Schluss > 0 ? fx.TsUtc : null,
                [], [], KeinBestandHinweis());
        }

        var achse = beitragend
            .SelectMany(r => r.Punkte.Select(p => p.Zeit))
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        var punkte = new List<InvestVerlaufPunkt>(achse.Count);
        var zeiger = reihen.ToDictionary(r => r, _ => 0);

        foreach (var zeit in achse)
        {
            decimal pos = 0, konto = 0, ein = 0;

            foreach (var r in reihen)
            {
                // Vor dem Beginn einer Reihe trägt sie nichts bei.
                if (r.Punkte[0].Zeit > zeit) continue;

                // Vergleichsläufe stehen in der Aufstellung, nicht in der Kurve.
                if (ausgenommen.Contains(r.Depot)) continue;


                var k = zeiger[r];
                while (k + 1 < r.Punkte.Count && r.Punkte[k + 1].Zeit <= zeit) k++;
                zeiger[r] = k;

                var pt = r.Punkte[k];
                pos += Um(pt.Positionen, r.Waehrung, zeit);
                konto += Um(pt.Konto, r.Waehrung, zeit);
                ein += Um(pt.Eingezahlt, r.Waehrung, zeit);
            }

            punkte.Add(new InvestVerlaufPunkt(zeit, Math.Round(pos, 2), Math.Round(konto, 2),
                                              Math.Round(pos + konto, 2), Math.Round(ein, 2)));
        }

        // Je Depot der heutige Stand, ebenfalls umgerechnet.
        var depots = reihen
            .GroupBy(r => r.Depot)
            .Select(g =>
            {
                var vermoegen = g.Sum(r => Um(r.Punkte[^1].Vermoegen, r.Waehrung, heute));
                var eingezahlt = g.Sum(r => Um(r.Punkte[^1].Eingezahlt, r.Waehrung, heute));
                var gewinn = vermoegen - eingezahlt;

                return new InvestGesamtDepot(
                    g.Key, Math.Round(vermoegen, 2), Math.Round(eingezahlt, 2),
                    Math.Round(gewinn, 2),
                    eingezahlt > 0 ? (double)(gewinn / eingezahlt) * 100 : null,
                    g.SelectMany(r => r.Werte).Distinct().Count(),
                    !ausgenommen.Contains(g.Key));
            })
            /*  Was zaehlt, steht vorn -- die Vergleichslaeufe darunter. Eine
                Aufstellung, in der die Summanden zwischen den Nicht-Summanden
                stehen, laedt zum Kopfrechnen ein, das nicht aufgeht.         */
            .OrderByDescending(d => d.Zaehlt).ThenByDescending(d => d.Vermoegen)
            .ToList();

        var letzte = punkte.Count > 0 ? punkte[^1] : null;
        var gesamt = letzte?.Vermoegen ?? 0;
        var gesamtEin = letzte?.Eingezahlt ?? 0;
        var gesamtGewinn = gesamt - gesamtEin;

        return new InvestGesamt(ziel, gesamt, gesamtEin, gesamtGewinn,
            gesamtEin > 0 ? (double)(gesamtGewinn / gesamtEin) * 100 : null,
            fx.Schluss > 0 ? fx.Schluss : null, fx.Schluss > 0 ? fx.TsUtc : null,
            depots, punkte,
            Zusammenfassung(ziel, depots));
    }

    private static string Zusammenfassung(string ziel, List<InvestGesamtDepot> depots)
    {
        var draussen = depots.Where(d => !d.Zaehlt).Select(d => d.Depot).ToList();

        var basis =
            $"Gerechnet in {ziel}, umgerechnet über EURUSD=X mit dem Kurs des jeweiligen "
          + "Tages, nicht dem von heute — sonst behauptete die Kurve, das Geld hätte "
          + "damals anders gestanden, als es dastand.";

        if (draussen.Count == 0) return basis;

        return basis
          + $" Nicht in der Summe: {string.Join(" und ", draussen)}. Die Strategien des "
          + "Autopiloten sind Gegenrechnungen auf dasselbe Geld — was aus demselben Budget "
          + "geworden wäre, wenn man so oder so vorgegangen wäre. Sie zu addieren zählte "
          + "dasselbe Geld mehrfach.";
    }

    /// <summary>
    /// Eine Kurve je Depot — für den Vergleich, welches sich besser entwickelt.
    ///
    /// <para>Anders als <see cref="GesamtAsync"/> wird hier <b>nicht</b>
    /// summiert, sondern nebeneinandergestellt. Genau deshalb dürfen die
    /// Vergleichsläufe hier mit: Was in einer Summe eine Doppelzählung wäre,
    /// ist als eigene Linie die Aussage.</para>
    ///
    /// <para>Ein Depot mit mehreren Währungen wird zusammengefasst; umgerechnet
    /// wird wie überall mit dem Kurs des jeweiligen Tages.</para>
    /// </summary>
    public async Task<IReadOnlyList<InvestDepotreihe>> DepotverlaufAsync(
        string zielWaehrung, CancellationToken ct = default)
    {
        var ziel = (zielWaehrung ?? "EUR").Trim().ToUpperInvariant();
        if (!Waehrungen.Contains(ziel)) ziel = "EUR";

        var reihen = await ReihenAsync(null, ct);
        if (reihen.Count == 0) return [];

        await using var conn = await factory.OpenAsync(ct);

        var fxTage = (await conn.QueryAsync<Kurspunkt>(new CommandDefinition("""
            SELECT p.asset_id AS AssetId, p.ts_utc AS TsUtc, p.[close] AS Schluss
              FROM dbo.price_bar p
              JOIN dbo.asset a ON a.asset_id = p.asset_id
             WHERE a.symbol = 'EURUSD=X' AND p.interval_code = '1d'
             ORDER BY p.ts_utc
            """, cancellationToken: ct)))
            .Select(x => (Tag: x.TsUtc.Date, x.Schluss)).ToList();

        var ausgenommen = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT depot FROM dbo.autopilot_einstellung WHERE zaehlt = 0",
            cancellationToken: ct))).ToHashSet();

        decimal Um(decimal betrag, string von, DateTime tag)
        {
            if (von == ziel) return betrag;

            var kurs = LetzterKurs(fxTage, tag) ?? 0m;
            if (kurs <= 0) return betrag;

            return ziel == "EUR" ? betrag / kurs : betrag * kurs;
        }

        var heute = DateTime.UtcNow.Date;
        var ergebnis = new List<InvestDepotreihe>();

        foreach (var gruppe in reihen.GroupBy(r => r.Depot))
        {
            // Die Zeitachse dieses Depots — die Vereinigung seiner Reihen.
            var achse = gruppe
                .SelectMany(r => r.Punkte.Select(x => x.Zeit))
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            var zeiten = new List<DateTime>(achse.Count);
            var werte = new List<decimal>(achse.Count);

            var zeiger = gruppe.ToDictionary(r => r, _ => 0);

            foreach (var zeit in achse)
            {
                var wert = 0m;

                foreach (var r in gruppe)
                {
                    if (r.Punkte[0].Zeit > zeit) continue;

                    var k = zeiger[r];
                    while (k + 1 < r.Punkte.Count && r.Punkte[k + 1].Zeit <= zeit) k++;
                    zeiger[r] = k;

                    wert += Um(r.Punkte[k].Vermoegen, r.Waehrung, zeit);
                }

                zeiten.Add(zeit);
                werte.Add(Math.Round(wert, 2));
            }

            /*  Bezugspunkt ist der erste Tag mit einem Wert über null. Auf einen
                Startwert von null zu beziehen ergäbe eine Division durch null --
                und das passiert real: Ein Depot, dessen erste Bewegung eine
                Einzahlung am zweiten Tag ist, steht am ersten bei null.        */
            var basis = werte.FirstOrDefault(w => w > 0);

            var indexiert = basis > 0
                ? werte.Select(w => (double)(w / basis) * 100).ToList()
                : werte.Select(_ => 100.0).ToList();

            ergebnis.Add(new InvestDepotreihe(gruppe.Key, !ausgenommen.Contains(gruppe.Key),
                ziel, zeiten, werte, indexiert));
        }

        return ergebnis
            .OrderByDescending(r => r.Zaehlt)
            .ThenByDescending(r => r.Werte.Count > 0 ? r.Werte[^1] : 0)
            .ToList();
    }

    private static string KeinBestandHinweis() =>
        "Noch nichts im Depot. Sobald etwas eingezahlt oder gekauft ist, steht hier das "
      + "Gesamtvermögen über alle Depots.";

    private readonly record struct Kurspunkt(int AssetId, DateTime TsUtc, decimal Schluss);

    /// <summary>Letzter bekannter Schluss an oder vor <paramref name="zeit"/>.</summary>
    private static decimal? LetzterKurs(List<(DateTime Zeit, decimal Schluss)> kurse,
                                        DateTime zeit)
    {
        /*  Binäre Suche statt linearer Vorwärtszeiger: Der Verlauf ruft je Tag
            und Wert einmal auf — bei einem Jahr und zehn Werten sind das 3.650
            Aufrufe. Ein Zeiger je Wert wäre schneller und bräuchte einen
            Zustand je Wert; der Unterschied liegt hier im Bereich von
            Millisekunden.                                                     */
        var lo = 0;
        var hi = kurse.Count - 1;
        var treffer = -1;

        while (lo <= hi)
        {
            var m = (lo + hi) / 2;

            if (kurse[m].Zeit <= zeit) { treffer = m; lo = m + 1; }
            else hi = m - 1;
        }

        return treffer >= 0 ? kurse[treffer].Schluss : null;
    }
}
