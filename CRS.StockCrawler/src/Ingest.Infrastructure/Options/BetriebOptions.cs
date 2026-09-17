namespace Ingest.Infrastructure.Options;

/// <summary>
/// Wie diese Instanz betrieben wird: als <b>Master</b>, der rechnet und
/// schreibt, oder als <b>Slave</b> auf einer replizierten Datenbank, der nur
/// zeigt.
///
/// <para><b>Warum ein Betriebsmodus und nicht nur eine Rolle.</b> Auf dem
/// Live-Server läuft die Anwendung gegen ein Replikat der Datenbank — dort
/// kann niemand schreiben, auch kein Verwalter, und es gibt keine GPU für die
/// Modelle. Das ist eine Eigenschaft der Instanz, nicht des Benutzers: Ein
/// Verwalter, der sich am Slave anmeldet, ist dort trotzdem nur Leser. Die
/// Instanz sagt das offen (Kopfzeile, Status), statt Läufe anzunehmen, die
/// dann an der Datenbank scheitern.</para>
///
/// <para><b>Was der Slave unterlässt:</b> den Zeitplan (Kursabruf, Prognose,
/// Bewertung — alles schreibt), jede Änderung über die API, jeden
/// Modellaufruf (Agent, Wissenssuche, Einbettung). Sitzungen hält er im
/// Speicher, weil auch <c>app_session</c> auf dem Replikat nicht beschreibbar
/// ist; sie enden mit einem Neustart, und das ist dort hinnehmbar.</para>
///
/// <para><b>Gastzugang.</b> Ohne Kennwort, nur lesend, ohne Modellaufrufe,
/// mit dauerhaft sichtbarem Hinweis, dass es sich um eine offene
/// Demonstration handelt. Auf Master und Slave gleichermaßen möglich; auf dem
/// Slave ist er der vorgesehene Weg für Besucher.</para>
/// </summary>
public sealed class BetriebOptions
{
    /// <summary><c>master</c> oder <c>slave</c>. Alles andere gilt als master.</summary>
    public string Rolle { get; set; } = "master";

    public bool IstSlave => string.Equals(Rolle?.Trim(), "slave", StringComparison.OrdinalIgnoreCase);

    /// <summary>Anmeldung als „gast" ohne Kennwort erlauben.</summary>
    public bool GastZugang { get; set; } = false;

    /// <summary>Der Text, der Gästen auf der Anmeldeseite und dauerhaft in der Kopfzeile steht.</summary>
    public string GastHinweis { get; set; } =
        "Open Lab Demo — Sie sehen eine offene Demonstration von CRSOFT.StockCrawler mit "
        + "echten Daten. Als Gast können Sie alles ansehen, aber nichts anstoßen: keine Läufe, "
        + "keine Modellabfragen, keine Einstellungen. Nichts hier ist eine Anlageempfehlung.";

    /// <summary>Kurzform für die Kopfzeile.</summary>
    public string GastKurz { get; set; } = "Open Lab Demo · Gastzugang, nur lesend";

    /// <summary>Kurzform für die Kopfzeile eines Slave.</summary>
    public string SlaveKurz { get; set; } = "Replikat · nur lesend, keine Modelle";
}
