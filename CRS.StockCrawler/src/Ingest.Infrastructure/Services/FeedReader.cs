using System.Globalization;
using System.Xml.Linq;

namespace Ingest.Infrastructure.Services;

/// <summary>Ein Eintrag aus einem Feed.</summary>
public sealed record FeedItem(string Url, string Title, DateTime? PublishedUtc, string? Summary);

/// <summary>
/// Liest RSS- und Atom-Feeds.
///
/// <b>Warum Feeds und nicht Seiten.</b> Eine Nachrichtenseite abzurufen liefert
/// bei jedem Lauf dieselbe Seite mit anderem Inhalt — man weiß danach nicht,
/// was daran neu war. Ein Feed liefert eine Liste einzelner Meldungen mit
/// Adresse und <b>Zeitstempel</b>. Erst das erlaubt beides, was diese Säule
/// braucht: nur Neues einzubetten, und eine Aussage später in Bezug zu einer
/// Kursbewegung zu setzen.
///
/// <b>Warum von Hand geparst.</b> <c>System.ServiceModel.Syndication</c> gibt
/// es zwar, aber es wirft bei unsauberen Feeds, und unsaubere Feeds sind in
/// dieser Ecke die Regel: fehlende Namensräume, gemischte Datumsformate,
/// HTML in Titeln. Ein toleranter Leser über LINQ to XML ist hier robuster als
/// ein strenger über eine Bibliothek — er nimmt, was er findet, und lässt den
/// Rest liegen, statt den ganzen Feed zu verwerfen.
/// </summary>
public static class FeedReader
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace DublinCore = "http://purl.org/dc/elements/1.1/";

    /// <summary>Erkennt RSS wie Atom und liefert die Einträge.</summary>
    public static IReadOnlyList<FeedItem> Parse(string xml, string feedUrl)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];

        XDocument doc;

        try
        {
            // Manche Feeds tragen eine Byte-Order-Mark oder führende Leerzeichen
            // vor der XML-Deklaration; beides lässt den Parser sonst scheitern.
            doc = XDocument.Parse(xml.TrimStart('﻿', ' ', '\r', '\n', '\t'),
                                  LoadOptions.None);
        }
        catch
        {
            return [];
        }

        var root = doc.Root;
        if (root is null) return [];

        var items = new List<FeedItem>();

        // --- RSS 2.0 und RDF ------------------------------------------------
        foreach (var e in root.Descendants().Where(x => x.Name.LocalName == "item"))
        {
            var link = Text(e, "link") ?? Text(e, "guid");
            if (string.IsNullOrWhiteSpace(link)) continue;

            items.Add(new FeedItem(
                Absolut(link.Trim(), feedUrl),
                Clean(Text(e, "title") ?? link),
                Datum(Text(e, "pubDate") ?? Text(e, "date")
                      ?? e.Elements(DublinCore + "date").FirstOrDefault()?.Value),
                Clean(Text(e, "description") ?? Text(e, "summary"))));
        }

        // --- Atom -----------------------------------------------------------
        foreach (var e in root.Descendants(Atom + "entry"))
        {
            /* Ein Atom-Eintrag kann mehrere link-Elemente haben — alternate ist
               der Artikel, self der Eintrag selbst, enclosure ein Anhang. Ohne
               diese Unterscheidung landet man auf dem Feed statt auf der
               Meldung. */
            var link = e.Elements(Atom + "link")
                        .FirstOrDefault(l => (string?)l.Attribute("rel") is null
                                          || (string?)l.Attribute("rel") == "alternate")
                       ?? e.Elements(Atom + "link").FirstOrDefault();

            var href = (string?)link?.Attribute("href")
                       ?? e.Element(Atom + "id")?.Value;

            if (string.IsNullOrWhiteSpace(href)) continue;

            items.Add(new FeedItem(
                Absolut(href.Trim(), feedUrl),
                Clean(e.Element(Atom + "title")?.Value ?? href),
                Datum(e.Element(Atom + "published")?.Value
                      ?? e.Element(Atom + "updated")?.Value),
                Clean(e.Element(Atom + "summary")?.Value)));
        }

        /* Dubletten innerhalb eines Feeds kommen vor — RDF-Feeds führen
           denselben Eintrag gelegentlich zweimal. Aussortiert wird hier, damit
           der Aufrufer sich darauf verlassen kann. */
        return items
            .GroupBy(i => i.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Ist das überhaupt ein Feed?</summary>
    public static bool LooksLikeFeed(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return false;

        var kopf = xml.Length > 2000 ? xml[..2000] : xml;

        return kopf.Contains("<rss", StringComparison.OrdinalIgnoreCase)
            || kopf.Contains("<feed", StringComparison.OrdinalIgnoreCase)
            || kopf.Contains("<rdf:RDF", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Text(XElement e, string name) =>
        e.Elements().FirstOrDefault(x =>
            string.Equals(x.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>
    /// Datum aus einem Feed. Erst die üblichen Formate, dann alles, was
    /// .NET erkennt — und zur Not gar nichts.
    /// </summary>
    private static DateTime? Datum(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        s = s.Trim();

        // RFC 822 (RSS) und ISO 8601 (Atom) decken fast alles ab.
        string[] formate =
        [
            "ddd, dd MMM yyyy HH:mm:ss zzz",
            "ddd, dd MMM yyyy HH:mm:ss K",
            "ddd, dd MMM yyyy HH:mm zzz",
            "yyyy-MM-ddTHH:mm:ssK",
            "yyyy-MM-ddTHH:mm:ss.fffK"
        ];

        if (DateTime.TryParseExact(s, formate, CultureInfo.InvariantCulture,
                                   DateTimeStyles.AdjustToUniversal
                                   | DateTimeStyles.AssumeUniversal, out var d))
            return d;

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                              DateTimeStyles.AdjustToUniversal
                              | DateTimeStyles.AssumeUniversal, out d))
            return d;

        /* „GMT" statt eines Versatzes kommt in älteren Feeds vor und lässt
           beide Versuche scheitern. */
        var ersetzt = s.Replace(" GMT", " +0000").Replace(" UT", " +0000");

        return DateTime.TryParse(ersetzt, CultureInfo.InvariantCulture,
                                 DateTimeStyles.AdjustToUniversal
                                 | DateTimeStyles.AssumeUniversal, out d)
            ? d : null;
    }

    /// <summary>Relative Adressen gegen die Feed-Adresse auflösen.</summary>
    private static string Absolut(string href, string feedUrl)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out _)) return href;

        return Uri.TryCreate(feedUrl, UriKind.Absolute, out var basis)
            && Uri.TryCreate(basis, href, out var voll)
            ? voll.ToString() : href;
    }

    /// <summary>HTML-Reste aus Titel und Zusammenfassung nehmen.</summary>
    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");

        return s.Trim();
    }
}
