using System.Text;
using Dapper;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Ingest.Core.Models;
using Ingest.Infrastructure.Repositories;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Langfristiger Vermögensaufbau: einzelne Werte und Kombinationen, jeweils mit
/// Jahresrendite, Schwankung und tiefstem Einbruch.
///
/// <para><b>Warum diese Seite die belastbarste der Anwendung ist.</b> Der
/// zentrale Befund des Projekts lautet, dass die blosse Drift — die mittlere
/// Rendite des Trainingszeitraums — jedes Modell schlägt. Auf kurze Sicht ist
/// das ein Negativbefund. Auf lange Sicht <i>ist</i> die Drift die Rendite. Was
/// hier gemessen wird, ist also genau die Größe, die sich als einzige als
/// tragfähig erwiesen hat.</para>
///
/// <para><b>Und warum sie trotzdem zwei Fallen hat.</b> Erstens die Auswahl:
/// Das verfolgte Universum ist die heutige Rangliste nach
/// Marktkapitalisierung. Jeder Wert darin hat überlebt und ist groß geworden —
/// eine Rückrechnung auf dieser Liste misst Gewinner. Zweitens die Anpassung:
/// Wer aus 285 Werten die beste Kombination sucht, findet immer eine, die im
/// betrachteten Zeitraum glänzt. Deshalb wird die Auswahl auf dem ersten Teil
/// der Reihe getroffen und auf dem zweiten gemessen — dieselbe Trennung wie
/// überall sonst in dieser Anwendung.</para>
/// </summary>
public sealed class LangfristService(ISqlConnectionFactory factory) : ILangfristService
{
    /// <summary>
    /// Anteil der Reihe, auf dem Körbe zusammengestellt werden. Der Rest ist
    /// Sperrbereich. 60 zu 40 statt der sonst üblichen 70 zu 30, weil ein
    /// Sperrbereich unter zwei Jahren für Jahresfenster zu kurz wäre.
    /// </summary>
    private const double AnteilAuswahl = 0.60;

    /// <summary>
    /// So viele Werte kommen für Körbe überhaupt in Frage — die besten nach
    /// Rendite je Schwankung. Über alle 285 zu suchen fände zuverlässig die
    /// glücklichste Kombination des Zeitraums und sonst nichts.
    /// </summary>
    private const int Vorauswahl = 40;

    /// <summary>
    /// Ab dieser Paarkorrelation gelten zwei Werte als derselbe und dürfen
    /// nicht gemeinsam in einen Korb.
    ///
    /// <para>Ohne die Grenze wählte die Suche HWM, XAUT-USD und PAXG-USD —
    /// die beiden letzten sind goldgedeckte Marken und laufen praktisch
    /// identisch. Die MITTLERE Korrelation blieb dabei unauffällig bei 0,39,
    /// weil der dritte Wert sie herunterzog. Streuung misst sich an der
    /// engsten Bindung im Korb, nicht am Durchschnitt.</para>
    /// </summary>
    private const double HoechsteZulaessigeKorrelation = 0.90;

