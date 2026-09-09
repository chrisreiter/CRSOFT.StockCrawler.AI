using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Services;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Kurvendiskussion: Hoch- und Tiefpunkte, Wendepunkte, Sattelpunkte und
/// Ausbrüche in Steigung und Krümmung — für jeden verfolgten Wert, abgelegt und
/// blätterbar.
///
/// <b>Warum ein Lauf und nicht eine Abfrage.</b> Über 300 Werte mit je einigen
/// tausend Bars ist die Rechnung keine Sekundensache, und ihr Ergebnis hängt an
/// einem halben Dutzend Einstellungen. Eine Rasteransicht mit Blättern braucht
/// einen festen Bestand — sonst blättert man durch etwas, das sich zwischen
/// Seite drei und vier neu berechnet.
/// </summary>
public static class CurveEndpoints
{
    public static void MapCurveEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/curve").WithTags("Kurvendiskussion");

        /* Einen Durchgang starten. */
        g.MapPost("/scan", async (ICurveDiscussionService svc,
                                  string interval = BarInterval.Daily,
                                  int halbfenster = 10,
                                  bool kausal = false,
                                  int vergleichsfenster = 250,
                                  double minZ = 2.5,
                                  int sperrzeit = 5,
                                  double minStufe = 20,
                                  int verknuepfungsfenster = 3,
                                  string? ids = null,
                                  DateTime? von = null,
                                  DateTime? bis = null,
                                  CancellationToken ct = default) =>
        {
            if (!BarInterval.IsValid(interval))
                return Results.BadRequest(new { error = "Intervall muss 1h oder 1d sein" });

            var assetIds = ids?.Split(',', StringSplitOptions.RemoveEmptyEntries
                                         | StringSplitOptions.TrimEntries)
                               .Select(x => int.TryParse(x, out var v) ? v : -1)
                               .Where(v => v > 0)
                               .ToArray();

            var opt = new CurveDiscussion.Options(
                HalfWindow: Math.Clamp(halbfenster, 3, 120),
                Causal: kausal,
                RefWindow: Math.Clamp(vergleichsfenster, 40, 2000),
                MinZ: Math.Clamp(minZ, 1.0, 8.0),
                Refractory: Math.Clamp(sperrzeit, 0, 60),
                MinSeverity: Math.Clamp(minStufe, 0, 99));

            var r = await svc.RunAsync(interval, opt, assetIds, von, bis,
                                       Math.Clamp(verknuepfungsfenster, 0, 30), ct);

            return Results.Ok(new
            {
                r.RunId,
                werte = r.Assets,
                ereignisse = r.Events,
                verknuepfungen = r.Links,
                dauerSekunden = Math.Round(r.Duration.TotalSeconds, 1),
                r.Note,

                hinweis = kausal
                    ? "Kausal geglättet — nur zurückblickend. Diese Funde dürfen in eine "
                    + "Prognose einfließen."
                    : "Zentriert geglättet — genauer, aber jede Stelle wurde unter Mitwirkung "
                    + "späterer Kurse bestimmt. Gut zur Beschreibung der Vergangenheit, "
                    + "UNBRAUCHBAR als Prognosemerkmal."
            });
        });

