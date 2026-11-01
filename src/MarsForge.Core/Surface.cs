using System.Text.Json;

namespace MarsForge.Core;

/// <summary>All rocks of a box. Lon/lat position, size in metres, turn around the up axis (degrees).</summary>
public sealed class RockSet
{
    public required double[] Lon { get; init; }
    public required double[] Lat { get; init; }
    public required float[] Diameter { get; init; }
    public required float[] Major { get; init; }
    public required float[] Minor { get; init; }
    public required float[] Height { get; init; }
    public required float[] Yaw { get; init; }
    public required bool[] Real { get; init; }
    public int Count => Diameter.Length;
    public double MinDiameterM { get; init; }
}

/// <summary>Soft-ground risk (0..1) on a lon/lat grid over the box (rows north to south) and the risky zones.</summary>
public sealed class SoftGroundMap
{
    public required float[] Risk { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int Zones { get; init; }
    public int HighZones { get; init; }
}

/// <summary>The rock model (trained on HiRISE areas): rocks per 50 m square in 3 size classes from Mars Trek layers.</summary>
public sealed class RockModel : IDisposable
{
    public static readonly double[] Bins = [1.0, 2.0, 4.0];
    public const double CellM = 50.0, BlockM = 1000.0;
    readonly IModel _model;
    public double PowerB { get; } = 2.5;
    public double MaxDiameterM { get; } = 8.0;
    public double[] ShapeQuantiles { get; } = [0.55, 0.65, 0.75, 0.85, 0.92];

    public RockModel(IModel model, string infoJson)
    {
        _model = model;
        using var doc = JsonDocument.Parse(infoJson);
        if (doc.RootElement.TryGetProperty("size_law", out var law))
        {
            if (law.TryGetProperty("power_b", out var b)) PowerB = b.GetDouble();
            if (law.TryGetProperty("max_diameter_m", out var m)) MaxDiameterM = m.GetDouble();
            if (law.TryGetProperty("minor_major_quantiles", out var q)) ShapeQuantiles = q.EnumerateArray().Select(v => v.GetDouble()).ToArray();
        }
    }

    /// <summary>Rocks per 50 m square for a batch of 5 x 200 x 200 windows: [window][class][row 0..19][col 0..19].</summary>
    public float[][] Rates(List<float[]> windows)
    {
        int b = windows.Count, n = Features.Size * Features.Size * 5;
        var x = new float[b * n];
        for (int i = 0; i < b; i++) Array.Copy(windows[i], 0, x, i * n, n);
        var o = _model.Run(x, [b, 5, Features.Size, Features.Size])[0];
        var outp = new float[b][];
        for (int i = 0; i < b; i++)
        {
            outp[i] = new float[3 * 400];
            for (int k = 0; k < 1200; k++) outp[i][k] = MathF.Exp(o[i * 1200 + k]);
        }
        return outp;
    }

    public void Dispose() => _model.Dispose();
}

/// <summary>Rock rates of one 1 km block, centred on (Lon, Lat): [class][row][col], row 0 = north.</summary>
public sealed record RateBlock(double Lon, double Lat, float[] Rates);

/// <summary>Places rocks: real HiRISE rocks where an image covers the box, predicted ones elsewhere.</summary>
public static class RockPlacer
{
    public const int MaxRocks = 250_000;

    static double PowerSample(Random rng, double lo, double hi, double b)
    {
        double u = rng.NextDouble();
        return Math.Pow(Math.Pow(lo, -b) - u * (Math.Pow(lo, -b) - Math.Pow(hi, -b)), -1.0 / b);
    }

    static double FracAbove(double d, double lo, double hi, double b)
    {
        d = Math.Clamp(d, lo, hi);
        return (Math.Pow(d, -b) - Math.Pow(hi, -b)) / (Math.Pow(lo, -b) - Math.Pow(hi, -b));
    }