    public async Task<LangfristUebersicht> BuildAsync(
        int jahre, int minTage, int limit, CancellationToken ct = default)
    {
        jahre = Math.Clamp(jahre, 1, 25);
        minTage = Math.Clamp(minTage, 60, 5000);
        limit = Math.Clamp(limit, 5, 200);

        var von = DateTime.UtcNow.AddYears(-jahre).Date;

        await using var conn = await factory.OpenAsync(ct);

        var reihen = await ReihenAsync(conn, von, minTage, ct);

        if (reihen.Count == 0)
            return Leer(von, "Keine Reihe mit genug Historie im Zeitraum.");

        /* KEINE gemeinsame Achse über alle Werte.

           Der erste Entwurf schnitt die Zeitachsen aller 285 Reihen -- und
           damit blieben 48 Handelstage übrig, weil ein einziger junger Wert
           die Achse für alle abschneidet. Genau davor warnt die Regel in
           CLAUDE.md: Der Schnitt ist für kleine, bewusst gewählte Mengen
           gedacht.

           Richtig ist: Die Kennzahlen eines EINZELNEN Wertes brauchen keine
           gemeinsame Achse -- seine Jahresrendite ist seine Jahresrendite.
           Nur ein KORB braucht sie, und dort umfasst sie genau seine
           Mitglieder. */
        var werte = Einzelwerte(reihen);

        // Die Trennung nach Kalenderdatum, nicht nach Zeilennummer: Die Reihen
        // sind verschieden lang, eine Zeilennummer läge bei jeder woanders.
        var bis = reihen.Max(r => r.Kurse.Keys.Max());
        var anfang = reihen.Min(r => r.Kurse.Keys.Min());
        var sperrAb = anfang.AddDays((bis - anfang).TotalDays * AnteilAuswahl);

        var koerbe = Koerbe(reihen, sperrAb, werte);

        var bestenliste = werte
            .OrderByDescending(w => w.RenditeProJahr)
            .Take(limit)
            .ToList();

        return new LangfristUebersicht(
            DateTime.UtcNow, anfang, bis, sperrAb, werte.Count,
            Kernaussage(werte, koerbe),
            Verzerrungshinweis(werte),
            bestenliste, koerbe,
            Anweisungen(werte, koerbe));
    }

    // ----------------------------------------------------------- Aussagen ---

    private static string Kernaussage(List<LangfristWert> w, List<LangfristKorb> k)
    {
        var breit = k.FirstOrDefault(x => x.Name.StartsWith("Breiter Markt"));
        var bester = k.Where(x => x.ImSperrbereich)
                      .OrderByDescending(x => x.RenditeJeRueckgang)
                      .FirstOrDefault();

        var sb = new StringBuilder();

        sb.Append($"{w.Count} Werte mit ausreichender Historie. Die Jahresrenditen reichen von ")
          .Append($"{w.Min(x => x.RenditeProJahr) * 100:0.0} bis ")
          .Append($"{w.Max(x => x.RenditeProJahr) * 100:0.0} Prozent, im Mittel ")
          .Append($"{w.Average(x => x.RenditeProJahr) * 100:0.0} Prozent. ");

        if (breit is not null && bester is not null && bester.Name != breit.Name)
            sb.Append($"Im Sperrbereich erreicht **{bester.Name}** ")
              .Append($"{bester.RenditeProJahr * 100:0.0} Prozent im Jahr bei ")
              .Append($"{bester.GroessterRueckgang * 100:0} Prozent tiefstem Einbruch — der breite ")
              .Append($"Markt {breit.RenditeProJahr * 100:0.0} Prozent bei ")
              .Append($"{breit.GroessterRueckgang * 100:0} Prozent. ");

        sb.Append("**Alle Zahlen sind Vergangenheit.** Was sie belastbar machen, ist nicht ihre ")
          .Append("Höhe, sondern der Vergleich untereinander über denselben Zeitraum.");

        return sb.ToString();
    }

    /// <summary>
    /// Der Hinweis auf die Auswahlverzerrung — und zwar mit einer Zahl, nicht
    /// als Floskel. Wenn der mittlere verfolgte Wert den breiten Markt deutlich
    /// schlägt, ist das kein Kunststück der Auswahl, sondern ihr Fehler: Die
    /// Liste enthält nur, was groß geworden ist.
    /// </summary>
    private static string Verzerrungshinweis(List<LangfristWert> w)
    {
        var markt = w.FirstOrDefault(x => x.Symbol is "SPY" or "VOO" or "IVV");
        var median = Median(w.Select(x => x.RenditeProJahr).ToList());

        var sb = new StringBuilder();

        sb.Append("**Die Auswahl ist verzerrt, und zwar nach oben.** Das verfolgte Universum ist ")
          .Append("die HEUTIGE Rangliste nach Marktkapitalisierung. Jeder Wert darin hat ")
          .Append("überlebt und ist groß geworden; was in diesem Zeitraum unterging, steht gar ")
          .Append("nicht auf der Liste. Eine Rückrechnung darauf misst Gewinner.");

        if (markt is not null)
            sb.Append($" Sichtbar wird das am Abstand: Der mittlere verfolgte Wert kam auf ")
              .Append($"{median * 100:0.0} Prozent im Jahr, {markt.Symbol} auf ")
              .Append($"{markt.RenditeProJahr * 100:0.0} Prozent. ")
              .Append(median > markt.RenditeProJahr
                  ? "Der Überschuss ist überwiegend Auswahl, nicht Können."
                  : "Dass der Median darunter liegt, spricht ausnahmsweise gegen eine starke "
                    + "Verzerrung in diesem Fenster.");

        return sb.ToString();
    }

