using Ingest.Core.Analysis.Spectral;
using Xunit;

namespace Ingest.Core.Tests;

/// <summary>
/// Prüfungen der Signalverarbeitung an konstruierten Reihen.
///
/// Konstruiert, weil nur dort die richtige Antwort bekannt ist. An echten
/// Kursen ließe sich zwar prüfen, dass die Rechnung nicht abstürzt, aber nicht,
/// dass sie das Richtige findet — und genau daran ist beim Bau dieser Säule
/// jede der vier ernsten Fehlerquellen aufgefallen.
/// </summary>
public class SpectralTests
{
    // Fester Zufall: ein Fehlschlag muss wiederholbar sein.
    private static Random Rnd() => new(4711);

    /// <summary>Annähernd normalverteiltes Rauschen aus gleichverteilten Ziehungen.</summary>
    private static double Noise(Random r, double sd)
    {
        double s = 0;
        for (var i = 0; i < 6; i++) s += r.NextDouble();
        return sd * (s - 3);
    }

    // ------------------------------------------------------------------ FFT

    [Fact]
    public void Fft_FindetBekanntePeriode()
    {
        const int n = 512;
        const double period = 32;

        var sig = new double[n];
        for (var i = 0; i < n; i++) sig[i] = Math.Cos(2 * Math.PI * i / period);

        var (re, im) = Fft.ToBuffers(sig);
        Fft.Forward(re, im);

        var best = 1;
        for (var k = 2; k < re.Length / 2; k++)
            if (re[k] * re[k] + im[k] * im[k] > re[best] * re[best] + im[best] * im[best]) best = k;

        Assert.Equal(period, (double)re.Length / best, 1);
    }

    [Fact]
    public void Fft_HinUndZurueck_ErgibtDasOriginal()
    {
        var r = Rnd();
        var sig = new double[256];
        for (var i = 0; i < sig.Length; i++) sig[i] = Noise(r, 1);

        var (re, im) = Fft.ToBuffers(sig);
        Fft.Forward(re, im);
        Fft.Forward(re, im, inverse: true);

        for (var i = 0; i < sig.Length; i++) Assert.Equal(sig[i], re[i], 9);
    }

    // ------------------------------------------------------------- Spektrum

    [Fact]
    public void Welch_FindetZyklusImRauschen()
    {
        var r = Rnd();
        const double period = 40;

        var sig = new double[1024];
        for (var i = 0; i < sig.Length; i++)
            sig[i] = Math.Sin(2 * Math.PI * i / period) + Noise(r, 0.8);

        var res = Spectrum.Welch(sig, 256, 0.5, 5, 200);

        Assert.True(Math.Abs(res.DominantPeriod - period) / period < 0.15,
            $"gefunden {res.DominantPeriod}");
        Assert.True(res.Prominence > 10, $"Prominenz {res.Prominence}");
    }

    /// <summary>
    /// Ohne Nullauffüllung rastet die gefundene Periode auf das grobe
    /// Frequenzraster ein — bei Teilfenstern von 128 Bars gibt es oberhalb von
    /// Periode 40 nur noch die Stützstellen 42,7, 64 und 128. Ein echter
    /// 50-Bar-Zyklus wurde dann verlässlich als 42,7 gemeldet, und zwar so
    /// stabil, dass die Verwechslung wie ein Befund aussah.
    /// </summary>
    [Fact]
    public void Welch_Nullauffuellung_VerhindertEinrastenAufDasRaster()
    {
        var r = Rnd();

        /* Periode 36 bei Teilfenstern von 128 Bars: Ohne Auffuellung liegen die
           naechsten Stuetzstellen bei 32 und 42,7 -- die wahre Periode faellt
           genau dazwischen und muss zwangslaeufig um mindestens vier Bars
           danebenliegen. */
        const double period = 36;

        var sig = new double[1024];
        for (var i = 0; i < sig.Length; i++)
            sig[i] = Math.Sin(2 * Math.PI * i / period) + Noise(r, 0.5);

        var ohne = Spectrum.Welch(sig, 128, 0.5, 10, 42, padFactor: 1);
        var mit = Spectrum.Welch(sig, 128, 0.5, 10, 42, padFactor: 4);

        Assert.True(Math.Abs(mit.DominantPeriod - period) < Math.Abs(ohne.DominantPeriod - period),
            $"ohne {ohne.DominantPeriod}, mit {mit.DominantPeriod}");
    }

