namespace Ingest.Core.Imaging;

/// <summary>Eine Kurve mit ihrer Farbe und Beschriftung.</summary>
public sealed record CurveLayer(string Label, double[] Values, byte R, byte G, byte B);

/// <summary>
/// Zeichnet Kursausschnitte als Bild — für die Vorlage an ein Bildmodell.
///
/// <b>Was hier bewusst NICHT gezeichnet wird:</b> keine Achsenbeschriftung,
/// keine Kurswerte, kein Datum, kein Symbol. Ein Bildmodell liest Text sehr
/// viel zuverlässiger als es Kurvenformen vergleicht. Stünde der Kursbereich
/// im Bild, würde das Modell mit hoher Wahrscheinlichkeit über Zahlen urteilen
/// statt über die Form — und die Antwort sähe fundiert aus, ohne es zu sein.
///
/// Aus demselben Grund wird jede Kurve auf denselben Wertebereich normiert.
/// Verglichen werden soll der <i>Verlauf</i>, nicht die Höhe: Ein Anstieg von
/// 10 auf 12 Dollar und einer von 300 auf 360 sind dieselbe Bewegung.
/// </summary>
public static class CurveRenderer
{
    private static readonly byte[] Background = [0x0F, 0x12, 0x16];
    private static readonly byte[] GridColor = [0x25, 0x2B, 0x33];

    /// <summary>
    /// Zeichnet eine oder mehrere Kurven übereinander in ein PNG.
    /// </summary>
    /// <param name="normalizePerCurve">
    /// Jede Kurve für sich auf 0..1 strecken. Für den Formvergleich richtig:
    /// Zwei Verläufe sollen sich decken, wenn sie dieselbe Gestalt haben, auch
    /// wenn der eine dreimal so stark ausschlug. Für die Darstellung
    /// tatsächlicher Größenverhältnisse falsch.
    /// </param>
    public static byte[] Render(
        IReadOnlyList<CurveLayer> layers,
        int width = 384,
        int height = 256,
        bool normalizePerCurve = true,
        bool grid = true)
    {
        var px = new byte[width * height * 3];

        for (var i = 0; i < width * height; i++)
        {
            px[i * 3] = Background[0];
            px[i * 3 + 1] = Background[1];
            px[i * 3 + 2] = Background[2];
        }

        if (grid) DrawGrid(px, width, height);

        // Gemeinsamer Bereich, falls nicht je Kurve normiert wird.
        double gMin = double.MaxValue, gMax = double.MinValue;

        if (!normalizePerCurve)
        {
            foreach (var l in layers)
                foreach (var v in l.Values)
                {
                    if (double.IsNaN(v)) continue;
                    if (v < gMin) gMin = v;
                    if (v > gMax) gMax = v;
                }
        }

        foreach (var layer in layers)
        {
            if (layer.Values.Length < 2) continue;

            double lo = gMin, hi = gMax;

            if (normalizePerCurve)
            {
                lo = double.MaxValue;
                hi = double.MinValue;

                foreach (var v in layer.Values)
                {
                    if (double.IsNaN(v)) continue;
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
            }

            // Eine waagerechte Linie hat keinen Bereich — in die Mitte legen.
            var span = hi - lo;
            if (span <= 0 || double.IsInfinity(span)) span = 1;

            var pad = 8;
            var w = width - 2 * pad;
            var h = height - 2 * pad;

            var n = layer.Values.Length;

            int? prevX = null, prevY = null;

            for (var i = 0; i < n; i++)
            {
                var v = layer.Values[i];
                if (double.IsNaN(v)) { prevX = null; prevY = null; continue; }

                var x = pad + (int)Math.Round((double)i / (n - 1) * (w - 1));
                var y = pad + (h - 1) - (int)Math.Round((v - lo) / span * (h - 1));

                y = Math.Clamp(y, 0, height - 1);

                if (prevX is { } px0 && prevY is { } py0)
                    DrawLine(px, width, height, px0, py0, x, y, layer.R, layer.G, layer.B);

                prevX = x;
                prevY = y;
            }
        }

        return PngWriter.Write(width, height, px);
    }

    private static void DrawGrid(byte[] px, int w, int h)
    {
        /* Ein grobes Raster, damit das Modell überhaupt einen Bezug hat.
           Zu fein wäre schädlich: Es würde als Struktur mitgelesen. */
        for (var i = 1; i < 4; i++)
        {
            var y = h * i / 4;
            for (var x = 0; x < w; x++) Set(px, w, h, x, y, GridColor[0], GridColor[1], GridColor[2]);

            var xx = w * i / 4;
            for (var y2 = 0; y2 < h; y2++) Set(px, w, h, xx, y2, GridColor[0], GridColor[1], GridColor[2]);
        }
    }

    /// <summary>Bresenham, zusätzlich eine Zeile dick — sonst reißt die Linie optisch.</summary>
    private static void DrawLine(byte[] px, int w, int h, int x0, int y0, int x1, int y1,
                                 byte r, byte g, byte b)
    {
        var dx = Math.Abs(x1 - x0);
        var dy = -Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            Set(px, w, h, x0, y0, r, g, b);
            Set(px, w, h, x0, y0 + 1, r, g, b);

            if (x0 == x1 && y0 == y1) break;

            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private static void Set(byte[] px, int w, int h, int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return;

        var i = (y * w + x) * 3;
        px[i] = r;
        px[i + 1] = g;
        px[i + 2] = b;
    }

    /// <summary>
    /// Zwei Ausschnitte nebeneinander, in einem Durchgang gezeichnet.
    /// </summary>
    public static byte[] Pair(double[] left, double[] right,
                              int width = 512, int height = 256)
    {
        var px = new byte[width * height * 3];

        for (var i = 0; i < width * height; i++)
        {
            px[i * 3] = Background[0];
            px[i * 3 + 1] = Background[1];
            px[i * 3 + 2] = Background[2];
        }

        var half = width / 2;

        DrawInto(px, width, height, 0, half, left, 0x6A, 0xA9, 0xFF);
        DrawInto(px, width, height, half, half, right, 0xFF, 0xCF, 0x70);

        for (var y = 0; y < height; y++)
        {
            Set(px, width, height, half - 1, y, 0x5A, 0x63, 0x6E);
            Set(px, width, height, half, y, 0x5A, 0x63, 0x6E);
        }

        return PngWriter.Write(width, height, px);
    }

    private static void DrawInto(byte[] px, int w, int h, int x0, int panelW,
                                 double[] values, byte r, byte g, byte b)
    {
        if (values.Length < 2) return;

        double lo = double.MaxValue, hi = double.MinValue;

        foreach (var v in values)
        {
            if (double.IsNaN(v)) continue;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }

        var span = hi - lo;
        if (span <= 0 || double.IsInfinity(span)) span = 1;

        const int pad = 10;
        var pw = panelW - 2 * pad;
        var ph = h - 2 * pad;

        int? prevX = null, prevY = null;

        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (double.IsNaN(v)) { prevX = null; prevY = null; continue; }

            var x = x0 + pad + (int)Math.Round((double)i / (values.Length - 1) * (pw - 1));
            var y = pad + (ph - 1) - (int)Math.Round((v - lo) / span * (ph - 1));

            y = Math.Clamp(y, 0, h - 1);

            if (prevX is { } a && prevY is { } c)
                DrawLine(px, w, h, a, c, x, y, r, g, b);

            prevX = x;
            prevY = y;
        }
    }
}