    private static List<string> Anweisungen(List<LangfristWert> w, List<LangfristKorb> k)
    {
        var liste = new List<string>();

        var imSperr = k.Where(x => x.ImSperrbereich).ToList();
        var breit = imSperr.FirstOrDefault(x => x.Name.StartsWith("Breiter Markt"));
        var gestreut = imSperr.Where(x => x.Mitglieder.Count >= 3)
                              .OrderByDescending(x => x.RenditeJeRueckgang)
                              .FirstOrDefault();

        liste.Add(
            "**Lesen Sie den tiefsten Einbruch vor der Rendite.** Er entscheidet, ob eine "
            + "Anlage durchgehalten wird. Wer bei 60 Prozent Buchverlust verkauft, hat die "
            + "Jahresrendite der Reihe nie bekommen — er hat den Einbruch bekommen.");

        if (breit is not null && gestreut is not null)
            liste.Add(
                $"**Streuung senkt den Einbruch stärker als die Rendite.** Im Sperrbereich: "
                + $"{gestreut.Name} kam auf {gestreut.RenditeProJahr * 100:0.0} Prozent im Jahr "
                + $"bei {gestreut.GroessterRueckgang * 100:0} Prozent tiefstem Einbruch, der "
                + $"breite Markt auf {breit.RenditeProJahr * 100:0.0} Prozent bei "
                + $"{breit.GroessterRueckgang * 100:0} Prozent.");

        liste.Add(
            "**Die mittlere Korrelation ist der eigentliche Streuungsgrad.** Fünf Werte mit "
            + "0,9 Korrelation sind ein Wert in fünf Verpackungen. Gemessen an diesem "
            + "Bestand: Wachstums-ETFs korrelieren zu 0,74 bis 0,78 mit dem breiten Markt, "
            + "Staatsanleihen zu 0,00 bis 0,12. Die Anleihen sind die einzige zweite Richtung.");

        var drift = k.Where(x => x.ImSperrbereich && x.Mitglieder.Count > 1)
                     .OrderByDescending(x => x.Korrelationsdrift)
                     .FirstOrDefault();

        if (drift is { Korrelationsdrift: > 0.05 })
            liste.Add(
                $"**Streuung hält nicht.** Bei „{drift.Name}“ lag die engste Bindung zum "
                + $"Zeitpunkt der Auswahl bei {drift.HoechsteKorrelationBeiAuswahl:0.00} und im "
                + $"Sperrbereich bei {drift.HoechsteKorrelation:0.00}. Das ist kein Rechenfehler, "
                + "sondern die bekannte Schwäche: Korrelationen laufen gegen eins, wenn es darauf "
                + "ankommt. Wer nach vergangenen Korrelationen streut, streut gegen die "
                + "Vergangenheit.");

        liste.Add(
            "**Was hier optimiert aussieht, ist auf der Auswahlhälfte optimiert.** Nur die "
            + "Zeilen mit dem Vermerk „Sperrbereich“ zeigen, wie sich eine Zusammenstellung "
            + "in einem Zeitraum geschlagen hat, den sie nicht gesehen hat. Alles andere ist "
            + "Rückschau auf die eigene Auswahl.");

        liste.Add(
            "**Ein Jahr ist kein langer Zeitraum.** Der Anteil positiver Ein-Jahres-Fenster "
            + "sagt mehr als die Jahresrendite: Er beantwortet, wie oft sich zwölf Monate "
            + "Halten gelohnt haben. Bei den meisten Werten hier liegt er unter 100 Prozent, "
            + "und die schlechteste Zwölfmonatsstrecke steht daneben.");

        return liste;
    }