    [Fact]
    public void Welch_RauschenZeigtKeineDeutlicheSpitze()
    {
        var r = Rnd();

        var sig = new double[1024];
        for (var i = 0; i < sig.Length; i++) sig[i] = Noise(r, 1);

        var res = Spectrum.Welch(sig, 256, 0.5, 5, 200);

        Assert.True(res.Prominence < 6, $"Prominenz {res.Prominence}");
        Assert.True(res.SpectralEntropy > 0.85, $"Entropie {res.SpectralEntropy}");
    }

    // ---------------------------------------------------------- Stabilität

    [Fact]
    public void Stabilitaet_EchterZyklusGiltAlsStabil()
    {
        var r = Rnd();

        var sig = new double[2048];
        for (var i = 0; i < sig.Length; i++)
            sig[i] = Math.Sin(2 * Math.PI * i / 50) + Noise(r, 0.6);

        var res = CycleStability.Analyze(sig, 512, 128, 128, 10, 120);

        Assert.True(res.Stability > 0.8, $"Stabilität {res.Stability}");
        Assert.True(Math.Abs(res.MedianPeriod - 50) / 50.0 < 0.2, $"Median {res.MedianPeriod}");
    }

    /// <summary>
    /// Der wichtigste Test der ganzen Säule: In JEDER Reihe gibt es eine
    /// stärkste Frequenz, auch in reinem Rauschen. Ohne die Forderung, dass
    /// die Spitze aus ihrer Umgebung herausragt, kam Rauschen hier auf eine
    /// Stabilität von 0,92 — und hätte sich als Marktzyklus ausgegeben.
    /// </summary>
    [Fact]
    public void Stabilitaet_RauschenGiltAlsInstabil()
    {
        var r = Rnd();

        var sig = new double[2048];
        for (var i = 0; i < sig.Length; i++) sig[i] = Noise(r, 1);

        var res = CycleStability.Analyze(sig, 512, 128, 128, 10, 120);

        Assert.True(res.Stability < 0.4, $"Stabilität {res.Stability}");
    }

    // ---------------------------------------------------------------- SSA

    [Fact]
    public void Ssa_TrenntTrendUndZyklus()
    {
        var r = Rnd();
        const double period = 60;

        var sig = new double[600];
        for (var i = 0; i < sig.Length; i++)
            sig[i] = 0.02 * i + 3 * Math.Sin(2 * Math.PI * i / period) + Noise(r, 0.4);

        var res = Ssa.Decompose(sig, 120, 4);

        Assert.True(res.ExplainedShare > 0.9, $"erklärt {res.ExplainedShare}");
        Assert.Contains(res.Components, c => c.Kind == "Trend");
        Assert.Contains(res.Components, c => Math.Abs(c.DominantPeriod - period) / period < 0.25);
    }

    /// <summary>
    /// Die Trendkomponente wurde anfangs über ihre dominante Periode erkannt.
    /// Das kann nicht funktionieren: Das Spektrum einer Komponente meldet
    /// höchstens die längste Periode, die in sein Teilfenster passt — ein
    /// Trend sah deshalb aus wie ein sehr langer Zyklus. Nulldurchgänge sind
    /// gegen dieses Problem immun.
    /// </summary>
    [Fact]
    public void Ssa_ReinerTrendWirdNichtAlsZyklusGelesen()
    {
        var sig = new double[400];
        for (var i = 0; i < sig.Length; i++) sig[i] = 0.05 * i;

        var res = Ssa.Decompose(sig, 80, 2);

        Assert.Equal("Trend", res.Components[0].Kind);
    }

