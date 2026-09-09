using System.Text.Json;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Ingest.Infrastructure;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Einen Wert von Hand aufnehmen.
///
/// <para><b>Warum das fehlte und auffiel.</b> Das Universum kommt aus
/// US-Screenern und CoinGecko. Im Bestand stand deshalb kein einziges Symbol
/// mit Punkt — also kein einziges nicht-amerikanisches Papier. Rheinmetall
/// notiert als <c>RHM.DE</c> in Xetra und war schlicht nicht auffindbar. Die
/// README behauptete, europäische Papiere liessen sich „über die Auswahl
/// ergänzen"; diesen Weg gab es nicht.</para>
///
/// <para><b>Die Währungsfalle.</b> Ein in Euro notiertes Papier neben lauter
/// Dollar-Papieren ist kein Problem, solange nur RENDITEN verglichen werden —
/// und genau das tun Korrelationen, Kreuzungen und Paargewinne. Es wird zum
/// Problem, sobald jemand Kurse addiert oder gewichtet. Deshalb steht die
/// Währung in der Antwort, und die Aufnahme sagt sie ausdrücklich dazu.</para>
/// </summary>
public static class AssetAddEndpoints
{
    public sealed record AufnahmeEingabe(string Symbol, string? Klasse);

    public static void MapAssetAddEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/assets");

        /* Die ganze Welt auf einmal.

           Die Liste steht als Datei neben der Anwendung, nicht im Code -- wer
           eine Börse ergänzt, soll das ohne Übersetzen tun können. Dieselbe
           Entscheidung wie bei den Nachrichtenquellen in quellen.json. */
        g.MapPost("/welt", async (IAssetRepository repo, IIngestService ingest,
                                  IHttpClientFactory http,
                                  string? region = null, int months = 300,
                                  bool nurAnlegen = false,
                                  CancellationToken ct = default) =>
        {
            var pfad = Ablage.Welt;

            if (!File.Exists(pfad))
                return Results.BadRequest(new { error = $"Weltliste nicht gefunden: {pfad}" });

            using var datei = JsonDocument.Parse(await File.ReadAllTextAsync(pfad, ct));

            var angelegt = new List<object>();
            var schonDa = new List<string>();

            /* Unbekannte Symbole werden GEMELDET, nicht verschluckt. Eine
               kuratierte Liste veraltet -- Firmen fusionieren, Kürzel ändern
               sich. Eine Liste, die still schrumpft, merkt niemand. */
            var unbekannt = new List<string>();

            foreach (var reg in datei.RootElement.GetProperty("regionen").EnumerateArray())
            {
                var name = reg.GetProperty("name").GetString() ?? "";

                if (!string.IsNullOrWhiteSpace(region)
                    && !name.Contains(region, StringComparison.OrdinalIgnoreCase))
                    continue;

                var klasse = reg.TryGetProperty("klasse", out var kl)
                             && Enum.TryParse<AssetClass>(kl.GetString(), true, out var kk)
                    ? kk : AssetClass.Stock;

                foreach (var sym in reg.GetProperty("symbole").EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();

                    var symbol = (sym.GetString() ?? "").Trim().ToUpperInvariant();
                    if (symbol.Length == 0) continue;

                    var (art, ergebnis) = await AufnehmenAsync(
                        repo, ingest, http, symbol, klasse,
                        nurAnlegen ? 0 : months, ct);

                    switch (art)
                    {
                        case "neu": angelegt.Add(ergebnis!); break;
                        case "vorhanden": schonDa.Add(symbol); break;
                        default: unbekannt.Add(symbol); break;
                    }
                }
            }

            return Results.Ok(new
            {
                angelegt = angelegt.Count,
                schonVorhanden = schonDa.Count,
                unbekannt = unbekannt.Count,
                werte = angelegt,
                unbekannteSymbole = unbekannt,
                hinweis = "Nicht in Dollar notierte Papiere sind für Korrelationen, Kreuzungen "
                        + "und Paargewinne unbedenklich — die rechnen mit Renditen. Wer Kurse "
                        + "addiert oder Kapital verteilt, muss umrechnen. Analyse und Prognosen "
                        + "erfassen die neuen Werte beim nächsten Lauf."
            });
        });

