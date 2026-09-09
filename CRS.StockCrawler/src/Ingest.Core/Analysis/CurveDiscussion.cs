namespace Ingest.Core.Analysis;

/// <summary>Die Arten von Stelle, die eine Kurvendiskussion kennt.</summary>
public static class CurveEventType
{
    /// <summary>f' wechselt von + nach −: lokales Maximum.</summary>
    public const string Hochpunkt = "hochpunkt";

    /// <summary>f' wechselt von − nach +: lokales Minimum.</summary>
    public const string Tiefpunkt = "tiefpunkt";

    /// <summary>f'' wechselt das Vorzeichen: die Bewegung kippt von
    /// beschleunigt in bremsend oder umgekehrt.</summary>
    public const string Wendepunkt = "wendepunkt";

    /// <summary>f' und f'' beide nahe null: der Trend läuft aus, ohne zu
    /// drehen.</summary>
    public const string Sattelpunkt = "sattelpunkt";

    /// <summary>|f'| ungewöhnlich groß: eine Bewegung setzt ein.</summary>
    public const string Steigungsausbruch = "steigungsausbruch";

    /// <summary>|f''| ungewöhnlich groß: die Bewegung ändert ihr Tempo
    /// abrupt.</summary>
    public const string Kruemmungsausbruch = "kruemmungsausbruch";

    /// <summary>Der Rohwert weicht stark von der Anpassung ab: ein Sprung, den
    /// die glatte Kurve nicht erklärt.</summary>
    public const string Sprung = "sprung";

    public static readonly string[] All =
    [
        Hochpunkt, Tiefpunkt, Wendepunkt, Sattelpunkt,
        Steigungsausbruch, Kruemmungsausbruch, Sprung
    ];
}

/// <summary>Eine gefundene Stelle mit allem, was sie beschreibt.</summary>
/// <param name="Index">Position in der Reihe.</param>
/// <param name="Type">Art der Stelle, siehe <see cref="CurveEventType"/>.</param>
/// <param name="Sign">+1 aufwärts gerichtet, −1 abwärts, 0 richtungslos.</param>
/// <param name="Severity">
/// Stufe von 1 bis 100. Vergleichbar über Werte hinweg, weil sie nicht in
/// Prozent misst, sondern in Vielfachen dessen, was für DIESE Reihe üblich ist.
/// </param>
/// <param name="Slope">Steigung der geglätteten Kurve, Log-Rendite je Bar.</param>
/// <param name="Curvature">Krümmung, Log-Rendite je Bar².</param>
/// <param name="Smoothed">Der geglättete Kurs an dieser Stelle.</param>
public sealed record CurvePoint(
    int Index,
    string Type,
    int Sign,
    double Severity,
    double Slope,
    double Curvature,
    double Smoothed);

/// <summary>
/// Die Kurvendiskussion einer Kursreihe: Hoch- und Tiefpunkte, Wendepunkte,
/// Sattelpunkte und die Stellen, an denen Steigung oder Krümmung aus dem Rahmen
/// fallen.
///
/// <b>Auf Log-Kursen, nicht auf Kursen.</b> Die Steigung einer Kurve in Dollar
/// hängt vom Kursniveau ab: Zehn Dollar am Tag sind bei einem Kurs von 20 eine
/// Explosion und bei 4.000 nichts. Auf dem Logarithmus ist die Steigung eine
/// Rendite je Bar und damit über alle Werte hinweg dieselbe Größe — genau die
/// Voraussetzung dafür, Ereignisse verschiedener Werte später überhaupt
/// nebeneinanderlegen zu dürfen.
///
/// <b>Die Stufe misst nicht in Prozent.</b> Eine Steigung von 2 % je Tag ist
/// bei einem Versorger außergewöhnlich und bei einem jungen Kryptowert
/// Alltag. Bewertet wird deshalb gegen die übliche Streuung DIESER Reihe in
/// einem vorangehenden Fenster. Erst dadurch heißt „Stufe 80" bei beiden
/// dasselbe.
///
/// <b>Sperrzeit.</b> Eine kräftige Bewegung erzeugt sonst an zehn
/// aufeinanderfolgenden Bars zehn Ereignisse. Jede spätere Häufigkeitsaussage
/// wäre damit wertlos, und die Verknüpfung über Werte hinweg fände vor allem
/// sich selbst. Je Art gilt eine eigene Sperrzeit — ein Wendepunkt und ein
/// Steigungsausbruch am selben Tag sind zwei verschiedene Beobachtungen.
/// </summary>
public static class CurveDiscussion
{
    /// <summary>Was ein Durchgang an Einstellungen braucht.</summary>
    /// <param name="HalfWindow">Halbe Fensterbreite der Anpassung, in Bars.</param>
    /// <param name="Causal">
    /// Wahr: nur zurückblickend. Falsch: zentriert — genauer für die
    /// Beschreibung der Vergangenheit, aber unbrauchbar für Prognosen.
    /// </param>
    /// <param name="RefWindow">Vergleichsfenster für „üblich", in Bars.</param>
    /// <param name="MinZ">Ab wie vielen Vielfachen des Üblichen ein Ausbruch zählt.</param>
    /// <param name="Refractory">Sperrzeit je Art, in Bars.</param>
    /// <param name="MinSeverity">Stufen darunter werden verworfen.</param>
    public sealed record Options(
        int HalfWindow = 10,
        bool Causal = false,
        int RefWindow = 250,
        double MinZ = 2.5,
        int Refractory = 5,
        double MinSeverity = 20);

