using Ingest.Core.Abstractions;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Das virtuelle Depot der Kursansicht — manuell wie automatisch.
///
/// <para>Die Anpassung ist bewusst ein POST und kein PUT auf eine Kennung: Es
/// wird nichts überschrieben, sondern eine Buchung angelegt. Dass die
/// Oberfläche dabei wie ein Feld aussieht, in das man einen Betrag schreibt,
/// ändert nichts daran, was darunter passiert — und die Schnittstelle soll
/// zeigen, was passiert, nicht wie es aussieht.</para>
///
/// <para><b>`depot` ist überall freiwillig und heisst sonst `manuell`.</b> Damit
/// bleiben alle Aufrufe gültig, die es vor dem Autopiloten gab — und wer den
/// Parameter vergisst, landet im Depot des Nutzers und nicht versehentlich in
/// einer laufenden Strategie.</para>
/// </summary>
public static class InvestEndpoints
{
    public sealed record InvestEingabe(string Symbol, decimal Betrag,
                                       string? Waehrung, string? Notiz, string? Depot);

    public sealed record KontoEingabe(string? Waehrung, decimal? Stand, decimal? GebuehrPct,
                                      string? Depot);

    /// <summary>
    /// Die vier bekannten Depots. Ein unbekannter Name wird zu `manuell` —
    /// stillschweigend ein fünftes Depot anzulegen, weil sich jemand vertippt
    /// hat, erzeugte einen Bestand, den niemand mehr findet.
    /// </summary>
    private static readonly string[] Bekannt = ["manuell", "streng", "aktiv", "halten", "invers"];

    internal static string Depot(string? wert)
    {
        var d = (wert ?? "manuell").Trim().ToLowerInvariant();
        return Bekannt.Contains(d) ? d : "manuell";
    }

    public static void MapInvestEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/invest");

