using System.IO.Compression;

namespace MarsForge.Core;

/// <summary>Small PNG writer (grey or RGBA, 8 bits).</summary>
public static class Png
{
    public static byte[] Gray(byte[] pixels, int w, int h) => Encode(pixels, w, h, 1, 0);
    public static byte[] Rgba(byte[] pixels, int w, int h) => Encode(pixels, w, h, 4, 6);

    static byte[] Encode(byte[] px, int w, int h, int channels, byte colorType)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            int stride = w * channels;
            var line = new byte[stride + 1];
            for (int r = 0; r < h; r++)
            {
                line[0] = 0;
                Buffer.BlockCopy(px, r * stride, line, 1, stride);
                z.Write(line, 0, line.Length);
            }
        }
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)w);
        WriteBE(ihdr, 4, (uint)h);
        ihdr[8] = 8;
        ihdr[9] = colorType;
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", raw.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len);
        var td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, td, 4, data.Length);
        s.Write(td);
        var crc = new byte[4];
        WriteBE(crc, 0, Crc32(td));
        s.Write(crc);
    }

    static void WriteBE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    static readonly uint[] Table = MakeTable();

    static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