    public static RockSet Place(List<RateBlock>? rates, RockModel? model, List<(double lon, double lat, FoundRock rock)>? real,
                                Func<double, double, bool>? covered, GeoBox box, double minD, int seed)
    {
        var rng = new Random(seed);
        double b = model?.PowerB ?? 2.5;
        double dmax = Math.Max(model?.MaxDiameterM ?? 8.0, 4.5);
        double[] edges = [.. RockModel.Bins, dmax];
        var q = model?.ShapeQuantiles ?? [0.55, 0.65, 0.75, 0.85, 0.92];
        double[] qx = [0.1, 0.25, 0.5, 0.75, 0.9];
        var lon = new List<double>();
        var lat = new List<double>();
        var d = new List<float>();
        var major = new List<float>();
        var minor = new List<float>();
        var yaw = new List<float>();
        var isReal = new List<bool>();

        if (rates is { Count: > 0 })
        {
            double expected = 0;
            foreach (var blk in rates)
                for (int k = 0; k < 3; k++)
                {
                    double s = 0;
                    for (int i = 0; i < 400; i++) s += blk.Rates[k * 400 + i];
                    expected += s * FracAbove(minD, edges[k], edges[k + 1], b);
                }
            if (expected > MaxRocks * 1.5) minD *= Math.Pow(expected / MaxRocks, 1 / b);   // too many: only the bigger ones
            for (int bi = 0; bi < rates.Count; bi++)
            {
                var blk = rates[bi];
                for (int k = 0; k < 3; k++)
                {
                    double lo = edges[k], hi = edges[k + 1];
                    if (minD >= hi) continue;
                    double frac = FracAbove(minD, lo, hi, b);
                    for (int row = 0; row < 20; row++)
                        for (int col = 0; col < 20; col++)
                        {
                            int cnt = TerrainBuilder.Poisson(rng, blk.Rates[k * 400 + row * 20 + col] * frac);
                            for (int t = 0; t < cnt; t++)
                            {
                                double e = -RockModel.BlockM / 2 + RockModel.CellM * col + rng.NextDouble() * RockModel.CellM;
                                double n = RockModel.BlockM / 2 - RockModel.CellM * row - rng.NextDouble() * RockModel.CellM;   // row 0 = north edge
                                var (lo2, la2) = Mars.EnToLonLat(e, n, blk.Lon, blk.Lat);
                                double dd = PowerSample(rng, Math.Max(lo, minD), hi, b);
                                double qq = Interp1(rng.NextDouble(), qx, q);
                                double yy = rng.NextDouble() * 180;
                                if (!box.Contains(lo2, la2) || (covered is not null && covered(lo2, la2))) continue;
                                lon.Add(lo2); lat.Add(la2); d.Add((float)dd);
                                major.Add((float)(dd / Math.Sqrt(qq))); minor.Add((float)(dd * Math.Sqrt(qq)));   // same area as a circle of width d
                                yaw.Add((float)yy); isReal.Add(false);
                            }
                        }
                }
            }
        }
        if (real is not null)
            foreach (var (rl, rt, rock) in real)
            {
                if (!box.Contains(rl, rt) || rock.DiameterM < minD) continue;
                lon.Add(rl); lat.Add(rt); d.Add((float)rock.DiameterM);
                major.Add((float)rock.MajorM); minor.Add((float)rock.MinorM); yaw.Add((float)rock.AngleDeg); isReal.Add(true);
            }
        int[] keep = Enumerable.Range(0, d.Count).ToArray();
        if (keep.Length > MaxRocks) keep = keep.OrderByDescending(i => d[i]).Take(MaxRocks).ToArray();   // keep the biggest
        return new RockSet
        {
            Lon = keep.Select(i => lon[i]).ToArray(), Lat = keep.Select(i => lat[i]).ToArray(),
            Diameter = keep.Select(i => d[i]).ToArray(), Major = keep.Select(i => major[i]).ToArray(),
            Minor = keep.Select(i => minor[i]).ToArray(), Height = keep.Select(i => 0.5f * minor[i]).ToArray(),   // height about half the width
            Yaw = keep.Select(i => yaw[i]).ToArray(), Real = keep.Select(i => isReal[i]).ToArray(), MinDiameterM = minD,
        };
    }