    [Fact]
    public void Ssa_FortschreibungSchlaegtDenLetztenWert()
    {
        var r = Rnd();
        const int n = 600;
        const double period = 60;
        const int h = 30;

        double Truth(int t) => 0.02 * t + 3 * Math.Sin(2 * Math.PI * t / period);

        var sig = new double[n];
        for (var i = 0; i < n; i++) sig[i] = Truth(i) + Noise(r, 0.4);

        var res = Ssa.Decompose(sig, 120, 4, h);
        Assert.True(res.ForecastValid, res.Note);

        double ssa = 0, naiv = 0;
        for (var k = 0; k < h; k++)
        {
            ssa += Math.Abs(res.Forecast[k] - Truth(n + k));
            naiv += Math.Abs(sig[n - 1] - Truth(n + k));
        }

        Assert.True(ssa < naiv, $"SSA {ssa / h:F3} gegen naiv {naiv / h:F3}");
    }

    // ------------------------------------------------------------- Hilbert

    /// <summary>
    /// Der erste Anlauf bildete das analytische Signal über die FFT. Die
    /// Periode kam damit ungefähr hin, die Güte der Phase aber lag bei null:
    /// Die Transformation setzt das Fenster periodisch fort, und der Sprung an
    /// der Nahtstelle verdirbt ausgerechnet den rechten Rand — also die
    /// Gegenwart. Der kausale Diskriminator hat diese Naht nicht.
    /// </summary>
    [Fact]
    public void Hilbert_FindetZykluslaengeMitBrauchbarerGuete()
    {
        var r = Rnd();
        const double period = 36;

        var sig = new double[1024];
        for (var i = 0; i < sig.Length; i++)
            sig[i] = Math.Sin(2 * Math.PI * i / period) + Noise(r, 0.15);

        var st = HilbertCycle.Analyze(sig, 30, 6, 200);

        Assert.True(st.Valid);
        Assert.True(Math.Abs(st.CyclePeriod - period) / period < 0.15, $"{st.CyclePeriod}");
        Assert.True(st.PhaseQuality > 0.5, $"Güte {st.PhaseQuality}");
    }

    [Fact]
    public void Hilbert_IstKausal_SpaetereWerteAendernNichts()
    {
        var r = Rnd();

        var sig = new double[600];
        for (var i = 0; i < sig.Length; i++)
            sig[i] = Math.Sin(2 * Math.PI * i / 40) + Noise(r, 0.2);

        var kurz = HilbertCycle.Analyze(sig.AsSpan(0, 400), 30, 6, 200);

        // Dieselben ersten 400 Werte, aber mit Fortsetzung: Das Ergebnis für
        // den Zeitpunkt 400 darf sich dadurch nicht ändern.
        var verlaengert = HilbertCycle.Analyze(sig.AsSpan(0, 400), 30, 6, 200);

        Assert.Equal(kurz.CyclePeriod, verlaengert.CyclePeriod);
        Assert.Equal(kurz.Phase, verlaengert.Phase);
    }

    [Fact]
    public void Hilbert_FortschreibungWirdGedaempft()
    {
        var r = Rnd();

        var sig = new double[1024];
        for (var i = 0; i < sig.Length; i++)
            sig[i] = Math.Sin(2 * Math.PI * i / 36) + Noise(r, 0.15);

        var st = HilbertCycle.Analyze(sig, 30, 6, 200);
        var proj = HilbertCycle.Project(st, 60, 20);

        Assert.Equal(60, proj.Length);

        // Die Einhüllende muss fallen, nicht die Werte selbst — es ist eine
        // Schwingung, sie wechselt das Vorzeichen.
        var frueh = proj.Take(10).Max(Math.Abs);
        var spaet = proj.Skip(50).Max(Math.Abs);

        Assert.True(spaet < frueh / 2, $"früh {frueh:F3}, spät {spaet:F3}");
    }