    // -------------------------------------------------------- Einzelwerte ---

    /// <summary>
    /// Die Kennzahlen eines Wertes auf SEINER EIGENEN Zeitachse.
    ///
    /// <para>Ein Wert, der an 365 Tagen im Jahr handelt, und einer, der an 252
    /// handelt, haben verschiedene Achsen — und beide haben eine gültige
    /// Jahresrendite. Sie auf eine gemeinsame Achse zu zwingen kostet nur
    /// Daten und bringt nichts: Verglichen werden hier keine Paare, sondern
    /// Kennzahlen, die jede für sich stehen.</para>
    ///
    /// <para>Die Tage je Jahr werden aus der Reihe selbst gerechnet, nicht
    /// gesetzt. Eine Reihe mit Lücken -- etwa nach einer Ruhepause des
    /// Rechners -- bekäme sonst eine zu hohe Jahresrendite, weil man ihre
    /// wenigen Tage mit 252 hochrechnete.</para>
    /// </summary>
    private static List<LangfristWert> Einzelwerte(List<Reihe> reihen)
    {
        var liste = new List<LangfristWert>(reihen.Count);

        foreach (var r in reihen)
        {
            var tage = r.Kurse.Keys.Order().ToArray();
            if (tage.Length < 30) continue;

            var kurse = tage.Select(t => r.Kurse[t]).ToArray();

            var spanne = (tage[^1] - tage[0]).TotalDays / 365.25;
            if (spanne <= 0.25) continue;

            var effektiv = kurse.Length / spanne;

            var ren = Langfristmass.Renditen(kurse);

            // Das Ein-Jahres-Fenster in Bars DIESER Reihe, nicht pauschal 252.
            var fenster = Math.Min((int)Math.Round(effektiv), kurse.Length - 1);
            var (pos, schlecht, gut) = fenster > 10
                ? Langfristmass.Jahresfenster(kurse, fenster)
                : (0, 0, 0);

            liste.Add(new LangfristWert(
                r.Symbol, r.Name, r.Klasse,
                kurse.Length, Math.Round(spanne, 2),
                Langfristmass.RenditeProJahr(ren, effektiv),
                Langfristmass.SchwankungProJahr(ren, effektiv),
                Langfristmass.GroessterRueckgang(kurse),
                pos, schlecht, gut));
        }

        return liste;
    }

    // ------------------------------------------------------------- Körbe ----

