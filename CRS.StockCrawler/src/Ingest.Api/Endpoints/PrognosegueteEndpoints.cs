using Ingest.Infrastructure.Services;

using Microsoft.Extensions.Options;
using Ingest.Infrastructure.Options;
namespace Ingest.Api.Endpoints;

/// <summary>
/// Prognose gegen Ist — wie gut haben wir tatsächlich vorausgesagt?
///
/// <para>Der Regelkreis läuft stündlich: neue Bars holen, fällige Prognosen gegen den
/// eingetroffenen Kurs rechnen, mit dem neuesten Bar neu prognostizieren. Was fehlte, war
/// die Sicht darauf — das Delta je Bar und die Frage, welchen Wert wir am besten getroffen
/// haben.</para>
///
/// <para><b>Nur abgegebene Prognosen.</b> Der nachträglich gerechnete Walk-Forward bleibt
/// draußen; er kennt die Zukunft, die er vorhersagt.</para>
/// </summary>
public static class PrognosegueteEndpoints
{
    public static void MapPrognosegueteEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/guete").WithTags("Prognosegüte");

        g.MapGet("/rangliste", async (IPrognosegueteService svc,
                                      DateTime? von, int? horizont,
                                      int mindestens = 5, int limit = 50,
                                      CancellationToken ct = default) =>
        {
            /* 23.08.2026: der Tag, an dem die Aufzeichnung der Live-Daten begann. Davor
               gibt es nur nachträglich gerechnete Prognosen, und die gehören nicht in
               eine Auswertung, die Treffsicherheit belegen soll. */
            var start = von ?? new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);

            var liste = await svc.RanglisteAsync(start, horizont, mindestens, limit, ct);

            var traegt = liste.Count(x => x.Traegt);

            return Results.Ok(new
            {
                von = start,
                horizont,
                werte = liste.Select(x => new
                {
                    x.Symbol, x.Name, klasse = x.Klasse.ToString(), x.Bewertet,
                    x.MittlererFehlerPct, x.TrefferquotePct,
                    x.StillstandFehlerPct, x.Fehlerverhaeltnis, x.Traegt,
                    x.ErsteBewertung, x.LetzteBewertung
                }),

                bilanz = liste.Count == 0
                    ? "Noch nichts bewertet in diesem Zeitraum."
                    : $"{traegt} von {liste.Count} Werten schlagen den Stillstand "
                      + $"({Math.Round(100.0 * traegt / liste.Count, 1)} %). "
                      + $"Median des Fehlerverhältnisses: "
                      + $"{(liste.Count > 0 ? Math.Round(liste.Select(x => x.Fehlerverhaeltnis).OrderBy(v => v).ElementAt(liste.Count / 2), 3) : 0)}",

                /* Die Fallzahl gehört neben das Ergebnis, nicht in eine Fußnote. Am
                   25.08.2026 lagen je Wert zwei bis drei Beobachtungen vor -- die
                   Aufzeichnung lief seit dem 23. Ein Verhältnis von 0,18 an der Spitze
                   ist dann der obere Rand des Rauschens und kein Befund. */
                fallzahl = liste.Count == 0 ? null : new
                {
                    kleinste = liste.Min(x => x.Bewertet),
                    groesste = liste.Max(x => x.Bewertet),
                    warnung = liste.Max(x => x.Bewertet) < 20
                        ? $"Höchstens {liste.Max(x => x.Bewertet)} Beobachtungen je Wert. "
                          + "Über einzelne Werte sagt das nichts — bei so wenigen Fällen "
                          + "streut das Verhältnis von selbst über den ganzen Bereich. "
                          + "Aussagekräftig sind derzeit nur der Median und der Anteil, "
                          + "der den Stillstand schlägt."
                        : null
                },

                hinweis =
                    "Sortiert nach dem Fehlerverhältnis, nicht nach dem Fehler. Ein "
                    + "mittlerer Fehler von 1,2 % sagt für sich nichts: Hat sich der Kurs "
                    + "ohnehin nur um 0,9 % bewegt, war Stillhalten besser. Unter 1 heißt "
                    + "besser als nichts tun. Nach dem rohen Fehler zu sortieren spülte "
                    + "zuverlässig die ruhigsten Werte nach oben, nicht die am besten "
                    + "getroffenen."
            });
        });

        /* ------------------------------------------------- alle Horizonte --

           Ein Aufruf, sieben Auswertungen: je Horizont die Bilanz und die besten
           Treffer. Sinnvoll ist die Trennung, weil die Horizonte nicht vergleichbar
           sind -- eine Tagesprognose und eine Jahresprognose reifen verschieden
           schnell, und wer sie in eine Liste wirft, vergleicht Fallzahlen von drei
           mit Fallzahlen von null. */
        g.MapGet("/uebersicht", async (IPrognosegueteService svc,
                                       IOptions<IngestOptions> opt,
                                       DateTime? von, int mindestens = 2,
                                       int proHorizont = 10,
                                       CancellationToken ct = default) =>
        {
            var start = von ?? new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);

            var bilanzen = await svc.UebersichtAsync(
                start, opt.Value.EffectiveHorizons, mindestens, proHorizont, ct);

            var mitDaten = bilanzen.Where(b => b.Werte > 0).ToList();

            return Results.Ok(new
            {
                von = start,
                horizonte = bilanzen.Select(b => new
                {
                    b.HorizonHours, b.Label, b.Werte, b.Traegt, b.MedianVerhaeltnis,
                    b.KleinsteFallzahl, b.GroessteFallzahl,

                    anteilTraegt = b.Werte > 0
                        ? Math.Round(100.0 * b.Traegt / b.Werte, 1)
                        : 0.0,

                    /* Ein Horizont ohne Zeilen ist kein Fehler, sondern eine Tatsache:
                       Eine Jahresprognose ist frühestens nach einem Jahr nachprüfbar.
                       Ohne diesen Satz sieht die leere Zeile nach Datenverlust aus. */
                    hinweis = b.Werte == 0
                        ? $"Noch keine abgelaufene Prognose über {b.Label}. Eine solche "
                          + $"ist frühestens {b.Label} nach ihrer Abgabe nachprüfbar."
                        : b.GroessteFallzahl < 20
                            ? $"Höchstens {b.GroessteFallzahl} Beobachtungen je Wert — "
                              + "über einzelne Werte sagt das nichts. Belastbar sind hier "
                              + "nur Median und Anteil."
                            : null,

                    beste = b.Beste.Select(x => new
                    {
                        x.Symbol, x.Name, klasse = x.Klasse.ToString(), x.Bewertet,
                        x.MittlererFehlerPct, x.TrefferquotePct,
                        x.StillstandFehlerPct, x.Fehlerverhaeltnis, x.Traegt
                    })
                }),

                bilanz = mitDaten.Count == 0
                    ? "Für keinen Horizont liegen abgelaufene Prognosen vor."
                    : string.Join(" · ", mitDaten.Select(b =>
                        $"{b.Label}: {b.Traegt}/{b.Werte} über dem Stillstand, "
                        + $"Median {b.MedianVerhaeltnis:0.000}")),

                hinweis =
                    "Verglichen wird gegen den Stillstand: den Fehler, den „der Kurs "
                    + "bleibt, wo er ist“ gemacht hätte. Unter 1 heißt besser als nichts "
                    + "tun. Je Wert und Zieltag zählt genau eine Prognose — die zuletzt "
                    + "gestellte; ohne das zählen bei ruhigen Werten dieselben "
                    + "Beobachtungen mehrfach und täuschen Fallzahl vor."
            });
        });

        /* ------------------------------------------------------ Lernkurve --

           Dass die Rueckkopplung laeuft, steht in model_weight: Die Gewichte bewegen
           sich und folgen der Trefferquote. Ob die Prognose dadurch BESSER wird, ist
           damit nicht gesagt -- ein Verfahren kann fleissig lernen und trotzdem auf
           der Stelle treten, weil das Signal nicht da ist. Diese Kurve entscheidet
           das, und nur sie. */
        g.MapGet("/lernkurve", async (IPrognosegueteService svc, DateTime? von,
                                      int horizont = 24, CancellationToken ct = default) =>
        {
            var start = von ?? new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);

            var tage = await svc.LernkurveAsync(start, horizont, ct);

            /* Ein Trend braucht mehr als zwei Punkte. Bei dreien ist jede Aussage
               darueber Kaffeesatz -- also wird gesagt, dass es zu frueh ist, statt
               eine Steigung auszurechnen, die niemand ernst nehmen darf. */
            var genug = tage.Count >= 7;

            return Results.Ok(new
            {
                von = start,
                horizont,
                tage = tage.Select(t => new
                {
                    tag = t.Tag,
                    t.Werte, t.MedianVerhaeltnis, t.Traegt, t.MittlereTrefferquotePct,
                    anteilTraegt = t.Werte > 0 ? Math.Round(100.0 * t.Traegt / t.Werte, 1) : 0
                }),

                trend = !genug
                    ? null
                    : new
                    {
                        erste = Math.Round(tage.Take(tage.Count / 2)
                                               .Average(t => t.MedianVerhaeltnis), 4),
                        zweite = Math.Round(tage.Skip(tage.Count / 2)
                                                .Average(t => t.MedianVerhaeltnis), 4)
                    },

                hinweis = tage.Count == 0
                    ? "Noch keine abgelaufenen Prognosen für diesen Horizont."
                    : genug
                        ? "Fällt der Median über die Tage, wird die Prognose besser. "
                          + "Bleibt er über 1, ist sie weiterhin schlechter als "
                          + "Stillhalten — auch wenn sie sich verbessert."
                        : $"Erst {tage.Count} Tage. Für eine Aussage über einen Trend "
                          + "sind das zu wenige; die Kurve ist ab etwa einer Woche "
                          + "lesbar. Bis dahin zeigt sie nur den Stand."
            });
        });

        /* Bringt das Mischen etwas? Die Frage, die die zweite Bewertung überhaupt
           rechtfertigt -- ohne sie wäre die gemischte Zahl eine Behauptung. */
        g.MapGet("/mischvergleich", async (IPrognosegueteService svc, DateTime? von,
                                           CancellationToken ct = default) =>
        {
            var start = von ?? new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
            var z = await svc.MischvergleichAsync(start, ct);

            return Results.Ok(new
            {
                von = start,
                horizonte = z.Select(x => new
                {
                    x.HorizonHours, x.Verglichen,
                    x.FehlerSaeule1Pct, x.FehlerMischungPct,
                    x.RichtungSaeule1Pct, x.RichtungMischungPct, x.MischungBesser,

                    anteilBesser = x.Verglichen > 0
                        ? Math.Round(100.0 * x.MischungBesser / x.Verglichen, 1) : 0,

                    urteil = x.Verglichen < 30
                        ? "zu wenige Fälle für eine Aussage"
                        : x.FehlerMischungPct < x.FehlerSaeule1Pct
                            ? "Mischung ist genauer"
                            : x.FehlerMischungPct > x.FehlerSaeule1Pct
                                ? "Säule 1 allein ist genauer"
                                : "kein Unterschied"
                }),

                hinweis = z.Count == 0
                    ? "Noch keine Prognose, für die beide Bewertungen vorliegen. Die "
                      + "Mischung gibt es erst seit dem Einbau der Säulen; verglichen wird "
                      + "nur, wo beide Zahlen stehen."
                    : "Verglichen werden nur Prognosen, für die BEIDE Bewertungen "
                      + "vorliegen — sonst wäre der Unterschied der zwischen den "
                      + "Zeiträumen, nicht der zwischen den Verfahren."
            });
        });

        g.MapGet("/verlauf", async (IPrognosegueteService svc, string symbol,
                                    DateTime? von, int? horizont, int limit = 100,
                                    CancellationToken ct = default) =>
        {
            var start = von ?? new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);

            var zeilen = await svc.VerlaufAsync(symbol, start, horizont, limit, ct);

            return Results.Ok(new
            {
                symbol,
                von = start,
                zeilen = zeilen.Select(z => new
                {
                    z.GestelltUtc, z.ZielUtc, z.BewertetUtc, z.HorizonHours,
                    z.Basis, z.Prognose, z.Ist,
                    z.DeltaPct, z.AbsFehlerPct, z.BewegungPct, z.RichtungKorrekt,

                    /* Die eigentliche Frage je Bar: War die Prognose näher am
                       eingetroffenen Kurs als der Ausgangskurs? */
                    besserAlsStillstand = z.AbsFehlerPct < z.BewegungPct
                }),

                hinweis = zeilen.Count == 0
                    ? "Keine bewerteten Prognosen — entweder ist der Zeitraum zu jung oder "
                      + "der Horizont noch nicht abgelaufen."
                    : "Delta ist die Abweichung des eingetroffenen Bars von der Prognose. "
                      + "Bewegung ist, was der Kurs seit dem Ausgangspunkt gemacht hat — "
                      + "der Fehler, den Stillhalten gekostet hätte."
            });
        });
    }
}
