using System.Diagnostics;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Infrastructure.Datenbank;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>Ein gespeichertes Urteil, wie die Oberfläche es zeigt.</summary>
public sealed record UrteilZeile(
    long UrteilId, int AssetId, string Symbol, DateTime MadeAtUtc, string Modell,
    int Richtung, double Zuversicht, string? Begruendung, string Werkzeuge,
    double Sekunden, DateTime? BewertetUtc, double? Realisiert, bool? Treffer);

/// <summary>Was die Urteile bisher wert sind.</summary>
/// <param name="Verdienst">Der Faktor, mit dem die Säule in die Mischung geht:
/// aus der Trefferquote ab 20 nachgeprüften Urteilen, davor 0,25.</param>
public sealed record UrteilStand(
    int Gesamt, int Offen, int Bewertet, int Gerichtet, int Treffer,
    double? Trefferquote, double Verdienst, string VerdienstGrund,
    DateTime? LetztesUtc, int Aktuell, IReadOnlyList<UrteilZeile> Letzte);

public sealed record UrteilLauf(
    int Versucht, int Gebildet, int Verworfen, double Sekunden,
    IReadOnlyList<string> Gruende);

public interface IReasoningUrteilService
{
    /// <summary>Bildet Urteile für die Werte, deren Urteil fehlt oder am ältesten
    /// ist — bis <paramref name="maxWerte"/> oder das Zeitbudget erreicht ist.</summary>
    Task<UrteilLauf> LaufeAsync(int maxWerte, TimeSpan budget, CancellationToken ct = default);

    /// <summary>Prüft offene Urteile nach fünf Handelstagen gegen den Kurs.</summary>
    Task<int> BewerteAsync(CancellationToken ct = default);

    Task<UrteilStand> StandAsync(int letzte = 30, CancellationToken ct = default);

    /// <summary>Der Verdienst aus dem Stand — dieselbe Rechnung wie in der Mischung.</summary>
    static (double Verdienst, string Grund) Verdienst(int gerichtet, int treffer)
    {
        /*  Zwanzig gerichtete, nachgeprüfte Urteile als Untergrenze -- darunter
            ist eine Trefferquote Rauschen. Davor der vorsichtige Zwischenwert
            0,25, derselbe wie bei der ersten Saeule, solange sie jung ist:
            weder bewaehrt noch widerlegt.

            Die Skala: 0,50 ist der Muenzwurf und gibt null; 0,70 gibt eins.
            Steiler als bei der ersten Saeule (0,523 -> 0,623), weil ein Urteil
            mit Richtung UND Zuversicht mehr behauptet als ein Vorzeichen.     */
        if (gerichtet < 20)
            return (0.25, $"erst {gerichtet} gerichtete Urteile nachgeprüft — vorsichtiger Ansatz");

        var quote = (double)treffer / gerichtet;
        return (Math.Clamp((quote - 0.5) / 0.2, 0, 1),
                $"Trefferquote {quote:P1} über {gerichtet} nachgeprüfte Urteile");
    }
}