    /// <summary>
    /// Führt die Diskussion durch. <paramref name="closes"/> sind Schlusskurse,
    /// nicht Logarithmen — die Umrechnung geschieht hier, damit sie nicht an
    /// jeder Aufrufstelle vergessen werden kann.
    /// </summary>
    public static List<CurvePoint> Analyze(double[] closes, Options? opt = null)
    {
        opt ??= new Options();

        var n = closes.Length;
        var result = new List<CurvePoint>();

        if (n < 3 * opt.HalfWindow + opt.RefWindow / 4) return result;

        var log = new double[n];
        for (var i = 0; i < n; i++)
            log[i] = closes[i] > 0 ? Math.Log(closes[i]) : double.NaN;

        // Lücken überbrücken, damit die Anpassung nicht an einer einzelnen
        // fehlenden Bar zerbricht. Wo alles fehlt, bleibt es fehlend.
        var last = double.NaN;
        for (var i = 0; i < n; i++)
        {
            if (double.IsNaN(log[i])) log[i] = last;
            else last = log[i];
        }

        var firstValid = 0;
        while (firstValid < n && double.IsNaN(log[firstValid])) firstValid++;
        if (n - firstValid < 3 * opt.HalfWindow) return result;

        for (var i = 0; i < firstValid; i++) log[i] = log[firstValid];

        var fit = SavitzkyGolay.Fit(log, opt.HalfWindow, opt.Causal);

        /* Laufende Streuung von Steigung und Krümmung.

           Das Vergleichsfenster endet VOR der bewerteten Stelle. Nähme es sie
           mit auf, senkte ein großer Ausschlag seinen eigenen Maßstab — starke
           Ereignisse erschienen dann kleiner, je stärker sie sind. */
        var sd1 = TrailingScale(fit.D1, opt.RefWindow);
        var sd2 = TrailingScale(fit.D2, opt.RefWindow);
        var sres = TrailingScale(Residual(log, fit.Value), opt.RefWindow);

        // Letzte Meldung je Art, für die Sperrzeit.
        var lastHit = new Dictionary<string, int>();
        foreach (var t in CurveEventType.All) lastHit[t] = -opt.Refractory - 1;

        var start = Math.Max(firstValid + opt.HalfWindow + 1, opt.HalfWindow + 1);
        var end = n - (opt.Causal ? 1 : opt.HalfWindow + 1);

        for (var i = start; i < end; i++)
        {
            var s1 = sd1[i];
            var s2 = sd2[i];

            if (double.IsNaN(s1) || s1 <= 0) continue;

            var d1 = fit.D1[i];
            var d2 = fit.D2[i];

            var z1 = Math.Abs(d1) / s1;
            var z2 = s2 > 0 ? Math.Abs(d2) / s2 : 0;

            void Add(string type, int sign, double magnitude)
            {
                if (i - lastHit[type] <= opt.Refractory) return;

                var sev = ToScale(magnitude);
                if (sev < opt.MinSeverity) return;

                lastHit[type] = i;
                result.Add(new CurvePoint(i, type, sign, Math.Round(sev, 2),
                                          d1, d2, Math.Exp(fit.Value[i])));
            }

            // --- Nullstellen der ersten Ableitung: Hoch- und Tiefpunkte ------
            //
            // Gefunden über den Vorzeichenwechsel, nicht über "nahe null". Ein
            // Schwellenwert auf |f'| fände in ruhigen Phasen hunderte Stellen
            // und in bewegten keine.
            var prev1 = fit.D1[i - 1];

            if (Math.Sign(prev1) != Math.Sign(d1) && prev1 != 0 && d1 != 0)
            {
                /* Wie ausgeprägt die Wende ist, steckt in der Krümmung: Eine
                   scharfe Spitze hat große |f''|, ein flaches Auslaufen kleine.
                   Der Vorzeichenwechsel allein sagt darüber nichts. */
                if (d2 < 0) Add(CurveEventType.Hochpunkt, -1, z2);
                else if (d2 > 0) Add(CurveEventType.Tiefpunkt, +1, z2);
            }

            // --- Nullstellen der zweiten Ableitung: Wendepunkte --------------
            var prev2 = fit.D2[i - 1];

            if (Math.Sign(prev2) != Math.Sign(d2) && prev2 != 0 && d2 != 0)
            {
                /* Beim Wendepunkt zählt die Steigung: Ein Wendepunkt in einem
                   kräftigen Trend ist eine Aussage über dessen Tempo, einer im
                   Seitwärtsband ist Rauschen. */
                Add(CurveEventType.Wendepunkt, Math.Sign(d1), z1);
            }

            // --- Sattelpunkt: beides nahe null zugleich ----------------------
            //
            // Hier ist ein Schwellenwert richtig, weil die Aussage gerade
            // lautet: nichts passiert mehr, obwohl vorher etwas passierte.
            if (z1 < 0.35 && z2 < 0.35)
            {
                // Vorlauf: Es muss vorher Bewegung gegeben haben, sonst ist ein
                // Stillstand keine Beobachtung, sondern der Normalzustand.
                var vorher = 0.0;
                for (var k = Math.Max(0, i - 3 * opt.HalfWindow); k < i - opt.HalfWindow; k++)
                    vorher = Math.Max(vorher, sd1[k] > 0 ? Math.Abs(fit.D1[k]) / sd1[k] : 0);

                if (vorher > opt.MinZ)
                    Add(CurveEventType.Sattelpunkt, 0, vorher);
            }

            // --- Ausbrüche in Steigung und Krümmung -------------------------
            if (z1 >= opt.MinZ)
                Add(CurveEventType.Steigungsausbruch, Math.Sign(d1), z1);

            if (z2 >= opt.MinZ)
                Add(CurveEventType.Kruemmungsausbruch, Math.Sign(d2), z2);

            // --- Sprung: was die glatte Kurve nicht erklärt ------------------
            //
            // Der Abstand des Rohwerts von der Anpassung. Groß wird er nur
            // dort, wo eine einzelne Bar aus der Reihe fällt — also bei
            // Meldungen, Quartalszahlen und Datenfehlern.
            var r = sres[i];

            if (r > 0)
            {
                var zr = Math.Abs(log[i] - fit.Value[i]) / r;
                if (zr >= opt.MinZ + 1)
                    Add(CurveEventType.Sprung, Math.Sign(log[i] - fit.Value[i]), zr);
            }
        }

        return result;
    }

