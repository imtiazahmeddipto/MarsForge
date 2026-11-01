using System.IO.Compression;

namespace MarsForge.Core;

public enum RockSize { All, MediumAndBig, OnlyBig }

public sealed record TerrainOptions(SurfaceDetail Detail, RockSize RockSize, bool Rocks, bool SoftGround)
{
    public double MinRockM => RockSize switch { RockSize.All => 1.0, RockSize.MediumAndBig => 2.0, _ => 4.0 };
}

/// <summary>Everything made for one box.</summary>
public sealed class TerrainResult
{
    public required TerrainMesh Mesh { get; init; }
    public required TerrainOptions Options { get; init; }
    public RockSet? Rocks { get; init; }
    public SoftGroundMap? Soil { get; init; }
    public required GrayImage GroundTexture { get; init; }
    public GrayImage? RiskTexture { get; init; }
}

/// <summary>Box -> heights, terrain, rocks, soft ground and texture. Progress messages are short and plain.</summary>
public sealed class TerrainJob(IGeoServices services, string modelsFolder, string? localDemFile = null)
{
    public const double HiriseMaxSideM = 2100.0;

    public async Task<TerrainResult> RunAsync(GeoBox box, TerrainOptions opt, IProgress<string> progress, CancellationToken ct)
    {
        Log.Write(FormattableString.Invariant($"Generate box W{box.West:F5} S{box.South:F5} E{box.East:F5} N{box.North:F5} {opt}"));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        progress.Report("Downloading heights and creating terrain…");
        string demPath = localDemFile is not null && File.Exists(localDemFile) ? localDemFile : Trek.Vsi(TerrainBuilder.DemUrl);
        using var demSrc = await Task.Run(() => services.OpenRaster(demPath), ct);
        var mesh = await Task.Run(() => TerrainBuilder.Build(demSrc, box, opt.Detail), ct);
        Log.Write($"  terrain {mesh.Nx}x{mesh.Ny} ({clock.Elapsed.TotalSeconds:F1} s)");
        var (W, H) = box.SizeM;
        RockSet? rocks = null;
        SoftGroundMap? soil = null;

        if (opt.Rocks || opt.SoftGround)
        {
            progress.Report("Downloading Mars images…");
            var trek = new Trek(services);
            var big = box.Grow(1200);
            var ctxTask = Trek_(trek, Trek.Ctx, big, ct);
            var themisTask = Trek_(trek, Trek.Themis, big, ct);
            Raster? demR = null;
            try { demR = await Task.Run(() => Raster.FromSource(demSrc, big), ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Error("Elevation window", ex); }
            var ctx = await ctxTask;
            var themis = await themisTask;
            Log.Write($"  CTX {(ctx is null ? "none" : $"{ctx.Width}x{ctx.Height}")}, THEMIS {(themis is null ? "none" : $"{themis.Width}x{themis.Height}")}, " +
                      $"elevation {(demR is null ? "none" : $"{demR.Width}x{demR.Height}")} ({clock.Elapsed.TotalSeconds:F1} s)");

            List<RateBlock>? rates = null;
            RockModel? model = null;
            string onnx = Path.Combine(modelsFolder, "rock_density_v1.onnx"), info = Path.Combine(modelsFolder, "rock_density_v1.json");
            if (File.Exists(onnx) && File.Exists(info))
            {
                model = new RockModel(services.LoadModel(onnx), File.ReadAllText(info));
                if (opt.Rocks || opt.SoftGround)
                {
                    progress.Report("Predicting rocks…");
                    rates = await Task.Run(() => Rates(model, ctx, themis, demR, box), ct);
                }
            }
            if (opt.Rocks)
            {
                List<(double, double, FoundRock)>? real = null;
                Func<double, double, bool>? covered = null;
                if (Math.Max(W, H) <= HiriseMaxSideM)
                {
                    progress.Report("Looking for HiRISE photos…");
                    try { (real, covered) = await RealRocks(trek, box, progress, ct); }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Error("HiRISE", ex); }   // no HiRISE: all rocks predicted
                    Log.Write($"  HiRISE rocks: {(real is null ? "none" : real.Count.ToString())}");
                }
                progress.Report("Placing rocks…");
                rocks = RockPlacer.Place(rates, model, real, covered, box, opt.MinRockM, box.Seed);
                Log.Write($"  rocks {rocks.Count} (from {rocks.MinDiameterM:F1} m)");
            }
            model?.Dispose();
            if (opt.SoftGround)
            {
                progress.Report("Mapping soft ground…");
                soil = await Task.Run(() => SoftGround.Map(ctx, themis, demR, box, rates), ct);
                Log.Write($"  soft ground: {(soil is null ? "none" : $"{soil.Zones} zones")}");
            }
        }

        progress.Report("Making texture…");
        var ground = await Task.Run(() => Textures.Ground(W, H, box.Seed), ct);
        GrayImage? riskTex = soil is null ? null : await Task.Run(() => Textures.Risk(soil.Risk, soil.Width, soil.Height, W, H, box.Seed), ct);
        Log.Write($"  done ({clock.Elapsed.TotalSeconds:F1} s)");
        return new TerrainResult { Mesh = mesh, Options = opt, Rocks = rocks, Soil = soil, GroundTexture = ground, RiskTexture = riskTex };
    }

