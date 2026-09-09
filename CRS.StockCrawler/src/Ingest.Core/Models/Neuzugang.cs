namespace Ingest.Core.Models;

/// <summary>
/// Ein Wert, der neu an den Markt kommt oder gerade gekommen ist.
/// </summary>
/// <param name="EntdecktUtc">Wann WIR davon erfahren haben.</param>
/// <param name="ErwartetAm">
/// Wann es laut Ankündigung losgeht. Der Abstand zu
/// <paramref name="EntdecktUtc"/> ist die eigentliche Frage: Wieviel Vorlauf
/// bleibt überhaupt?
/// </param>
/// <param name="Status">
/// <c>angekuendigt</c>, <c>gehandelt</c> oder <c>ausgefallen</c>. Eine
/// Ankündigung, aus der nichts wird, bleibt stehen — sie zu löschen wäre
/// dasselbe Auswahlproblem, das diese Sammlung beheben soll.
/// </param>
public sealed record Neuzugang(
    int NeuzugangId, string Quelle, string Art, string Symbol, string? Name, string? Markt,
    DateTime EntdecktUtc, DateTime? ErwartetAm,
    decimal? PreisVon, decimal? PreisBis, decimal? Volumen,
    string Status, int? AssetId,
    decimal? ErsterKurs, DateTime? ErsterKursUtc,
    string? Url, string? Titel, DateTime AktualisiertUtc)
{
    /*  DateTime und nicht DateOnly, obwohl hier ein blosses Datum steht.

        Dapper kennt DateOnly WEDER als Parameter NOCH als Ergebnis: Als
        Parameter heisst es „cannot be used as a parameter value", beim Lesen
        verlangt es einen Konstruktor mit DateTime an dieser Stelle. Dieselbe
        Klasse Fehler wie bei den ValueTuples -- der Typ, der im Modell der
        richtige waere, ist es an der Schnittstelle zur Datenbank nicht. Die
        Uhrzeit steht auf Mitternacht und wird nirgends angezeigt.            */

    /// <summary>Tage bis zum erwarteten Start; negativ, wenn er vorbei ist.</summary>
    public int? TageBis => ErwartetAm is { } d
        ? (int)(d.Date - DateTime.UtcNow.Date).TotalDays
        : null;

    /// <summary>
    /// Wieviel Vorlauf die Ankündigung liess — in Tagen zwischen Entdeckung
    /// und erwartetem Start.
    /// </summary>
    public int? Vorlauf => ErwartetAm is { } d
        ? (int)(d.Date - EntdecktUtc.Date).TotalDays
        : null;
}

/// <summary>Was ein Sammellauf je Quelle ergeben hat.</summary>
public sealed record NeuzugangLauf(
    int LaufId, DateTime GestartetUtc, string Quelle,
    int Gefunden, int Neu, bool Erfolg, string? Meldung);

/// <summary>
/// Die Seite „Neuzugänge“ als Ganzes.
/// </summary>
/// <param name="Hinweis">
/// Was man über diese Zahlen wissen muss, bevor man sie liest — steht in der
/// Antwort und nicht im Kleingedruckten.
/// </param>
public sealed record NeuzugangUebersicht(
    IReadOnlyList<Neuzugang> Anstehend,
    IReadOnlyList<Neuzugang> Gestartet,
    IReadOnlyList<NeuzugangLauf> Laeufe,
    int KohorteSeitTagen,
    int Angekuendigt, int Gehandelt, int Ausgefallen,
    double? MittlererVorlaufTage,
    DateTime StandUtc,
    string Hinweis);