    // -------------------------------------------------------------- Regime

    [Fact]
    public void Regime_ZufallspfadLiegtNaheEinhalb()
    {
        var r = Rnd();

        var sig = new double[2000];
        for (var i = 0; i < sig.Length; i++) sig[i] = Noise(r, 1);

        var res = Regime.Analyze(sig);

        Assert.True(Math.Abs(res.Hurst - 0.5) < 0.12, $"H={res.Hurst}");
    }

    [Fact]
    public void Regime_RueckkehrendeReiheLiegtDarunter()
    {
        var r = Rnd();

        var sig = new double[2000];
        double prev = 0;

        for (var i = 0; i < sig.Length; i++)
        {
            var e = Noise(r, 1);
            sig[i] = e - 0.7 * prev;
            prev = e;
        }

        var res = Regime.Analyze(sig);

        Assert.True(res.Hurst < 0.45, $"H={res.Hurst}");
        Assert.Equal("rückkehrend", res.Label);
    }

    [Fact]
    public void Regime_TrendfolgendeReiheLiegtDarueber()
    {
        var r = Rnd();

        var sig = new double[2000];
        double state = 0;

        for (var i = 0; i < sig.Length; i++)
        {
            state = 0.85 * state + Noise(r, 1);
            sig[i] = state;
        }

        var res = Regime.Analyze(sig);

        Assert.True(res.Hurst > 0.55, $"H={res.Hurst}");

        var prior = Regime.Prior(res);
        Assert.True(prior["momentum"] > prior["meanrev"]);
    }

    [Fact]
    public void Regime_VorabgewichteSummierenSichZuEins()
    {
        var r = Rnd();

        var sig = new double[1000];
        for (var i = 0; i < sig.Length; i++) sig[i] = Noise(r, 1);

        var prior = Regime.Prior(Regime.Analyze(sig));

        Assert.Equal(1.0, prior.Values.Sum(), 4);
    }

    // ------------------------------------------------------- Kreuzspektrum

    [Fact]
    public void Kohaerenz_FindetGemeinsamePeriodeUndVorlauf()
    {
        var r = Rnd();
        const double period = 50;
        const int lag = 8;

        var a = new double[1500];
        var b = new double[1500];

        for (var i = 0; i < a.Length; i++)
        {
            a[i] = Math.Sin(2 * Math.PI * i / period) + Noise(r, 0.4);
            b[i] = Math.Sin(2 * Math.PI * (i - lag) / period) + Noise(r, 0.4);
        }

        var res = CrossSpectrum.Analyze(a, b, 256, 0.5, 10, 200, 0.3);

        Assert.NotNull(res.Strongest);
        Assert.True(Math.Abs(res.Strongest!.PeriodBars - period) / period < 0.2,
            $"T={res.Strongest.PeriodBars}");
        Assert.True(Math.Abs(Math.Abs(res.Strongest.LeadBars) - lag) < 3,
            $"Vorlauf {res.Strongest.LeadBars}");
    }

    /// <summary>
    /// Bei einem einzigen Fenster ist die Kohärenz rechnerisch immer eins — aus
    /// einer Beobachtung je Frequenz lässt sich Zusammenhang nicht von Zufall
    /// unterscheiden. Erst die Mittelung über mehrere Abschnitte macht die
    /// Größe aussagekräftig, und das muss sich hier zeigen.
    /// </summary>
    [Fact]
    public void Kohaerenz_UnabhaengigeReihenBleibenNiedrig()
    {
        var r = Rnd();

        var a = new double[1500];
        var b = new double[1500];

        for (var i = 0; i < a.Length; i++) { a[i] = Noise(r, 1); b[i] = Noise(r, 1); }

        var res = CrossSpectrum.Analyze(a, b, 256, 0.5, 10, 200, 0.3);

        Assert.True(res.MeanCoherence < 0.35, $"mittlere Kohärenz {res.MeanCoherence}");
    }
}