        g.MapPost("/aufnehmen", async (IAssetRepository repo, IIngestService ingest,
                                       IHttpClientFactory http, AufnahmeEingabe e,
                                       int months = 300,
                                       CancellationToken ct = default) =>
        {
            var symbol = (e.Symbol ?? "").Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(symbol))
                return Results.BadRequest(new { error = "Kein Symbol übergeben." });

            var vorhanden = (await repo.ListAsync(search: symbol, limit: 20, ct: ct))
                .FirstOrDefault(a => string.Equals(a.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

            if (vorhanden is not null)
                return Results.Ok(new
                {
                    vorhanden.AssetId, vorhanden.Symbol, vorhanden.Name,
                    schonDa = true,
                    hinweis = vorhanden.IsTracked
                        ? "Der Wert wird bereits verfolgt."
                        : "Der Wert ist bekannt, aber nicht verfolgt — über /api/assets/track einschalten."
                });

            /* Erst nachschlagen, dann anlegen.

               Ein Symbol anzulegen, das der Kursanbieter nicht kennt, erzeugt
               einen Wert ohne Bars — davon führt der Bestand schon genug mit,
               und sie fallen in jedem Diagramm als fehlende Linie auf. */
            var kopf = await NachschlagenAsync(http, symbol, ct);

            if (kopf is null)
                return Results.BadRequest(new
                {
                    error = $"Yahoo kennt das Symbol {symbol} nicht.",
                    hinweis = "Nicht-amerikanische Papiere brauchen das Börsenkürzel: "
                            + "Xetra .DE, Wien .VI, Schweiz .SW, London .L, Paris .PA, "
                            + "Amsterdam .AS, Mailand .MI, Madrid .MC, Tokio .T. "
                            + "Rheinmetall ist RHM.DE, SAP ist SAP.DE."
                });

            var klasse = e.Klasse is not null
                         && Enum.TryParse<AssetClass>(e.Klasse, ignoreCase: true, out var k)
                ? k
                : symbol.EndsWith("-USD", StringComparison.OrdinalIgnoreCase)
                    ? AssetClass.Crypto
                    : AssetClass.Stock;

            var neu = new Asset
            {
                Symbol = symbol,
                Name = kopf.Value.Name,
                AssetClass = klasse,
                Currency = kopf.Value.Currency,
                Exchange = kopf.Value.Exchange,
                Country = kopf.Value.Country,
                Provider = ProviderId.Yahoo,
                ProviderSymbol = symbol,
                IsTracked = true
            };

            var id = await repo.UpsertAsync(neu, ct);

            // `upsert_asset` schreibt `is_tracked` nicht — getrennt einschalten.
            await repo.SetTrackedAsync([id], true, ct);

            // Historie sofort holen — ein Wert ohne Bars ist kein Wert.
            var tag = await ingest.IngestOneAsync(id, BarInterval.Daily,
                DateTime.UtcNow.AddMonths(-Math.Clamp(months, 1, 600)), DateTime.UtcNow, ct);

            var stunde = await ingest.IngestOneAsync(id, BarInterval.Hourly,
                DateTime.UtcNow.AddMonths(-12), DateTime.UtcNow, ct);

            return Results.Ok(new
            {
                assetId = id,
                neu.Symbol, neu.Name,
                klasse = klasse.ToString(),
                neu.Currency, neu.Exchange, neu.Country,
                tagesbars = tag,
                stundenbars = stunde,
                hinweis = string.Equals(neu.Currency, "USD", StringComparison.OrdinalIgnoreCase)
                    ? "Aufgenommen und verfolgt. Analyse und Prognosen erfassen ihn beim "
                    + "nächsten Lauf."
                    : $"Aufgenommen und verfolgt. **Notiert in {neu.Currency}**, der übrige "
                    + "Bestand überwiegend in USD. Für Korrelationen, Kreuzungen und "
                    + "Paargewinne ist das unerheblich — sie rechnen mit Renditen. Wer "
                    + "Kurse addiert oder Kapital verteilt, muss umrechnen."
            });
        });
    }

