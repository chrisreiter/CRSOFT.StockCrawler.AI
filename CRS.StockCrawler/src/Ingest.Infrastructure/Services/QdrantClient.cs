using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>Ein Treffer aus der Vektorsuche.</summary>
public sealed record VectorHit(Guid Id, double Score, IReadOnlyDictionary<string, string> Payload);

public interface IVectorStore
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task EnsureCollectionAsync(string name, int dimensions, CancellationToken ct = default);

    Task UpsertAsync(string collection,
                     IReadOnlyList<(Guid Id, float[] Vector, Dictionary<string, object> Payload)> points,
                     CancellationToken ct = default);

    Task<IReadOnlyList<VectorHit>> SearchAsync(
        string collection, float[] query, int limit,
        IReadOnlyDictionary<string, string>? filter = null,
        CancellationToken ct = default);

    Task DeleteBySourceAsync(string collection, int sourceId, CancellationToken ct = default);

    /// <summary>Entfernt einzelne Punkte — für den Überhang einer gekürzten Quelle.</summary>
    Task DeleteByIdsAsync(string collection, IReadOnlyList<Guid> ids,
                          CancellationToken ct = default);

    Task<long> CountAsync(string collection, CancellationToken ct = default);

    /// <summary>Alle Kennungen einer Sammlung, seitenweise geholt.</summary>
    Task<IReadOnlyList<Guid>> AllIdsAsync(string collection, CancellationToken ct = default);
}