    static async Task<Raster?> Trek_(Trek trek, Trek.Layer layer, GeoBox box, CancellationToken ct)
    {
        try { return await trek.Tiles(layer, box, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Error("Map layer " + layer.Url, ex); return null; }
    }

    /// <summary>Rocks per 50 m square, block by block (each block = 1 km; inputs = 2 km around it).</summary>
    static List<RateBlock> Rates(RockModel model, Raster? ctx, Raster? themis, Raster? dem, GeoBox box)
    {
        var (W, H) = box.SizeM;
        int nbx = Math.Max(1, (int)Math.Ceiling(W / RockModel.BlockM)), nby = Math.Max(1, (int)Math.Ceiling(H / RockModel.BlockM));
        var centres = new List<(double lon, double lat)>();
        for (int j = 0; j < nby; j++)
            for (int i = 0; i < nbx; i++)
            {
                double ce = -nbx * RockModel.BlockM / 2 + RockModel.BlockM * (i + 0.5);
                double cn = nby * RockModel.BlockM / 2 - RockModel.BlockM * (j + 0.5);
                centres.Add(Mars.EnToLonLat(ce, cn, box.CenterLon, box.CenterLat));
            }
        var windows = new float[centres.Count][];
        Parallel.For(0, centres.Count, k => windows[k] = Features.Window(ctx, themis, dem, centres[k].lon, centres[k].lat));
        var outp = new List<RateBlock>();
        for (int k = 0; k < centres.Count; k += 8)
        {
            var batch = windows.Skip(k).Take(8).ToList();
            var r = model.Rates(batch);
            for (int i = 0; i < batch.Count; i++) outp.Add(new RateBlock(centres[k + i].lon, centres[k + i].lat, r[i]));
        }
        return outp;
    }

    async Task<(List<(double, double, FoundRock)>?, Func<double, double, bool>?)> RealRocks(Trek trek, GeoBox box, IProgress<string> progress, CancellationToken ct)
    {
        string detector = Path.Combine(modelsFolder, "rockboxnet_v1.onnx");
        if (!File.Exists(detector)) return (null, null);
        foreach (var site in await trek.HiriseSites(box, ct))
        {
            progress.Report("Downloading HiRISE photo…");
            var img = await Task.Run(() => trek.HiriseWindow(site, box), ct);
            if (img is null || img.Data.Count(v => v > 0) < 0.05 * img.Data.Length) continue;
            progress.Report("Finding real rocks…");
            using var model = services.LoadModel(detector);
            var found = await Task.Run(() => new RockDetector(model).Detect(img.Data, img.Width, img.Height, img.PixelM), ct);
            var gt = img.GeoTransform;
            int n = found.Count;
            var x = new double[n];
            var y = new double[n];
            for (int i = 0; i < n; i++) { x[i] = gt[0] + (found[i].X + 0.5) * gt[1]; y[i] = gt[3] + (found[i].Y + 0.5) * gt[5]; }
            var lon = new double[n];
            var lat = new double[n];
            img.Projection.ToLonLat(x, y, lon, lat);
            var real = Enumerable.Range(0, n).Select(i => (Mars.Wrap180(lon[i]), lat[i], found[i])).ToList();
            bool Covered(double lo, double la)
            {
                Span<double> mx = stackalloc double[1];
                Span<double> my = stackalloc double[1];
                img.Projection.ToMap([lo], [la], mx, my);
                int c = (int)Math.Floor((mx[0] - gt[0]) / gt[1]), r = (int)Math.Floor((my[0] - gt[3]) / gt[5]);
                return c >= 0 && r >= 0 && c < img.Width && r < img.Height && img.Data[r * img.Width + c] > 0;
            }
            return (real, Covered);
        }
        return (null, null);
    }
}

/// <summary>The download: an FBX for Blender (terrain with its texture inside + rocks) and the texture PNG.</summary>
public static class BlenderExport
{
    public static readonly (double R, double G, double B) RockColor = (0.30, 0.22, 0.17);

