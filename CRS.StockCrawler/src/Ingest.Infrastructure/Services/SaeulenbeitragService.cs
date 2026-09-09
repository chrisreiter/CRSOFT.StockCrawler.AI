using Dapper;
using Ingest.Core.Analysis;
using Ingest.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>Was eine Säule für einen Wert und Horizont zu sagen hat.</summary>
/// <param name="Saeule">learning, deep, math, knowledge, semantic, flow.</param>
/// <param name="Rendite">Die vorhergesagte Log-Rendite über den Horizont.</param>
/// <param name="Verdienst">
/// Der <b>gemessene</b> Vorsprung gegenüber „der Kurs bleibt stehen", auf [0,1] gestaucht.
/// Null heisst: trägt nichts bei, egal wie hoch der Regler steht.
/// </param>
/// <param name="Begruendung">Woher die Zahl kommt — damit sie nachprüfbar bleibt.</param>
/// <summary>Wie eine Säule in die Mischung eingeht.</summary>
public enum Beitragsart
{
    /// <summary>
    /// Eine eigenständige Schätzung der Rendite. Wird mit den anderen Grundlagen
    /// gewichtet gemittelt.
    /// </summary>
    Grundlage,

    /// <summary>
    /// Ein Auf- oder Abschlag auf die Grundlage.
    ///
    /// <para><b>Warum diese Unterscheidung nötig ist.</b> Der erste Entwurf behandelte
    /// alle Säulen als gleichrangige Prognostiker und mittelte sie gewichtet. Das ging
    /// schief, sobald die Beträge weit auseinanderliegen: Bei FCX schätzte die erste
    /// Säule −15,7 % über einen Monat bei geringer Zuversicht, die Wissenssäule +0,25 %
    /// aus vier gemessenen Mustern — und weil deren Verdienst höher war, bekam die Zahl
    /// von einem Viertelprozent 72 % Gewicht und zog die Prognose auf −4,1 %. Ein
    /// Musterüberschuss ist aber keine Prognose des Kurses, sondern eine Aussage
    /// darüber, was ZUSÄTZLICH zum üblichen Gang zu erwarten ist. Er gehört
    /// aufaddiert, nicht gegengerechnet.</para>
    /// </summary>
    Aufschlag
}

/// <param name="Art">Grundlage oder Aufschlag — siehe <see cref="Beitragsart"/>.</param>
public readonly record struct Saeulenbeitrag(
    string Saeule, double Rendite, double Verdienst, string Begruendung,
    Beitragsart Art = Beitragsart.Grundlage);

/// <summary>Die Beiträge aller Säulen für einen Wert, je Horizont.</summary>
public sealed class Saeulenlage
{
    public Dictionary<int, List<Saeulenbeitrag>> JeHorizont { get; } = [];

    public void Fuege(int horizont, Saeulenbeitrag b)
    {
        if (!JeHorizont.TryGetValue(horizont, out var l))
            JeHorizont[horizont] = l = [];

        l.Add(b);
    }
}

public interface ISaeulenbeitragService
{
    /// <summary>
    /// Sammelt die Beiträge aller Säulen ausser <c>learning</c> für alle verfolgten Werte.
    ///
    /// <para><c>learning</c> bleibt draussen, weil es der Prognoselauf ohnehin gerade
    /// gerechnet hat — es noch einmal aus der Datenbank zu holen wäre dieselbe Zahl auf
    /// dem Umweg.</para>
    /// </summary>
    Task<IReadOnlyDictionary<int, Saeulenlage>> SammleAsync(
        IReadOnlyList<int> horizonte, CancellationToken ct = default);

    /// <summary>Die eingestellten Säulengewichte, 0 bis 100.</summary>
    Task<IReadOnlyDictionary<string, int>> GewichteAsync(CancellationToken ct = default);
}

