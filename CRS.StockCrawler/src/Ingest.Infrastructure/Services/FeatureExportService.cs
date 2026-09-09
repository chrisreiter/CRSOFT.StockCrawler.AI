using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ingest.Core.Abstractions;
using Ingest.Core.Analysis;
using Ingest.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Ingest.Infrastructure.Services;

/// <summary>
/// Baut die Merkmalsmatrix und schreibt sie als CSV — Stufe 2.
///
/// Das Ergebnis ist die Schnittstelle zum Training: alles, was das Modell über
/// den Markt wissen soll, steht in dieser Datei. Die Spaltenreihenfolge kommt
/// aus <see cref="FeatureSet"/>, damit Training und spätere Inferenz garantiert
/// dasselbe meinen.
/// </summary>
public sealed class FeatureExportService : IFeatureExportService
{
    private readonly IAssetRepository _assets;
    private readonly IPriceBarRepository _bars;
    private readonly IPairStatRepository _pairs;
    private readonly ILogger<FeatureExportService> _log;

    /* Der geladene Bestand wird kurz gehalten.

       `LoadAsync` holt die Kurse ALLER verfolgten Werte -- das ist die Natur
       der Sache, denn die Merkmale eines Wertes enthalten den Markt um ihn
       herum. Ein Fensteraufbau kostet damit rund zwei Sekunden.

       Bei der kombinierten Prognose ueber fuenf Werte und drei Baender sind das
       fuenfzehn Aufrufe: gemessen 29 Sekunden fuer einen Wert, 130 fuer fuenf.
       Fuer ein Diagramm, das sich beim Klicken neu zeichnet, ist das
       unbrauchbar -- und die Ladung ist dabei jedes Mal dieselbe.

       Fuenf Minuten sind unbedenklich: Tagesbars aendern sich einmal taeglich,
       Stundenbars stuendlich. Wer waehrend eines Kursabrufs genau in dieses
       Fenster faellt, sieht die Bar eine Ladung spaeter -- das ist weniger
       schlimm als zwei Minuten Wartezeit bei jedem Klick.

       Statisch, weil der Dienst je Anfrage neu gebaut wird (Scoped) und ein
       Zwischenspeicher je Instanz nie treffen wuerde. */
    private static readonly SemaphoreSlim _ladeSperre = new(1, 1);

    private static readonly Dictionary<string, (DateTime Stand,
                                                List<FeatureAsset> Assets,
                                                DateTime[] Grid)> _bestand = [];

    private static readonly TimeSpan _haltbarkeit = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Verwirft den Zwischenspeicher. Nach einem Kursabruf aufzurufen, sonst
    /// sieht die Prognose bis zu fünf Minuten lang die alten Bars.
    /// </summary>
    public static void BestandVerwerfen()
    {
        lock (_bestand) _bestand.Clear();
    }

    public FeatureExportService(
        IAssetRepository assets,
        IPriceBarRepository bars,
        IPairStatRepository pairs,
        ILogger<FeatureExportService> log)
    {
        _assets = assets;
        _bars = bars;
        _pairs = pairs;
        _log = log;
    }

    public async Task<FeatureExportResult> ExportAsync(
        string intervalCode, int[] horizonBars, string? outputPath, CancellationToken ct = default)
    {
        if (!BarInterval.IsValid(intervalCode))
            throw new ArgumentException($"Unbekanntes Intervall: {intervalCode}", nameof(intervalCode));

        var sw = Stopwatch.StartNew();

        var path = outputPath ?? Path.Combine(
            Directory.GetCurrentDirectory(), "export", $"features_{intervalCode}.csv");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var (assets, grid) = await LoadAsync(intervalCode, ct);
        if (assets.Count < 2)
            throw new InvalidOperationException("Zu wenige Werte mit ausreichender Historie.");

        var byId = assets.ToDictionary(a => a.AssetId);
        var (market, classR1, classShare) = FeatureMatrix.ComputeMarket(assets, grid.Length);

        _log.LogInformation(
            "Merkmalsexport {Interval}: {Assets} Werte, {Points} Zeitpunkte, Ziel {Path}",
            intervalCode, assets.Count, grid.Length, path);

        long rows = 0;
        var buf = new double[FeatureSet.Count];
        var inv = CultureInfo.InvariantCulture;

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
                                                FileShare.None, 1 << 20);
        await using var w = new StreamWriter(stream, new UTF8Encoding(false));

        // Kopfzeile
        var header = new StringBuilder("ts_utc,asset_id,symbol,asset_class");
        foreach (var n in FeatureSet.Names) header.Append(',').Append(n);
        foreach (var h in horizonBars) header.Append(",y_h").Append(h);
        await w.WriteLineAsync(header.ToString());

        var line = new StringBuilder(512);

