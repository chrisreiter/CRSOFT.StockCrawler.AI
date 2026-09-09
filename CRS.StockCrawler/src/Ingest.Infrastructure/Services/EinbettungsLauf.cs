using System.Text.Json.Serialization;
using Dapper;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>Welche Quellen ein Sammellauf anfasst.</summary>
public enum Einbettungsumfang
{
    /// <summary>
    /// Nur, was nie durchgekommen ist — und davon nur das, was Aussicht hat.
    ///
    /// <para>Ausgenommen sind Quellen mit dem Status „kein Text gefunden": Dort scheiterte
    /// die Textgewinnung, nicht das Einbetten. Ein erneuter Versuch lädt dieselbe Seite
    /// noch einmal und scheitert genauso. Am Bestand vom 25.08.2026 sind das
    /// <b>469 von 504</b> — wer sie mitnimmt, wartet fast die ganze Zeit auf Fehlschläge.</para>
    /// </summary>
    Fehlend,

    /// <summary>
    /// Auch die mit „kein Text gefunden".
    ///
    /// <para>Sinnvoll nach einer Änderung an der Textgewinnung — der Absatzfilter etwa hat
    /// einmal von zwölf Büchern elf verworfen, weil er zeilenweise urteilte. Danach war ein
    /// erneuter Versuch genau richtig. Ohne einen solchen Anlass ist es verlorene Zeit.</para>
    /// </summary>
    AuchLeere,

    /// <summary>
    /// Alles neu, auch Vorhandenes.
    ///
    /// <para>Erzwingt die Neugewinnung des Textes UND das Einbetten. Nötig, wenn sich
    /// <c>VerarbeitungsFassung</c> geändert hat — dann ist der Inhalts-Hash derselbe, aber
    /// das Ergebnis ein anderes, und ohne Zwang meldet jeder Lauf „alles unverändert".</para>
    /// </summary>
    Alles
}

/// <summary>Eine Zeile der Quellenauswahl.</summary>
internal sealed record Quellzeile(int Id, string Titel);

/// <summary>Der Stand eines laufenden oder beendeten Sammellaufs.</summary>
public sealed record EinbettungsStand
{
    public string Saeule { get; init; } = "";
    public string Umfang { get; init; } = "";
    public bool Laeuft { get; init; }
    public int Gesamt { get; init; }
    public int Erledigt { get; init; }
    public int Eingebettet { get; init; }
    public int Abschnitte { get; init; }
    public int OhneText { get; init; }
    public int Fehler { get; init; }
    public string? Aktuell { get; init; }
    public DateTime BegonnenUtc { get; init; }
    public DateTime? BeendetUtc { get; init; }
    public string? Meldung { get; init; }

    [JsonIgnore]
    public double Anteil => Gesamt == 0 ? 0 : (double)Erledigt / Gesamt;
}

public interface IEinbettungsLauf
{
    /// <summary>Der Stand je Säule, oder <c>null</c>, wenn dort noch nie gelaufen wurde.</summary>
    EinbettungsStand? Stand(string saeule);

    /// <summary>
    /// Startet einen Lauf im Hintergrund. Gibt zurück, ob gestartet wurde —
    /// <c>false</c>, wenn für diese Säule schon einer läuft.
    /// </summary>
    bool Starten(string saeule, Einbettungsumfang umfang);

    /// <summary>Bricht einen laufenden Lauf ab.</summary>
    bool Abbrechen(string saeule);
}

