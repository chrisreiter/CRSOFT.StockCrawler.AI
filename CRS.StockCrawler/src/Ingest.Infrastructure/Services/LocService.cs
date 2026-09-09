using System.Collections.Concurrent;
using System.Xml.Linq;
using Ingest.Core.Abstractions;
using Ingest.Core.Models;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Die Sprachdateien: gefunden, gelesen, ausgeliefert.
///
/// <para><b>Eine neue Sprache ist eine Datei und sonst nichts.</b> Wer
/// <c>loc.res.it.xml</c> in das Sprachverzeichnis legt, findet Italienisch beim
/// nächsten Start in der Auswahl. Es gibt keine Liste im Quelltext, in die man
/// sie zusätzlich eintragen müsste — eine solche Liste wäre die Stelle, an der
/// eine mitgelieferte Sprache stillschweigend nicht erscheint.</para>
///
/// <para><b>Warum beim Start und nicht bei jeder Anfrage.</b> Das Verzeichnis
/// bei jedem Zugriff zu lesen hiesse, 1.711 Einträge je Sprachwechsel neu zu
/// zerlegen. Gelesen wird deshalb einmal; <see cref="Auffrischen"/> gibt es
/// trotzdem, weil man beim Übersetzen sonst für jedes geänderte Wort die
/// Anwendung neu starten müsste.</para>
/// </summary>
public sealed class LocService : ILocService
{
    private readonly ILogger<LocService> _log;
    private readonly string _verzeichnis;

    private readonly ConcurrentDictionary<string, Sprachdatei> _dateien = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyList<Sprache> _liste = [];

    /// <summary>
    /// Deutsch ist der Katalog: Die Schlüssel SIND die deutschen Texte, also
    /// ist diese Datei zugleich die Liste dessen, was überhaupt übersetzbar
    /// ist. Nur gegen sie lässt sich sagen, was einer anderen Sprache fehlt.
    /// </summary>
    public const string Katalogsprache = "de";

    public LocService(ILogger<LocService> log)
    {
        _log = log;
        _verzeichnis = Ablage.Sprachen;
        Auffrischen();
    }

    public IReadOnlyList<Sprache> Sprachen() => _liste;

    public Sprachdatei? Lade(string code) =>
        _dateien.TryGetValue(code, out var d) ? d : null;

