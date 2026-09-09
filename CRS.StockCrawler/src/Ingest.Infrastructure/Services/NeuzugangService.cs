using System.Globalization;
using System.Text.Json;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Models;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Vorankündigungen: was demnächst neu an den Markt kommt.
///
/// <para><b>Wozu.</b> In CLAUDE.md steht seit Monaten, dass sich Neuzugänge aus
/// dem eigenen Bestand nicht auswerten lassen: Das Universum ist die Rangliste
/// nach Marktkapitalisierung, ein Wert taucht dort erst auf, <i>nachdem</i> er
/// gestiegen ist. Wer Neulinge untersuchen will, braucht die vollständige
/// Kohorte aus Ankündigungen — Fehlschläge eingeschlossen. Diese Sammlung ist
/// genau das.</para>
///
/// <para><b>Sie beginnt heute und wächst vorwärts.</b> Rückwirkend füllen liesse
/// sie sich nur aus dem Bestand, und damit hätte man den Überlebensirrtum
/// wieder eingebaut, den sie beheben soll.</para>
///
/// <para><b>Jede Quelle in ihrem eigenen <c>try</c>.</b> Fällt der IPO-Kalender
/// aus, sollen die Börsenankündigungen trotzdem hereinkommen — und der
/// Ausfall soll sichtbar sein. Eine Quelle, die stillschweigend nichts mehr
/// liefert, sieht sonst aus wie ein Markt ohne Neuzugänge.</para>
/// </summary>
public sealed class NeuzugangService(
    ISqlConnectionFactory factory,
    IHttpClientFactory http,
    ILogger<NeuzugangService> log) : INeuzugangService
{
    /*  Yahoo und CoinGecko brauchen einen Browser-Kopf, sonst 403 -- dasselbe
        gilt fuer die beiden Quellen hier. Zentral in DependencyInjection, hier
        noch einmal, weil dieser Dienst einen eigenen Klienten nimmt.         */
    private const string Browser =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      + "(KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    public async Task<IReadOnlyList<NeuzugangLauf>> SammleAsync(CancellationToken ct = default)
    {
        var ergebnis = new List<NeuzugangLauf>();

        foreach (var (quelle, sammler) in new (string, Func<CancellationToken, Task<int>>)[]
        {
            ("nasdaq-ipo", NasdaqAsync),
            ("binance",    BinanceAsync)
        })
        {
            var lauf = await MitProtokollAsync(quelle, sammler, ct);
            ergebnis.Add(lauf);
        }

        await ErstkurseNachtragenAsync(ct);

        return ergebnis;
    }

    /// <summary>
    /// Führt einen Sammler aus und schreibt das Ergebnis fest — auch den
    /// Fehlschlag. Eine Quelle, die nichts liefert, muss sich von einer Quelle
    /// unterscheiden lassen, bei der es nichts zu liefern gab.
    /// </summary>
    private async Task<NeuzugangLauf> MitProtokollAsync(
        string quelle, Func<CancellationToken, Task<int>> sammler, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);

        var vorher = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.neuzugang WHERE quelle = @q",
            new { q = quelle }, cancellationToken: ct));

        int gefunden;
        var erfolg = true;
        string? meldung = null;

        try
        {
            gefunden = await sammler(ct);
        }
        catch (Exception ex)
        {
            gefunden = 0;
            erfolg = false;
            meldung = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
            log.LogWarning(ex, "Neuzugänge: Quelle {Quelle} nicht erreichbar", quelle);
        }

        var nachher = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.neuzugang WHERE quelle = @q",
            new { q = quelle }, cancellationToken: ct));

        var laufId = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO dbo.neuzugang_lauf (quelle, gefunden, neu, erfolg, meldung)
            OUTPUT INSERTED.lauf_id VALUES (@q, @gef, @neu, @ok, @msg)
            """,
            new { q = quelle, gef = gefunden, neu = nachher - vorher, ok = erfolg, msg = meldung },
            cancellationToken: ct));

        return new NeuzugangLauf(laufId, DateTime.UtcNow, quelle,
            gefunden, nachher - vorher, erfolg, meldung);
    }

    // ------------------------------------------------------------- Nasdaq --

    /// <summary>
    /// Der IPO-Kalender von Nasdaq. Liefert <c>expectedPriceDate</c> — also eine
    /// echte Vorankündigung mit Datum, nicht bloss eine Liste dessen, was schon
    /// da ist.
    ///
    /// <para>Drei Monate im Voraus: Der laufende Monat ist meist schon
    /// abgearbeitet, und weiter als ein Quartal reicht der Kalender selten.</para>
    /// </summary>
    private async Task<int> NasdaqAsync(CancellationToken ct)
    {
        var klient = http.CreateClient();
        klient.Timeout = TimeSpan.FromSeconds(25);

        var gefunden = 0;

        for (var m = 0; m < 3; m++)
        {
            var monat = DateTime.UtcNow.AddMonths(m).ToString("yyyy-MM");

            using var anfrage = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.nasdaq.com/api/ipo/calendar?date={monat}");

            anfrage.Headers.TryAddWithoutValidation("User-Agent", Browser);
            anfrage.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var antwort = await klient.SendAsync(anfrage, ct);
            antwort.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await antwort.Content.ReadAsStringAsync(ct));

            if (!doc.RootElement.TryGetProperty("data", out var daten)) continue;

            /*  Zwei Tabellen: `upcoming` sind Ankuendigungen, `priced` sind
                bereits bepreiste. Beide gehoeren in die Kohorte -- wer nur die
                anstehenden nimmt, verliert genau die, die schon losgelaufen
                sind, und damit den interessanten Teil.                       */
            foreach (var (pfad, art) in new[]
            {
                ("upcoming", "angekuendigt"),
                ("priced",   "gehandelt")
            })
            {
                if (!daten.TryGetProperty(pfad, out var abschnitt)) continue;

                var tabelle = pfad == "upcoming"
                    ? (abschnitt.TryGetProperty("upcomingTable", out var t) ? t : default)
                    : abschnitt;

                if (tabelle.ValueKind != JsonValueKind.Object) continue;
                if (!tabelle.TryGetProperty("rows", out var zeilen)) continue;
                if (zeilen.ValueKind != JsonValueKind.Array) continue;

                foreach (var z in zeilen.EnumerateArray())
                {
                    var symbol = Text(z, "proposedTickerSymbol") ?? Text(z, "symbol");
                    if (string.IsNullOrWhiteSpace(symbol)) continue;

                    gefunden++;

                    var (von, bis) = Preisspanne(Text(z, "proposedSharePrice"));

                    await MerkeAsync("nasdaq-ipo", "ipo", symbol.Trim().ToUpperInvariant(),
                        Text(z, "companyName"),
                        Text(z, "proposedExchange") ?? Text(z, "market"),
                        Datum(Text(z, "expectedPriceDate") ?? Text(z, "pricedDate")),
                        von, bis,
                        Zahl(Text(z, "dollarValueOfSharesOffered")),
                        art,
                        $"https://www.nasdaq.com/market-activity/ipos",
                        Text(z, "companyName"), ct);
                }
            }
        }

        return gefunden;
    }

    // ------------------------------------------------------------ Binance --

    /// <summary>
    /// Binances Katalog „New Cryptocurrency Listing“ (catalogId 48).
    ///
    /// <para>Das Symbol steht im Titel und nicht in einem eigenen Feld — es
    /// wird deshalb aus der Klammer gelesen („… (DJTB) …“). Fehlt eine, wird
    /// der Eintrag mit dem Titel als Kennung abgelegt: Lieber eine Zeile ohne
    /// sauberes Symbol als eine Ankündigung, die niemand sieht.</para>
    /// </summary>
    private async Task<int> BinanceAsync(CancellationToken ct)
    {
        var klient = http.CreateClient();
        klient.Timeout = TimeSpan.FromSeconds(25);

        using var anfrage = new HttpRequestMessage(HttpMethod.Get,
            "https://www.binance.com/bapi/composite/v1/public/cms/article/list/query"
          /*  20, nicht 30. Binance laesst nur bestimmte Seitengroessen zu:
              gemessen antworten 5, 10, 20 und 50 mit 200 -- 30 dagegen mit
              400. Eine Zahl dazwischen zu waehlen sieht harmlos aus und
              scheitert stumm.                                              */
          + "?type=1&catalogId=48&pageNo=1&pageSize=20");

        anfrage.Headers.TryAddWithoutValidation("User-Agent", Browser);
        anfrage.Headers.TryAddWithoutValidation("Accept", "application/json");
        anfrage.Headers.TryAddWithoutValidation("Accept-Language", "en");

        using var antwort = await klient.SendAsync(anfrage, ct);
        antwort.EnsureSuccessStatusCode();

        var roh = await antwort.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(roh)) throw new InvalidOperationException("leere Antwort");

        using var doc = JsonDocument.Parse(roh);

        if (!doc.RootElement.TryGetProperty("data", out var daten) ||
            !daten.TryGetProperty("catalogs", out var kataloge) ||
            kataloge.ValueKind != JsonValueKind.Array) return 0;

        var gefunden = 0;

        foreach (var k in kataloge.EnumerateArray())
        {
            if (!k.TryGetProperty("articles", out var artikel) ||
                artikel.ValueKind != JsonValueKind.Array) continue;

            foreach (var a in artikel.EnumerateArray())
            {
                var titel = Text(a, "title");
                if (string.IsNullOrWhiteSpace(titel)) continue;

                gefunden++;

                var symbol = AusKlammer(titel) ?? titel[..Math.Min(38, titel.Length)];
                var code = Text(a, "code");

                /*  Der Zeitstempel ist die Veroeffentlichung der Ankuendigung.
                    Das Startdatum steht meist im Titel; es dort herauszulesen
                    waere Ratearbeit ueber wechselnde Formulierungen. Deshalb
                    bleibt `erwartet_am` leer, und die Anzeige nennt statt eines
                    erfundenen Datums den Ankuendigungszeitpunkt.             */
                await MerkeAsync("binance", "listing", symbol.Trim().ToUpperInvariant(),
                    titel.Length > 190 ? titel[..190] : titel,
                    "Binance", null, null, null, null, "angekuendigt",
                    code is null ? null : $"https://www.binance.com/en/support/announcement/{code}",
                    titel, ct);
            }
        }

        return gefunden;
    }

    // ----------------------------------------------------------- Ablegen ---

    /// <summary>
    /// Legt eine Ankündigung ab oder frischt sie auf.
    ///
    /// <para>MERGE über (Quelle, Symbol): Die Sammler laufen täglich über
    /// dieselben Listen. Ohne diesen Riegel stünde nach einer Woche jede
    /// Ankündigung siebenmal da — dieselbe Scheinmenge wie bei den stündlich
    /// wiederholten Prognosen auf denselben Zielbar.</para>
    ///
    /// <para><b>`entdeckt_utc` wird beim Auffrischen NICHT angefasst.</b> Es
    /// hält fest, wann wir es zuerst gesehen haben — und daraus entsteht die
    /// einzige Zahl, die diese Seite wirklich beantwortet: wieviel Vorlauf eine
    /// Ankündigung lässt.</para>
    /// </summary>
    private async Task MerkeAsync(string quelle, string art, string symbol, string? name,
                                  string? markt, DateTime? erwartet,
                                  decimal? von, decimal? bis, decimal? volumen,
                                  string status, string? url, string? titel,
                                  CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.neuzugang WITH (HOLDLOCK) AS t
            USING (SELECT @quelle AS quelle, @symbol AS symbol) AS s
               ON t.quelle = s.quelle AND t.symbol = s.symbol
            WHEN MATCHED THEN UPDATE SET
                 name        = COALESCE(@name, t.name),
                 markt       = COALESCE(@markt, t.markt),
                 erwartet_am = COALESCE(@erwartet, t.erwartet_am),
                 preis_von   = COALESCE(@von, t.preis_von),
                 preis_bis   = COALESCE(@bis, t.preis_bis),
                 volumen     = COALESCE(@volumen, t.volumen),
                 -- Einmal 'gehandelt' bleibt 'gehandelt'.
                 status      = CASE WHEN t.status = 'gehandelt' THEN t.status ELSE @status END,
                 url         = COALESCE(@url, t.url),
                 titel       = COALESCE(@titel, t.titel),
                 aktualisiert_utc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                 INSERT (quelle, art, symbol, name, markt, erwartet_am,
                         preis_von, preis_bis, volumen, status, url, titel)
                 VALUES (@quelle, @art, @symbol, @name, @markt, @erwartet,
                         @von, @bis, @volumen, @status, @url, @titel);
            """,
            /*  `erwartet` als DateTime, nicht als DateOnly.

                Dapper weist DateOnly als PARAMETER zurueck -- „The member
                erwartet of type System.DateOnly cannot be used as a parameter
                value". Dieselbe Lehre wie bei den ValueTuples, die als Ergebnis
                gehen und als Parameter nicht: Der Typ, der im Modell richtig
                ist, muss es an der Schnittstelle zur Datenbank nicht sein.
                Die Spalte ist DATE, ein DateTime um Mitternacht passt genau.  */
            new { quelle, art, symbol, name, markt,
                  erwartet,
                  von, bis, volumen, status, url, titel },
            cancellationToken: ct));
    }

    /// <summary>
    /// Trägt für Ankündigungen, die inzwischen im Bestand auftauchen, den
    /// ersten bekannten Kurs nach.
    ///
    /// <para>Damit wird aus einer Liste von Namen eine messbare Kohorte: Erst
    /// wenn Startkurs und Zeitpunkt feststehen, lässt sich später sagen, was
    /// aus einem Neuzugang geworden ist.</para>
    /// </summary>
    private async Task ErstkurseNachtragenAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);

        var n = await conn.ExecuteAsync(new CommandDefinition("""
            /*  Verknuepfung ueber das Symbol. Bei Aktien passt es unmittelbar;
                bei Krypto traegt der Bestand das Suffix -USD, deshalb beide
                Schreibweisen.                                                */
            WITH treffer AS (
              SELECT nz.neuzugang_id, a.asset_id,
                     k.erster, k.erster_utc
                FROM dbo.neuzugang nz
                JOIN dbo.asset a
                  ON a.symbol = nz.symbol OR a.symbol = nz.symbol + '-USD'
               CROSS APPLY (SELECT TOP 1 p.[close] AS erster, p.ts_utc AS erster_utc
                              FROM dbo.price_bar p
                             WHERE p.asset_id = a.asset_id AND p.[close] > 0
                             ORDER BY p.ts_utc) k
               WHERE nz.asset_id IS NULL
            )
            UPDATE nz
               SET nz.asset_id = t.asset_id,
                   nz.erster_kurs = t.erster,
                   nz.erster_kurs_utc = t.erster_utc,
                   nz.status = 'gehandelt',
                   nz.aktualisiert_utc = SYSUTCDATETIME()
              FROM dbo.neuzugang nz JOIN treffer t ON t.neuzugang_id = nz.neuzugang_id
            """, cancellationToken: ct));

        if (n > 0) log.LogInformation("Neuzugänge: {N} mit Erstkurs verknüpft", n);
    }

    // ---------------------------------------------------------- Übersicht --

    public async Task<NeuzugangUebersicht> UebersichtAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var alle = (await conn.QueryAsync<Neuzugang>(new CommandDefinition("""
            SELECT neuzugang_id AS NeuzugangId, quelle AS Quelle, art AS Art,
                   symbol AS Symbol, name AS Name, markt AS Markt,
                   entdeckt_utc AS EntdecktUtc, erwartet_am AS ErwartetAm,
                   preis_von AS PreisVon, preis_bis AS PreisBis, volumen AS Volumen,
                   status AS Status, asset_id AS AssetId,
                   erster_kurs AS ErsterKurs, erster_kurs_utc AS ErsterKursUtc,
                   url AS Url, titel AS Titel, aktualisiert_utc AS AktualisiertUtc
              FROM dbo.neuzugang
             ORDER BY COALESCE(erwartet_am, CONVERT(date, entdeckt_utc)) DESC,
                      entdeckt_utc DESC
            """, cancellationToken: ct))).ToList();

        var laeufe = (await conn.QueryAsync<NeuzugangLauf>(new CommandDefinition("""
            SELECT TOP 12 lauf_id AS LaufId, gestartet_utc AS GestartetUtc, quelle AS Quelle,
                   gefunden AS Gefunden, neu AS Neu, erfolg AS Erfolg, meldung AS Meldung
              FROM dbo.neuzugang_lauf ORDER BY lauf_id DESC
            """, cancellationToken: ct))).ToList();

        var seit = alle.Count > 0
            ? (int)(DateTime.UtcNow.Date - alle.Min(x => x.EntdecktUtc).Date).TotalDays + 1
            : 0;

        var vorlauf = alle.Select(x => x.Vorlauf).Where(v => v is >= 0).ToList();

        return new NeuzugangUebersicht(
            alle.Where(x => x.Status == "angekuendigt").ToList(),
            alle.Where(x => x.Status != "angekuendigt").ToList(),
            laeufe,
            seit,
            alle.Count(x => x.Status == "angekuendigt"),
            alle.Count(x => x.Status == "gehandelt"),
            alle.Count(x => x.Status == "ausgefallen"),
            vorlauf.Count > 0 ? vorlauf.Average(v => v!.Value) : null,
            DateTime.UtcNow,
            Hinweis(seit, alle.Count));
    }

    private static string Hinweis(int seitTagen, int gesamt) =>
        $"Die Kohorte läuft seit {seitTagen} Tag{(seitTagen == 1 ? "" : "en")} und umfasst "
      + $"{gesamt} Ankündigungen. Sie wächst nur vorwärts: Rückwirkend füllen liesse sie sich "
      + "allein aus dem eigenen Bestand — und dort steht ein Wert erst, NACHDEM er gestiegen "
      + "ist. Ein Neuzugang, der nach dem ersten Tag um achtzig Prozent fiel, ist dort nie "
      + "aufgetaucht, und genau er ist der Fall, auf den es ankommt. Ankündigungen, aus denen "
      + "nichts wird, bleiben deshalb als „ausgefallen“ stehen statt zu verschwinden.";

    // ----------------------------------------------------------- Helfer ----

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>„$17.00-$19.00“ oder „$12.00“ → von/bis.</summary>
    private static (decimal? Von, decimal? Bis) Preisspanne(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, null);

        var teile = s.Replace("$", "").Split('-', StringSplitOptions.TrimEntries
                                                | StringSplitOptions.RemoveEmptyEntries);

        var von = Zahl(teile.Length > 0 ? teile[0] : null);
        var bis = Zahl(teile.Length > 1 ? teile[1] : null);

        return (von, bis ?? von);
    }

    private static decimal? Zahl(string? s) =>
        decimal.TryParse((s ?? "").Replace("$", "").Replace(",", "").Trim(),
            NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>„9/08/2026“ oder „2026-09-08“ → Datum, Uhrzeit auf Mitternacht.</summary>
    private static DateTime? Datum(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        foreach (var f in new[] { "M/dd/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "M/d/yyyy" })
            if (DateTime.TryParseExact(s.Trim(), f, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var d))
                return d.Date;

        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                 DateTimeStyles.None, out var e) ? e.Date : null;
    }

    /// <summary>Das Symbol aus „… (DJTB) …“ — die letzte kurze Klammer gewinnt.</summary>
    private static string? AusKlammer(string titel)
    {
        string? treffer = null;

        for (var i = 0; i < titel.Length; i++)
        {
            if (titel[i] != '(') continue;

            var zu = titel.IndexOf(')', i + 1);
            if (zu < 0) break;

            var inhalt = titel[(i + 1)..zu].Trim();

            /*  Nur was wie ein Ticker aussieht: Grossbuchstaben und Ziffern,
                zwei bis zwoelf Zeichen, sonst nichts.

                „Keine Leerzeichen und kurz" reichte nicht: Binance-Titel enden
                auf „- 2026-08-30", und das stand danach als Symbol in der
                Tabelle -- eine Ankuendigung, deren Kennung ein Datum ist. Der
                Bindestrich ist das Unterscheidungsmerkmal; ein Ticker hat
                keinen.                                                        */
            if (inhalt.Length is >= 2 and <= 12 && inhalt.All(c => char.IsAsciiLetterUpper(c)
                                                                || char.IsAsciiDigit(c)))
                treffer = inhalt;

            i = zu;
        }

        return treffer;
    }
}