/// <summary>
/// Ein knapper Zugang zu Qdrant über dessen HTTP-Schnittstelle.
///
/// <b>Warum kein fertiges Paket.</b> Gebraucht werden fünf Aufrufe: Sammlung
/// anlegen, Punkte schreiben, suchen, löschen, zählen. Das offizielle Paket
/// bringt gRPC, eigene Typen und eine Versionsabhängigkeit zum Server mit —
/// für fünf Aufrufe ein schlechter Tausch. Die HTTP-Schnittstelle ist stabil
/// und lesbar.
///
/// <b>Warum Kosinus und nicht euklidisch.</b> Die Vektoren kommen normiert
/// hier an; bei Länge eins sind beide Maße monoton ineinander überführbar. Der
/// Kosinus ist die Konvention der Einbettungsmodelle, und die Zahlen liegen
/// dann im gewohnten Bereich zwischen −1 und 1 statt in einer Einheit, die von
/// der Dimension abhängt.
/// </summary>
public sealed class QdrantClient(HttpClient http, ILogger<QdrantClient> log) : IVectorStore
{
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await http.GetAsync("/collections", ct);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Qdrant nicht erreichbar");
            return false;
        }
    }

    public async Task EnsureCollectionAsync(string name, int dimensions, CancellationToken ct = default)
    {
        using var vorhanden = await http.GetAsync($"/collections/{name}", ct);
        if (vorhanden.StatusCode != HttpStatusCode.NotFound) return;

        using var res = await http.PutAsJsonAsync($"/collections/{name}", new
        {
            vectors = new { size = dimensions, distance = "Cosine" }
        }, ct);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Sammlung {name} konnte nicht angelegt werden: "
                + await res.Content.ReadAsStringAsync(ct));

        /* Ein Index auf source_id. Ohne ihn wird beim Löschen einer Quelle die
           ganze Sammlung durchgegangen — bei einem Fachbuch mit zehntausend
           Abschnitten merkt man das. */
        using var idx = await http.PutAsJsonAsync($"/collections/{name}/index", new
        {
            field_name = "source_id",
            field_schema = "integer"
        }, ct);

        log.LogInformation("Sammlung {Name} angelegt, {D} Dimensionen", name, dimensions);
    }

    /* Die Nutzlast trägt gemischte Typen, und das ist kein Schönheitsfehler.

       Der erste Entwurf legte alles als Zeichenkette ab, auch `source_id`. Der
       Löschfilter vergleicht aber gegen eine Zahl — „1“ ist nicht 1, also traf
       er nie etwas. Die Folge war still und schlimm: Beim erneuten Einbetten
       blieben die alten Vektoren stehen, die zugehörigen Abschnitte in SQL
       waren aber gelöscht. Die Suche fand daraufhin Punkte, zu denen es keinen
       Text mehr gab, und lieferte für die meisten Fragen gar nichts. */
    public async Task UpsertAsync(
        string collection,
        IReadOnlyList<(Guid Id, float[] Vector, Dictionary<string, object> Payload)> points,
        CancellationToken ct = default)
    {
        // In Blöcken, sonst wird der Rumpf bei einigen tausend Punkten mit je
        // 1024 Zahlen zweistellig megabyte-groß.
        const int block = 256;

        for (var i = 0; i < points.Count; i += block)
        {
            ct.ThrowIfCancellationRequested();

            var teil = points.Skip(i).Take(block).Select(p => new
            {
                id = p.Id.ToString(),
                vector = p.Vector,
                payload = p.Payload
            }).ToArray();

            using var res = await http.PutAsJsonAsync(
                $"/collections/{collection}/points?wait=true", new { points = teil }, ct);

            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Schreiben fehlgeschlagen bei Punkt {i}: "
                    + await res.Content.ReadAsStringAsync(ct));
        }
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        string collection, float[] query, int limit,
        IReadOnlyDictionary<string, string>? filter = null,
        CancellationToken ct = default)
    {
        object? qFilter = null;

        if (filter is { Count: > 0 })
            qFilter = new
            {
                must = filter.Select(kv => new
                {
                    key = kv.Key,
                    match = new { value = kv.Value }
                }).ToArray()
            };

        using var res = await http.PostAsJsonAsync(
            $"/collections/{collection}/points/search",
            new { vector = query, limit, with_payload = true, filter = qFilter }, ct);

        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Suche fehlgeschlagen: {Body}", await res.Content.ReadAsStringAsync(ct));
            return [];
        }

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));

        var treffer = new List<VectorHit>();

        foreach (var r in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var payload = new Dictionary<string, string>();

            if (r.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var f in p.EnumerateObject())
                    payload[f.Name] = f.Value.ValueKind == JsonValueKind.String
                        ? f.Value.GetString() ?? ""
                        : f.Value.ToString();

            treffer.Add(new VectorHit(
                Guid.Parse(r.GetProperty("id").GetString() ?? Guid.Empty.ToString()),
                r.GetProperty("score").GetDouble(),
                payload));
        }

        return treffer;
    }

    public async Task DeleteBySourceAsync(string collection, int sourceId, CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync(
            $"/collections/{collection}/points/delete?wait=true", new
            {
                filter = new
                {
                    must = new[] { new { key = "source_id", match = new { value = sourceId } } }
                }
            }, ct);

        if (!res.IsSuccessStatusCode)
            log.LogWarning("Löschen fehlgeschlagen: {Body}",
                await res.Content.ReadAsStringAsync(ct));
    }

    public async Task DeleteByIdsAsync(
        string collection, IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;

        using var res = await http.PostAsJsonAsync(
            $"/collections/{collection}/points/delete?wait=true",
            new { points = ids.Select(i => i.ToString()).ToArray() }, ct);

        if (!res.IsSuccessStatusCode)
            log.LogWarning("Löschen einzelner Punkte fehlgeschlagen: {Body}",
                await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Holt alle Kennungen einer Sammlung.
    ///
    /// Qdrant liefert sie seitenweise; der Zeiger der letzten Seite ist der
    /// Einstieg in die nächste. Nutzlast und Vektoren bleiben außen vor — es
    /// geht nur darum, welche Punkte es gibt, und ein Vektor mit 1024 Zahlen je
    /// Punkt würde die Antwort um das Tausendfache aufblähen.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> AllIdsAsync(
        string collection, CancellationToken ct = default)
    {
        var alle = new List<Guid>();
        object? zeiger = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using var res = await http.PostAsJsonAsync(
                $"/collections/{collection}/points/scroll",
                new { limit = 4096, with_payload = false, with_vector = false, offset = zeiger },
                ct);

            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Durchblättern fehlgeschlagen: {Body}",
                    await res.Content.ReadAsStringAsync(ct));
                return alle;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement.GetProperty("result");

            foreach (var punkt in r.GetProperty("points").EnumerateArray())
                if (Guid.TryParse(punkt.GetProperty("id").GetString(), out var g))
                    alle.Add(g);

            if (!r.TryGetProperty("next_page_offset", out var next)
                || next.ValueKind == JsonValueKind.Null)
                return alle;

            zeiger = next.GetString() ?? (object)next.GetRawText();
        }
    }

    public async Task<long> CountAsync(string collection, CancellationToken ct = default)
    {
        try
        {
            using var res = await http.PostAsJsonAsync(
                $"/collections/{collection}/points/count", new { exact = true }, ct);

            if (!res.IsSuccessStatusCode) return 0;

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("result").GetProperty("count").GetInt64();
        }
        catch
        {
            return 0;
        }
    }
}
