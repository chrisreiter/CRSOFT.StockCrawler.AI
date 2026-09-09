using System.Text;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>Ein Abschnitt des Tagesjournals.</summary>
public sealed record JournalSection(string Heading, string Body, string? Note = null);

/// <summary>Das Journal eines Tages.</summary>
public sealed record Journal(
    DateTime ForDateUtc,
    string Title,
    string Lead,
    IReadOnlyList<JournalSection> Sections,
    string Markdown,
    IReadOnlyList<string> Sources);

public interface IJournalService
{
    Task<Journal> BuildAsync(
        IReadOnlyDictionary<string, int> pillarWeights,
        int[]? assetIds, CancellationToken ct = default);
}

/// <summary>
/// Schreibt das Tagesjournal: was sich bewegt hat, was darüber berichtet wurde
/// und was die Messung dazu hergibt.
///
/// <b>Warum kein Sprachmodell.</b> Es wäre naheliegend, die Bausteine an ein
/// Modell zu geben und einen flüssigen Text zurückzubekommen. Genau das wurde
/// in dieser Sitzung geprüft und verworfen: Ein kleineres Modell las ein
/// Fehlerverhältnis von 1,0034 als „nahezu perfekt" und behauptete das Gegenteil
/// der Werkzeugausgabe. Ein Journal, das man einem Blog vorwirft, muss
/// nachrechenbar bleiben — jede Zahl darin stammt aus einer Abfrage, und der
/// Satz drumherum ist Vorlage, nicht Erzeugnis.
///
/// <b>Warum keine Handelsempfehlung.</b> Nicht aus Vorsicht, sondern weil die
/// Messung sie nicht trägt: Kein Verfahren dieser Anwendung schlägt im
/// Sperrbereich die blosse Drift. Das Journal sagt, was war und was gemessen
/// wurde. Was man daraus macht, ist die Entscheidung dessen, der es liest.
///
/// <b>Markdown als Ausgabeformat.</b> Es lässt sich anzeigen, kopieren und
/// unverändert in ein Blog stellen — und es bleibt lesbar, wenn niemand es
/// rendert.
/// </summary>
public sealed class JournalService(
    ISqlConnectionFactory factory,
    IAssetRepository assets,
    IPriceBarRepository bars,
    ICurveDiscussionService curve,
    IDeepForecastService deep,
    IKnowledgeService knowledge,
    ICrossingOpportunityService kreuzungen) : IJournalService
{
    public async Task<Journal> BuildAsync(
        IReadOnlyDictionary<string, int> pillarWeights,
        int[]? assetIds, CancellationToken ct = default)
    {
        var heute = DateTime.UtcNow;

        var alle = await assets.GetTrackedAsync(ct);

        var beobachtet = assetIds is { Length: > 0 }
            ? alle.Where(a => assetIds.Contains(a.AssetId)).ToList()
            : alle.ToList();

        await using var conn = await factory.OpenAsync(ct);

        var abschnitte = new List<JournalSection>();
        var quellen = new List<string>();

        // ------------------------------------------------------- Kurse ------

        var (kursText, kursNotiz, bewegungen) =
            await KurseAsync(beobachtet, ct);

        abschnitte.Add(new JournalSection("Die Kurse", kursText, kursNotiz));

        // ------------------------------------------------- Auffälligkeiten --

        abschnitte.Add(await StellenAsync(beobachtet, ct));

        // ------------------------------------------------- Umschichtung -----

        abschnitte.Add(await UmschichtungAsync(ct));

        // ------------------------------------------------- Nachrichtenlage --

        var (nachrichten, quellenListe) = await NachrichtenAsync(conn, bewegungen, ct);

        abschnitte.Add(nachrichten);
        quellen.AddRange(quellenListe);

        // ------------------------------------------------------- Modelle ----

        abschnitte.Add(Modelle(pillarWeights));

        // ------------------------------------------------------ Einordnung --

        abschnitte.Add(Einordnung());

        var lead = Lead(bewegungen, beobachtet.Count);

        var titel = $"Marktjournal {heute:dd.MM.yyyy}";

        return new Journal(heute, titel, lead, abschnitte,
                           AlsMarkdown(titel, heute, lead, abschnitte, quellen),
                           quellen);
    }

    // ----------------------------------------------------------------------

    /// <summary>
    /// Wo die relative Stärke gekippt ist — und was davon nach Abzug der
    /// Handelskosten übrig bleibt.
    ///
    /// <para><b>Warum die Kostenzeile nicht wegzulassen ist.</b> Gemessen an
    /// diesem Datenbestand bestehen 54 von 154 Paaren mit Vorgeschichte die
    /// Trefferquote von 55 Prozent — aber nur 42 davon bringen im Mittel mehr
    /// ein, als der Tausch kostet. Zwölf Paare treffen häufiger als der Zufall
    /// UND verlieren Geld; ETHFI-USD gegen TLH trifft in 59 Prozent der Fälle
    /// und bringt im Mittel −1,17 Prozent. Das ist die schiefe Verteilung, vor
    /// der jede Strategiedarstellung warnt: viele kleine Gewinne, seltene
    /// große Verluste. Ein Journal, das nur die Trefferquote nennt, empfiehlt
    /// zuverlässig solche Paare.</para>
    /// </summary>
    private async Task<JournalSection> UmschichtungAsync(CancellationToken ct)
    {
        var u = await kreuzungen.BuildAsync("1d", 14, 20, 40, false, 2, ct);

        var mitRueckhalt = u.Zeilen.Where(z => z.Bewaehrt).Take(6).ToList();

        var knappVerfehlt = u.Zeilen.Count(z =>
            z.HistorischeTrefferquote >= 0.55 && z.MittelgewinnNachKosten <= 0);

        var sb = new StringBuilder();

        sb.Append("Eine Kreuzung heißt nicht, dass ein Wert steigt — sie heißt, dass er einen ")
          .Append("anderen überholt hat. Gerechnet wird deshalb als Paar: Rendite der oberen ")
          .Append("Seite minus Rendite der unteren.\r\n\r\n");

        if (u.Zeilen.Count == 0)
        {
            sb.Append("In den letzten vierzehn Tagen ist nichts gekippt, was hier zu berichten wäre.");
            return new JournalSection("Wo umgeschichtet wurde", sb.ToString(),
                                      "Kein Befund ist auch ein Befund.");
        }

        sb.Append($"**{u.PaareGeprueft} Paare** hatten in den letzten vierzehn Tagen eine ")
          .Append("Kreuzung. Davon halten ");

        if (mitRueckhalt.Count == 0)
        {
            sb.Append("**keine** dem doppelten Test stand: Trefferquote über dem Münzwurf ")
              .Append("UND mittlerer Ertrag über den Handelskosten.\r\n\r\n")
              .Append("Was heute oben in der Rangliste steht, ist damit ausschließlich ")
              .Append("Vergangenheit — die Bewegung ist gelaufen, bevor sie sichtbar wurde.");
        }
        else
        {
            sb.Append($"**{mitRueckhalt.Count}** dem doppelten Test stand — Trefferquote über ")
              .Append("dem Münzwurf UND mittlerer Ertrag über den Handelskosten:\r\n\r\n");

            foreach (var z in mitRueckhalt)
                sb.Append($"- **{z.SymbolKaufen}** hat **{z.SymbolVerkaufen}** überholt ")
                  .Append($"({z.Kreuzung:dd.MM.}, vor {z.TageSeither} Tagen). Seither ")
                  .Append($"{z.Paargewinn * 100:+0.0;-0.0} Prozent Unterschied. ")
                  .Append($"Frühere Kreuzungen dieses Paares: {z.HistorischeKreuzungen} Stück, ")
                  .Append($"{z.HistorischeTrefferquote * 100:0} Prozent getroffen, ")
                  .Append($"im Mittel {z.MittelgewinnNachKosten * 100:+0.0;-0.0} Prozent nach Kosten.\r\n");
        }

        sb.Append($"\r\nGerechnet gegen **{u.Kostenhinweis}**. ");

        if (knappVerfehlt > 0)
            sb.Append(knappVerfehlt == 1
                          ? "**Ein Paar** besteht die Trefferquote und scheitert "
                          : $"**{knappVerfehlt} Paare** bestehen die Trefferquote und scheitern ")
              .Append("trotzdem: Der mittlere Ertrag liegt unter den Kosten. Eine hohe ")
              .Append("Trefferquote bei kleinen Gewinnen und seltenen großen Verlusten ist ")
              .Append("kein Vorteil, sondern die häufigste Art, sich selbst zu betrügen.");

        if (u.Ausgelassen.Count > 0)
            sb.Append(u.Ausgelassen.Count == 1
                          ? " Ein Paar wurde wegen eines Kurssprungs "
                          : $" {u.Ausgelassen.Count} Paare wurden wegen eines Kurssprungs ")
              .Append("ausgelassen — dort ist entweder ein Split oder ein Datenfehler im Spiel.");

        return new JournalSection("Wo umgeschichtet wurde", sb.ToString(),
            "Kreuzungen und Bewährung stammen aus getrennten Zeiträumen: Die Bewährung "
            + "misst ausschließlich Kreuzungen VOR dem Anzeigefenster, sonst benotete "
            + "sich jede Zeile selbst.");
    }

    private async Task<(string Text, string Notiz, List<(string Symbol, string? Name, double Pct)> Bewegungen)>
        KurseAsync(IReadOnlyList<Core.Models.Asset> beobachtet, CancellationToken ct)
    {
        var von = DateTime.UtcNow.AddDays(-12);
        var b = new List<(string Symbol, string? Name, double Pct)>();

        foreach (var a in beobachtet.Take(80))
        {
            var reihe = await bars.GetAsync(a.AssetId, BarInterval.Daily, von, DateTime.UtcNow, ct);
            if (reihe.Count < 2) continue;

            var letzte = reihe[^1];
            var davor = reihe[^2];

            if (davor.Close <= 0) continue;

            b.Add((a.Symbol, a.Name, (double)(letzte.Close / davor.Close - 1) * 100));
        }

        if (b.Count == 0)
            return ("Keine Kursdaten für den betrachteten Bestand.", "", b);

        var auf = b.OrderByDescending(x => x.Pct).Take(4).ToList();
        var ab = b.OrderBy(x => x.Pct).Take(4).ToList();

        var mittel = b.Average(x => x.Pct);
        var breite = b.Count(x => x.Pct > 0) * 100.0 / b.Count;

        var sb = new StringBuilder();

        sb.AppendLine($"Über {b.Count} beobachtete Werte lag die Veränderung zum Vortag im "
                    + $"Mittel bei {mittel:+0.00;-0.00} Prozent. "
                    + $"{breite:F0} Prozent der Werte schlossen höher als am Vortag.");

        sb.AppendLine();

        sb.AppendLine("**Am stärksten zugelegt:** " + string.Join(", ",
            auf.Select(x => $"{x.Symbol} {x.Pct:+0.0;-0.0} %")) + ".");

        sb.AppendLine();

        sb.AppendLine("**Am stärksten verloren:** " + string.Join(", ",
            ab.Select(x => $"{x.Symbol} {x.Pct:+0.0;-0.0} %")) + ".");

        /* Die Breite ist die eigentliche Aussage, nicht der Mittelwert.

           Ein Mittel von null bei achtzig Prozent Gewinnern heißt etwas völlig
           anderes als eines bei zwanzig — im ersten Fall zieht ein einzelner
           Einbruch die Zahl, im zweiten trägt der Markt insgesamt nicht. */
        var notiz = breite > 70
            ? "Breit getragene Aufwärtsbewegung — die Mehrheit der Werte trägt sie, "
            + "nicht einzelne Ausreißer."
            : breite < 30
                ? "Breite Abwärtsbewegung. Auch hier gilt: Der Mittelwert allein sagt wenig, "
                + "die Breite sagt, ob es den ganzen Markt betrifft."
                : "Gemischtes Bild — der Mittelwert verdeckt hier mehr, als er zeigt.";

        return (sb.ToString().TrimEnd(), notiz, b);
    }

    private async Task<JournalSection> StellenAsync(
        IReadOnlyList<Core.Models.Asset> beobachtet, CancellationToken ct)
    {
        var lauf = await curve.LatestCausalRunAsync(ct);

        if (lauf is null)
            return new JournalSection(
                "Auffällige Stellen",
                "Es liegt kein kausal gerechneter Kurvenlauf vor. Ein zentriert geglätteter "
                + "kann die jüngsten Tage grundsätzlich nicht bewerten — sein Fenster reicht "
                + "dort über das Ende der Reihe hinaus.");

        var seite = await curve.PageAsync(
            lauf, 1, 60, null, null, null, 75, DateTime.UtcNow.AddDays(-7), null, "stufe", ct);

        var ids = beobachtet.Select(a => a.AssetId).ToHashSet();

        var gebuendelt = seite.Rows
            .Where(r => ids.Contains(r.AssetId))
            .GroupBy(r => (r.AssetId, r.TsUtc.Date))
            .Select(g => g.OrderByDescending(x => x.Severity).First())
            .OrderByDescending(r => r.Severity)
            .Take(5)
            .ToList();

        if (gebuendelt.Count == 0)
            return new JournalSection(
                "Auffällige Stellen",
                "In den letzten sieben Tagen fand die Kurvendiskussion nichts über Stufe 75. "
                + "Das ist ein Ergebnis, kein Fehler.");

        var sb = new StringBuilder();

        sb.AppendLine("Die Kurvendiskussion bewertet jede Reihe an ihrer **eigenen** üblichen "
                    + "Schwankung, nicht in Prozent. Stufe 100 heißt: außergewöhnlich für "
                    + "diesen Wert — bei einem Anleihen-ETF können das 0,3 Prozent sein.");

        sb.AppendLine();

        foreach (var r in gebuendelt)
            sb.AppendLine($"- **{r.Symbol}**, {r.TsUtc:dd.MM.}: {Bezeichnung(r.EventType, r.Sign)}, "
                        + $"Stufe {r.Severity:F0}. Kurs {Kurs(r.ClosePrice)}, "
                        + $"Steigung {(Math.Exp(r.Slope) - 1) * 100:+0.00;-0.00} Prozent je Tag.");

        return new JournalSection("Auffällige Stellen", sb.ToString().TrimEnd(),
            "Kausal gerechnet — nur mit Daten, die zum jeweiligen Zeitpunkt vorlagen.");
    }

    private async Task<(JournalSection Abschnitt, List<string> Quellen)> NachrichtenAsync(
        Microsoft.Data.SqlClient.SqlConnection conn,
        List<(string Symbol, string? Name, double Pct)> bewegungen,
        CancellationToken ct)
    {
        var neu = (await conn.QueryAsync<(string Title, string Origin, DateTime? Published, string? Region)>(
            new CommandDefinition(
                """
                /* Je Schlagzeile ein Eintrag.

                   Dieselbe Meldung erscheint in mehreren Feeds -- Reuters
                   liefert sie, MarketWatch uebernimmt sie, Yahoo spiegelt
                   beide. Ungefiltert stand sie dreimal im Journal, und das
                   liest sich wie ein Fehler, weil es einer ist. */
                WITH einmalig AS (
                    SELECT title, origin, published_utc, region,
                           ROW_NUMBER() OVER (PARTITION BY title
                                              ORDER BY published_utc DESC) AS rn
                      FROM dbo.knowledge_source
                     WHERE pillar = 'semantic' AND kind = 'article'
                       AND published_utc IS NOT NULL
                       AND published_utc > DATEADD(HOUR, -36, SYSUTCDATETIME())
                )
                SELECT TOP 12 title, origin, published_utc, region
                  FROM einmalig WHERE rn = 1
                 ORDER BY published_utc DESC;
                """, cancellationToken: ct))).ToList();

        var quellen = new List<string>();

        if (neu.Count == 0)
            return (new JournalSection(
                "Was berichtet wurde",
                "In den letzten 36 Stunden wurden keine Meldungen eingelesen. "
                + "Läuft der Zeitplan?"), quellen);

        var sb = new StringBuilder();

        sb.AppendLine($"{neu.Count} Meldungen aus den letzten 36 Stunden, "
                    + $"neueste von {neu[0].Published:dd.MM. HH:mm} UTC.");

        sb.AppendLine();

        foreach (var n in neu.Take(8))
        {
            sb.AppendLine($"- *[{n.Region ?? "?"}]* {n.Title}");
            quellen.Add(n.Origin);
        }

        /* Zu den größten Bewegern wird gezielt gesucht.

           Das ist die einzige Stelle, an der Text und Kurs im Journal
           aufeinandertreffen — und sie ist ausdrücklich keine Erklärung. Dass
           zu einem Wert etwas geschrieben wurde, sagt nicht, dass es die
           Bewegung verursacht hat. Die Reihenfolge ist meist umgekehrt. */
        var groesste = bewegungen
            .OrderByDescending(b => Math.Abs(b.Pct))
            .Take(2)
            .ToList();

        foreach (var g in groesste)
        {
            var frage = g.Name ?? g.Symbol;

            var roh = await knowledge.SearchAsync("semantic", frage, 4, ct);

            /* Eine Schwelle, und zwar eine hohe.

               Die Ähnlichkeitssuche liefert IMMER etwas -- sie ordnet nach
               Nähe, nicht nach Eignung. Bei 421 Meldungen findet sie zu
               „Southern Copper“ den nächstgelegenen Text, und das war eine
               Analyse über Konsumgüter-ETFs. Im Journal stand damit eine
               Fundstelle, die mit dem Wert nichts zu tun hat -- und das ist
               schlimmer als gar keine, weil sie einen Zusammenhang behauptet.

               0,55 ist streng: Unterhalb davon steht der Text zufällig dort. */
            var treffer = roh.Where(t => t.Score >= 0.55).Take(2).ToList();

            sb.AppendLine();

            if (treffer.Count == 0)
            {
                sb.AppendLine($"**Zu {g.Symbol} ({g.Pct:+0.0;-0.0} %)** fand die "
                            + "Ähnlichkeitssuche nichts Passendes"
                            + (roh.Count > 0
                                ? $" — der nächstgelegene Text lag bei {roh[0].Score:F2} "
                                + "und damit unter der Schwelle."
                                : "."));
                continue;
            }

            sb.AppendLine($"**Zu {g.Symbol} ({g.Pct:+0.0;-0.0} %)** fand die "
                        + "Ähnlichkeitssuche:");

            foreach (var t in treffer)
            {
                var text = t.Content.Length > 260 ? t.Content[..260] + " …" : t.Content;

                sb.AppendLine($"- *{t.Title}* (Ähnlichkeit {t.Score:F2}) — "
                            + text.Replace("\n", " "));

                quellen.Add(t.Origin);
            }
        }

        return (new JournalSection("Was berichtet wurde", sb.ToString().TrimEnd(),
            "Fundstellen aus der Ähnlichkeitssuche. Dass zu einem Wert etwas geschrieben "
            + "wurde, erklärt seine Bewegung nicht — meist ist der Kurs schneller als die "
            + "Meldung."), quellen);
    }

    private JournalSection Modelle(IReadOnlyDictionary<string, int> gewichte)
    {
        var sb = new StringBuilder();

        if (!deep.IsLoaded)
        {
            sb.AppendLine("Kein Deep-Learning-Modell geladen.");
        }
        else
        {
            sb.AppendLine("| Band | Horizonte | Fehlerverhältnis | Richtung | Drift schlägt |");
            sb.AppendLine("| --- | --- | ---: | ---: | --- |");

            foreach (var m in deep.Models)
                sb.AppendLine($"| {m.Band} | {string.Join(", ", m.Horizons)} Tage | "
                            + $"{m.TestErrorRatio:F4} | {m.TestHitRate * 100:F1} % | "
                            + (m.CarriesAny() ? "**ja**" : "nein") + " |");

            sb.AppendLine();

            sb.AppendLine("Ein Fehlerverhältnis unter 1 heißt: näher an der Wirklichkeit als "
                        + "die Annahme, der Kurs bleibe stehen. Die **zweite** Latte ist die "
                        + "blosse Drift — die mittlere Rendite des Trainingszeitraums, eine "
                        + "einzige Zahl. Über ein Jahr steigen Aktien im Mittel; wer nur "
                        + "„aufwärts“ sagt, schlägt den Stillstand zwangsläufig.");
        }

        sb.AppendLine();

        sb.AppendLine("**Eingestellte Gewichte:** " + string.Join(", ",
            gewichte.Where(g => g.Value > 0).Select(g => $"{g.Key} {g.Value}")));

        return new JournalSection("Was die Modelle sagen", sb.ToString().TrimEnd(),
            "Alle Zahlen aus dem Sperrbereich — Daten, die das Training nie gesehen hat.");
    }

    private static JournalSection Einordnung() => new(
        "Einordnung",
        "Dieses Journal beschreibt, was war und was gemessen wurde. Es enthält **keine "
        + "Handelsempfehlung**, und das ist keine Vorsicht, sondern der Stand der Messung: "
        + "Kein Verfahren dieser Anwendung schlägt im Sperrbereich die blosse Drift.\n\n"
        + "Drei Befunde stützen dieselbe Aussage aus verschiedenen Richtungen. Der "
        + "gemeinsame Marktmodus erklärt 53 Prozent der Bewegung bei einer Phasenstreuung "
        + "von 2,8 Bars. Der Querschnitt aller Kurse erklärt die Bewegung eines einzelnen am "
        + "**selben** Tag mit einem Bestimmtheitsmaß von 0,355 — einen Tag voraus bleiben "
        + "0,0039 davon übrig. Und die Kurvendiskussion findet Paare mit dem Faktor 9 über "
        + "der Erwartung, bei einem Median-Abstand von null Tagen.\n\n"
        + "Kurse bewegen sich **gemeinsam**, nicht **nacheinander**. Wo kein Vorlauf ist, "
        + "ist nichts vorherzusagen.");

    private static string Lead(
        List<(string Symbol, string? Name, double Pct)> b, int werte)
    {
        if (b.Count == 0) return $"{werte} Werte beobachtet, keine Kursdaten.";

        var mittel = b.Average(x => x.Pct);
        var breite = b.Count(x => x.Pct > 0) * 100.0 / b.Count;
        var staerkste = b.OrderByDescending(x => Math.Abs(x.Pct)).First();

        return $"{b.Count} Werte, im Mittel {mittel:+0.00;-0.00} Prozent zum Vortag, "
             + $"{breite:F0} Prozent im Plus. Die größte Einzelbewegung: "
             + $"{staerkste.Symbol} mit {staerkste.Pct:+0.0;-0.0} Prozent.";
    }

    private static string AlsMarkdown(
        string titel, DateTime tag, string lead,
        IReadOnlyList<JournalSection> abschnitte, IReadOnlyList<string> quellen)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {titel}");
        sb.AppendLine();
        sb.AppendLine($"*Stand: {tag:dd.MM.yyyy HH:mm} UTC*");
        sb.AppendLine();
        sb.AppendLine(lead);
        sb.AppendLine();

        foreach (var a in abschnitte)
        {
            sb.AppendLine($"## {a.Heading}");
            sb.AppendLine();
            sb.AppendLine(a.Body);

            if (!string.IsNullOrWhiteSpace(a.Note))
            {
                sb.AppendLine();
                sb.AppendLine($"> {a.Note}");
            }

            sb.AppendLine();
        }

        if (quellen.Count > 0)
        {
            sb.AppendLine("## Quellen");
            sb.AppendLine();

            foreach (var q in quellen.Distinct().Take(20))
                sb.AppendLine($"- {q}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Ein Kurs mit bedeutsamen Stellen statt fester Nachkommastellen.
    ///
    /// Vier Nachkommastellen sind für eine Aktie richtig und für einen
    /// Kryptowert falsch: SHIB stand mit „0,0000" im Journal, was gar nichts
    /// sagt. Die Zahl der Stellen muss sich nach der Größenordnung richten,
    /// nicht nach einem festen Format.
    /// </summary>
    private static string Kurs(decimal k)
    {
        var a = Math.Abs(k);

        /* Kein G-Format für sehr kleine Zahlen.

           `G4` liefert dort „5E-06“, und das liest niemand als Kurs. Bei
           Kryptowerten mit sechs Nullen nach dem Komma sind zehn
           Nachkommastellen die ehrlichere Anzeige — lang, aber lesbar. */
        return a >= 100 ? k.ToString("N2")
             : a >= 1 ? k.ToString("N4")
             : a >= 0.001m ? k.ToString("N6")
             : k.ToString("N10").TrimEnd('0') + "0";
    }

    private static string Bezeichnung(string type, int sign) => type switch
    {
        "hochpunkt" => "Hochpunkt",
        "tiefpunkt" => "Tiefpunkt",
        "wendepunkt" => sign >= 0 ? "Wendepunkt im Aufwärtstrend" : "Wendepunkt im Abwärtstrend",
        "sattelpunkt" => "Sattelpunkt",
        "steigungsausbruch" => sign >= 0 ? "Steigungsausbruch aufwärts" : "Steigungsausbruch abwärts",
        "kruemmungsausbruch" => sign >= 0 ? "Beschleunigung" : "Abbremsen",
        "sprung" => sign >= 0 ? "Ausreißer über der Glättung" : "Ausreißer unter der Glättung",
        _ => type
    };
}