    static double Interp1(double x, double[] xs, double[] ys)
    {
        if (x <= xs[0]) return ys[0];
        for (int i = 1; i < xs.Length; i++)
            if (x <= xs[i]) return ys[i - 1] + (ys[i] - ys[i - 1]) * (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
        return ys[^1];
    }
}

/// <summary>Risk of loose, fine-grained ground (the kind that trapped Spirit and Opportunity), from signs seen
/// from orbit: cold at night in THEMIS infrared (loose material), smooth CTX image, few rocks, low ground.
/// A rule of thumb, not a measurement of sulfate.</summary>
public static class SoftGround
{
    public static SoftGroundMap? Map(Raster? ctx, Raster? themis, Raster? dem, GeoBox box, List<RateBlock>? rates, int maxGrid = 800)
    {
        var (W, H) = box.SizeM;
        double step = Math.Max(10.0, Math.Max(W, H) / maxGrid);
        int nx = Math.Max(2, (int)Math.Round(W / step)), ny = Math.Max(2, (int)Math.Round(H / step));
        var lons = Enumerable.Range(0, nx).Select(c => box.West + (box.East - box.West) * c / (nx - 1)).ToArray();
        var lats = Enumerable.Range(0, ny).Select(r => box.North + (box.South - box.North) * r / (ny - 1)).ToArray();
        int n = nx * ny;
        double px = step;
        var parts = new List<float[]>();

        if (themis is not null)
        {
            var th = themis.SampleGrid(lons, lats, 3);
            if (Img.FiniteShare(th) > 0.5)
            {
                for (int i = 0; i < n; i++) th[i] /= 255f;
                Img.FillNaN(th, (float)Img.NanMean(th));
                var rel = Z(Img.Gaussian(th, nx, ny, 100 / px));
                var p = new float[n];
                for (int i = 0; i < n; i++) p[i] = (float)(1.2 * Math.Clamp((0.45 - th[i]) / 0.2, -1.5, 1.5) - 0.6 * rel[i]);   // cold = dark
                parts.Add(p);
            }
        }
        if (ctx is not null)
        {
            var c = ctx.SampleGrid(lons, lats, 1);
            if (Img.FiniteShare(c) > 0.5)
            {
                for (int i = 0; i < n; i++) c[i] /= 255f;
                Img.FillNaN(c, (float)Img.NanMean(c));
                var blur = Img.Gaussian(c, nx, ny, Math.Max(1.0, 30 / px));
                var hp2 = new float[n];
                for (int i = 0; i < n; i++) hp2[i] = (c[i] - blur[i]) * (c[i] - blur[i]);
                var rough = Img.Gaussian(hp2, nx, ny, Math.Max(1.0, 40 / px));
                for (int i = 0; i < n; i++) rough[i] = MathF.Log(MathF.Sqrt(Math.Max(rough[i], 0)) + 1e-3f);
                var zr = Z(rough);
                parts.Add(zr.Select(v => -0.8f * v).ToArray());
            }
        }
        if (rates is { Count: > 0 })
        {
            var dens = new float[n];
            double dLon = (box.East - box.West) / (nx - 1), dLat = (box.North - box.South) / (ny - 1);
            foreach (var blk in rates)
            {
                var (lw, ls) = Mars.EnToLonLat(-RockModel.BlockM / 2, -RockModel.BlockM / 2, blk.Lon, blk.Lat);
                var (le, ln) = Mars.EnToLonLat(RockModel.BlockM / 2, RockModel.BlockM / 2, blk.Lon, blk.Lat);
                int c0 = Math.Max(0, (int)Math.Floor((lw - box.West) / dLon)), c1 = Math.Min(nx - 1, (int)Math.Ceiling((le - box.West) / dLon));
                int r0 = Math.Max(0, (int)Math.Floor((box.North - ln) / dLat)), r1 = Math.Min(ny - 1, (int)Math.Ceiling((box.North - ls) / dLat));
                for (int r = r0; r <= r1; r++)
                    for (int col = c0; col <= c1; col++)
                    {
                        var (e, nn) = Mars.LocalEN(lons[col], lats[r], blk.Lon, blk.Lat);
                        if (Math.Abs(e) > RockModel.BlockM / 2 || Math.Abs(nn) > RockModel.BlockM / 2) continue;
                        int ci = Math.Clamp((int)Math.Floor((e + RockModel.BlockM / 2) / RockModel.CellM), 0, 19);
                        int ri = Math.Clamp((int)Math.Floor((RockModel.BlockM / 2 - nn) / RockModel.CellM), 0, 19);
                        dens[r * nx + col] = blk.Rates[ri * 20 + ci] + blk.Rates[400 + ri * 20 + ci] + blk.Rates[800 + ri * 20 + ci];
                    }
            }
            var zd = Z(dens.Select(v => MathF.Log(v + 0.1f)).ToArray());
            parts.Add(zd.Select(v => -0.6f * v).ToArray());
        }
        var slope = new float[n];
        if (dem is not null)
        {
            var hgt = dem.SampleGrid(lons, lats, 3);
            if (Img.FiniteShare(hgt) > 0)
            {
                Img.FillNaN(hgt, (float)Img.NanMean(hgt));
                var (gy, gx) = Img.Gradient(hgt, nx, ny, step);
                for (int i = 0; i < n; i++) slope[i] = (float)Mars.Deg(Math.Atan(Math.Sqrt(gx[i] * gx[i] + gy[i] * gy[i])));
                var smooth = Img.Gaussian(hgt, nx, ny, Math.Max(1.0, 600 / px), EdgeMode.Nearest);
                parts.Add(hgt.Select((v, i) => (float)(-0.4 * Math.Clamp((v - smooth[i]) / 20.0, -2, 2))).ToArray());
            }
        }
        if (parts.Count == 0) return null;
        var risk = new float[n];
        double norm = Math.Sqrt(parts.Count);
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            foreach (var p in parts) s += p[i];
            double score = s / norm - 0.04 * Math.Max(slope[i] - 8, 0) - 1.0;
            risk[i] = (float)(1 / (1 + Math.Exp(-1.6 * score)));
        }
        risk = Img.Gaussian(risk, nx, ny, 1.0);

        var mask = risk.Select(v => v >= 0.6f).ToArray();
        var (lab, count) = Img.Label(mask, nx, ny, eight: true);
        var size = new int[count + 1];
        var peak = new float[count + 1];
        for (int i = 0; i < n; i++) if (lab[i] > 0) { size[lab[i]]++; peak[lab[i]] = Math.Max(peak[lab[i]], risk[i]); }
        int zones = 0, high = 0;
        for (int k = 1; k <= count; k++)
            if (size[k] >= 4) { zones++; if (peak[k] >= 0.8f) high++; }
        return new SoftGroundMap { Risk = risk, Width = nx, Height = ny, Zones = zones, HighZones = high };
    }

    static float[] Z(float[] a)
    {
        double m = Img.Mean(a), sd = Img.Std(a);
        return sd > 1e-6 ? a.Select(v => (float)((v - m) / sd)).ToArray() : new float[a.Length];
    }
}
