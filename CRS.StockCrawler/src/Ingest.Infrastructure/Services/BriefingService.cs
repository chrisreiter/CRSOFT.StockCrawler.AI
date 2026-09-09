using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>Ein Punkt der Tagesübersicht.</summary>
/// <param name="Kind">Womit man ihn einordnet: <c>befund</c>, <c>warnung</c>,
/// <c>wartung</c> oder <c>lage</c>.</param>
/// <param name="Weight">
/// Wie ernst er zu nehmen ist, 0 bis 100. Nicht wie auffällig er aussieht,
/// sondern was die Messung dahinter hergibt.
/// </param>
public sealed record BriefingItem(
    string Kind, string Pillar, string Title, string Detail, double Weight,
    string? Symbol = null, string? Link = null);

/// <summary>Die Tagesübersicht mit ihren Quellen.</summary>
public sealed record Briefing(
    DateTime GeneratedUtc,
    IReadOnlyList<BriefingItem> Items,
    IReadOnlyList<string> Caveats,
    IReadOnlyDictionary<string, int> PillarWeights,
    string Summary);

public interface IBriefingService
{
    Task<Briefing> BuildAsync(
        IReadOnlyDictionary<string, int> pillarWeights,
        int[]? assetIds, CancellationToken ct = default);
}