    public static string BaseName(TerrainMesh m)
    {
        double lat = m.CenterLat, lon = m.CenterLon;
        return FormattableString.Invariant($"mars_{Math.Abs(lat):F4}{(lat >= 0 ? 'N' : 'S')}_{Math.Abs(lon):F4}{(lon >= 0 ? 'E' : 'W')}");
    }

    /// <summary>Saves a zip with &lt;name&gt;.fbx (terrain with its texture inside + rocks) and &lt;name&gt;_texture.png,
    /// where &lt;name&gt; is the zip's own name. The texture name stored in the FBX matches the PNG.</summary>
    public static void SaveZip(string zipPath, TerrainResult result, byte[] texturePng, bool includeRocks)
    {
        var m = result.Mesh;
        string stem = Path.GetFileNameWithoutExtension(zipPath);
        string textureName = stem + "_texture.png";
        var meshes = new List<FbxMesh>
        {
            new()
            {
                Name = "MarsTerrain", Positions = m.Positions, Normals = m.Normals, Triangles = m.Indices, Uvs = m.Uvs,
                TexturePng = texturePng, TextureName = textureName,
                Properties = new() { ["ElevationBase_m"] = m.ElevationBaseM, ["CenterLatitude"] = m.CenterLat, ["CenterLongitude"] = m.CenterLon,
                                     ["Source"] = "USGS Mars MGS MOLA - MEX HRSC Blended DEM Global 200m" },
            },
        };
        if (includeRocks && result.Rocks is { Count: > 0 } rocks)
        {
            var (x, y, z) = m.Ground(rocks.Lon, rocks.Lat);
            var (pos, nrm, tri) = RockMesh.Build(x, y, z, rocks.Major, rocks.Minor, rocks.Height, rocks.Yaw, 7);
            meshes.Add(new FbxMesh { Name = "MarsRocks", Positions = pos, Normals = nrm, Triangles = tri, Color = RockColor });
        }
        var fbx = Fbx.Build(meshes);
        string tmp = zipPath + ".part";                               // write fully, then replace: no half-written zip
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var e = zip.CreateEntry(stem + ".fbx", CompressionLevel.Fastest).Open()) e.Write(fbx);
            using (var e = zip.CreateEntry(textureName, CompressionLevel.NoCompression).Open()) e.Write(texturePng);
        }
        File.Move(tmp, zipPath, overwrite: true);
    }
}

/// <summary>All rocks as one mesh: each a lumpy, flat-bottomed 20-sided stone, about a quarter under the ground.</summary>
public static class RockMesh
{
    static readonly double T = (1 + Math.Sqrt(5)) / 2;
    static readonly double[,] V =
    {
        { -1, T, 0 }, { 1, T, 0 }, { -1, -T, 0 }, { 1, -T, 0 }, { 0, -1, T }, { 0, 1, T },
        { 0, -1, -T }, { 0, 1, -T }, { T, 0, -1 }, { T, 0, 1 }, { -T, 0, -1 }, { -T, 0, 1 },
    };
    static readonly int[] F =
    [
        0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
        3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
    ];