        for (var t = FeatureSet.MinHistoryBars; t < grid.Length; t++)
        {
            if ((t & 255) == 0) ct.ThrowIfCancellationRequested();

            foreach (var a in assets)
            {
                if (!FeatureMatrix.TryBuildRow(a, byId, market, classR1, classShare, t, buf))
                    continue;

                /* Zeilen ohne einzige verwertbare Zielgröße wegzulassen spart
                   spürbar Platz — am rechten Rand der Historie fehlt für die
                   langen Horizonte naturgemäß die Zukunft. */
                var targets = horizonBars
                    .Select(h => FeatureMatrix.ForwardReturn(a.Close, t, h))
                    .ToArray();

                if (targets.All(double.IsNaN)) continue;

                line.Clear();
                line.Append(grid[t].ToString("yyyy-MM-dd HH:mm:ss", inv)).Append(',')
                    .Append(a.AssetId).Append(',')
                    .Append(a.Symbol).Append(',')
                    .Append(a.AssetClass);

                foreach (var v in buf)
                    line.Append(',').Append(v.ToString("G9", inv));

                foreach (var y in targets)
                    line.Append(',').Append(double.IsNaN(y) ? "" : y.ToString("G9", inv));

                await w.WriteLineAsync(line);
                rows++;
            }
        }

        await w.FlushAsync(ct);

        _log.LogInformation("Merkmalsexport fertig: {Rows} Zeilen in {Elapsed:n1}s",
            rows, sw.Elapsed.TotalSeconds);