/// <summary>
/// Der Urteilslauf und seine Nachprüfung.
///
/// <para><b>Warum entkoppelt vom Prognoselauf.</b> Ein Urteil kostet auf der CPU
/// mit dem 33-Milliarden-Modell mehrere Minuten; 644 Werte wären ein Tag. Der
/// Prognoselauf liest deshalb nur die gespeicherten Urteile (höchstens drei
/// Tage alt), und dieser Lauf füllt sie mit Zeitbudget — die Werte ohne Urteil
/// zuerst, dann die mit dem ältesten.</para>
///
/// <para><b>Die Nachprüfung ist der Kern.</b> Ohne sie wäre das Gewicht der
/// Säule eine Einstellung; mit ihr ist es eine Messung. Fünf Handelstage nach
/// dem Urteil wird der Kurs geholt, die Log-Rendite gebildet, und bei einem
/// gerichteten Urteil (Richtung ≠ 0) zählt, ob das Vorzeichen stimmte.</para>
/// </summary>
public sealed class ReasoningUrteilService(
    ISqlConnectionFactory factory,
    IReasoningService reasoning,
    IBriefingService briefing,
    ILogger<ReasoningUrteilService> log) : IReasoningUrteilService
{
    private SqlDialekt d => factory.Dialekt;

    /// <summary>Frist je Urteil. Sechs Runden mit Denkmodus brauchen auf der CPU
    /// bis zu zehn Minuten; darüber ist etwas anderes kaputt.</summary>
    private static readonly TimeSpan FristJeUrteil = TimeSpan.FromMinutes(10);

    public async Task<UrteilLauf> LaufeAsync(int maxWerte, TimeSpan budget, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var gruende = new List<string>();
        int versucht = 0, gebildet = 0, verworfen = 0;

        if (!await reasoning.IsAvailableAsync(ct))
        {
            gruende.Add("Ollama oder das Modell ist nicht erreichbar.");
            return new UrteilLauf(0, 0, 0, 0, gruende);
        }

        await using var conn = await factory.OpenAsync(ct);

        /*  Reihenfolge: ohne Urteil zuerst, dann das aelteste. Gehaltene
            Positionen der Depots vor allen anderen -- dort wirkt ein Urteil
            ueber den Autopiloten auf Geld, bei den uebrigen nur auf eine Zahl. */
        var kandidaten = (await conn.QueryAsync<(int AssetId, string Symbol, decimal Close)>(new CommandDefinition($"""
            SELECT a.asset_id, a.symbol, p."close"
              FROM dbo.asset a
              JOIN (SELECT asset_id, MAX(ts_utc) AS ts FROM dbo.price_bar WHERE interval_code = '1d' GROUP BY asset_id) l
                ON l.asset_id = a.asset_id
              JOIN dbo.price_bar p ON p.asset_id = a.asset_id AND p.interval_code = '1d' AND p.ts_utc = l.ts
              LEFT JOIN (SELECT asset_id, MAX(made_at_utc) AS zuletzt FROM dbo.reasoning_urteil GROUP BY asset_id) u
                ON u.asset_id = a.asset_id
              LEFT JOIN (SELECT asset_id, SUM(anteile) AS bestand
                           FROM dbo.invest_buchung GROUP BY asset_id) b
                ON b.asset_id = a.asset_id
             WHERE a.is_tracked = {d.Wahr} AND p."close" > 0
             ORDER BY CASE WHEN COALESCE(b.bestand, 0) > 0 THEN 0 ELSE 1 END,
                      CASE WHEN u.zuletzt IS NULL THEN 0 ELSE 1 END, u.zuletzt, a.asset_id
            OFFSET 0 ROWS FETCH NEXT (@max) ROWS ONLY
            """, new { max = Math.Clamp(maxWerte, 1, 200) }, cancellationToken: ct))).ToList();

        // Der Tageskontext einmal je Lauf, nicht je Wert.
        var kontext = await TageskontextAsync(conn, ct);

        foreach (var (assetId, symbol, close) in kandidaten)
        {
            if (sw.Elapsed > budget) { gruende.Add($"Zeitbudget von {budget.TotalMinutes:0} Minuten erreicht."); break; }
            ct.ThrowIfCancellationRequested();

            versucht++;
            var eigen = kontext.TryGetValue(symbol, out var k) ? k : null;
            var (urteil, grund) = await reasoning.UrteilAsync(symbol, eigen, FristJeUrteil, ct);

            if (urteil is null)
            {
                verworfen++;
                gruende.Add($"{symbol}: {grund}");
                log.LogInformation("Urteil {Symbol} verworfen: {Grund}", symbol, grund);
                continue;
            }

            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO dbo.reasoning_urteil
                    (asset_id, modell, richtung, zuversicht, begruendung, werkzeuge, runden, sekunden, base_close)
                VALUES (@assetId, @modell, @richtung, @zuversicht, @begr, @werkzeuge, @runden, @sekunden, @close)
                """, new
                {
                    assetId, modell = urteil.Model, richtung = (short)urteil.Richtung, zuversicht = urteil.Zuversicht,
                    begr = urteil.Begruendung, werkzeuge = string.Join(",", urteil.Werkzeuge),
                    runden = urteil.Rounds, sekunden = urteil.Seconds, close
                }, cancellationToken: ct));

            gebildet++;
            log.LogInformation("Urteil {Symbol}: {Richtung:+0;-0;0} bei {Z:P0} in {S:F0} s ({Werkzeuge})",
                symbol, urteil.Richtung, urteil.Zuversicht, urteil.Seconds, string.Join(",", urteil.Werkzeuge));
        }

        return new UrteilLauf(versucht, gebildet, verworfen, Math.Round(sw.Elapsed.TotalSeconds, 1), gruende);
    }

    /// <summary>
    /// Journal-Einleitung und die Punkte der Tagesübersicht je Symbol — kurz,
    /// damit sie in den Kontext passen und das Modell nicht darin ertrinkt.
    /// </summary>
    private async Task<Dictionary<string, string>> TageskontextAsync(
        System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var gewichte = (await conn.QueryAsync<(string Pillar, int Weight)>(
                new CommandDefinition("SELECT pillar, weight FROM dbo.pillar_weight", cancellationToken: ct)))
                .ToDictionary(x => x.Pillar, x => x.Weight);

            var b = await briefing.BuildAsync(gewichte, null, ct);
            var allgemein = "Lage: " + b.Summary
                + (b.Caveats.Count > 0 ? "\nVorbehalte: " + string.Join(" ", b.Caveats.Take(3)) : "");

            foreach (var g in b.Items.Where(i => !string.IsNullOrEmpty(i.Symbol)).GroupBy(i => i.Symbol!))
            {
                var zeilen = g.OrderByDescending(i => i.Weight).Take(6)
                    .Select(i => $"- [{i.Kind}/{i.Pillar}, Gewicht {i.Weight:0}] {i.Title}: {i.Detail}");
                result[g.Key] = allgemein + "\n" + string.Join("\n", zeilen);
            }
            result["*"] = allgemein;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Tageskontext für Urteile nicht verfügbar — Urteile ohne Kontext");
        }
        return result;
    }

    public async Task<int> BewerteAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        /*  Offen und mindestens sieben Kalendertage alt -- fuenf Handelstage
            sind dann sicher vorbei. Der Kurs ist der Schluss des fuenften
            Handelstags NACH dem Urteil; fehlt er noch (Feiertage, Luecke),
            bleibt das Urteil offen und kommt morgen wieder dran.              */
        var offen = (await conn.QueryAsync<(long UrteilId, int AssetId, DateTime MadeAt, short Richtung, decimal Base)>(
            new CommandDefinition($"""
                SELECT urteil_id, asset_id, made_at_utc, richtung, base_close
                  FROM dbo.reasoning_urteil
                 WHERE bewertet_utc IS NULL AND made_at_utc <= {d.PlusTage("-7", d.Jetzt)}
                """, cancellationToken: ct))).ToList();

        var bewertet = 0;
        foreach (var u in offen)
        {
            var close = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition("""
                SELECT "close" FROM dbo.price_bar
                 WHERE asset_id = @id AND interval_code = '1d' AND ts_utc > @von AND "close" > 0
                 ORDER BY ts_utc OFFSET 4 ROWS FETCH NEXT 1 ROWS ONLY
                """, new { id = u.AssetId, von = u.MadeAt }, cancellationToken: ct));
            if (close is null || u.Base <= 0) continue;

            var r = Math.Log((double)(close.Value / u.Base));
            bool? treffer = u.Richtung == 0 ? null : Math.Sign(r) == Math.Sign(u.Richtung);

            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE dbo.reasoning_urteil SET bewertet_utc = {d.Jetzt}, realisiert = @r, treffer = @treffer
                 WHERE urteil_id = @id
                """, new { r, treffer, id = u.UrteilId }, cancellationToken: ct));
            bewertet++;
        }

        if (bewertet > 0) log.LogInformation("Reasoning-Urteile nachgeprüft: {N}", bewertet);
        return bewertet;
    }

    public async Task<UrteilStand> StandAsync(int letzte = 30, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var z = await conn.QuerySingleAsync<(int Gesamt, int Offen, int Bewertet, int Gerichtet, int Treffer, DateTime? Letztes, int Aktuell)>(
            new CommandDefinition($"""
                SELECT CAST(COUNT(*) AS INT),
                       CAST(SUM(CASE WHEN bewertet_utc IS NULL THEN 1 ELSE 0 END) AS INT),
                       CAST(SUM(CASE WHEN bewertet_utc IS NOT NULL THEN 1 ELSE 0 END) AS INT),
                       CAST(SUM(CASE WHEN bewertet_utc IS NOT NULL AND richtung <> 0 THEN 1 ELSE 0 END) AS INT),
                       CAST(SUM(CASE WHEN treffer = {d.Wahr} THEN 1 ELSE 0 END) AS INT),
                       MAX(made_at_utc),
                       CAST(SUM(CASE WHEN made_at_utc >= {d.PlusTage("-3", d.Jetzt)} THEN 1 ELSE 0 END) AS INT)
                  FROM dbo.reasoning_urteil
                """, cancellationToken: ct));

        var zeilen = (await conn.QueryAsync<UrteilZeile>(new CommandDefinition("""
            SELECT u.urteil_id AS UrteilId, u.asset_id AS AssetId, a.symbol AS Symbol, u.made_at_utc AS MadeAtUtc,
                   u.modell AS Modell, CAST(u.richtung AS INT) AS Richtung, u.zuversicht AS Zuversicht,
                   u.begruendung AS Begruendung, u.werkzeuge AS Werkzeuge, u.sekunden AS Sekunden,
                   u.bewertet_utc AS BewertetUtc, u.realisiert AS Realisiert, u.treffer AS Treffer
              FROM dbo.reasoning_urteil u JOIN dbo.asset a ON a.asset_id = u.asset_id
             ORDER BY u.made_at_utc DESC OFFSET 0 ROWS FETCH NEXT (@n) ROWS ONLY
            """, new { n = Math.Clamp(letzte, 1, 500) }, cancellationToken: ct))).ToList();

        var (verdienst, grund) = IReasoningUrteilService.Verdienst(z.Gerichtet, z.Treffer);

        return new UrteilStand(z.Gesamt, z.Offen, z.Bewertet, z.Gerichtet, z.Treffer,
            z.Gerichtet > 0 ? Math.Round((double)z.Treffer / z.Gerichtet, 4) : null,
            verdienst, grund, z.Letztes, z.Aktuell, zeilen);
    }
}