    /// <summary>x east, y ground height, z north (metres). Returns positions, normals (east, up, north), triangles.</summary>
    public static (float[] pos, float[] nrm, int[] tri) Build(float[] x, float[] y, float[] z, float[] major, float[] minor, float[] height, float[] yawDeg, int seed)
    {
        int n = x.Length;
        var rng = new Random(seed);
        var pos = new float[n * 36];
        var tri = new int[n * 60];
        double len = Math.Sqrt(1 + T * T);
        for (int k = 0; k < n; k++)
        {
            double a = Mars.Rad(yawDeg[k]), ca = Math.Cos(a), sa = Math.Sin(a);
            for (int v = 0; v < 12; v++)
            {
                double s = 0.5 * (0.8 + 0.35 * rng.NextDouble());                // lumpy
                double ve = V[v, 0] / len * s, vu = Math.Max(V[v, 1] / len * s, -0.3), vn = V[v, 2] / len * s;   // flat bottom
                ve *= major[k]; vn *= minor[k]; vu *= height[k];
                int o = 3 * (12 * k + v);
                pos[o] = (float)(x[k] + ve * ca - vn * sa);
                pos[o + 1] = (float)(y[k] + 0.05 * height[k] + vu);              // bottom about 0.25 h below ground
                pos[o + 2] = (float)(z[k] + ve * sa + vn * ca);
            }
            for (int f = 0; f < 20; f++)
            {
                // (east, up, north) is left-handed: reverse the winding so faces point outwards
                tri[60 * k + 3 * f] = 12 * k + F[3 * f];
                tri[60 * k + 3 * f + 1] = 12 * k + F[3 * f + 2];
                tri[60 * k + 3 * f + 2] = 12 * k + F[3 * f + 1];
            }
        }
        var nrm = new float[pos.Length];
        for (int k = 0; k < n; k++)
        {
            double cx = 0, cy = 0, cz = 0;
            for (int v = 0; v < 12; v++) { cx += pos[3 * (12 * k + v)]; cy += pos[3 * (12 * k + v) + 1]; cz += pos[3 * (12 * k + v) + 2]; }
            cx /= 12; cy /= 12; cz /= 12;
            for (int f = 0; f < 20; f++)
            {
                int i0 = tri[60 * k + 3 * f], i1 = tri[60 * k + 3 * f + 1], i2 = tri[60 * k + 3 * f + 2];
                double ux = pos[3 * i1] - pos[3 * i0], uy = pos[3 * i1 + 1] - pos[3 * i0 + 1], uz = pos[3 * i1 + 2] - pos[3 * i0 + 2];
                double wx = pos[3 * i2] - pos[3 * i0], wy = pos[3 * i2 + 1] - pos[3 * i0 + 1], wz = pos[3 * i2 + 2] - pos[3 * i0 + 2];
                double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                double mx = (pos[3 * i0] + pos[3 * i1] + pos[3 * i2]) / 3 - cx, my = (pos[3 * i0 + 1] + pos[3 * i1 + 1] + pos[3 * i2 + 1]) / 3 - cy,
                       mz = (pos[3 * i0 + 2] + pos[3 * i1 + 2] + pos[3 * i2 + 2]) / 3 - cz;
                if (nx * mx + ny * my + nz * mz < 0) { nx = -nx; ny = -ny; nz = -nz; }   // point outwards
                foreach (int i in new[] { i0, i1, i2 }) { nrm[3 * i] += (float)nx; nrm[3 * i + 1] += (float)ny; nrm[3 * i + 2] += (float)nz; }
            }
        }
        for (int i = 0; i < nrm.Length; i += 3)
        {
            float l = MathF.Sqrt(nrm[i] * nrm[i] + nrm[i + 1] * nrm[i + 1] + nrm[i + 2] * nrm[i + 2]);
            if (l > 0) { nrm[i] /= l; nrm[i + 1] /= l; nrm[i + 2] /= l; }
        }
        return (pos, nrm, tri);
    }
}
