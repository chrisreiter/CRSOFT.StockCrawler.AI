using Dapper;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Api.Endpoints;

/// <summary>
/// Die Säulengewichte — dauerhaft und für alle Sitzungen dieselben.
///
/// <para><b>Warum nicht im Sitzungszustand.</b> Dort lagen sie zuerst, und der
/// Fehler wurde sichtbar, sobald jemand den Browser wechselte: Jede Säule stand
/// auf null, und die Tagesübersicht meldete das als Zustand des Systems statt
/// als fehlende Eingabe. Ein Regler, der die Zahlen im Diagramm verändert, ist
/// Konfiguration und keine Ansichtseinstellung.</para>
/// </summary>
public static class PillarWeightEndpoints
{
    /// <summary>
    /// Die Reihenfolge, in der die Säulen überall auftauchen. Fest verdrahtet,
    /// damit eine fehlende Zeile in der Tabelle nicht zu einer fehlenden Säule
    /// in der Oberfläche wird.
    /// </summary>
    public static readonly string[] Saeulen =
        ["learning", "math", "flow", "deep", "knowledge", "semantic"];

    /// <summary>
    /// Liest die abgelegten Gewichte. Fehlende Säulen kommen mit null zurück —
    /// aber sie kommen zurück.
    /// </summary>
    public static async Task<Dictionary<string, int>> LadenAsync(
        ISqlConnectionFactory factory, CancellationToken ct)
    {
        var w = Saeulen.ToDictionary(s => s, _ => 0);

        try
        {
            await using var conn = await factory.OpenAsync(ct);

            var rows = await conn.QueryAsync<(string Pillar, int Weight)>(new CommandDefinition(
                "SELECT pillar, weight FROM dbo.pillar_weight", cancellationToken: ct));

            foreach (var (p, g) in rows)
                if (w.ContainsKey(p)) w[p] = g;
        }
        catch
        {
            /* Fehlt die Tabelle, ist das kein Grund, eine ganze Seite scheitern
               zu lassen. Alle Gewichte auf null ist ein zulässiger Zustand --
               er bedeutet dann eben, dass nichts eingestellt wurde. */
        }

        return w;
    }

    /// <summary>
    /// Liest die Gewichte aus einem Abfrageparameter der Form
    /// <c>learning:40,math:20</c> — und fällt auf die abgelegten zurück, wenn
    /// keiner übergeben wurde. Genau diese Rückfallebene fehlte: Ohne sie sah
    /// ein Aufruf ohne Parameter aus wie eine Anwendung ohne Gewichte.
    /// </summary>
    public static async Task<Dictionary<string, int>> AusParameterOderAblageAsync(
        ISqlConnectionFactory factory, string? gewichte, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gewichte))
            return await LadenAsync(factory, ct);

        var w = Saeulen.ToDictionary(s => s, _ => 0);

        foreach (var teil in gewichte.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = teil.Split(':', 2);
            if (kv.Length == 2 && int.TryParse(kv[1], out var v))
                w[kv[0].Trim()] = Math.Clamp(v, 0, 100);
        }

        return w;
    }

    public static void MapPillarWeightEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/saeulen");

        g.MapGet("/gewichte", async (ISqlConnectionFactory factory, CancellationToken ct) =>
        {
            await using var conn = await factory.OpenAsync(ct);

            var rows = (await conn.QueryAsync<Zeile>(new CommandDefinition(
                """
                SELECT pillar AS Pillar, weight AS Weight,
                       updated_utc AS UpdatedUtc, begruendung AS Begruendung
                  FROM dbo.pillar_weight
                """, cancellationToken: ct))).ToList();

            var summe = rows.Sum(r => r.Weight);

            return Results.Ok(new
            {
                summe,
                gewichte = Saeulen.Select(s =>
                {
                    var r = rows.FirstOrDefault(x => x.Pillar == s);
                    return new
                    {
                        saeule = s,
                        gewicht = r?.Weight ?? 0,
                        begruendung = r?.Begruendung,
                        stand = r?.UpdatedUtc
                    };
                }),
                hinweis = "Gemischt wird nach Gewicht MAL Verdienst. Eine Säule ohne "
                        + "gemessenen Vorsprung im Sperrbereich trägt auch mit hohem Gewicht "
                        + "nichts bei — das Gewicht allein zählt nur, wenn keine Säule Verdienst hat."
            });
        });

        g.MapPost("/gewichte", async (ISqlConnectionFactory factory,
                                      Dictionary<string, int> eingabe,
                                      CancellationToken ct) =>
        {
            var unbekannt = eingabe.Keys.Where(k => !Saeulen.Contains(k)).ToList();

            if (unbekannt.Count > 0)
                return Results.BadRequest(new
                {
                    error = $"Unbekannte Säule: {string.Join(", ", unbekannt)}",
                    bekannt = Saeulen
                });

            await using var conn = await factory.OpenAsync(ct);

            foreach (var (saeule, gewicht) in eingabe)
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    MERGE dbo.pillar_weight WITH (HOLDLOCK) AS t
                    USING (SELECT @p AS pillar) AS s ON t.pillar = s.pillar
                    WHEN MATCHED THEN UPDATE SET weight = @g, updated_utc = SYSUTCDATETIME()
                    WHEN NOT MATCHED THEN INSERT (pillar, weight) VALUES (@p, @g);
                    """,
                    new { p = saeule, g = Math.Clamp(gewicht, 0, 100) }, cancellationToken: ct));

            return Results.Ok(await LadenAsync(factory, ct));
        });
    }

    private sealed class Zeile
    {
        public string Pillar { get; set; } = "";
        public int Weight { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public string? Begruendung { get; set; }
    }
}
