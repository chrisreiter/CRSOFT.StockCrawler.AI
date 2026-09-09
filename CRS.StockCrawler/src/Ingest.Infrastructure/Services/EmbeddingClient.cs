using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

public interface IEmbeddingClient
{
    /// <summary>Wie viele Zahlen ein Vektor hat. Muss zur Sammlung passen.</summary>
    int Dimensions { get; }

    string Model { get; }

    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Bettet mehrere Texte ein. Reihenfolge bleibt erhalten.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>
/// Einbettungen über ein lokal laufendes Ollama.
///
/// <b>Warum lokal und nicht über einen Dienst.</b> Es geht um hochgeladene
/// Bücher und beobachtete Kanäle — Material, das den Rechner nicht verlassen
/// muss. Dazu kommt die Menge: Ein Fachbuch ergibt einige tausend Abschnitte,
/// und die einmal einzubetten ist lokal eine Frage von Minuten und bei einem
/// bezahlten Dienst eine Frage der Rechnung.
///
/// <b>Warum bge-m3.</b> Es ist mehrsprachig — die Quellen hier sind teils
/// deutsch, teils englisch, und ein rein englisches Modell bettet deutsche
/// Abschnitte in eine Ecke des Raums, in der sie sich nur noch untereinander
/// ähneln. Außerdem verträgt es lange Abschnitte, was die Zerlegung
/// entspannt.
/// </summary>
public sealed class OllamaEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly IOllamaEndpointService _endpunkte;
    private readonly ILogger<OllamaEmbeddingClient> _log;

    public int Dimensions { get; } = 1024;
    public string Model { get; }

    public OllamaEmbeddingClient(
        HttpClient http, IOllamaEndpointService endpunkte,
        ILogger<OllamaEmbeddingClient> log, string model = "bge-m3:latest")
    {
        _http = http;
        _endpunkte = endpunkte;
        _log = log;
        Model = model;

        /* Keine feste Basisadresse mehr: Sie kommt je Aufruf vom
           Endpunktdienst, weil sie sich mit dem gewählten Endpunkt ändert. Eine
           einmal gesetzte BaseAddress liesse sich später nicht mehr wechseln. */

        // Ein Fachbuch kann tausende Abschnitte haben; die Zeitüberschreitung
        // muss den langsamsten Block überstehen, nicht den schnellsten.
        _http.Timeout = TimeSpan.FromMinutes(10);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var (basis, anmeldung) = await _endpunkte.ZugangAsync(ct);

            using var res = await _http.HolenAsync($"{basis}/api/tags", anmeldung, ct);
            if (!res.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));

            return doc.RootElement.GetProperty("models").EnumerateArray()
                      .Any(m => (m.GetProperty("name").GetString() ?? "")
                                .StartsWith(Model.Split(':')[0], StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Ollama nicht erreichbar");
            return false;
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<float[]>(texts.Count);

        /* Ollama nimmt zwar eine Liste entgegen, aber ein einzelner überlanger
           Abschnitt in der Mitte lässt den ganzen Block scheitern — und man
           weiß hinterher nicht, welcher es war. In Blöcken von acht ist der
           Verlust im Fehlerfall klein und die Meldung noch zuzuordnen. */
        const int block = 8;

        for (var i = 0; i < texts.Count; i += block)
        {
            ct.ThrowIfCancellationRequested();

            var teil = texts.Skip(i).Take(block).ToArray();

            var (basis, anmeldung) = await _endpunkte.ZugangAsync(ct);

            using var res = await _http.SendenAsync(
                $"{basis}/api/embed", new { model = Model, input = teil }, anmeldung, ct);

            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"Einbettung fehlgeschlagen ({(int)res.StatusCode}) bei Abschnitt {i}: {body}");
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));

            foreach (var v in doc.RootElement.GetProperty("embeddings").EnumerateArray())
            {
                var arr = v.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();

                /* Auf Länge eins bringen. Qdrant rechnet hier mit dem
                   Kosinusmaß; ohne Normierung entscheidet bei manchen Modellen
                   die Textlänge über die Ähnlichkeit statt der Inhalt. */
                var norm = MathF.Sqrt(arr.Sum(x => x * x));
                if (norm > 1e-8f)
                    for (var k = 0; k < arr.Length; k++) arr[k] /= norm;

                result.Add(arr);
            }
        }

        return result;
    }
}