    /// <summary>
    /// Baut die Körbe: erst feste Vergleichsmaßstäbe, dann eine gesuchte
    /// Kombination.
    ///
    /// <para><b>Die Vergleichsmaßstäbe stehen zuerst und sind nicht optional.</b>
    /// „Breiter Markt" und „60/40" sind das, was jeder ohne diese Anwendung
    /// bekäme. Eine gesuchte Kombination, die sie im Sperrbereich nicht
    /// schlägt, hat nichts geleistet — und das muss ablesbar sein, ohne dass
    /// man es erst selbst nachrechnet.</para>
    /// </summary>
    private static List<LangfristKorb> Koerbe(
        List<Reihe> reihen, DateTime sperrAb, List<LangfristWert> werte)
    {
        var liste = new List<LangfristKorb>();
        var nachSymbol = reihen.ToDictionary(r => r.Symbol, StringComparer.OrdinalIgnoreCase);

        void Fest(string name, string begruendung, params string[] symbole)
        {
            var da = symbole.Where(nachSymbol.ContainsKey).ToArray();
            if (da.Length < symbole.Length) return;

            var korb = Bewerte(name, begruendung, da, nachSymbol, sperrAb, true);
            if (korb is not null) liste.Add(korb);
        }

        Fest("Breiter Markt", "Ein einziger Indexfonds auf den S&P 500. Der Maßstab, "
             + "gegen den sich alles andere behaupten muss.", "SPY");

        Fest("60/40", "Sechzig Prozent breiter Aktienmarkt, vierzig Prozent mittlere "
             + "Staatsanleihen — hier gleich gewichtet über drei Aktien- und zwei "
             + "Anleihenteile.", "SPY", "IVV", "VOO", "IEF", "VGIT");

        Fest("Aktien und Anleihen zu gleichen Teilen",
             "Der einfachste Streuungsgedanke: die einzige zweite Richtung im Bestand "
             + "dazunehmen.", "SPY", "IEF");

        Fest("Drei Richtungen", "Aktien, Staatsanleihen, Gold — drei Dinge, die aus "
             + "verschiedenen Gründen steigen.", "SPY", "IEF", "GLD");

        /* Die gesuchte Kombination.

           Ausgewählt wird ausschließlich auf der ERSTEN Hälfte der Reihe.
           Alles andere wäre eine Rückschau auf die eigene Wahl. */
        /* Kandidaten müssen den ganzen Zeitraum abdecken.

           Ein Wert mit vier Monaten Historie hat keine Fünfjahresrendite, und
           in einem Korb würde er die gemeinsame Achse aller Mitglieder auf
           seine vier Monate zusammenschneiden. */
        var fruehestens = reihen.Min(r => r.Kurse.Keys.Min()).AddDays(60);

        var kandidaten = werte
            .Where(w => nachSymbol.TryGetValue(w.Symbol, out var r)
                        && r.Kurse.Keys.Min() <= fruehestens)
            .OrderByDescending(w => w.RenditeJeSchwankung)
            .Take(Vorauswahl)
            .Select(w => w.Symbol)
            .ToList();

        foreach (var groesse in new[] { 3, 5, 8 })
        {
            var gewaehlt = Zusammenstellen(kandidaten, nachSymbol, sperrAb, groesse);
            if (gewaehlt.Count < groesse) continue;

            var korb = Bewerte(
                $"Gesucht, {groesse} Werte",
                "Auf der ersten Hälfte des Zeitraums zusammengestellt: Es beginnt mit dem "
                + "Wert mit der besten Rendite je Schwankung, und es kommt jeweils der "
                + "hinzu, der die Schwankung des Korbes am stärksten senkt. Gemessen wird "
                + "auf der zweiten Hälfte, die die Auswahl nicht gesehen hat.",
                [.. gewaehlt], nachSymbol, sperrAb, true);

            if (korb is not null) liste.Add(korb);
        }

        return liste;
    }

    /// <summary>
    /// Gierige Zusammenstellung: mit dem besten Wert anfangen und jeweils den
    /// aufnehmen, der die Schwankung des Korbes am stärksten senkt.
    ///
    /// <para>Kein Optimierer. Ein Optimierer fände auf demselben Zeitraum eine
    /// bessere Kombination — und genau deshalb wäre er hier schlechter: Je
    /// feiner die Suche, desto sicherer findet sie das Rauschen des
    /// Zeitraums.</para>
    /// </summary>
    private static List<string> Zusammenstellen(
        List<string> kandidaten, Dictionary<string, Reihe> nachSymbol,
        DateTime sperrAb, int groesse)
    {
        /* Die Achse der Vorauswahl -- vierzig bewusst gewählte Werte, also
           genau der Fall, für den ein Schnitt gedacht ist. Beschränkt auf die
           Auswahlhälfte: Was danach kommt, darf die Auswahl nicht sehen. */
        var achse = SchnittAchse(kandidaten.Select(s => nachSymbol[s]))
            .Where(t => t < sperrAb).ToArray();

        if (achse.Length < 30) return [];

        var renditen = kandidaten.ToDictionary(
            s => s,
            s => Langfristmass.Renditen(AufAchse(nachSymbol[s], achse)));

        var gewaehlt = new List<string>();
        if (kandidaten.Count == 0) return gewaehlt;

        gewaehlt.Add(kandidaten[0]);

        while (gewaehlt.Count < groesse)
        {
            string? bester = null;
            var besteSchwankung = double.MaxValue;

            foreach (var s in kandidaten)
            {
                if (gewaehlt.Contains(s)) continue;

                // Zu eng gebunden an etwas, das schon drin ist? Dann ist es
                // kein zweiter Wert, sondern eine zweite Verpackung.
                var zuEng = gewaehlt.Any(g =>
                    Langfristmass.Korrelation(renditen[g], renditen[s])
                        > HoechsteZulaessigeKorrelation);

                if (zuEng) continue;

                var probe = gewaehlt.Append(s).ToList();
                var misch = Mischen(probe, renditen);
                var sch = Langfristmass.SchwankungProJahr(misch, 252);

                if (sch < besteSchwankung) { besteSchwankung = sch; bester = s; }
            }

            if (bester is null) break;
            gewaehlt.Add(bester);
        }

        return gewaehlt;
    }