    /// <summary>
    /// Legt einen Wert an, sofern er neu und beim Kursanbieter bekannt ist.
    /// Gibt zurück, was geschah: <c>neu</c>, <c>vorhanden</c> oder
    /// <c>unbekannt</c>.
    /// </summary>
    /// <param name="months">
    /// Wie weit die Historie geholt wird. <c>0</c> legt nur an — nützlich für
    /// einen ersten Durchlauf über hundertachtzig Symbole, bei dem man erst
    /// wissen will, welche überhaupt existieren, bevor man Stunden in
    /// Kursabrufe steckt.
    /// </param>
    private static async Task<(string Art, object? Ergebnis)> AufnehmenAsync(
        IAssetRepository repo, IIngestService ingest, IHttpClientFactory http,
        string symbol, AssetClass klasse, int months, CancellationToken ct)
    {
        var vorhanden = (await repo.ListAsync(search: symbol, limit: 20, ct: ct))
            .FirstOrDefault(a => string.Equals(a.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

        if (vorhanden is not null) return ("vorhanden", null);

        var kopf = await NachschlagenAsync(http, symbol, ct);
        if (kopf is null) return ("unbekannt", null);

        var neu = new Asset
        {
            Symbol = symbol,
            Name = kopf.Value.Name,
            AssetClass = klasse,
            Currency = kopf.Value.Currency,
            Exchange = kopf.Value.Exchange,
            Country = kopf.Value.Country,
            Provider = ProviderId.Yahoo,
            ProviderSymbol = symbol,
            IsTracked = true
        };

        var id = await repo.UpsertAsync(neu, ct);

        /* Getrennt einschalten.

           `upsert_asset` kennt die Spalte `is_tracked` nicht -- sie am Modell
           zu setzen bleibt wirkungslos. Beim ersten Weltlauf waren deshalb 269
           Werte angelegt und keiner verfolgt: Sie standen in der Datenbank und
           tauchten nirgends auf. */
        await repo.SetTrackedAsync([id], true, ct);

        var tag = 0;
        var stunde = 0;

        if (months > 0)
        {
            tag = await ingest.IngestOneAsync(id, BarInterval.Daily,
                DateTime.UtcNow.AddMonths(-Math.Clamp(months, 1, 600)), DateTime.UtcNow, ct);

            stunde = await ingest.IngestOneAsync(id, BarInterval.Hourly,
                DateTime.UtcNow.AddMonths(-12), DateTime.UtcNow, ct);
        }

        return ("neu", new
        {
            assetId = id,
            neu.Symbol, neu.Name, neu.Currency, neu.Exchange, neu.Country,
            klasse = klasse.ToString(),
            tagesbars = tag, stundenbars = stunde
        });
    }

    /// <summary>
    /// Fragt Yahoo nach den Stammdaten. Verwendet dieselbe Kursschnittstelle
    /// wie der Kursabruf — ein Symbol, das hier keine Antwort liefert, liefert
    /// auch später keine Bars.
    /// </summary>
    private static async Task<(string? Name, string? Currency, string? Exchange, string? Country)?>
        NachschlagenAsync(IHttpClientFactory factory, string symbol, CancellationToken ct)
    {
        try
        {
            var client = factory.CreateClient("browser");

            using var antwort = await client.GetAsync(
                $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}"
                + "?range=5d&interval=1d", ct);

            if (!antwort.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await antwort.Content.ReadAsStringAsync(ct));

            var meta = doc.RootElement
                .GetProperty("chart").GetProperty("result")[0]
                .GetProperty("meta");

            string? Lies(string name) =>
                meta.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;

            /* Das Land steckt nicht in den Stammdaten, wohl aber in der
               Zeitzone der Börse. Europe/Berlin heißt Deutschland, und das
               genügt für die Filter der Oberfläche. */
            var zone = Lies("exchangeTimezoneName") ?? "";

            var land = zone switch
            {
                "Europe/Berlin" => "DE",
                "Europe/Vienna" => "AT",
                "Europe/Zurich" => "CH",
                "Europe/London" => "GB",
                "Europe/Paris" => "FR",
                "Europe/Amsterdam" => "NL",
                "Europe/Rome" => "IT",
                "Europe/Madrid" => "ES",
                "Asia/Tokyo" => "JP",
                "America/New_York" => "US",
                "America/Toronto" => "CA",
                "Australia/Sydney" => "AU",
                "Asia/Hong_Kong" => "HK",
                "Europe/Stockholm" => "SE",
                "Europe/Copenhagen" => "DK",
                "Europe/Oslo" => "NO",
                "Europe/Helsinki" => "FI",
                "Europe/Brussels" => "BE",
                "Europe/Lisbon" => "PT",
                "Europe/Dublin" => "IE",
                _ => null
            };

            return (Lies("longName") ?? Lies("shortName"),
                    Lies("currency"),
                    Lies("fullExchangeName"),
                    land);
        }
        catch
        {
            return null;
        }
    }
}