    /// <summary>
    /// Bildet ein Vielfaches des Üblichen auf eine Stufe von 0 bis 100 ab.
    ///
    /// Dieselbe Kennlinie wie beim Ereignisdetektor der ersten Säule, und das
    /// mit Absicht: Zwei Stufenskalen, die verschieden gemeint sind, aber gleich
    /// heißen, führen zwangsläufig dazu, dass sie irgendwann verglichen werden.
    /// </summary>
    private static double ToScale(double magnitude) =>
        100 * (1 - Math.Exp(-Math.Max(0, magnitude) / 4));

    /// <summary>
    /// Laufender Streuungsmaßstab aus einem Fenster, das VOR der Stelle endet.
    ///
    /// Verwendet wird der Median der Beträge statt der Standardabweichung: Ein
    /// einzelner großer Ausschlag hebt die Standardabweichung so weit an, dass
    /// er sich selbst klein rechnet. Der Median bleibt davon unberührt.
    /// </summary>
    private static double[] TrailingScale(double[] x, int window)
    {
        var n = x.Length;
        var scale = new double[n];

        Array.Fill(scale, double.NaN);

        var buf = new double[window];

        for (var i = window + 1; i < n; i++)
        {
            var k = 0;

            for (var j = i - window; j < i; j++)
            {
                var v = Math.Abs(x[j]);
                if (!double.IsNaN(v) && !double.IsInfinity(v)) buf[k++] = v;
            }

            if (k < window / 2) continue;

            var slice = buf.AsSpan(0, k).ToArray();
            Array.Sort(slice);

            var med = slice[k / 2];

            // 1,4826 macht aus dem Median der Beträge einen Schätzer, der bei
            // normalverteilten Daten mit der Standardabweichung zusammenfällt.
            scale[i] = med * 1.4826;
        }

        return scale;
    }

    private static double[] Residual(double[] y, double[] fit)
    {
        var r = new double[y.Length];
        for (var i = 0; i < y.Length; i++) r[i] = y[i] - fit[i];
        return r;
    }
}