        return new FeatureExportResult(path, intervalCode, assets.Count, grid.Length,
            rows, horizonBars, FeatureSet.Version, sw.Elapsed);
    }

    /// <inheritdoc />
    public async Task<FeatureHistory?> BuildHistoryAsync(
        int assetId, string intervalCode, CancellationToken ct = default)
    {
        if (!BarInterval.IsValid(intervalCode)) return null;

        var (assets, grid) = await LoadAsync(intervalCode, ct);
        if (assets.Count < 2) return null;

        var byId = assets.ToDictionary(a => a.AssetId);
        if (!byId.TryGetValue(assetId, out var self)) return null;

        var (market, classR1, classShare) = FeatureMatrix.ComputeMarket(assets, grid.Length);

        var rows = new List<double[]>(grid.Length);
        var close = new List<double>(grid.Length);
        var ts = new List<DateTime>(grid.Length);

        var buf = new double[FeatureSet.Count];

        /* Dieselbe Auswahl wie beim Export: nur Positionen, an denen sich für
           DIESEN Wert eine vollständige Zeile bauen lässt. Damit entsteht genau
           die Folge, aus der das Training seine Sequenzen geschnitten hat. */
        for (var t = FeatureSet.MinHistoryBars; t < grid.Length; t++)
        {
            if (!FeatureMatrix.TryBuildRow(self, byId, market, classR1, classShare, t, buf))
                continue;

            var c = self.Close[t];
            if (double.IsNaN(c) || c <= 0) continue;

            rows.Add((double[])buf.Clone());
            close.Add(c);
            ts.Add(grid[t]);
        }

        return rows.Count == 0 ? null : new FeatureHistory(rows, close, ts);
    }

    /// <summary>
    /// Lädt Kurse und Umsätze aller verfolgten Werte auf ein gemeinsames Raster
    /// und hängt Nachbarn und Frühindikatoren aus der Analyse an.
    /// </summary>
    /// <inheritdoc />
    public async Task<FeatureWindow?> BuildWindowAsync(
        int assetId, string intervalCode, int seqLen, CancellationToken ct = default)
    {
        if (!BarInterval.IsValid(intervalCode)) return null;

        var (assets, grid) = await LoadAsync(intervalCode, ct);
        if (assets.Count < 2 || grid.Length < seqLen + FeatureSet.MinHistoryBars) return null;

        var byId = assets.ToDictionary(a => a.AssetId);
        if (!byId.TryGetValue(assetId, out var self)) return null;

        var (market, classR1, classShare) = FeatureMatrix.ComputeMarket(assets, grid.Length);

        /* Rückwärts durch das Raster, und nur verwertbare Zeilen DIESES Wertes
           sammeln.

           Der erste Entwurf nahm einfach die letzten seqLen Rasterpositionen.
           Das Raster ist aber die Vereinigung über alle verfolgten Werte, und
           Krypto handelt am Wochenende — eine Aktie hat dort keine Zeile, und
           das Fenster brach ab, bevor es begonnen hatte.

           Entscheidender noch: Beim Training entstehen die Sequenzen aus
           aufeinanderfolgenden Zeilen desselben Wertes, denn der Export
           schreibt nur verwertbare Zeilen. Die Ausführung muss dieselbe
           Reihenfolge herstellen, sonst sieht das Modell eine andere Art von
           Sequenz als die, auf der es gelernt hat — und rechnet stillschweigend
           falsch. */
        var stack = new List<double[]>(seqLen);
        var buf = new double[FeatureSet.Count];

        var lastIdx = -1;

        for (var t = grid.Length - 1; t >= FeatureSet.MinHistoryBars && stack.Count < seqLen; t--)
        {
            if (!FeatureMatrix.TryBuildRow(self, byId, market, classR1, classShare, t, buf))
                continue;

            if (lastIdx < 0) lastIdx = t;

            stack.Add((double[])buf.Clone());
        }

        if (stack.Count < seqLen || lastIdx < 0) return null;

        // Rückwärts gesammelt, vorwärts gebraucht.
        stack.Reverse();

        var lastClose = self.Close[lastIdx];
        if (double.IsNaN(lastClose) || lastClose <= 0) return null;

        return new FeatureWindow(stack, lastClose, grid[lastIdx]);
    }

    private async Task<(List<FeatureAsset> Assets, DateTime[] Grid)> LoadAsync(
        string intervalCode, CancellationToken ct)
    {
        lock (_bestand)
        {
            if (_bestand.TryGetValue(intervalCode, out var da)
                && DateTime.UtcNow - da.Stand < _haltbarkeit)
                return (da.Assets, da.Grid);
        }

        /* Eine Sperre um das Laden.

           Ohne sie laden bei einem Diagramm mit fuenf Werten fuenf Anfragen
           gleichzeitig denselben Bestand -- der Zwischenspeicher greift erst,
           wenn die erste fertig ist, und bis dahin ist die Arbeit fuenffach
           getan. Wer wartet, findet danach den fertigen Bestand vor. */
        await _ladeSperre.WaitAsync(ct);

        try
        {
            lock (_bestand)
            {
                if (_bestand.TryGetValue(intervalCode, out var da2)
                    && DateTime.UtcNow - da2.Stand < _haltbarkeit)
                    return (da2.Assets, da2.Grid);
            }

            var geladen = await LadeFrischAsync(intervalCode, ct);

            lock (_bestand)
                _bestand[intervalCode] = (DateTime.UtcNow, geladen.Assets, geladen.Grid);

            return geladen;
        }
        finally
        {
            _ladeSperre.Release();
        }
    }

    private async Task<(List<FeatureAsset> Assets, DateTime[] Grid)> LadeFrischAsync(
        string intervalCode, CancellationToken ct)
    {
        var tracked = await _assets.GetTrackedAsync(ct);

        var series = await _bars.GetManyAsync(
            tracked.Select(a => a.AssetId).ToArray(), intervalCode,
            new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.UtcNow, ct);

        var usable = series
            .Where(kv => kv.Value.Count >= FeatureSet.MinHistoryBars + 5)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var grid = usable.Values
            .SelectMany(v => v.Select(b => b.TsUtc))
            .Distinct()
            .OrderBy(t => t)
            .ToArray();

        var index = new Dictionary<DateTime, int>(grid.Length);
        for (var i = 0; i < grid.Length; i++) index[grid[i]] = i;

        var meta = tracked.ToDictionary(a => a.AssetId);
        var result = new List<FeatureAsset>(usable.Count);

        foreach (var (id, bars) in usable)
        {
            if (!meta.TryGetValue(id, out var asset)) continue;

            var close = new double[grid.Length];
            var flow = new double[grid.Length];
            Array.Fill(close, double.NaN);
            Array.Fill(flow, double.NaN);

            foreach (var b in bars)
            {
                if (!index.TryGetValue(b.TsUtc, out var g)) continue;

                close[g] = (double)b.Close;

                // Krypto meldet das Volumen bereits in Dollar — siehe FlowMetrics.
                var mf = FlowMetrics.MoneyFlow(asset.AssetClass, b.Close, b.Volume);
                if (mf is not null) flow[g] = (double)mf.Value;
            }

            result.Add(new FeatureAsset
            {
                AssetId = id,
                Symbol = asset.Symbol,
                AssetClass = asset.AssetClass,
                Close = close,
                Flow = flow
            });
        }

        await AttachRelationsAsync(result, intervalCode, ct);
        return (result, grid);
    }

    /// <summary>
    /// Hängt je Wert die stärksten Nachbarn und Frühindikatoren an. Ohne diese
    /// Beziehungen bliebe die Matrix eine Sammlung isolierter Kurse.
    /// </summary>
    private async Task AttachRelationsAsync(
        List<FeatureAsset> assets, string intervalCode, CancellationToken ct)
    {
        var known = assets.Select(a => a.AssetId).ToHashSet();

        foreach (var a in assets)
        {
            var stats = await _pairs.GetForAsync(a.AssetId, intervalCode, ct);

            a.Neighbours = stats
                .Select(s => (Id: s.AssetIdA == a.AssetId ? s.AssetIdB : s.AssetIdA, s.Corr0))
                .Where(x => known.Contains(x.Id) && Math.Abs(x.Corr0) > 0.05)
                .OrderByDescending(x => Math.Abs(x.Corr0))
                .Take(FeatureSet.NeighbourCount)
                .Select(x => (x.Id, x.Corr0))
                .ToArray();

            var leaders = await _pairs.GetTopLeadersAsync(
                a.AssetId, intervalCode, FeatureSet.NeighbourCount, ct);

            a.Leaders = leaders
                .Where(l => known.Contains(l.AssetIdA))
                .Select(l => (l.AssetIdA, Math.Abs(l.BestLagBars), l.BestLagCorr))
                .ToArray();
        }
    }
}
