using Ingest.Core.Abstractions;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Sammelt die Kennzahlen eines Walk-Forward-Durchlaufs.
///
/// Gespeichert werden nicht die Millionen Einzelprognosen, sondern Summen je
/// Zeitfenster — daraus entsteht die Lernkurve, die die eigentliche Frage
/// beantwortet: wird die Prognose über den Verlauf besser?
///
/// Alles liegt in flachen Arrays, weil im heißen Pfad Millionen Aufrufe
/// erfolgen und Wörterbuch-Zugriffe dort spürbar wären.
/// </summary>
internal sealed class PassAggregate
{
    private readonly int[] _horizons;
    private readonly int _models;
    private readonly int _buckets;

    // [horizont, bucket]
    private readonly int[,] _hN;
    private readonly double[,] _hErr;
    private readonly int[,] _hHits;

    // [modell, bucket]
    private readonly int[,] _mN;
    private readonly double[,] _mErr;
    private readonly int[,] _mHits;

    // [modell, bucket] – Momentaufnahmen der Gewichte
    private readonly double[,] _wSum;
    private readonly int[] _wCount;

    public PassAggregate(int[] horizons, int models, int buckets)
    {
        _horizons = horizons;
        _models = models;
        _buckets = buckets;

        _hN = new int[horizons.Length, buckets];
        _hErr = new double[horizons.Length, buckets];
        _hHits = new int[horizons.Length, buckets];

        _mN = new int[models, buckets];
        _mErr = new double[models, buckets];
        _mHits = new int[models, buckets];

        _wSum = new double[models, buckets];
        _wCount = new int[buckets];
    }

    public void AddHorizon(int hIdx, int bucket, double absPctError, bool hit)
    {
        _hN[hIdx, bucket]++;
        _hErr[hIdx, bucket] += absPctError;
        if (hit) _hHits[hIdx, bucket]++;
    }

    public void AddModel(int modelIdx, int bucket, double error, bool hit)
    {
        _mN[modelIdx, bucket]++;
        _mErr[modelIdx, bucket] += error;
        if (hit) _mHits[modelIdx, bucket]++;
    }

    /// <summary>
    /// Hält fest, wie die Gewichte zu diesem Zeitpunkt aussahen. Erst dadurch
    /// wird sichtbar, wie sich der Einfluss der Teilmodelle über den Durchlauf
    /// verschiebt.
    /// </summary>
    public void SnapshotWeights(int bucket, double[] weights)
    {
        for (var m = 0; m < _models && m < weights.Length; m++) _wSum[m, bucket] += weights[m];
        _wCount[bucket]++;
    }

    public (double Mape, double HitRate) Overall()
    {
        long n = 0, hits = 0;
        double err = 0;

        for (var h = 0; h < _horizons.Length; h++)
            for (var b = 0; b < _buckets; b++)
            {
                n += _hN[h, b];
                err += _hErr[h, b];
                hits += _hHits[h, b];
            }

        return n == 0 ? (0, 0) : (err / n, (double)hits / n);
    }

    public IReadOnlyList<HorizonScore> ByHorizon()
    {
        var list = new List<HorizonScore>(_horizons.Length);

        for (var h = 0; h < _horizons.Length; h++)
        {
            long n = 0, hits = 0;
            double err = 0;

            for (var b = 0; b < _buckets; b++)
            {
                n += _hN[h, b];
                err += _hErr[h, b];
                hits += _hHits[h, b];
            }

            list.Add(new HorizonScore(_horizons[h], (int)n,
                n == 0 ? 0 : err / n,
                n == 0 ? 0 : (double)hits / n));
        }

        return list;
    }

    public IReadOnlyList<ModelScore> ByModel(string[] names)
    {
        var list = new List<ModelScore>(_models);

        for (var m = 0; m < _models; m++)
        {
            long n = 0, hits = 0;
            double err = 0, wSum = 0;
            var wN = 0;

            for (var b = 0; b < _buckets; b++)
            {
                n += _mN[m, b];
                err += _mErr[m, b];
                hits += _mHits[m, b];
                wSum += _wSum[m, b];
                wN += _wCount[b];
            }

            list.Add(new ModelScore(names[m], (int)n,
                n == 0 ? 0 : err / n,
                n == 0 ? 0 : (double)hits / n,
                wN == 0 ? 0 : wSum / wN));
        }

        return list;
    }

    /// <summary>Lernkurve je Zeitfenster und Horizont.</summary>
    public IReadOnlyList<CurvePoint> BuildCurve(DateTime[] grid, int bucketSize)
    {
        var list = new List<CurvePoint>();

        for (var b = 0; b < _buckets; b++)
        {
            var fromIdx = Math.Min(grid.Length - 1, b * bucketSize);
            var toIdx = Math.Min(grid.Length - 1, (b + 1) * bucketSize - 1);
            if (grid.Length == 0) break;

            for (var h = 0; h < _horizons.Length; h++)
            {
                var n = _hN[h, b];
                if (n == 0) continue;

                list.Add(new CurvePoint(b, _horizons[h], grid[fromIdx], grid[toIdx], n,
                    _hErr[h, b] / n, (double)_hHits[h, b] / n));
            }
        }

        return list;
    }

    public IReadOnlyList<ModelCurvePoint> BuildModelCurve(string[] names)
    {
        var list = new List<ModelCurvePoint>();

        for (var b = 0; b < _buckets; b++)
        {
            if (_wCount[b] == 0 && _mN[0, b] == 0) continue;

            for (var m = 0; m < _models; m++)
            {
                var n = _mN[m, b];

                list.Add(new ModelCurvePoint(b, names[m],
                    _wCount[b] == 0 ? 0 : _wSum[m, b] / _wCount[b],
                    n == 0 ? 0 : (double)_mHits[m, b] / n,
                    n == 0 ? 0 : _mErr[m, b] / n,
                    n));
            }
        }

        return list;
    }

    /// <summary>Horizont-Index zur Stundenzahl — für die Zuordnung beim Schreiben.</summary>
    public int HorizonAt(int index) => _horizons[index];
}
