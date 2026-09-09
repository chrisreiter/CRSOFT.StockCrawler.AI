namespace Ingest.Infrastructure;

/// <summary>
/// Wo die Anwendung ihre mitgelieferten Dateien sucht — Modelle, Quellenlisten.
///
/// <para><b>Warum das eine eigene Stelle braucht.</b> Drei Dienste suchten ihre
/// Dateien über <c>Directory.GetCurrentDirectory() + "../../ml/models"</c>. Das
/// stimmt genau dann, wenn die Anwendung aus <c>src/Ingest.Api</c> heraus
/// gestartet wird — also beim Entwickeln. In einem veröffentlichten Verzeichnis
/// zeigt derselbe Pfad irgendwohin, und die Anwendung startet ohne Modelle und
/// ohne Quellenliste. Ohne Fehler: <c>Directory.GetFiles</c> auf ein nicht
/// vorhandenes Verzeichnis wirft zwar, aber der Ladevorgang fängt es ab und
/// meldet „kein Modell geladen" — was auch der Zustand ist, wenn wirklich
/// keines da ist. Zwei sehr verschiedene Ursachen, dieselbe Meldung.</para>
///
/// <para>Gesucht wird deshalb der Reihe nach: die ausdrückliche Angabe, dann
/// das Verzeichnis neben der Anwendung, dann der Entwicklungspfad. Die erste
/// Stelle, an der die Datei liegt, gewinnt.</para>
/// </summary>
public static class Ablage
{
    /// <summary>Verzeichnis mit den ONNX-Modellen und ihren Beschreibungen.</summary>
    public static string Modelle =>
        Finde("CRS_MODELS_DIR", ["modelle", "ml/models"], ["ml", "models"]);

    /// <summary>Die Liste der Nachrichten- und Literaturquellen.</summary>
    public static string Quellen =>
        FindeDatei("CRS_QUELLEN", "quellen.json", ["infra", "."], ["infra"]);

    /// <summary>
    /// Das Verzeichnis mit den Sprachdateien (<c>loc.res.de.xml</c> und so fort).
    ///
    /// <para>Es wird nur GELESEN, also gilt die gewöhnliche Reihenfolge. Wer
    /// eine eigene Übersetzung mitgeben will, ohne die Auslieferung anzufassen,
    /// setzt <c>CRS_SPRACHEN</c>.</para>
    /// </summary>
    public static string Sprachen
    {
        get
        {
            var gesetzt = Environment.GetEnvironmentVariable("CRS_SPRACHEN");
            if (!string.IsNullOrWhiteSpace(gesetzt) && Directory.Exists(gesetzt)) return gesetzt;

            /*  Hier gewinnt der ENTWICKLUNGSPFAD, anders als bei Modellen und
                Quellenliste. Der Grund ist der Gebrauch: An einer Uebersetzung
                wird waehrend der Entwicklung dauernd gearbeitet, und das csproj
                kopiert die Dateien beim Bauen nach bin/loc. Gaebe die Kopie den
                Ausschlag, sae man seine Aenderung an infra/loc erst nach dem
                naechsten Bauen -- also genau die Falle, wegen der die
                Fassungsnummern aus dem Aenderungszeitpunkt der Datei kommen und
                nicht aus der Startzeit.                                       */
            var dev = Path.GetFullPath(Path.Combine(
                Directory.GetCurrentDirectory(), "..", "..", "infra", "loc"));

            if (Directory.Exists(dev)) return dev;

            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "loc"));
        }
    }

    /// <summary>Die kuratierte Weltliste der Börsenplätze.</summary>
    public static string Welt =>
        FindeDatei("CRS_WELT", "welt.json", ["infra", "."], ["infra"]);

    /// <summary>
    /// Die Liste der Ollama-Endpunkte.
    ///
    /// <para>Anders als die übrigen Dateien wird sie auch <b>geschrieben</b>, und das ändert
    /// die Suchreihenfolge: <see cref="FindeDatei"/> liefert am Ende den Entwicklungspfad
    /// zurück, auch wenn dort nichts liegt — zum Lesen richtig, zum Schreiben eine Datei
    /// irgendwo neben der Anwendung. Entschieden wird deshalb am <b>Verzeichnis</b>: Existiert
    /// das Entwicklungs-<c>infra</c>, gilt es; sonst wird neben der Anwendung geschrieben.</para>
    /// </summary>
    public static string OllamaEndpunkte =>
        SchreibbareDatei("CRS_OLLAMA_ENDPUNKTE", "ollama-endpunkte.json", "infra");

    private static string SchreibbareDatei(string variable, string name, string unterordner)
    {
        var gesetzt = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(gesetzt)) return gesetzt;

        var dev = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", unterordner));

        if (Directory.Exists(dev)) return Path.Combine(dev, name);

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, unterordner, name));
    }

    /// <summary>
    /// Sucht ein Verzeichnis: Umgebungsvariable, dann neben der Anwendung,
    /// dann zwei Ebenen darüber (Entwicklungsstand).
    /// </summary>
    private static string Finde(string variable, string[] nebenAnwendung, string[] entwicklung)
    {
        var gesetzt = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(gesetzt) && Directory.Exists(gesetzt)) return gesetzt;

        foreach (var teil in nebenAnwendung)
        {
            var p = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, teil));
            if (Directory.Exists(p)) return p;
        }

        var dev = Path.GetFullPath(Path.Combine(
            [Directory.GetCurrentDirectory(), "..", "..", .. entwicklung]));

        // Auch wenn es nicht existiert: Der Aufrufer soll den Pfad melden
        // können, den er vergeblich gesucht hat.
        return dev;
    }

    private static string FindeDatei(string variable, string name,
                                     string[] nebenAnwendung, string[] entwicklung)
    {
        var gesetzt = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(gesetzt) && File.Exists(gesetzt)) return gesetzt;

        foreach (var teil in nebenAnwendung)
        {
            var p = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, teil, name));
            if (File.Exists(p)) return p;
        }

        return Path.GetFullPath(Path.Combine(
            [Directory.GetCurrentDirectory(), "..", "..", .. entwicklung, name]));
    }
}
