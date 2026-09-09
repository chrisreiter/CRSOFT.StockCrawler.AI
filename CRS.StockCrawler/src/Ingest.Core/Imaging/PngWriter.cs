using System.IO.Compression;

namespace Ingest.Core.Imaging;

/// <summary>
/// Minimaler PNG-Schreiber für RGB-Bilder.
///
/// <b>Warum selbst geschrieben.</b> Gebraucht wird genau eine Sache: ein
/// Rasterbild aus einem Pixelfeld erzeugen, damit es an ein Bildmodell
/// geschickt werden kann. <c>System.Drawing</c> dafür aufzunehmen hieße, eine
/// Bibliothek mit nativen Anteilen und Plattformbindung mitzuschleppen, die
/// außerhalb von Windows nicht mehr unterstützt wird — für Rechteck-Malen und
/// Linienziehen. PNG ohne Vorfilterung ist ein Header, ein Zlib-Strom und eine
/// Prüfsumme.
///
/// Bewusst ohne Zeilenfilter (Filtertyp 0). Filter würden die Datei kleiner
/// machen; bei Diagrammen von wenigen hundert Pixeln Kantenlänge geht es um
/// Kilobytes, und der Empfänger ist ein Sprachmodell, kein Netzwerk.
/// </summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// Schreibt ein RGB-Bild. <paramref name="rgb"/> enthält je Pixel drei
    /// Bytes, zeilenweise von oben nach unten.
    /// </summary>
    public static byte[] Write(int width, int height, byte[] rgb)
    {
        if (rgb.Length != width * height * 3)
            throw new ArgumentException($"Erwartet {width * height * 3} Bytes, bekam {rgb.Length}");

        using var ms = new MemoryStream();

        // Signatur.
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR: Breite, Höhe, 8 Bit je Kanal, Farbtyp 2 (RGB), keine Verflechtung.
        var ihdr = new byte[13];
        WriteInt(ihdr, 0, width);
        WriteInt(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 2;

        WriteChunk(ms, "IHDR", ihdr);

        /* Jede Zeile bekommt ein führendes Filterbyte. Ohne dieses Byte ist der
           Strom um genau height Bytes zu kurz, und jeder Decoder liest die
           Zeilen um eine Position verschoben — das Bild sieht dann aus wie
           schräg gescherter Rauschteppich. */
        var raw = new byte[height * (1 + width * 3)];

        for (var y = 0; y < height; y++)
        {
            var dst = y * (1 + width * 3);
            raw[dst] = 0;
            Array.Copy(rgb, y * width * 3, raw, dst + 1, width * 3);
        }

        WriteChunk(ms, "IDAT", ZlibCompress(raw));
        WriteChunk(ms, "IEND", []);

        return ms.ToArray();
    }

    /// <summary>
    /// Zlib-Hülle um den rohen Deflate-Strom.
    ///
    /// <c>DeflateStream</c> liefert Deflate ohne Hülle; PNG verlangt aber Zlib.
    /// Das sind zwei Kopfbytes davor und eine Adler-Prüfsumme dahinter. Fehlen
    /// sie, meldet jeder Decoder einen kaputten Datenstrom.
    /// </summary>
    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();

        ms.WriteByte(0x78);   // 32K Fenster, Deflate
        ms.WriteByte(0x9C);   // Standardstufe, Prüfbits stimmen

        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(data, 0, data.Length);

        var adler = Adler32(data);
        ms.WriteByte((byte)(adler >> 24));
        ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));
        ms.WriteByte((byte)adler);

        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteInt(len, 0, data.Length);
        s.Write(len);

        var head = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) head[i] = (byte)type[i];
        Array.Copy(data, 0, head, 4, data.Length);

        s.Write(head);

        var crc = new byte[4];
        WriteInt(crc, 0, (int)Crc32(head));
        s.Write(crc);
    }

    private static void WriteInt(byte[] b, int off, int v)
    {
        b[off] = (byte)(v >> 24);
        b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8);
        b[off + 3] = (byte)v;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }

        return t;
    }

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;

        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }
}