        /* Die Rasteransicht mit Blättern. */
        g.MapGet("/events", async (ICurveDiscussionService svc,
                                   int? lauf = null,
                                   int seite = 1,
                                   int groesse = 50,
                                   int? assetId = null,
                                   string? art = null,
                                   int? richtung = null,
                                   double minStufe = 0,
                                   DateTime? von = null,
                                   DateTime? bis = null,
                                   string sortierung = "stufe",
                                   CancellationToken ct = default) =>
        {
            var p = await svc.PageAsync(lauf, seite, groesse, assetId, art, richtung,
                                        minStufe, von, bis, sortierung, ct);

            var seiten = p.Size > 0 ? (int)Math.Ceiling(p.Total / (double)p.Size) : 0;

            return Results.Ok(new
            {
                lauf = p.RunId,
                seite = p.Page,
                groesse = p.Size,
                gesamt = p.Total,
                seiten,

                zeilen = p.Rows.Select(r => new
                {
                    r.CurveEventId,
                    r.AssetId,
                    r.Symbol,
                    r.Name,
                    klasse = r.AssetClass.ToString(),
                    ts = r.TsUtc,
                    art = r.EventType,
                    bezeichnung = Bezeichnung(r.EventType, r.Sign),
                    richtung = (int)r.Sign,
                    stufe = Math.Round(r.Severity, 1),
                    kurs = r.ClosePrice,
                    geglaettet = r.Smoothed,

                    /* Steigung und Krümmung in Prozent je Bar statt in
                       Log-Einheiten. Die Rechnung läuft auf Logarithmen, weil
                       nur dort verschiedene Kursniveaus vergleichbar sind — die
                       Anzeige muss deswegen nicht in einer Einheit erfolgen,
                       die niemand im Kopf hat. */
                    steigungPct = Math.Round((Math.Exp(r.Slope) - 1) * 100, 4),
                    kruemmung = Math.Round(r.Curvature * 10000, 3)
                }),

                arten = CurveEventType.All
            });
        });

        /* Welche Läufe es gibt und mit welchen Einstellungen. */
        g.MapGet("/runs", async (ICurveDiscussionService svc, CancellationToken ct) =>
            Results.Ok(await svc.RunsAsync(ct)));

        /* Häufigkeit je Art — die erste Plausibilitätsprüfung eines Laufs. */
        g.MapGet("/stats", async (ICurveDiscussionService svc, int? lauf,
                                  CancellationToken ct) =>
            Results.Ok(new
            {
                arten = await svc.TypeStatsAsync(lauf, ct),

                hinweis = "Sind fast alle Funde von einer einzigen Art, stimmt die Schwelle "
                        + "nicht. Erwartet wird eine Mischung, in der Wende- und Hochpunkte "
                        + "seltener sind als Ausbrüche."
            }));

        /* Die Verknüpfungen zwischen den Werten. */
        g.MapGet("/links", async (ICurveDiscussionService svc, int? lauf, int limit = 100,
                                  int? assetId = null,
                                  CancellationToken ct = default) =>
        {
            var l = await svc.LinksAsync(lauf, limit, assetId, ct);

            return Results.Ok(new
            {
                verknuepfungen = l,

                hinweis = "Der Faktor (lift) ist beobachtet geteilt durch erwartet. Ohne diese "
                        + "Normierung gewännen immer die Werte mit den meisten Ereignissen. "
                        + "Ein hoher Faktor bei einem Median-Abstand um null heißt: Die beiden "
                        + "bewegen sich GEMEINSAM — das ist eine Aussage über Struktur, keine "
                        + "über Vorhersagbarkeit. Erst ein Abstand deutlich über null bei "
                        + "einem Vorlaufanteil klar über 0,5 wäre ein Vorlauf, und auch der "
                        + "müsste sich in einem getrennten Zeitraum wiederholen."
            });
        });
    }

    /// <summary>Was die Art in der Rasteransicht heißt.</summary>
    private static string Bezeichnung(string type, int sign) => type switch
    {
        CurveEventType.Hochpunkt => "Hochpunkt",
        CurveEventType.Tiefpunkt => "Tiefpunkt",
        CurveEventType.Wendepunkt => sign >= 0 ? "Wendepunkt im Aufwärtstrend"
                                               : "Wendepunkt im Abwärtstrend",
        CurveEventType.Sattelpunkt => "Sattelpunkt",
        CurveEventType.Steigungsausbruch => sign >= 0 ? "Steigungsausbruch aufwärts"
                                                      : "Steigungsausbruch abwärts",
        CurveEventType.Kruemmungsausbruch => sign >= 0 ? "Beschleunigung" : "Abbremsen",
        CurveEventType.Sprung => sign >= 0 ? "Sprung nach oben" : "Sprung nach unten",
        _ => type
    };
}