    /// <summary>Liest das Sprachverzeichnis neu ein.</summary>
    public int Auffrischen()
    {
        if (!Directory.Exists(_verzeichnis))
        {
            _log.LogWarning("Sprachverzeichnis {Pfad} gibt es nicht — die Oberfläche "
                          + "bleibt deutsch", _verzeichnis);
            return 0;
        }

        _dateien.Clear();
        _wortschatz.Clear();

        foreach (var pfad in Directory.EnumerateFiles(_verzeichnis, "loc.res.*.xml"))
        {
            /*  Jede Datei in ihrem eigenen try.

                Eine kaputte Sprachdatei darf nicht die uebrigen mitreissen --
                sonst nimmt ein Tippfehler in der italienischen Fassung auch
                Englisch vom Netz, und die Ursache steht an einer Stelle, an
                der niemand sie sucht.                                        */
            try
            {
                var datei = Lies(pfad);
                _dateien[datei.Code] = datei;

                /*  Nur kurze Woerter. Fachbegriffe wie „Fehlerverhaeltnis"
                    stehen in jeder Sprachfassung fast gleich und trennen
                    nicht; die Funktionswoerter tun es.                       */
                _wortschatz[datei.Code] = datei.Texte.Values
                    .SelectMany(Woerter)
                    .Where(w => w.Length <= 6)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Sprachdatei {Pfad} übersprungen", pfad);
            }
        }

        /*  Die Luecken werden gegen den deutschen Katalog gezaehlt.

            Ohne diese Zahl sieht eine zur Haelfte uebersetzte Sprache aus wie
            eine fertige, in der zufaellig deutsche Woerter stehen -- und der
            Uebersetzer erfaehrt nie, dass er noch nicht fertig ist.          */
        var katalog = _dateien.TryGetValue(Katalogsprache, out var k) ? k.Texte : null;

        _liste = _dateien.Values
            .Select(d => new Sprache(d.Code, d.Name, d.Texte.Count,
                katalog is null || d.Code.Equals(Katalogsprache, StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : katalog.Keys.Count(s => !d.Texte.ContainsKey(s))))
            .OrderBy(s => s.Code == Katalogsprache ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _log.LogInformation("Sprachen: {Liste}",
            string.Join(", ", _liste.Select(s =>
                s.Luecken > 0 ? $"{s.Code} ({s.Texte}, {s.Luecken} offen)"
                              : $"{s.Code} ({s.Texte})")));

        return _liste.Count;
    }

    /*  Der Wortschatz je Sprache entsteht aus der Sprachdatei selbst.

        Eine eingebaute Wortliste je Sprache waere die zweite Stelle, die man
        beim Hinzufuegen einer Sprache pflegen muesste -- und genau die
        vergisst man. Die Werte von loc.res.it.xml SIND italienisch; daraus
        laesst sich der Wortschatz ableiten, ohne ein Wort zusaetzlich zu
        pflegen. Eine duenn besetzte Sprachdatei erkennt entsprechend schlecht,
        und das ist richtig so: Wer dreizehn Zeilen uebersetzt hat, hat keine
        Grundlage fuer eine Aussage ueber eine ganze Frage.                   */
    private readonly ConcurrentDictionary<string, HashSet<string>> _wortschatz =
        new(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Woerter(string t)
    {
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     t.ToLowerInvariant(), @"[\p{L}]{2,}"))
            yield return m.Value;
    }

    public string? Erkenne(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var woerter = Woerter(text).ToList();

        /*  Unter fuenf Woertern wird nicht geraten. „NVDA?" ist keine Sprache,
            und eine falsche Vermutung ist hier schlechter als keine: Sie
            ueberstimmt eine ausdrueckliche Einstellung.                      */
        if (woerter.Count < 5) return null;

        var treffer = _wortschatz
            .Select(w => (Code: w.Key, N: woerter.Count(x => w.Value.Contains(x))))
            .OrderByDescending(x => x.N)
            .ToList();

        if (treffer.Count == 0) return null;

        var beste = treffer[0];
        var zweite = treffer.Count > 1 ? treffer[1].N : 0;

        /*  Ein klarer Vorsprung, nicht bloss ein Vorsprung. Deutsch und
            Englisch teilen sich Woerter wie „so", „in", „was" -- ein Treffer
            mehr als die andere Sprache sagt nichts.                          */
        return beste.N >= 3 && beste.N >= zweite * 2 ? beste.Code : null;
    }

    private static Sprachdatei Lies(string pfad)
    {
        var wurzel = XDocument.Load(pfad).Root
            ?? throw new InvalidDataException("leere Datei");

        /*  Der Code kommt aus dem Dateinamen, nicht aus dem Attribut.

            Sonst koennen beide auseinanderlaufen: Eine Datei loc.res.it.xml
            mit code="en" wuerde Englisch ueberschreiben, und man suchte den
            Fehler in der englischen Uebersetzung.                            */
        var name = Path.GetFileNameWithoutExtension(pfad);          // loc.res.it
        var code = name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidDataException($"kein Sprachkürzel in „{name}“");

        var texte = new Dictionary<string, string>(StringComparer.Ordinal);
        var offen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var e in wurzel.Elements("t"))
        {
            var schluessel = (string?)e.Attribute("k");
            if (string.IsNullOrEmpty(schluessel)) continue;

            /*  Ein leerer Eintrag ist eine LUECKE, keine leere Uebersetzung.

                Wer ihn uebernaehme, loeschte den Text in der Oberflaeche --
                aus einem noch nicht uebersetzten Wort wuerde eine leere
                Stelle, und das sieht nach einem Fehler der Anwendung aus.    */
            var wert = e.Value;
            if (string.IsNullOrWhiteSpace(wert)) continue;

            texte[schluessel] = wert;
            if ((string?)e.Attribute("offen") == "1") offen[schluessel] = wert;
        }

        return new Sprachdatei(code,
            (string?)wurzel.Attribute("name") ?? code.ToUpperInvariant(),
            texte, offen);
    }
}
