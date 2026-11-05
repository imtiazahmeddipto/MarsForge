using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MarsForge.Core;

/// <summary>The Unity 6+ download: a zip with a ready Unity TerrainData (&lt;name&gt;_Terrain.asset) and its terrain layer
/// (&lt;name&gt;_Layer.terrainlayer, with the colour texture inside it), each with its .meta file (the ids that let
/// the terrain find its layer). Written directly as Unity text assets, the same format Unity 6 itself saves.
/// Unity axes: +X east, +Y up, +Z north, 1 unit = 1 m; the terrain's south-west corner is its origin.</summary>
public static class UnityExport
{
    const int MaxHeight16 = 32766;            // Unity heightmap samples run 0 .. 32766
    const int PatchQuads = 16;                // Unity terrain patch: 16 x 16 quads
    const long TerrainId = 15600000, SplatId = 2800000, LayerId = 8574412962073106934, TextureId = 2800002;

    /// <summary>Unity heightmap size: the smallest 2^n + 1 that keeps every mesh sample (33 .. 4097).</summary>
    public static int HeightmapResolution(TerrainMesh m)
    {
        int need = Math.Max(m.Nx, m.Ny), r = 33;
        while (r < need && r < 4097) r = 2 * (r - 1) + 1;
        return r;
    }

    /// <summary>Heights (metres above the lowest point) on a res x res grid; rows south to north, columns west to east.</summary>
    public static float[] Heightmap(TerrainMesh m, int res)
    {
        var h = new float[m.Nx * m.Ny];
        for (int i = 0; i < h.Length; i++) h[i] = m.Positions[3 * i + 1];
        var outp = new float[res * res];
        Parallel.For(0, res, j =>
        {
            double r = (m.Ny - 1) * (1 - j / (double)(res - 1));      // mesh rows run north to south
            for (int i = 0; i < res; i++)
                outp[j * res + i] = Math.Max(0f, Interp.Cubic(h, m.Nx, m.Ny, r, (m.Nx - 1) * i / (double)(res - 1)));
        });
        return outp;
    }