        /*  `werte` sind die gerade in der Kursansicht gewählten Kennungen,
            kommagetrennt. Sie kommen als Parameter und nicht aus dem
            Sitzungszustand, weil die Auswahl im Browser lebt: Der Server
            kennt sie erst, wenn sie mitgeschickt wird.                       */
        g.MapGet("/", async (IInvestService svc, string? werte, string? depot,
                             CancellationToken ct) =>
        {
            var d = Depot(depot);

            var ids = (werte ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var i) ? i : 0)
                .Where(i => i > 0)
                .Distinct()
                .ToList();

            var u = await svc.UebersichtAsync(d, ids, ct);

            /*  Die Konten kommen mit, auch wenn sie leer sind: Die Oberfläche
                braucht die Zeilen zum Einstellen, bevor etwas darauf steht.
                Die Blöcke in `waehrungen` lassen leere Währungen dagegen aus —
                eine Karte „USD 0,00" sieht aus wie ein Bestand.               */
            var konten = await svc.KontenAsync(d, ct);

            return Results.Ok(new
            {
                depot = d,
                u.StandUtc,
                u.Hinweis,
                konten,
                waehrungen = u.Waehrungen,
                positionen = u.Positionen.Select(p => new
                {
                    p.AssetId, p.Symbol, p.Name, klasse = p.Klasse.ToString(),
                    p.Waehrung, p.Anteile, p.Eingezahlt, p.Entnommen, p.Einsatz,
                    p.Buchungen, p.SeitUtc, p.Kurs, p.KursUtc, p.KursQuelle, p.Gewaehlt,
                    p.Stand, p.Gewinn, p.RenditePct, p.EinsatzJeAnteil, p.Gebuehren
                })
            });
        });

        /*  Der Betrag ist der SOLL-Stand, nicht die Veränderung. Gebucht wird
            die Differenz zum heutigen Stand — siehe InvestService.            */
        g.MapPost("/", async (IInvestService svc, InvestEingabe e, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(e.Symbol))
                return Results.BadRequest(new { error = "Symbol fehlt." });

            if (e.Betrag < 0)
                return Results.BadRequest(new
                {
                    error = "Ein negativer Stand ergibt keinen Sinn. Null löst die Position auf."
                });

            var w = (e.Waehrung ?? "EUR").Trim().ToUpperInvariant();

            if (w is not ("EUR" or "USD"))
                return Results.BadRequest(new
                {
                    error = $"Währung muss EUR oder USD sein, war: {w}. Andere Zähleinheiten "
                          + "sind nicht ausgeschlossen, weil sie unmöglich wären, sondern "
                          + "weil für sie keine Devisenreihe geführt wird."
                });

            var r = await svc.SetzeAsync(Depot(e.Depot), e.Symbol, e.Betrag, w, e.Notiz, ct);

            return r is null
                ? Results.BadRequest(new
                {
                    error = $"Kein Wert mit dem Symbol {e.Symbol}. Die Schreibweise muss der "
                          + "verfolgten entsprechen — Krypto trägt das Suffix -USD."
                })
                : Results.Ok(r);
        });

        /*  Das Konto. `stand` und `gebuehrPct` sind beide freiwillig: Wer nur
            am Gebührensatz dreht, soll nicht den Stand mitschicken müssen —
            und umgekehrt. Ein Pflichtfeld an dieser Stelle führte dazu, dass
            die Oberfläche den jeweils anderen Wert zurückschickt, und ein
            zurückgeschickter Wert ist ein Wert, der veraltet sein kann.       */
        g.MapGet("/konto", async (IInvestService svc, string? depot, CancellationToken ct) =>
            Results.Ok(new { konten = await svc.KontenAsync(Depot(depot), ct) }));

        g.MapPost("/konto", async (IInvestService svc, KontoEingabe e, CancellationToken ct) =>
        {
            if (e.Stand is < 0)
                return Results.BadRequest(new
                {
                    error = "Ein negativer Kontostand lässt sich nicht setzen. Dieses Depot "
                          + "kennt keinen Kredit — was nicht da ist, kann nicht investiert werden."
                });

            if (e.GebuehrPct is < 0 or > 10)
                return Results.BadRequest(new
                {
                    error = "Der Gebührensatz je Vorgang muss zwischen 0 und 10 % liegen. "
                          + "Zum Vergleich: Die Handelsseiten rechnen mit 0,3 % je Rundlauf, "
                          + "also 0,15 % je Bein."
                });

            var k = await svc.KontoSetzeAsync(Depot(e.Depot), e.Waehrung ?? "EUR",
                                              e.Stand, e.GebuehrPct, ct);

            return k is null
                ? Results.BadRequest(new { error = "Währung muss EUR oder USD sein." })
                : Results.Ok(k);
        });

        g.MapGet("/kontobewegungen", async (IInvestService svc, string waehrung = "EUR",
                                            string? depot = null, int grenze = 200,
                                            CancellationToken ct = default) =>
            Results.Ok(await svc.KontobewegungenAsync(Depot(depot), waehrung, grenze, ct)));

        g.MapGet("/buchungen/{assetId:int}", async (IInvestService svc, int assetId,
                                                    string? depot, CancellationToken ct) =>
            Results.Ok(await svc.BuchungenAsync(Depot(depot), assetId, ct)));

        /*  Das vollstaendige Journal eines Depots -- fuer die Nachvollziehbarkeit
            der einzige Weg, den Kontostand nachzurechnen.                     */
        g.MapGet("/journal", async (IInvestService svc, string? depot, int grenze = 500,
                                    CancellationToken ct = default) =>
        {
            /*  `autopilot` ist hier ein gueltiger Wert und geht deshalb an
                `Depot()` vorbei -- es steht fuer alle drei Strategien
                zusammen.                                                      */
            var d = (depot ?? "").Trim().ToLowerInvariant() == "autopilot"
                ? "autopilot" : Depot(depot);

            return Results.Ok(await svc.JournalAsync(d, grenze, ct));
        });

        g.MapGet("/verlauf", async (IInvestService svc, string? depot, CancellationToken ct) =>
            Results.Ok(new { reihen = await svc.VerlaufAsync(Depot(depot), ct) }));

        /*  Das Gesamtvermögen über ALLE Depots. Kein `depot`-Parameter — das
            ist der Sinn der Sache.                                            */
        g.MapGet("/gesamt", async (IInvestService svc, string waehrung = "EUR",
                                   CancellationToken ct = default) =>
            Results.Ok(await svc.GesamtAsync(waehrung, ct)));

        /*  Eine Kurve je Depot. Getrennt von `/gesamt`, weil es die andere
            Frage beantwortet: dort „wieviel habe ich", hier „welche
            Entscheidung war die bessere".                                     */
        g.MapGet("/depotverlauf", async (IInvestService svc, string waehrung = "EUR",
                                         CancellationToken ct = default) =>
        {
            var reihen = await svc.DepotverlaufAsync(waehrung, ct);

            return Results.Ok(new
            {
                waehrung,
                reihen,
                punkte = reihen.Count > 0 ? reihen.Max(r => r.Zeiten.Count) : 0,
                hinweis =
                    "Nebeneinander, nicht summiert — deshalb sind hier auch die "
                  + "Vergleichsläufe des Autopiloten dabei. Indexiert lassen sich Depots "
                  + "mit verschiedenem Budget vergleichen; in Beträgen sieht ein grosses "
                  + "Depot immer besser aus, auch wenn es schlechter läuft."
            });
        });

        g.MapDelete("/{assetId:int}", async (IInvestService svc, int assetId, string? depot,
                                             CancellationToken ct) =>
        {
            var n = await svc.LoescheAsync(Depot(depot), assetId, ct);

            return n > 0
                ? Results.Ok(new { assetId, geloescht = n })
                : Results.NotFound(new { error = "Keine Buchungen zu dieser Kennung." });
        });
    }
}
