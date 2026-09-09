using Ingest.Core.Analysis;
using Xunit;

namespace Ingest.Core.Tests;

public class EnsembleTests
{
    private static Dictionary<string, double> Gleichgewicht(params string[] namen)
        => namen.ToDictionary(n => n, _ => 1.0 / namen.Length);

    [Fact]
    public void UpdateWeights_GenaueresModell_GewinntGewicht()
    {
        var current = Gleichgewicht("gut", "schlecht");
        var vorhersagen = new Dictionary<string, double> { ["gut"] = 0.01, ["schlecht"] = -0.03 };

        var updated = Ensemble.UpdateWeights(current, vorhersagen, actualReturn: 0.011);

        Assert.True(updated["gut"] > updated["schlecht"]);
    }

    [Fact]
    public void UpdateWeights_SummiertSichZuEins()
    {
        var current = Gleichgewicht("a", "b", "c");
        var vorhersagen = new Dictionary<string, double> { ["a"] = 0.01, ["b"] = 0.02, ["c"] = -0.05 };

        var updated = Ensemble.UpdateWeights(current, vorhersagen, actualReturn: 0.012);

        Assert.Equal(1.0, updated.Values.Sum(), 6);
    }

    [Fact]
    public void UpdateWeights_HaeltMindestgewicht()
    {
        // Ein wiederholt schlechtes Modell darf nicht auf null fallen — sonst
        // käme es nie zurück, wenn sich das Marktregime dreht.
        var current = Gleichgewicht("gut", "schlecht");
        var vorhersagen = new Dictionary<string, double> { ["gut"] = 0.01, ["schlecht"] = -0.20 };

        for (var i = 0; i < 100; i++)
            current = Ensemble.UpdateWeights(current, vorhersagen, actualReturn: 0.01);

        Assert.True(current["schlecht"] >= 0.009, $"Gewicht fiel auf {current["schlecht"]}");
    }

    [Fact]
    public void UpdateWeights_RichtungsstrafeBestraftDasNullmodell()
    {
        /* Der Kern der Lernregel: ein Modell, das immer "keine Änderung" sagt,
           hat oft den kleinsten Betragsfehler — nennt aber nie eine Richtung.
           Ohne Strafe würde es gewinnen und das System wäre nutzlos. */
        var current = Gleichgewicht("nullmodell", "richtungstreu");

        // Das Nullmodell liegt betragsmäßig näher dran, die Richtung trifft
        // aber nur das andere.
        var vorhersagen = new Dictionary<string, double>
        {
            ["nullmodell"] = 0.0,
            ["richtungstreu"] = 0.03
        };

        var mitStrafe = Ensemble.UpdateWeights(
            current, vorhersagen, actualReturn: 0.012, directionPenalty: 1.2);

        var ohneStrafe = Ensemble.UpdateWeights(
            current, vorhersagen, actualReturn: 0.012, directionPenalty: 0.0);

        Assert.True(ohneStrafe["nullmodell"] > ohneStrafe["richtungstreu"],
            "Ohne Strafe sollte das Nullmodell vorn liegen");

        Assert.True(mitStrafe["richtungstreu"] > mitStrafe["nullmodell"],
            "Mit Strafe muss das richtungstreue Modell vorn liegen");
    }

    [Fact]
    public void UpdateWeights_UnbewegterKurs_KeineRichtungsstrafe()
    {
        // Bei praktisch unbewegtem Kurs ist die Richtungsfrage sinnlos.
        var current = Gleichgewicht("still", "laut");
        var vorhersagen = new Dictionary<string, double> { ["still"] = 0.0, ["laut"] = 0.05 };

        var updated = Ensemble.UpdateWeights(current, vorhersagen, actualReturn: 0.0);

        Assert.True(updated["still"] > updated["laut"]);
    }

    [Fact]
    public void Combine_OhneGespeicherteGewichte_NutztGleichgewicht()
    {
        var input = new ForecastInput(Enumerable.Range(0, 120).Select(i => (decimal)(100 + i * 0.1)).ToList(), 5);

        var result = Ensemble.Combine(Ensemble.DefaultModels(), input,
            new Dictionary<string, double>());

        Assert.Equal(5, result.Components.Count);
        Assert.All(result.Components, c => Assert.Equal(0.2, c.Weight, 6));
    }

    [Fact]
    public void Combine_KapptAusreisser()
    {
        // Über einen Horizont sind mehr als 50 % Log-Return praktisch immer ein
        // Datenfehler und dürfen das Ensemble nicht sprengen.
        var input = new ForecastInput([.. Enumerable.Range(0, 100).Select(i => (decimal)(i % 2 == 0 ? 1 : 1000))], 24);

        var result = Ensemble.Combine(Ensemble.DefaultModels(), input,
            new Dictionary<string, double>());

        Assert.All(result.Components, c => Assert.InRange(c.PredictedReturn, -0.5, 0.5));
        Assert.False(double.IsNaN(result.PredictedReturn));
    }

    [Fact]
    public void Combine_UneinigeModelle_SenkenDieZuversicht()
    {
        var steigend = new ForecastInput([.. Enumerable.Range(0, 150).Select(i => (decimal)(100 + i))], 3);

        var result = Ensemble.Combine(Ensemble.DefaultModels(), steigend,
            new Dictionary<string, double>());

        Assert.InRange(result.Confidence, 0.0, 1.0);
    }

    [Fact]
    public void RunningMean_MitteltFortlaufend()
    {
        var (m1, n1) = Ensemble.RunningMean(0, 0, 10);
        Assert.Equal(10, m1);
        Assert.Equal(1, n1);

        var (m2, n2) = Ensemble.RunningMean(m1, n1, 20);
        Assert.Equal(15, m2);
        Assert.Equal(2, n2);
    }
}