/// <summary>
/// Stellt zusammen, was der heutige Tag an Beobachtungen hergibt.
///
/// <b>Was das ist und was nicht.</b> Es ist eine Lagemeldung über den Zustand
/// des Systems und über das, was heute in den Daten auffällt. Es ist
/// <i>keine</i> Anlageempfehlung, und es sagt niemandem, was er kaufen soll.
/// Der Unterschied ist nicht juristische Vorsicht, sondern der Stand der
/// Messung: Diese Anwendung hat bis heute kein Verfahren hervorgebracht, das im
/// Sperrbereich besser wäre als die blosse Drift. Eine Kaufempfehlung wäre eine
/// Behauptung ohne Deckung.
///
/// <b>Warum jeder Punkt ein Gewicht trägt.</b> Ohne Gewicht steht ein
/// Kurvenereignis der Stufe 100 neben einer Modellvorhersage, die nachweislich
/// nichts taugt, und beide sehen gleich wichtig aus. Das Gewicht kommt aus zwei
/// Größen: der Auffälligkeit des Einzelfalls und dem gemessenen Wert der Säule,
/// aus der er stammt. Eine Säule mit Gewicht null liefert deshalb Punkte mit
/// Gewicht null — sie erscheinen, aber sie drängen sich nicht auf.
/// </summary>
public sealed class BriefingService(
    ISqlConnectionFactory factory,
    IAssetRepository assets,
    IPriceBarRepository bars,
    IDeepForecastService deep,
    ICurveDiscussionService curve) : IBriefingService
{
    public async Task<Briefing> BuildAsync(
        IReadOnlyDictionary<string, int> pillarWeights,
        int[]? assetIds, CancellationToken ct = default)
    {
        var items = new List<BriefingItem>();
        var caveats = new List<string>();

        int Gewicht(string saeule) =>
            pillarWeights.TryGetValue(saeule, out var w) ? Math.Clamp(w, 0, 100) : 0;

        var alle = await assets.GetTrackedAsync(ct);

        var beobachtet = assetIds is { Length: > 0 }
            ? alle.Where(a => assetIds.Contains(a.AssetId)).ToList()
            : alle.ToList();

        await using var conn = await factory.OpenAsync(ct);

        // ------------------------------------------------------- Lage ------

        await LageAsync(conn, items, beobachtet, ct);

        // -------------------------------------------- Kurvendiskussion -----

        await KurvenAsync(items, beobachtet, Gewicht("math"), ct);

        // ------------------------------------------------- Nachrichten -----

        await NachrichtenAsync(conn, items, Gewicht("semantic"), ct);

        // ------------------------------------------------------ Modell -----

        Modelle(items, caveats, Gewicht("deep"));

        // ----------------------------------------------------- Wartung -----

        await WartungAsync(conn, items, ct);

        /* Die Einschränkungen stehen nicht am Ende als Kleingedrucktes,
           sondern werden mit der Übersicht ausgeliefert. Wer sie überliest,
           soll sie wenigstens gesehen haben. */
        caveats.Add("Dies ist eine Lagemeldung, keine Anlageempfehlung. "
                  + "Kein Verfahren dieser Anwendung schlägt im Sperrbereich die "
                  + "blosse Drift — die mittlere Rendite des Trainingszeitraums, "
                  + "eine einzige Zahl.");

        caveats.Add("Gleichzeitigkeit ist kein Vorlauf. Kurse bewegen sich gemeinsam, "
                  + "nicht nacheinander: gemeinsamer Modus 53 % bei 2,8 Bars "
                  + "Phasenstreuung, Querschnitt R² 0,355 gleichzeitig gegen 0,0039 "
                  + "einen Tag voraus.");

        var sortiert = items.OrderByDescending(i => i.Weight).ToList();

        return new Briefing(
            DateTime.UtcNow,
            sortiert,
            caveats,
            pillarWeights.ToDictionary(k => k.Key, v => v.Value),
            Zusammenfassung(sortiert, beobachtet.Count));
    }

    // ---------------------------------------------------------------------

    /// <summary>Was die verfolgten Werte gestern gemacht haben.</summary>
    private async Task LageAsync(
        Microsoft.Data.SqlClient.SqlConnection conn, List<BriefingItem> items,
        IReadOnlyList<Core.Models.Asset> beobachtet, CancellationToken ct)
    {
        var von = DateTime.UtcNow.AddDays(-10);

        var bewegungen = new List<(string Symbol, double Pct, decimal Close)>();

        foreach (var a in beobachtet.Take(60))
        {
            var reihe = await bars.GetAsync(a.AssetId, BarInterval.Daily, von, DateTime.UtcNow, ct);
            if (reihe.Count < 2) continue;

            var letzte = reihe[^1];
            var davor = reihe[^2];

            if (davor.Close <= 0) continue;

            bewegungen.Add((a.Symbol,
                (double)(letzte.Close / davor.Close - 1) * 100,
                letzte.Close));
        }

        if (bewegungen.Count == 0) return;

        var auf = bewegungen.OrderByDescending(b => b.Pct).Take(3).ToList();
        var ab = bewegungen.OrderBy(b => b.Pct).Take(3).ToList();

        items.Add(new BriefingItem(
            "lage", "kurse", "Bewegung seit dem Vortag",
            "Stärkste Zunahme: " + string.Join(", ",
                auf.Select(b => $"{b.Symbol} {b.Pct:+0.0;-0.0} %")) +
            " · Stärkste Abnahme: " + string.Join(", ",
                ab.Select(b => $"{b.Symbol} {b.Pct:+0.0;-0.0} %")),
            40));
    }

    /// <summary>Auffällige Stellen der letzten Tage aus dem jüngsten Lauf.</summary>
    private async Task KurvenAsync(
        List<BriefingItem> items, IReadOnlyList<Core.Models.Asset> beobachtet,
        int gewicht, CancellationToken ct)
    {
        var seit = DateTime.UtcNow.AddDays(-14);

        /* Nur ein KAUSALER Lauf kann über die letzten Tage etwas sagen.

           Ein zentriert geglätteter kann die letzten halbfenster Bars
           grundsätzlich nicht bewerten — sein Fenster reicht dort über das Ende
           der Reihe hinaus. Gemessen: zentrierter Lauf bis 11. August, jüngste
           Kursbar 22. August. Elf Tage Lücke, genau die halbe Fensterbreite.
           Ein frisch gerechneter zentrierter Lauf hilft dagegen nicht. */
        var lauf = await curve.LatestCausalRunAsync(ct);

        if (lauf is null)
        {
            items.Add(new BriefingItem(
                "wartung", "math", "Kein kausaler Kurvenlauf vorhanden",
                "Es gibt nur zentriert geglättete Läufe. Die können die jüngsten Tage "
                + "grundsätzlich nicht bewerten, weil ihr Fenster über das Ende der Reihe "
                + "hinausreicht. Unter „Mathematische Analyse“ einen Durchgang mit "
                + "„kausal glätten“ starten.",
                65));
            return;
        }

        var seite = await curve.PageAsync(
            lauf, 1, 40, null, null, null, 70, seit, null, "stufe", ct);

        if (seite.Rows.Count == 0)
        {
            items.Add(new BriefingItem(
                "wartung", "math", "Keine auffälligen Stellen in den letzten vierzehn Tagen",
                $"Der kausale Lauf {lauf} ist aktuell, findet aber nichts über Stufe 70. "
                + "Das ist ein Ergebnis, kein Fehler.",
                30));
            return;
        }

        var ids = beobachtet.Select(a => a.AssetId).ToHashSet();

        /* Ein Tag, ein Eintrag je Wert.

           Ein einzelner Einbruch löst mehrere Arten zugleich aus: Sprung,
           Steigungsausbruch und Abbremsen beschreiben dieselbe Bewegung von
           drei Seiten. Ungefiltert füllt ein Wert die halbe Übersicht und
           verdrängt die anderen. Gezeigt wird die stärkste Art; die übrigen
           stehen dahinter, damit nichts verschwiegen wird. */
        var gebuendelt = seite.Rows
            .Where(r => ids.Contains(r.AssetId))
            .GroupBy(r => (r.AssetId, r.TsUtc.Date))
            .Select(g => new
            {
                Stelle = g.OrderByDescending(x => x.Severity).First(),
                Weitere = g.OrderByDescending(x => x.Severity).Skip(1)
                           .Select(x => Bezeichnung(x.EventType, x.Sign)).ToList()
            })
            .OrderByDescending(x => x.Stelle.Severity)
            .Take(6);

        foreach (var b in gebuendelt)
        {
            var r = b.Stelle;
            /* Das Gewicht ist das Produkt aus Auffälligkeit und Säulengewicht.
               Eine Stufe von 100 in einer Säule mit Gewicht 0 ergibt 0 — sie
               steht in der Liste, aber ganz unten. */
            var w = r.Severity * gewicht / 100.0;

            items.Add(new BriefingItem(
                "befund", "math",
                $"{r.Symbol}: {Bezeichnung(r.EventType, r.Sign)}",
                $"{r.TsUtc:yyyy-MM-dd}, Stufe {r.Severity:F0} von 100, Kurs {r.ClosePrice:F4}, "
                + $"Steigung {(Math.Exp(r.Slope) - 1) * 100:+0.000;-0.000} % je Bar. "
                + "Gemessen an der üblichen Schwankung dieses Wertes, nicht in Prozent."
                + (b.Weitere.Count > 0
                    ? $" Am selben Tag ebenfalls: {string.Join(", ", b.Weitere)}."
                    : ""),
                Math.Round(w, 1), r.Symbol));
        }
    }

    /// <summary>Die jüngsten eingelesenen Meldungen.</summary>
    private async Task NachrichtenAsync(
        Microsoft.Data.SqlClient.SqlConnection conn, List<BriefingItem> items,
        int gewicht, CancellationToken ct)
    {
        var neu = (await conn.QueryAsync<(string Title, string Origin, DateTime? Published, string? Region)>(
            new CommandDefinition(
                """
                SELECT TOP 8 title, origin, published_utc, region
                  FROM dbo.knowledge_source
                 WHERE pillar = 'semantic' AND kind = 'article'
                   AND published_utc IS NOT NULL
                 ORDER BY published_utc DESC;
                """, cancellationToken: ct))).ToList();

        if (neu.Count == 0)
        {
            items.Add(new BriefingItem(
                "wartung", "semantic", "Keine Meldungen mit Zeitstempel",
                "Die Feeds haben noch nichts geliefert, oder der Zeitplan lief noch nicht. "
                + "Unter „Semantik“ lässt er sich von Hand anstoßen.",
                50));
            return;
        }

        var juengste = neu[0].Published!.Value;
        var alter = (DateTime.UtcNow - juengste).TotalHours;

        items.Add(new BriefingItem(
            "lage", "semantic",
            $"{neu.Count} jüngste Meldungen, neueste vor {alter:F0} Stunden",
            string.Join(" · ", neu.Take(5).Select(n =>
                $"[{n.Region ?? "?"}] {(n.Title.Length > 78 ? n.Title[..78] + "…" : n.Title)}")),
            Math.Round(35.0 * gewicht / 100.0, 1),
            null, neu[0].Origin));

        if (alter > 30)
            items.Add(new BriefingItem(
                "wartung", "semantic", "Nachrichten sind über einen Tag alt",
                $"Die neueste eingelesene Meldung stammt von {juengste:yyyy-MM-dd HH:mm} UTC. "
                + "Läuft der Zeitplan?",
                60));
    }

    /// <summary>Was die Modelle über sich selbst sagen.</summary>
    private void Modelle(List<BriefingItem> items, List<string> caveats, int gewicht)
    {
        if (!deep.IsLoaded)
        {
            items.Add(new BriefingItem(
                "wartung", "deep", "Kein Modell geladen",
                "Die Deep-Learning-Säule ist ohne Modell still.", 45));
            return;
        }

        var tragend = deep.Models.Where(m => m.CarriesAny()).ToList();

        if (tragend.Count == 0)
        {
            /* Das ist der wichtigste Satz der ganzen Übersicht, und er gehört
               nach oben, nicht ans Ende. Eine Säule, die Zahlen liefert und
               nachweislich nichts kann, ist gefährlicher als eine, die
               schweigt. */
            items.Add(new BriefingItem(
                "warnung", "deep", "Kein Deep-Learning-Band trägt",
                "Alle geladenen Bänder bleiben im Sperrbereich hinter der blossen Drift "
                + "zurück. Bei 250 Tagen kam das Modell auf Fehlerverhältnis 0,9604 und "
                + "64,3 % Richtung, die Drift auf 0,9139 und 75,1 % — das Netz war also "
                + "schlechter als eine einzige Zahl. Seine Vorhersagen sind keine "
                + "Handelsgrundlage.",
                90));

            caveats.Add($"Die Deep-Learning-Säule steht auf Gewicht {gewicht}, obwohl keines "
                      + "ihrer Bänder die Messlatte nimmt. Das Gewicht ist eine Einstellung, "
                      + "kein Befund.");
            return;
        }

        foreach (var m in tragend)
            items.Add(new BriefingItem(
                "befund", "deep", $"Band „{m.Band}“ trägt",
                $"Fehlerverhältnis {m.TestErrorRatio:F4}, Richtung {m.TestHitRate * 100:F1} % "
                + "im Sperrbereich — besser als Stillstand und Drift.",
                Math.Round(70.0 * gewicht / 100.0, 1)));
    }

    /// <summary>Was gepflegt werden müsste.</summary>
    private async Task WartungAsync(
        Microsoft.Data.SqlClient.SqlConnection conn, List<BriefingItem> items,
        CancellationToken ct)
    {
        var stumm = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*) FROM dbo.asset a
             WHERE a.is_tracked = 1
               AND (SELECT MAX(b.ts_utc) FROM dbo.price_bar b
                     WHERE b.asset_id = a.asset_id AND b.interval_code = '1d')
                   < DATEADD(DAY, -30, SYSUTCDATETIME());
            """, cancellationToken: ct));

        if (stumm > 0)
            items.Add(new BriefingItem(
                "wartung", "kurse", $"{stumm} verfolgte Werte ohne frische Kurse",
                "Seit über dreißig Tagen keine Tagesbar. Sie erscheinen in keinem Diagramm "
                + "und verfälschen jede Auswertung über „alle Werte“. Unter „Auswahl“ "
                + "lassen sie sich aus der Verfolgung nehmen.",
                50));

        var letzterLauf = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(finished_utc) FROM dbo.curve_run;", cancellationToken: ct));

        if (letzterLauf is null || letzterLauf < DateTime.UtcNow.AddDays(-7))
            items.Add(new BriefingItem(
                "wartung", "math", "Kurvendiskussion älter als eine Woche",
                letzterLauf is null
                    ? "Noch kein Durchgang gerechnet."
                    : $"Letzter Durchgang: {letzterLauf:yyyy-MM-dd}.",
                45));
    }

    private static string Zusammenfassung(IReadOnlyList<BriefingItem> items, int werte)
    {
        var warnungen = items.Count(i => i.Kind == "warnung");
        var wartung = items.Count(i => i.Kind == "wartung");
        var befunde = items.Count(i => i.Kind == "befund");

        return $"{werte} Werte beobachtet. {befunde} Beobachtungen, {warnungen} Warnungen, "
             + $"{wartung} Punkte zur Pflege. "
             + (warnungen > 0
                 ? "Die Warnungen zuerst lesen — sie betreffen die Belastbarkeit der Zahlen."
                 : "Nichts, was gegen die Belastbarkeit der Zahlen spricht.");
    }

    private static string Bezeichnung(string type, int sign) => type switch
    {
        "hochpunkt" => "Hochpunkt",
        "tiefpunkt" => "Tiefpunkt",
        "wendepunkt" => sign >= 0 ? "Wendepunkt im Aufwärtstrend" : "Wendepunkt im Abwärtstrend",
        "sattelpunkt" => "Sattelpunkt",
        "steigungsausbruch" => sign >= 0 ? "Steigungsausbruch aufwärts" : "Steigungsausbruch abwärts",
        "kruemmungsausbruch" => sign >= 0 ? "Beschleunigung" : "Abbremsen",
        /* Beim Sprung ist das Vorzeichen der Abstand zur geglätteten Kurve,
           nicht die Richtung des Trends. Ein Kurs kann über der Glättung
           liegen, während der Trend fällt — „Sprung nach oben“ las sich dann
           wie ein Widerspruch zur negativen Steigung daneben. */
        "sprung" => sign >= 0 ? "Ausreißer über der Glättung" : "Ausreißer unter der Glättung",
        _ => type
    };
}
