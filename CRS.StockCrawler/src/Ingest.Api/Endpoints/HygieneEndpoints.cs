using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Api.Endpoints;

/// <summary>Ein verfolgter Wert, der nichts Brauchbares mehr liefert.</summary>
public sealed record StummerWert(
    int AssetId, string Symbol, string? Name, string AssetClass,
    int Bars, DateTime? LetzteBar, decimal? Kurs, decimal? Hoch, string Grund);

/// <summary>
/// Pflege des verfolgten Bestands.
///
/// <b>Warum das eine eigene Funktion braucht.</b> Der Universum-Lauf nimmt auf,
/// was nach Marktkapitalisierung oben steht — und lässt es dort, auch wenn es
/// seit Jahren nicht mehr handelt. Solche Werte erscheinen in keinem Diagramm,
/// verfälschen jede Auswertung über „alle Werte" und beherrschen die
/// Tagesübersicht mit Scheinbefunden: Ein Token, das von 0,0003 auf 0,0000
/// fällt, meldet eine Steigung von −58 % je Bar und Stufe 100 von 100.
///
/// Formal ist das kein Fehler — die Zahl stimmt. Gemessen an der üblichen
/// Schwankung dieser Reihe ist der Ausschlag tatsächlich außergewöhnlich. Nur
/// beschreibt er das Ende eines Wertes und nicht das Verhalten eines Marktes.
/// </summary>
public static class HygieneEndpoints
{
    public static void MapHygieneEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/hygiene").WithTags("Pflege");

        g.MapGet("/stumm", async (ISqlConnectionFactory factory,
                                  int minBars = 250, int maxAlterTage = 30,
                                  double kollapsAnteil = 0.001,
                                  CancellationToken ct = default) =>
        {
            var liste = await FindeAsync(factory, minBars, maxAlterTage, kollapsAnteil, ct);

            return Results.Ok(new
            {
                gefunden = liste.Count,
                schwellen = new { minBars, maxAlterTage, kollapsAnteil },

                werte = liste.Select(x => new
                {
                    x.AssetId, x.Symbol, x.Name, klasse = x.AssetClass,
                    x.Bars, letzteBar = x.LetzteBar, x.Kurs, x.Hoch, x.Grund
                }),

                hinweis = "Abschalten nimmt sie aus der Verfolgung, löscht aber nichts. "
                        + "Die Kurse bleiben in der Datenbank; wer den Wert später wieder "
                        + "aufnimmt, hat seine Historie noch."
            });
        });

        g.MapPost("/stumm/abschalten", async (ISqlConnectionFactory factory,
                                              IAssetRepository assets,
                                              int minBars = 250, int maxAlterTage = 30,
                                              double kollapsAnteil = 0.001,
                                              bool wirklich = false,
                                              CancellationToken ct = default) =>
        {
            var liste = await FindeAsync(factory, minBars, maxAlterTage, kollapsAnteil, ct);

            /* Ohne `wirklich` wird nur gezeigt, was geschähe.

               Das Abschalten von vier Dutzend Werten ändert jede Auswertung, die
               über „alle Werte“ geht. Wer es aus Versehen auslöst, merkt es erst
               an Zahlen, die sich nicht mehr mit den gestrigen decken. */
            if (!wirklich)
                return Results.Ok(new
                {
                    probelauf = true,
                    betroffen = liste.Count,
                    werte = liste.Select(x => new { x.Symbol, x.Grund }),
                    hinweis = "Nichts geändert. Mit `wirklich=true` wird es ausgeführt."
                });

            // In einem Aufruf, nicht Wert für Wert: Die Schnittstelle nimmt
            // eine Liste, und vier Dutzend einzelne Fahrten zur Datenbank
            // wären für dieselbe Wirkung.
            var n = await assets.SetTrackedAsync(liste.Select(w => w.AssetId), false, ct);

            return Results.Ok(new
            {
                abgeschaltet = n,
                werte = liste.Select(x => new { x.Symbol, x.Grund }),
                hinweis = "Auswertungen über „alle Werte“ liefern ab jetzt andere Zahlen "
                        + "als vorher — das ist der Zweck, aber es erklärt auch Sprünge "
                        + "in Reihen, die man nebeneinanderlegt."
            });
        });
    }

    /// <summary>
    /// Findet die stummen Werte.
    ///
    /// Vier Gründe, bewusst getrennt benannt: „zu wenig Historie" ist etwas
    /// anderes als „seit Monaten tot" und wieder etwas anderes als „auf null
    /// gefallen". Ein einziger Sammelgrund würde die Entscheidung, was man
    /// damit tut, unmöglich machen.
    /// </summary>
    private static async Task<List<StummerWert>> FindeAsync(
        ISqlConnectionFactory factory, int minBars, int maxAlterTage,
        double kollapsAnteil, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);

        var rows = (await conn.QueryAsync<(int AssetId, string Symbol, string? Name,
                                           byte AssetClass, int Bars, DateTime? LetzteBar,
                                           decimal? Kurs, decimal? Hoch)>(
            new CommandDefinition(
                """
                WITH letzte AS (
                    SELECT b.asset_id, b.ts_utc, b.[close],
                           ROW_NUMBER() OVER (PARTITION BY b.asset_id
                                              ORDER BY b.ts_utc DESC) AS rn
                      FROM dbo.price_bar b
                     WHERE b.interval_code = '1d'
                ),
                zahlen AS (
                    SELECT asset_id, COUNT(*) AS bars, MAX([close]) AS hoch
                      FROM dbo.price_bar WHERE interval_code = '1d'
                     GROUP BY asset_id
                )
                SELECT a.asset_id, a.symbol, a.name, a.asset_class,
                       ISNULL(z.bars, 0) AS bars, l.ts_utc, l.[close], z.hoch
                  FROM dbo.asset a
                  LEFT JOIN letzte l ON l.asset_id = a.asset_id AND l.rn = 1
                  LEFT JOIN zahlen z ON z.asset_id = a.asset_id
                 WHERE a.is_tracked = 1;
                """, cancellationToken: ct))).ToList();

        var grenze = DateTime.UtcNow.AddDays(-maxAlterTage);
        var treffer = new List<StummerWert>();

        foreach (var r in rows)
        {
            string? grund = null;

            if (r.Bars == 0)
                grund = "keine einzige Tagesbar";
            else if (r.LetzteBar is not null && r.LetzteBar < grenze)
                grund = $"letzte Bar vom {r.LetzteBar:yyyy-MM-dd}, "
                      + $"also über {maxAlterTage} Tage alt";
            else if (r.Hoch > 0 && r.Kurs is not null
                     && (double)(r.Kurs.Value / r.Hoch.Value) < kollapsAnteil)
                grund = $"Kurs {r.Kurs:G4} liegt unter einem Promille des Hochs {r.Hoch:G4} — "
                      + "jede Auswertung meldet hier Ausschläge, die das Ende des Wertes "
                      + "beschreiben und nicht den Markt";
            else if (r.Bars < minBars)
                grund = $"nur {r.Bars} Tagesbars, weniger als die geforderten {minBars}";

            if (grund is null) continue;

            treffer.Add(new StummerWert(
                r.AssetId, r.Symbol, r.Name,
                ((Core.Enums.AssetClass)r.AssetClass).ToString(),
                r.Bars, r.LetzteBar, r.Kurs, r.Hoch, grund));
        }

        return treffer.OrderBy(t => t.Symbol).ToList();
    }
}