/// <summary>
/// „Alles einbetten" für eine Säule.
///
/// <para><b>Warum im Hintergrund und nicht in der Anfrage.</b> Der Bestand zählt 2.320
/// Semantik-Quellen; <see cref="IKnowledgeService.IndexAsync"/> lädt jede einzeln neu. Ein
/// vollständiger Lauf dauert deshalb Minuten bis Stunden, und eine HTTP-Anfrage, die so lange
/// offen steht, läuft in die Zeitgrenze eines jeden vorgeschalteten Proxys. Der Aufruf startet
/// also nur und die Oberfläche fragt den Stand ab.</para>
///
/// <para><b>Warum sequenziell.</b> Eingebettet wird gegen ein einziges Ollama. Zehn
/// gleichzeitige Quellen teilen sich dieselbe Rechenzeit und kommen zusammen nicht schneller
/// an — sie machen den Fortschritt nur unleserlich und die Abbruchmöglichkeit wertlos.</para>
///
/// <para><b>Ein Fehlschlag hält den Lauf nicht auf.</b> Bei tausenden Quellen ist immer eine
/// tot, umgezogen oder hinter einer Anmeldung. Wer beim ersten Fehler abbricht, kommt nie
/// durch; gezählt wird stattdessen, und am Ende steht die Bilanz.</para>
/// </summary>
public sealed class EinbettungsLauf : IEinbettungsLauf
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EinbettungsLauf> _log;

    private readonly Dictionary<string, EinbettungsStand> _stand = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _abbruch = new(StringComparer.OrdinalIgnoreCase);
    /* `System.Threading.Lock` gaebe es erst ab .NET 9; dieses Projekt steht auf 8. */
    private readonly object _sperre = new();

    public EinbettungsLauf(IServiceScopeFactory scopes, ILogger<EinbettungsLauf> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public EinbettungsStand? Stand(string saeule)
    {
        lock (_sperre) return _stand.GetValueOrDefault(saeule);
    }

    public bool Starten(string saeule, Einbettungsumfang umfang)
    {
        lock (_sperre)
        {
            if (_stand.TryGetValue(saeule, out var da) && da.Laeuft) return false;

            var quelle = new CancellationTokenSource();
            _abbruch[saeule] = quelle;

            _stand[saeule] = new EinbettungsStand
            {
                Saeule = saeule,
                Umfang = umfang.ToString(),
                Laeuft = true,
                BegonnenUtc = DateTime.UtcNow,
                Meldung = "wird vorbereitet"
            };

            /* Bewusst ohne await: Der Aufrufer soll sofort zurückbekommen, dass gestartet
               wurde. Der Lauf hängt an keiner Anfrage und überlebt sie. */
            _ = Task.Run(() => LaufenAsync(saeule, umfang, quelle.Token), CancellationToken.None);

            return true;
        }
    }

    public bool Abbrechen(string saeule)
    {
        lock (_sperre)
        {
            if (!_abbruch.TryGetValue(saeule, out var q)) return false;
            q.Cancel();
            return true;
        }
    }

    private async Task LaufenAsync(string saeule, Einbettungsumfang umfang, CancellationToken ct)
    {
        var eingebettet = 0;
        var abschnitte = 0;
        var ohneText = 0;
        var fehler = 0;

        try
        {
            using var bereich = _scopes.CreateScope();
            var fabrik = bereich.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

            List<Quellzeile> quellen;

            await using (var conn = await fabrik.OpenAsync(ct))
            {
                /* Die Auswahl gehört in die Abfrage, nicht dahinter -- sonst holt der Lauf
                   2.320 Zeilen und verwirft 1.800 davon im Speicher. */
                var wo = umfang switch
                {
                    Einbettungsumfang.Fehlend =>
                        "AND ISNULL(chunks, 0) = 0 AND ISNULL(status, '') <> 'kein Text gefunden'",
                    Einbettungsumfang.AuchLeere => "AND ISNULL(chunks, 0) = 0",
                    _ => ""
                };

                /* Ein benannter Typ, kein ValueTuple: Dapper fuellt Tupel nicht
                   verlaesslich -- die Spalten kaemen als Standardwerte zurueck, und der
                   Lauf wuerde ueber lauter Quelle 0 gehen, ohne zu klagen. */
                var zeilen = await conn.QueryAsync<Quellzeile>(new CommandDefinition(
                    $"""
                     SELECT source_id AS Id, ISNULL(title, origin) AS Titel
                       FROM dbo.knowledge_source
                      WHERE pillar = @saeule
                        AND active = 1
                        AND kind <> 'feed'
                        {wo}
                      ORDER BY source_id
                     """,
                    new { saeule }, cancellationToken: ct));

                quellen = zeilen.ToList();
            }

            Melden(saeule, s => s with { Gesamt = quellen.Count, Meldung = null });

            _log.LogInformation("Einbetten {Saeule} ({Umfang}): {Zahl} Quellen",
                                saeule, umfang, quellen.Count);

            foreach (var (id, titel) in quellen.Select(q => (q.Id, q.Titel)))
            {
                if (ct.IsCancellationRequested) break;

                Melden(saeule, s => s with { Aktuell = titel });

                try
                {
                    /* Ein eigener Bereich je Quelle: Die Verbindung eines Scoped-Dienstes
                       über tausende Aufrufe offenzuhalten ist die zuverlässigste Art, in
                       einen Zeitüberlauf zu laufen. */
                    using var einzeln = _scopes.CreateScope();
                    var svc = einzeln.ServiceProvider.GetRequiredService<IKnowledgeService>();

                    var (n, notiz) = await svc.IndexAsync(
                        id, umfang == Einbettungsumfang.Alles, ct);

                    if (n > 0) { eingebettet++; abschnitte += n; }
                    else if (notiz.Contains("kein Text", StringComparison.OrdinalIgnoreCase))
                        ohneText++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    fehler++;
                    _log.LogDebug(ex, "Quelle {Id} ({Titel}) fehlgeschlagen", id, titel);
                }

                Melden(saeule, s => s with
                {
                    Erledigt = s.Erledigt + 1,
                    Eingebettet = eingebettet,
                    Abschnitte = abschnitte,
                    OhneText = ohneText,
                    Fehler = fehler
                });
            }

            Fertig(saeule, ct.IsCancellationRequested ? "abgebrochen" : null);
        }
        catch (OperationCanceledException)
        {
            Fertig(saeule, "abgebrochen");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sammellauf {Saeule} abgebrochen", saeule);
            Fertig(saeule, $"abgebrochen: {ex.Message[..Math.Min(200, ex.Message.Length)]}");
        }
    }

    private void Melden(string saeule, Func<EinbettungsStand, EinbettungsStand> aendern)
    {
        lock (_sperre)
            if (_stand.TryGetValue(saeule, out var s)) _stand[saeule] = aendern(s);
    }

    private void Fertig(string saeule, string? meldung)
    {
        lock (_sperre)
        {
            if (_stand.TryGetValue(saeule, out var s))
                _stand[saeule] = s with
                {
                    Laeuft = false,
                    Aktuell = null,
                    BeendetUtc = DateTime.UtcNow,
                    Meldung = meldung
                };

            if (_abbruch.Remove(saeule, out var q)) q.Dispose();
        }
    }
}
