using System.Net.Http.Json;
using System.Text.Json;

namespace Ingest.Infrastructure.Providers;

/// <summary>Antwort eines Bildmodells auf einen Formvergleich.</summary>
public sealed record VlmVerdict(
    bool Ok,
    int Similarity,
    string Pattern,
    string Note,
    string RawText,
    int ElapsedMs);

public interface IVlmClient
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>Ein Bild und eine Frage an das Modell.</summary>
    Task<VlmVerdict> AskAsync(byte[] png, string prompt, string model,
                              CancellationToken ct = default);
}

/// <summary>
/// Zugang zu einem lokal laufenden Ollama.
///
/// <b>Warum lokal und nicht über einen Dienst:</b> Die Vorauswahl liefert je
/// Anfrage ein bis zwei Dutzend Bildpaare, und beim Durchmustern der Historie
/// werden es Tausende. Das über eine kostenpflichtige Schnittstelle zu schicken
/// wäre teuer und langsam; ein 3B-Modell auf der eigenen Karte antwortet in
/// unter einer Sekunde und kostet nichts.
///
/// <b>Was dabei zu beachten ist:</b> Ollama lädt ein Modell beim ersten Aufruf
/// in den Speicher. Die erste Antwort dauert deshalb um ein Vielfaches länger
/// als die folgenden. Wer die erste Messung für die typische hält, verwirft ein
/// brauchbares Verfahren als zu langsam.
/// </summary>
public sealed class OllamaClient : IVlmClient
{
    private readonly HttpClient _http;

    public OllamaClient(HttpClient http) => _http = http;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await _http.GetAsync("/api/version", ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await _http.GetAsync("/api/tags", ct);
            if (!res.IsSuccessStatusCode) return [];

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));

            if (!doc.RootElement.TryGetProperty("models", out var models)) return [];

            return models.EnumerateArray()
                         .Select(m => m.GetProperty("name").GetString() ?? "")
                         .Where(s => s.Length > 0)
                         .ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<VlmVerdict> AskAsync(byte[] png, string prompt, string model,
                                            CancellationToken ct = default)
    {
        var started = Environment.TickCount64;

        var body = new
        {
            model,
            prompt,
            images = new[] { Convert.ToBase64String(png) },
            stream = false,

            /* Temperatur null: Es geht um ein Urteil, nicht um Formulierung.
               Bei jedem Aufruf dieselbe Antwort auf dasselbe Bild zu bekommen
               ist hier keine Nebensache — ohne das ließe sich nicht messen, ob
               das Modell überhaupt etwas Reproduzierbares sieht. */
            options = new { temperature = 0.0, num_predict = 200 }
        };

        try
        {
            using var res = await _http.PostAsJsonAsync("/api/generate", body, ct);

            if (!res.IsSuccessStatusCode)
                return new VlmVerdict(false, 0, "", $"HTTP {(int)res.StatusCode}", "",
                    (int)(Environment.TickCount64 - started));

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));

            var text = doc.RootElement.TryGetProperty("response", out var r)
                ? r.GetString() ?? ""
                : "";

            var (sim, pattern, note) = Parse(text);

            return new VlmVerdict(true, sim, pattern, note, text,
                (int)(Environment.TickCount64 - started));
        }
        catch (Exception ex)
        {
            return new VlmVerdict(false, 0, "", ex.Message, "",
                (int)(Environment.TickCount64 - started));
        }
    }

    /// <summary>
    /// Holt das Urteil aus der Antwort.
    ///
    /// Kleine Bildmodelle halten sich nicht zuverlässig an ein Ausgabeformat.
    /// Sie schreiben JSON in einen Codeblock, stellen einen Satz voran oder
    /// hängen eine Erklärung an. Deshalb wird erst nach JSON gesucht und
    /// andernfalls nach einer Zahl im Text — statt die Antwort zu verwerfen und
    /// das Verfahren für untauglich zu erklären.
    /// </summary>
    private static (int Similarity, string Pattern, string Note) Parse(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                var root = doc.RootElement;

                var sim = root.TryGetProperty("similarity", out var s)
                    ? (s.ValueKind == JsonValueKind.Number ? s.GetInt32() : ParseInt(s.GetString()))
                    : 0;

                var pat = root.TryGetProperty("pattern", out var p) ? p.GetString() ?? "" : "";
                var note = root.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";

                return (Math.Clamp(sim, 0, 100), pat, note);
            }
            catch (JsonException)
            {
                // Fällt auf die Textsuche zurück.
            }
        }

        foreach (var token in text.Split([' ', '\n', '\r', '\t', ':', ',', '%'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out var v) && v is >= 0 and <= 100)
                return (v, "", text.Trim());
        }

        return (0, "", text.Trim());
    }

    private static int ParseInt(string? s) => int.TryParse(s, out var v) ? v : 0;
}
