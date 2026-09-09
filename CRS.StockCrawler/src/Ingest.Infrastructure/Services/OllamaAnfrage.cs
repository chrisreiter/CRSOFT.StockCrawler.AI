using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Anfragen an Ollama, die eine Anmeldung mitführen können.
///
/// <para><b>Warum nicht einfach <c>DefaultRequestHeaders</c>.</b> Die <see cref="HttpClient"/>
/// dieser Anwendung sind geteilt — <c>EmbeddingClient</c> und <c>ReasoningService</c> bekommen
/// je einen aus dem Container und benutzen ihn für alle Läufe. Die Kopfzeile dort zu setzen
/// wirkt auf jede gleichzeitig laufende Anfrage; wechselt jemand während eines Einbettungslaufs
/// den Endpunkt, geht der Schlüssel des einen an den Server des anderen. Ein
/// <see cref="HttpRequestMessage"/> je Anfrage kann das nicht.</para>
///
/// <para>Ohne Anmeldung verhalten sich beide Methoden wie <c>GetAsync</c>/<c>PostAsync</c> —
/// der Aufrufer muss also nicht unterscheiden, ob sein Endpunkt eine verlangt.</para>
/// </summary>
internal static class OllamaAnfrage
{
    public static Task<HttpResponseMessage> HolenAsync(
        this HttpClient http, string url, AuthenticationHeaderValue? anmeldung,
        CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Authorization = anmeldung } };
        return http.SendAsync(req, ct);
    }

    public static Task<HttpResponseMessage> SendenAsync(
        this HttpClient http, string url, object rumpf, AuthenticationHeaderValue? anmeldung,
        CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Headers = { Authorization = anmeldung },
            Content = new StringContent(
                JsonSerializer.Serialize(rumpf), Encoding.UTF8, "application/json")
        };

        return http.SendAsync(req, ct);
    }
}