/// <summary>
/// Holt die Zahlenbeiträge der übrigen Säulen — <b>in einem Zug für alle Werte</b>.
///
/// <para><b>Warum als Massenabfrage und nicht je Wert.</b> Der Prognoselauf geht über 600
/// verfolgte Werte. Je Wert eine eigene Abfrage je Säule wären mehrere tausend Fahrten zur
/// Datenbank für einen Lauf, der heute dreizehn Sekunden dauert. Gesammelt wird deshalb
/// einmal, und die Zuordnung geschieht im Speicher.</para>
///
/// <para><b>Der Verdienst kommt aus der Messung, nie aus der Einstellung.</b> Das ist die
/// Regel, an der dieses Projekt hängt: Der Regler in der Oberfläche bestimmt, WIE VIEL von
/// etwas Brauchbarem einfliesst — nicht, ob Unbrauchbares mitzählt. Eine Säule ohne
/// gemessenen Vorsprung bekommt null und bewegt nichts, auch wenn der Regler auf hundert
/// steht.</para>
/// </summary>
public sealed class SaeulenbeitragService : ISaeulenbeitragService
{
    private readonly ISqlConnectionFactory _factory;
    private readonly ILogger<SaeulenbeitragService> _log;

    public SaeulenbeitragService(ISqlConnectionFactory factory,
                                 ILogger<SaeulenbeitragService> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task<IReadOnlyDictionary<string, int>> GewichteAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var zeilen = await conn.QueryAsync<(string Pillar, int Weight)>(new CommandDefinition(
            "SELECT pillar, weight FROM dbo.pillar_weight", cancellationToken: ct));

        return zeilen.ToDictionary(z => z.Pillar, z => z.Weight, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<int, Saeulenlage>> SammleAsync(
        IReadOnlyList<int> horizonte, CancellationToken ct = default)
    {
        var lage = new Dictionary<int, Saeulenlage>();

        Saeulenlage Fuer(int assetId)
        {
            if (!lage.TryGetValue(assetId, out var l)) lage[assetId] = l = new Saeulenlage();
            return l;
        }

        await using var conn = await _factory.OpenAsync(ct);

        await WissenAsync(conn, horizonte, Fuer, ct);
        await SemantikAsync(conn, horizonte, Fuer, ct);

        return lage;
    }

    // ====================================================== Säule Wissen ====

    /// <summary>
    /// Die Muster, über die in der Literatur geschrieben wird — und was sie gemessen wert
    /// sind.
    ///
    /// <para><b>Warum ausgerechnet die Bot-Auslöser der Wissensbeitrag sind.</b> Die
    /// Wissenssäule sammelt Texte über Handelsstrategien: RSI-Schwellen, Bollinger-Bänder,
    /// gleitende Durchschnitte, das Goldene Kreuz. Diese Muster stehen nicht nur in den
    /// Texten, sie sind auch die einzigen daraus, die sich am Kurs prüfen lassen — und
    /// genau das ist in <c>bot_trigger_stat</c> geschehen: je Auslöser der Median-Ertrag
    /// nach 1, 5 und 20 Tagen, <b>gegen eine Grundlinie</b> aus denselben Werten im selben
    /// Zeitraum, über tausende Ereignisse.</para>
    ///
    /// <para>Der Überschuss über die Grundlinie ist der Beitrag. Nicht der rohe Ertrag: Der
    /// enthält den Marktgang, und den hätte man auch ohne Muster bekommen.</para>
    ///
    /// <para><b>Der Verdienst hängt an der Richtungstrefferquote, nicht am Überschuss.</b>
    /// Ein Auslöser mit grossem Überschuss und einer Trefferquote von 0,40 beschreibt
    /// seltene grosse Ausschläge, nicht Vorhersagbarkeit — genau das Profil, das in jeder
    /// Rückrechnung glänzt und im Betrieb verliert. Gemessen: Bei Aktien hat JEDER der
    /// sieben Auslöser einen positiven Zwanzigtagesüberschuss, auch die bärischen, und
    /// deren Richtungstrefferquote liegt bei 0,40 bis 0,43.</para>
    /// </summary>
    private async Task WissenAsync(
        Microsoft.Data.SqlClient.SqlConnection conn, IReadOnlyList<int> horizonte,
        Func<int, Saeulenlage> fuer, CancellationToken ct)
    {
        try
        {
            var aktiv = (await conn.QueryAsync<AktiverAusloeser>(new CommandDefinition(
                """
                CREATE TABLE #a (ausloeser NVARCHAR(100), richtung INT, asset_id INT,
                                 asset_class TINYINT, ts_utc DATETIME2(0));
                INSERT INTO #a EXEC dbo.get_active_triggers @tage = 3;

                SELECT a.asset_id AS AssetId, a.ausloeser AS Ausloeser, a.richtung AS Richtung,
                       s.r1 - s.b1 AS Ueber1, s.r5 - s.b5 AS Ueber5, s.r20 - s.b20 AS Ueber20,
                       s.richtung1 AS Treffer1, s.richtung5 AS Treffer5, s.richtung20 AS Treffer20,
                       s.ereignisse AS Ereignisse
                  FROM #a a
                  JOIN dbo.bot_trigger_stat s
                    ON s.ausloeser = a.ausloeser AND s.klasse = a.asset_class
                   AND s.run_id = (SELECT MAX(run_id) FROM dbo.bot_trigger_stat);

                DROP TABLE #a;
                """, cancellationToken: ct))).ToList();

            if (aktiv.Count == 0) return;

            /* Je Wert und Auslöser nur EINMAL.

               Das Fenster reicht drei Tage zurück, und ein Bollinger-Ausbruch feuert
               gern an zwei aufeinanderfolgenden Tagen. Ungefiltert stand dann
               „Bollinger-Ausbruch nach oben, Bollinger-Ausbruch nach oben" in der
               Begründung, und der Auslöser ging mit doppeltem Gewicht ein -- dieselbe
               Scheinfallzahl wie bei den stündlich wiederholten Prognosen auf
               denselben Zielbar. Genommen wird der jüngste. */
            aktiv = aktiv
                .GroupBy(x => (x.AssetId, x.Ausloeser))
                .Select(g => g.First())
                .ToList();

            foreach (var g in aktiv.GroupBy(x => x.AssetId))
            {
                foreach (var h in horizonte)
                {
                    var tage = h / 24.0;

                    double summe = 0, gewichte = 0;
                    var namen = new List<string>();

                    foreach (var t in g)
                    {
                        /* Zwischen den drei gemessenen Stützstellen wird linear
                           interpoliert, darüber hinaus NICHT fortgeschrieben: Aus einem
                           Zwanzigtageseffekt einen Jahreseffekt hochzurechnen hiesse,
                           dreissig Mal dasselbe zu behaupten, was einmal gemessen wurde. */
                        var (ueber, quote) = Interpoliere(t, tage);
                        if (ueber is null) continue;

                        /* Widerspricht die Messung dem Muster, gibt es keinen Beitrag.

                           Ein „Goldenes Kreuz" ist ein Aufwärtsmuster. Misst man dafür
                           einen negativen Überschuss, hat man kein Verkaufssignal
                           gefunden, sondern gar keins -- die Messung sagt dann das
                           Gegenteil dessen, wofür das Muster steht, und beides zusammen
                           ergibt keine Aussage.

                           Das ist nicht theoretisch: Zwischen zwei Messläufen drei Tage
                           auseinander kippte der Zwanzigtagesüberschuss des Goldenen
                           Kreuzes von +0,130 % auf −0,473 %, der des 52-Wochen-Hochs von
                           +0,168 % auf −0,024 %. Die Richtungstrefferquoten blieben dabei
                           stabil (55,5 → 51,3 bzw. 56,5 → 54,2). Ohne diese Prüfung
                           schöbe dasselbe Aufwärtsmuster die Prognose je nach Messlauf
                           einmal hoch und einmal runter.

                           `ueber` trägt bereits das Vorzeichen des Auslösers; positiv
                           heisst also „in die Richtung, die das Muster nahelegt". */
                        if (ueber.Value <= 0) continue;

                        var verdienst = Math.Clamp((Math.Abs(quote - 0.5) - 0.02) / 0.08, 0, 1);
                        if (verdienst <= 0) continue;

                        summe += ueber.Value * verdienst;
                        gewichte += verdienst;

                        if (namen.Count < 4) namen.Add($"{t.Ausloeser} ({quote:P0})");
                    }

                    if (gewichte <= 1e-9) continue;

                    var rendite = summe / gewichte;
                    var mittlererVerdienst = Math.Clamp(gewichte / g.Count(), 0, 1);

                    fuer(g.Key).Fuege(h, new Saeulenbeitrag(
                        "knowledge", rendite, mittlererVerdienst,
                        $"{g.Count()} Muster aktiv: {string.Join(", ", namen)}",
                        Beitragsart.Aufschlag));
                }
            }

            _log.LogInformation("Wissens-Säule: {Werte} Werte mit aktiven Mustern",
                                aktiv.Select(a => a.AssetId).Distinct().Count());
        }
        catch (Exception ex)
        {
            /* Eine Säule, die nicht liefert, darf den Prognoselauf nicht anhalten. Sie
               fehlt dann in der Mischung, und das steht in `pillar_mix` -- besser als ein
               Lauf, der wegen einer Nebensäule gar keine Prognose stellt. */
            _log.LogWarning(ex, "Wissens-Säule übersprungen");
        }
    }

    /// <summary>
    /// Zwischen 1, 5 und 20 Tagen linear; darüber hinaus nichts.
    /// </summary>
    private static (double? Ueberschuss, double Quote) Interpoliere(AktiverAusloeser t, double tage)
    {
        // Das Vorzeichen des Auslösers gehört auf den Überschuss, nicht auf die Quote.
        double U(double u) => u * t.Richtung;

        if (tage <= 1) return (U(t.Ueber1), t.Treffer1);
        if (tage <= 5)
        {
            var f = (tage - 1) / 4.0;
            return (U(t.Ueber1 + f * (t.Ueber5 - t.Ueber1)), t.Treffer1 + f * (t.Treffer5 - t.Treffer1));
        }
        if (tage <= 20)
        {
            var f = (tage - 5) / 15.0;
            return (U(t.Ueber5 + f * (t.Ueber20 - t.Ueber5)), t.Treffer5 + f * (t.Treffer20 - t.Treffer5));
        }

        /* Über zwanzig Tage hinaus ist nichts gemessen. Der Effekt wird deshalb NICHT
           fortgeschrieben -- er wird auf den Zwanzigtageswert gedeckelt und sein Verdienst
           mit der Entfernung gestreckt gegen null. */
        return (U(t.Ueber20), 0.5 + (t.Treffer20 - 0.5) * Math.Max(0, 1 - (tage - 20) / 60.0));
    }

    private sealed class AktiverAusloeser
    {
        public int AssetId { get; set; }
        public string Ausloeser { get; set; } = "";
        public int Richtung { get; set; }
        public double Ueber1 { get; set; }
        public double Ueber5 { get; set; }
        public double Ueber20 { get; set; }
        public double Treffer1 { get; set; }
        public double Treffer5 { get; set; }
        public double Treffer20 { get; set; }
        public int Ereignisse { get; set; }
    }

    // ==================================================== Säule Semantik ====

    /// <summary>
    /// Was gerade über einen Wert geschrieben wird — als Auf- oder Abschlag.
    ///
    /// <para><b>Wie aus Text eine Zahl wird.</b> Die Artikel der Semantik-Säule der letzten
    /// Tage werden nach dem Wert durchsucht (Symbol oder Name), die Treffer mit
    /// <see cref="Nachrichtenstimmung"/> bewertet und mit einer Halbwertszeit von einem Tag
    /// zusammengefasst. Das ergibt eine Stimmung zwischen −1 und +1.</para>
    ///
    /// <para><b>Und warum diese Stimmung nicht einfach eine Rendite ist.</b> Zwischen „die
    /// Nachrichten sind positiv" und „der Kurs steigt um x Prozent" liegt ein Faktor, den
    /// niemand raten darf. Er kommt aus <c>pillar_skill</c> — dort steht, was die
    /// Kalibrierung gegen eingetroffene Renditen ergeben hat. <b>Ohne Kalibrierung ist der
    /// Verdienst null</b>, und die Säule bewegt nichts.</para>
    ///
    /// <para>Das ist keine Vorsicht, sondern Erfahrung: Gemessen an GDELT lag der
    /// prognostische Zusammenhang von Nachrichtenton und SPY bei <b>0,0419</b> gegen eine
    /// Signifikanzschwelle von 0,077 — gleichzeitig dagegen bei 0,1365. Nachrichten hängen
    /// mit heute zusammen, nicht mit morgen. Wer daraus ungeprüft eine Prognose macht,
    /// baut sich Lookahead ein.</para>
    /// </summary>
    private async Task SemantikAsync(
        Microsoft.Data.SqlClient.SqlConnection conn, IReadOnlyList<int> horizonte,
        Func<int, Saeulenlage> fuer, CancellationToken ct)
    {
        try
        {
            var kalibrierung = (await conn.QueryAsync<PillarSkill>(new CommandDefinition(
                """
                SELECT horizon_hours AS HorizonHours, skill AS Skill,
                       n_obs AS NObs, detail AS Detail
                  FROM dbo.pillar_skill
                 WHERE pillar = 'semantic' AND asset_id = 0
                """, cancellationToken: ct))).ToDictionary(x => x.HorizonHours);

            var werte = (await conn.QueryAsync<Wertname>(new CommandDefinition(
                "SELECT asset_id AS AssetId, symbol AS Symbol, name AS Name "
                + "FROM dbo.asset WHERE is_tracked = 1", cancellationToken: ct))).ToList();

            /* Die Artikel der letzten drei Tage. Länger zurück lohnt nicht: Die
               Halbwertszeit von einem Tag drückt alles Ältere ohnehin unter ein Achtel. */
            var texte = (await conn.QueryAsync<Artikel>(new CommandDefinition(
                """
                SELECT TOP 20000
                       c.content AS Text,
                       ISNULL(c.occurred_utc, s.published_utc) AS AlsUtc
                  FROM dbo.knowledge_chunk c
                  JOIN dbo.knowledge_source s ON s.source_id = c.source_id
                 WHERE s.pillar = 'semantic'
                   AND ISNULL(c.occurred_utc, s.published_utc) >= DATEADD(day, -3, SYSUTCDATETIME())
                 ORDER BY ISNULL(c.occurred_utc, s.published_utc) DESC
                """, cancellationToken: ct))).ToList();

            if (texte.Count == 0) return;

            /* Zuordnung über einen Wortindex statt über 600 × 20.000 Textsuchen.

               Naiv wäre das ein Vergleich je Wert und Artikel -- zwölf Millionen
               Teilstring-Suchen für einen Lauf. Stattdessen wird jeder Artikel einmal in
               Grosswörter zerlegt und in einem Wörterbuch nachgeschlagen. */
            var index = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            void Merke(string schluessel, int assetId)
            {
                if (schluessel.Length < 3) return;
                if (!index.TryGetValue(schluessel, out var l)) index[schluessel] = l = [];
                if (!l.Contains(assetId)) l.Add(assetId);
            }

            foreach (var w in werte)
            {
                // BTC-USD -> auch BTC; RHM.DE -> auch RHM
                Merke(w.Symbol, w.AssetId);

                var kurz = w.Symbol.Split('-', '.')[0];
                if (kurz.Length >= 3) Merke(kurz, w.AssetId);

                /* Vom Namen nur das erste tragende Wort: „Apple Inc." -> Apple. Ganze
                   Namen treffen in Fliesstext praktisch nie, und Wörter wie „Inc" oder
                   „AG" träfen alles. */
                var ersteWorte = (w.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (ersteWorte.Length > 0 && ersteWorte[0].Length >= 4
                    && !Allerwelt.Contains(ersteWorte[0]))
                {
                    Merke(ersteWorte[0].Trim(',', '.'), w.AssetId);
                }
            }

            var jeWert = new Dictionary<int, List<(string, DateTime)>>();

            foreach (var a in texte)
            {
                if (string.IsNullOrWhiteSpace(a.Text)) continue;

                var gesehen = new HashSet<int>();

                foreach (var token in a.Text.Split(
                             [' ', '\n', '\r', '\t', ',', ';', ':', '(', ')', '"', '\''],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!index.TryGetValue(token.Trim('.', '-'), out var ids)) continue;

                    foreach (var id in ids)
                    {
                        if (!gesehen.Add(id)) continue;

                        if (!jeWert.TryGetValue(id, out var l)) jeWert[id] = l = [];
                        if (l.Count < 60) l.Add((a.Text, a.AlsUtc));
                    }
                }
            }

            var jetzt = DateTime.UtcNow;
            var mitStimmung = 0;

            foreach (var (assetId, liste) in jeWert)
            {
                var s = Nachrichtenstimmung.Fasse(liste, jetzt);
                if (s.Treffer == 0) continue;

                mitStimmung++;

                foreach (var h in horizonte)
                {
                    /* Ohne Kalibrierung: Beitrag null. Die Stimmung wird trotzdem
                       eingetragen, damit sie in `pillar_mix` sichtbar ist -- man soll
                       sehen, dass etwas erkannt wurde UND dass es nichts bewegt. */
                    kalibrierung.TryGetValue(h, out var k);

                    var verdienst = k is null ? 0 : Math.Clamp(k.Skill, 0, 1);

                    /* Der Umrechnungsfaktor steckt im Detailfeld der Kalibrierung. Ohne
                       ihn gibt es keine Rendite -- eine Stimmung von 0,4 ist keine
                       Rendite von 0,4. */
                    var faktor = k?.Faktor ?? 0;

                    fuer(assetId).Fuege(h, new Saeulenbeitrag(
                        "semantic", s.Wert * faktor, verdienst,
                        $"Stimmung {s.Wert:+0.00;-0.00;0} aus {liste.Count} Meldungen "
                        + $"({string.Join(", ", s.Woerter.Take(4))})"
                        + (verdienst <= 0
                            ? " — ohne kalibrierten Rückhalt, Beitrag null"
                            : $" — Rückhalt {verdienst:F2}"),
                        Beitragsart.Aufschlag));
                }
            }

            _log.LogInformation(
                "Semantik-Säule: {Werte} Werte mit Meldungen, Kalibrierung für {Anzahl} Horizonte",
                mitStimmung, kalibrierung.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Semantik-Säule übersprungen");
        }
    }

    /// <summary>Namensbestandteile, die auf zu viele Werte passen.</summary>
    private static readonly HashSet<string> Allerwelt = new(StringComparer.OrdinalIgnoreCase)
    {
        "The", "First", "Global", "International", "National", "United", "American",
        "Group", "Holding", "Holdings", "Corp", "Corporation", "Company", "Inc",
        "Bank", "Banco", "Banca", "Industries", "Technologies", "Systems", "Solutions",
        "Capital", "Financial", "Energy", "Motors", "Pharmaceuticals", "Partners",
        "Trust", "Index", "Fund", "ETF", "Shares", "Vanguard", "iShares", "SPDR"
    };

    private sealed class Wertname
    {
        public int AssetId { get; set; }
        public string Symbol { get; set; } = "";
        public string? Name { get; set; }
    }

    private sealed class Artikel
    {
        public string? Text { get; set; }
        public DateTime AlsUtc { get; set; }
    }

    private sealed class PillarSkill
    {
        public int HorizonHours { get; set; }
        public double Skill { get; set; }
        public int NObs { get; set; }
        public string? Detail { get; set; }

        /// <summary>
        /// Der Umrechnungsfaktor von Stimmung auf Log-Rendite, aus der Kalibrierung.
        ///
        /// <para>Steht als <c>faktor=…</c> im Detailfeld. Eine eigene Spalte wäre sauberer;
        /// sie hätte eine weitere Wanderung des Schemas bedeutet für eine Zahl, die nur
        /// zusammen mit dem Skill gelesen wird.</para>
        /// </summary>
        public double Faktor
        {
            get
            {
                if (string.IsNullOrEmpty(Detail)) return 0;

                var i = Detail.IndexOf("faktor=", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return 0;

                var rest = Detail[(i + 7)..];
                var ende = rest.IndexOf(';');
                if (ende > 0) rest = rest[..ende];

                return double.TryParse(rest, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture,
                                       out var v)
                    ? v : 0;
            }
        }
    }
}