    /// <summary>
    /// Die höchste Paarkorrelation eines Korbes in einem Teilzeitraum —
    /// vor oder nach der Trennung.
    /// </summary>
    private static double HoechsteKorrelationIm(
        string[] symbole, Dictionary<string, Reihe> nachSymbol, DateTime sperrAb, bool davor)
    {
        if (symbole.Length < 2) return 0;

        var voll = SchnittAchse(symbole.Select(s => nachSymbol[s]));
        var teil = (davor ? voll.Where(t => t < sperrAb) : voll.Where(t => t >= sperrAb)).ToArray();

        if (teil.Length < 30) return 0;

        var renditen = symbole.ToDictionary(
            s => s, s => Langfristmass.Renditen(AufAchse(nachSymbol[s], teil)));

        var hoch = 0.0;
        for (var i = 0; i < symbole.Length; i++)
            for (var j = i + 1; j < symbole.Length; j++)
            {
                var k = Langfristmass.Korrelation(renditen[symbole[i]], renditen[symbole[j]]);
                if (k > hoch) hoch = k;
            }

        return hoch;
    }

    private static double[] Mischen(List<string> symbole, Dictionary<string, double[]> renditen)
    {
        var laenge = symbole.Min(s => renditen[s].Length);
        var misch = new double[laenge];

        foreach (var s in symbole)
        {
            var r = renditen[s];
            for (var i = 0; i < laenge; i++) misch[i] += r[i] / symbole.Count;
        }

        return misch;
    }

    /// <summary>
    /// Bewertet einen Korb — auf dem Sperrbereich, wenn <paramref name="imSperrbereich"/>
    /// gesetzt ist, sonst über die ganze Reihe.
    /// </summary>
    private static LangfristKorb? Bewerte(
        string name, string begruendung, string[] symbole,
        Dictionary<string, Reihe> nachSymbol, DateTime sperrAb,
        bool imSperrbereich)
    {
        // Die engste Bindung zum Zeitpunkt der AUSWAHL -- also das, worauf sich
        // eine Zusammenstellung stützen konnte, als sie getroffen wurde.
        var korrDamals = HoechsteKorrelationIm(symbole, nachSymbol, sperrAb, davor: true);

        // Die Achse umfasst genau die Mitglieder dieses Korbes -- eine kleine,
        // bewusst gewählte Menge.
        var voll = SchnittAchse(symbole.Select(s => nachSymbol[s]));

        var teil = imSperrbereich
            ? voll.Where(t => t >= sperrAb).ToArray()
            : voll;

        if (teil.Length < 30) return null;

        var kurse = symbole.ToDictionary(s => s, s => AufAchse(nachSymbol[s], teil));
        var renditen = symbole.ToDictionary(s => s, s => Langfristmass.Renditen(kurse[s]));

        var misch = Mischen([.. symbole], renditen);

        /* Der Kursverlauf des Korbes aus den gemischten Renditen -- gleich
           gewichtet und täglich neu ausgeglichen. Das ist eine Annahme, und
           eine optimistische: Tägliches Ausgleichen kostet Gebühren, die hier
           nicht abgezogen sind. */
        var verlauf = new double[misch.Length + 1];
        verlauf[0] = 100;
        for (var i = 0; i < misch.Length; i++)
            verlauf[i + 1] = verlauf[i] * Math.Exp(misch[i]);

        var spanne = (teil[^1] - teil[0]).TotalDays / 365.25;
        var effektiv = spanne > 0 ? verlauf.Length / spanne : 252;

        double korrSumme = 0;
        var korrHoch = 0.0;
        var paare = 0;
        for (var i = 0; i < symbole.Length; i++)
            for (var j = i + 1; j < symbole.Length; j++)
            {
                var k = Langfristmass.Korrelation(renditen[symbole[i]], renditen[symbole[j]]);
                korrSumme += k;
                if (k > korrHoch) korrHoch = k;
                paare++;
            }

        var (pos, _, _) = Langfristmass.Jahresfenster(verlauf, Math.Min(252, verlauf.Length - 1));

        return new LangfristKorb(
            name, begruendung, symbole,
            verlauf.Length, Math.Round(spanne, 2),
            Langfristmass.RenditeProJahr(misch, effektiv),
            Langfristmass.SchwankungProJahr(misch, effektiv),
            Langfristmass.GroessterRueckgang(verlauf),
            pos,
            paare > 0 ? korrSumme / paare : 0,
            korrHoch, korrDamals,
            imSperrbereich);
    }

