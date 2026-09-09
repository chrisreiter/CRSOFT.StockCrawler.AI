namespace Ingest.Infrastructure.Repositories;

/// <summary>
/// Zerlegt Schlüssellisten in Blöcke, die SQL Server annimmt.
///
/// <b>Der Grund, und er ist eine harte Grenze.</b> SQL Server nimmt je Befehl
/// höchstens <b>2100 Parameter</b> entgegen. Dapper macht aus
/// <c>WHERE id IN @ids</c> genau einen Parameter je Element — bei 5000
/// Schlüsseln also 5000 Parameter, und der Server antwortet mit
/// „The incoming request has too many parameters".
///
/// Aufgefallen im Scheduler beim Auswerten fälliger Prognosen: Der Stapel ist
/// auf 5000 gesetzt, und sobald tatsächlich mehr als rund 2100 Prognosen fällig
/// waren, brach der Lauf ab. Bei kleinen Rückständen lief er, bei großen nicht
/// — also genau dann nicht, wenn er gebraucht wurde.
///
/// Die Blockgröße liegt bewusst deutlich unter der Grenze: In derselben Abfrage
/// stehen neben der Schlüsselliste weitere Parameter, und diese Reserve
/// erspart es, bei jeder neuen Bedingung nachzurechnen.
/// </summary>
public static class SqlBatching
{
    /// <summary>Höchstzahl Schlüssel je Befehl.</summary>
    public const int MaxKeysPerCommand = 1000;

    /// <summary>Zerlegt eine Liste in Blöcke höchstens zulässiger Größe.</summary>
    public static IEnumerable<T[]> Chunks<T>(IReadOnlyList<T> items, int size = MaxKeysPerCommand)
    {
        if (items.Count == 0) yield break;

        // Der häufige Fall bleibt ein einziger Durchgang ohne Kopie.
        if (items.Count <= size)
        {
            yield return items as T[] ?? items.ToArray();
            yield break;
        }

        for (var i = 0; i < items.Count; i += size)
            yield return items.Skip(i).Take(size).ToArray();
    }
}