    public static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s) sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        return sb.Length > 0 ? sb.ToString() : "mars_terrain";
    }

    /// <summary>Saves the zip. rgba = the colour texture (north row first), the same one the FBX gets.</summary>
    public static void SaveZip(string zipPath, TerrainResult result, byte[] rgba, int texW, int texH)
    {
        var m = result.Mesh;
        string name = SafeName(Path.GetFileNameWithoutExtension(zipPath));
        var (sizeX, sizeZ) = m.Box.SizeM;
        int res = HeightmapResolution(m);
        var hm = Heightmap(m, res);
        double maxH = Math.Max(1.0, hm.Max());
        var h16 = new short[res * res];
        for (int i = 0; i < hm.Length; i++) h16[i] = (short)Math.Clamp(Math.Round(hm[i] / maxH * MaxHeight16), 0, MaxHeight16);

        string terrainGuid = Guid(name + "/terrain"), layerGuid = Guid(name + "/layer");
        string terrainYaml = TerrainYaml(name, h16, res, sizeX, sizeZ, maxH, layerGuid);
        string layerYaml = LayerYaml(name, rgba, texW, texH, sizeX, sizeZ);

        var files = new (string path, string text)[]
        {
            ($"{name}_Terrain.asset", terrainYaml),
            ($"{name}_Terrain.asset.meta", Meta(terrainGuid, TerrainId)),
            ($"{name}_Layer.terrainlayer", layerYaml),
            ($"{name}_Layer.terrainlayer.meta", Meta(layerGuid, LayerId)),
        };
        string tmp = zipPath + ".part";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            foreach (var (path, text) in files)
            {
                using var e = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
                e.Write(new UTF8Encoding(false).GetBytes(text));
            }
        File.Move(tmp, zipPath, overwrite: true);
    }

    // ------------------------------------------------------------------ terrain

    static string TerrainYaml(string name, short[] h16, int res, double sizeX, double sizeZ, double maxH, string layerGuid)
    {
        int levels = (int)Math.Round(Math.Log2((res - 1) / (double)PatchQuads));
        var (error, minMax) = Patches(h16, res, levels);
        var splat = new byte[16 * 16 * 4];
        for (int i = 0; i < 256; i++) splat[4 * i] = 255;                // layer 0 everywhere

        var y = new StringBuilder(h16.Length * 4 + 4096);
        y.Append("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n");
        y.Append($"--- !u!156 &{TerrainId}\nTerrainData:\n");
        Header(y, name + "_Terrain");
        y.Append("  m_SplatDatabase:\n    serializedVersion: 2\n");
        y.Append($"    m_TerrainLayers:\n    - {{fileID: {LayerId}, guid: {layerGuid}, type: 2}}\n");
        y.Append($"    m_AlphaTextures:\n    - {{fileID: {SplatId}}}\n");
        y.Append("    m_AlphamapResolution: 16\n    m_BaseMapResolution: 1024\n");
        y.Append("  m_DetailDatabase:\n    serializedVersion: 5\n");
        y.Append("    m_Patches:\n    - serializedVersion: 2\n      layerIndices: \n      coverage: \n");
        y.Append("    m_DetailPrototypes: []\n    m_PatchCount: 1\n    m_PatchSamples: 16\n");
        y.Append("    WavingGrassTint: {r: 0.7, g: 0.6, b: 0.5, a: 0}\n");
        y.Append("    m_WavingGrassStrength: 0.5\n    m_WavingGrassAmount: 0.5\n    m_WavingGrassSpeed: 0.5\n");
        y.Append("    m_DetailScatterMode: 0\n");
        y.Append("    m_TreeInstances: []\n    m_TreePrototypes: []\n    m_PreloadTextureAtlasData: []\n");
        y.Append("  m_Heightmap:\n    serializedVersion: 5\n");
        y.Append("    m_Heights: ").Append(Hex(MemoryMarshal.AsBytes(h16.AsSpan()))).Append('\n');
        y.Append("    m_Holes: \n    m_HolesLOD: \n    m_EnableHolesTextureCompression: 1\n");
        y.Append("    m_PrecomputedError: ").Append(Hex(MemoryMarshal.AsBytes(error.AsSpan()))).Append('\n');
        y.Append("    m_MinMaxPatchHeights: ").Append(Hex(MemoryMarshal.AsBytes(minMax.AsSpan()))).Append('\n');
        y.Append($"    m_Resolution: {res}\n    m_Levels: {levels}\n");
        y.Append($"    m_Scale: {{x: {F(sizeX / (res - 1))}, y: {F(maxH)}, z: {F(sizeZ / (res - 1))}}}\n");
        y.Append("  m_PreloadShaders: []\n");
        Texture(y, SplatId, "SplatAlpha 0", 16, 16, format: 4, mips: 1, splat, readable: true, srgb: false, filter: 1, aniso: 1);
        return y.ToString();
    }

    /// <summary>Unity's per-patch data, finest level first, rows south to north: the largest height error a
    /// coarser level makes (for its distance LOD) and each patch's min / max height (for culling); 0..1 units.</summary>
    static (float[] error, float[] minMax) Patches(short[] h16, int res, int levels)
    {
        var offset = new int[levels + 1];
        int total = 0;
        for (int l = 0; l <= levels; l++) { offset[l] = total; int n = 1 << (levels - l); total += n * n; }
        var error = new float[total];
        var minMax = new float[2 * total];
        float H(int x, int z) => h16[z * res + x] / (float)MaxHeight16;

        for (int l = 0; l <= levels; l++)
        {
            int n = 1 << (levels - l), span = PatchQuads << l, step = 1 << l;
            Parallel.For(0, n * n, p =>
            {
                int px = p % n, pz = p / n, x0 = px * span, z0 = pz * span;
                float lo = float.MaxValue, hi = float.MinValue, err = 0;
                for (int z = z0; z <= z0 + span; z++)
                    for (int x = x0; x <= x0 + span; x++)
                    {
                        float v = H(x, z);
                        if (v < lo) lo = v;
                        if (v > hi) hi = v;
                        if (l == 0) continue;
                        // what the coarse grid (every step-th sample) shows here
                        int cx = Math.Min(x - (x - x0) % step, x0 + span - step), cz = Math.Min(z - (z - z0) % step, z0 + span - step);
                        float fx = (x - cx) / (float)step, fz = (z - cz) / (float)step;
                        float c = (H(cx, cz) * (1 - fx) + H(cx + step, cz) * fx) * (1 - fz) + (H(cx, cz + step) * (1 - fx) + H(cx + step, cz + step) * fx) * fz;
                        err = Math.Max(err, Math.Abs(v - c));
                    }
                if (l > 0)                                                   // never less than its four children
                    for (int c = 0; c < 4; c++)
                        err = Math.Max(err, error[offset[l - 1] + (2 * pz + c / 2) * (2 * n) + 2 * px + c % 2]);
                int i = offset[l] + pz * n + px;
                error[i] = err;
                minMax[2 * i] = lo;
                minMax[2 * i + 1] = hi;
            });
        }
        return (error, minMax);
    }

    // ------------------------------------------------------------------ layer (+ its texture)

    static string LayerYaml(string name, byte[] rgba, int w, int h, double sizeX, double sizeZ)
    {
        var (dxt, mips) = Dxt1WithMips(rgba, w, h);
        var y = new StringBuilder(dxt.Length * 2 + 4096);
        y.Append("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n");
        y.Append($"--- !u!1953259897 &{LayerId}\nTerrainLayer:\n");
        Header(y, name + "_Layer");
        y.Append($"  m_DiffuseTexture: {{fileID: {TextureId}}}\n");
        y.Append("  m_NormalMapTexture: {fileID: 0}\n  m_MaskMapTexture: {fileID: 0}\n");
        y.Append($"  m_TileSize: {{x: {F(sizeX)}, y: {F(sizeZ)}}}\n");                 // one texture over the whole terrain
        y.Append("  m_TileOffset: {x: 0, y: 0}\n  m_Specular: {r: 0, g: 0, b: 0, a: 0}\n");
        y.Append("  m_Metallic: 0\n  m_Smoothness: 0.05\n  m_NormalScale: 1\n");
        y.Append("  m_DiffuseRemapMin: {x: 0, y: 0, z: 0, w: 0}\n  m_DiffuseRemapMax: {x: 1, y: 1, z: 1, w: 1}\n");
        y.Append("  m_MaskMapRemapMin: {x: 0, y: 0, z: 0, w: 0}\n  m_MaskMapRemapMax: {x: 1, y: 1, z: 1, w: 1}\n");
        y.Append("  m_SmoothnessSource: 0\n");
        Texture(y, TextureId, name + "_Texture", w, h, format: 10, mips, dxt, readable: false, srgb: true, filter: 2, aniso: 8);
        return y.ToString();
    }

    static void Texture(StringBuilder y, long id, string name, int w, int h, int format, int mips, byte[] data,
                        bool readable, bool srgb, int filter, int aniso)
    {
        y.Append($"--- !u!28 &{id}\nTexture2D:\n");
        Header(y, name);
        y.Append("  m_ImageContentsHash:\n    serializedVersion: 2\n    Hash: 00000000000000000000000000000000\n");
        y.Append("  m_IsAlphaChannelOptional: 0\n  serializedVersion: 3\n");
        y.Append($"  m_Width: {w}\n  m_Height: {h}\n  m_CompleteImageSize: {data.Length}\n  m_MipsStripped: 0\n");
        y.Append($"  m_TextureFormat: {format}\n  m_MipCount: {mips}\n  m_IsReadable: {(readable ? 1 : 0)}\n");
        y.Append("  m_IsPreProcessed: 0\n  m_IgnoreMipmapLimit: 0\n  m_MipmapLimitGroupName: \n");
        y.Append("  m_StreamingMipmaps: 0\n  m_StreamingMipmapsPriority: 0\n  m_VTOnly: 0\n  m_AlphaIsTransparency: 0\n");
        y.Append("  m_ImageCount: 1\n  m_TextureDimension: 2\n");
        y.Append($"  m_TextureSettings:\n    serializedVersion: 2\n    m_FilterMode: {filter}\n    m_Aniso: {aniso}\n    m_MipBias: 0\n");
        y.Append("    m_WrapU: 1\n    m_WrapV: 1\n    m_WrapW: 1\n");
        y.Append($"  m_LightmapFormat: 0\n  m_ColorSpace: {(srgb ? 1 : 0)}\n  m_PlatformBlob: \n");
        y.Append($"  image data: {data.Length}\n  _typelessdata: ").Append(Hex(data)).Append('\n');
        y.Append("  m_StreamData:\n    serializedVersion: 2\n    offset: 0\n    size: 0\n    path: \n");
    }

    /// <summary>DXT1 (BC1) blocks for every mip level down to 1 x 1. Unity textures start with the bottom row,
    /// so the picture (north row first) is flipped. Returns the data and the number of mip levels.</summary>
    static (byte[], int) Dxt1WithMips(byte[] rgba, int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int s = 4 * ((h - 1 - r) * w + c), d = 3 * (r * w + c);
                rgb[d] = rgba[s]; rgb[d + 1] = rgba[s + 1]; rgb[d + 2] = rgba[s + 2];
            }
        using var ms = new MemoryStream();
        int mips = 0;
        while (true)
        {
            ms.Write(Dxt1(rgb, w, h));
            mips++;
            if (w == 1 && h == 1) break;
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var next = new byte[nw * nh * 3];
            for (int r = 0; r < nh; r++)
                for (int c = 0; c < nw; c++)
                    for (int k = 0; k < 3; k++)
                    {
                        int r0 = Math.Min(2 * r, h - 1), r1 = Math.Min(2 * r + 1, h - 1), c0 = Math.Min(2 * c, w - 1), c1 = Math.Min(2 * c + 1, w - 1);
                        next[3 * (r * nw + c) + k] = (byte)((rgb[3 * (r0 * w + c0) + k] + rgb[3 * (r0 * w + c1) + k] + rgb[3 * (r1 * w + c0) + k] + rgb[3 * (r1 * w + c1) + k] + 2) / 4);
                    }
            rgb = next; w = nw; h = nh;
        }
        return (ms.ToArray(), mips);
    }

    static byte[] Dxt1(byte[] rgb, int w, int h)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        var outp = new byte[bw * bh * 8];
        Parallel.For(0, bh, by =>
        {
            Span<int> px = stackalloc int[48];
            Span<int> pal = stackalloc int[12];
            for (int bx = 0; bx < bw; bx++)
            {
                int[] lo = [255, 255, 255], hi = [0, 0, 0];
                for (int i = 0; i < 16; i++)
                {
                    int x = Math.Min(4 * bx + i % 4, w - 1), yy = Math.Min(4 * by + i / 4, h - 1);
                    for (int k = 0; k < 3; k++)
                    {
                        int v = rgb[3 * (yy * w + x) + k];
                        px[3 * i + k] = v;
                        lo[k] = Math.Min(lo[k], v); hi[k] = Math.Max(hi[k], v);
                    }
                }
                for (int k = 0; k < 3; k++)                                      // inset the box a little (less banding)
                {
                    int inset = (hi[k] - lo[k]) / 16;
                    lo[k] += inset; hi[k] -= inset;
                }
                int c0 = To565(hi), c1 = To565(lo);
                int o = 8 * (by * bw + bx);
                if (c0 == c1)
                {
                    outp[o] = (byte)c0; outp[o + 1] = (byte)(c0 >> 8); outp[o + 2] = (byte)c1; outp[o + 3] = (byte)(c1 >> 8);
                    continue;                                                     // all indices 0
                }
                if (c0 < c1) (c0, c1) = (c1, c0);                                 // c0 > c1: four-colour mode
                From565(c0, pal[..3]); From565(c1, pal[3..6]);
                for (int k = 0; k < 3; k++) { pal[6 + k] = (2 * pal[k] + pal[3 + k] + 1) / 3; pal[9 + k] = (pal[k] + 2 * pal[3 + k] + 1) / 3; }
                uint bits = 0;
                for (int i = 0; i < 16; i++)
                {
                    int best = 0, bestD = int.MaxValue;
                    for (int j = 0; j < 4; j++)
                    {
                        int dr = px[3 * i] - pal[3 * j], dg = px[3 * i + 1] - pal[3 * j + 1], db = px[3 * i + 2] - pal[3 * j + 2];
                        int d = dr * dr + dg * dg + db * db;
                        if (d < bestD) { bestD = d; best = j; }
                    }
                    bits |= (uint)best << (2 * i);
                }
                outp[o] = (byte)c0; outp[o + 1] = (byte)(c0 >> 8); outp[o + 2] = (byte)c1; outp[o + 3] = (byte)(c1 >> 8);
                outp[o + 4] = (byte)bits; outp[o + 5] = (byte)(bits >> 8); outp[o + 6] = (byte)(bits >> 16); outp[o + 7] = (byte)(bits >> 24);
            }
        });
        return outp;
    }

    static int To565(int[] c) => ((c[0] * 31 + 127) / 255 << 11) | ((c[1] * 63 + 127) / 255 << 5) | ((c[2] * 31 + 127) / 255);

    static void From565(int v, Span<int> c)
    {
        int r = (v >> 11) & 31, g = (v >> 5) & 63, b = v & 31;
        c[0] = (r << 3) | (r >> 2); c[1] = (g << 2) | (g >> 4); c[2] = (b << 3) | (b >> 2);
    }

    // ------------------------------------------------------------------ helpers

    static void Header(StringBuilder y, string name)
    {
        y.Append("  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n");
        y.Append("  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n");
        y.Append($"  m_Name: {name}\n");
    }

    static string Meta(string guid, long mainId) =>
        $"fileFormatVersion: 2\nguid: {guid}\nNativeFormatImporter:\n  externalObjects: {{}}\n  mainObjectFileID: {mainId}\n" +
        "  userData: \n  assetBundleName: \n  assetBundleVariant: \n";

    /// <summary>The same id for the same name, so exporting the same box again replaces it without breaking scenes.</summary>
    static string Guid(string key) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("MarsForge/" + key))).ToLowerInvariant();

    static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    static string F(double v) => ((float)v).ToString("R", CultureInfo.InvariantCulture);
}