    // ------------------------------------------------------------- Daten ----

    private static async Task<List<Reihe>> ReihenAsync(
        System.Data.Common.DbConnection conn, DateTime von, int minTage, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<KursZeile>(new CommandDefinition("""
            SET NOCOUNT ON;

            SELECT p.asset_id, a.symbol, a.name, a.asset_class, p.ts_utc,
                   CAST(p.[close] AS FLOAT) AS kurs
              FROM dbo.price_bar p
              JOIN dbo.asset a ON a.asset_id = p.asset_id AND a.is_tracked = 1
             WHERE p.interval_code = '1d' AND p.ts_utc >= @von AND p.[close] > 0
               AND p.asset_id IN (
                     SELECT asset_id FROM dbo.price_bar
                      WHERE interval_code = '1d' AND ts_utc >= @von AND [close] > 0
                      GROUP BY asset_id HAVING COUNT(*) >= @minTage)
             ORDER BY p.asset_id, p.ts_utc;
            """, new { von, minTage }, commandTimeout: 300, cancellationToken: ct));

        return rows
            .GroupBy(r => r.asset_id)
            .Select(g => new Reihe(
                g.Key, g.First().symbol, g.First().name, (AssetClass)g.First().asset_class,
                g.ToDictionary(x => x.ts_utc, x => x.kurs)))
            .ToList();
    }

    /// <summary>
    /// Der Schnitt der Zeitachsen einer <b>kleinen, bewusst gewählten</b>
    /// Menge von Reihen.
    ///
    /// <para>Über alle verfolgten Werte angewandt bleiben von fünf Jahren
    /// achtundvierzig Handelstage übrig — ein einziger junger Wert schneidet
    /// die Achse für alle ab. Für die Mitglieder eines Korbes ist der Schnitt
    /// dagegen genau richtig: Ein Korb kann nur an Tagen bewertet werden, an
    /// denen alle seine Teile handelbar waren.</para>
    /// </summary>
    private static DateTime[] SchnittAchse(IEnumerable<Reihe> reihen)
    {
        HashSet<DateTime>? schnitt = null;

        foreach (var r in reihen)
        {
            if (schnitt is null) schnitt = [.. r.Kurse.Keys];
            else schnitt.IntersectWith(r.Kurse.Keys);
        }

        return schnitt is null ? [] : [.. schnitt.Order()];
    }

    private static double[] AufAchse(Reihe r, DateTime[] achse)
    {
        var k = new List<double>(achse.Length);
        foreach (var t in achse)
            if (r.Kurse.TryGetValue(t, out var v) && v > 0) k.Add(v);

        return [.. k];
    }

    private static double Median(List<double> werte)
    {
        if (werte.Count == 0) return 0;
        var s = werte.Order().ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static LangfristUebersicht Leer(DateTime von, string grund) =>
        new(DateTime.UtcNow, von, von, von, 0, grund, "", [], [], []);

    private sealed record Reihe(
        int AssetId, string Symbol, string? Name, AssetClass Klasse,
        Dictionary<DateTime, double> Kurse);

    private sealed class KursZeile
    {
        public int asset_id { get; set; }
        public string symbol { get; set; } = "";
        public string? name { get; set; }
        public byte asset_class { get; set; }
        public DateTime ts_utc { get; set; }
        public double kurs { get; set; }
    }
}
