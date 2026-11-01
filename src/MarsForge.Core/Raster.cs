namespace MarsForge.Core;

/// <summary>A map layer in memory: values (row by row, north first) + georeference.
/// Sample(lon, lat) gives NaN outside the layer.</summary>
public sealed class Raster
{
    public readonly float[] Data;
    public readonly int Width, Height;
    public readonly double[] GeoTransform;
    public readonly IProjection Projection;
    readonly bool _empty;

    /// <param name="noData">Values equal to this (and NaN) count as missing; they are filled with the mean of the rest.</param>
    public Raster(float[] data, int width, int height, double[] geoTransform, IProjection projection, double? noData = null)
    {
        Width = width;
        Height = height;
        GeoTransform = geoTransform;
        Projection = projection;
        Data = (float[])data.Clone();
        double sum = 0;
        long n = 0;
        bool anyBad = false;
        for (int i = 0; i < Data.Length; i++)
        {
            float v = Data[i];
            if (float.IsFinite(v) && (noData is null || v != noData.Value)) { sum += v; n++; }
            else anyBad = true;
        }
        _empty = n == 0;
        if (anyBad && n > 0)
        {
            float mean = (float)(sum / n);
            for (int i = 0; i < Data.Length; i++)
            {
                float v = Data[i];
                if (!float.IsFinite(v) || (noData is not null && v == noData.Value)) Data[i] = mean;
            }
        }
    }

    public float this[int row, int col] => Data[row * Width + col];

    /// <summary>Values at lon/lat points. order: 0 nearest, 1 linear, 3 cubic. NaN outside.</summary>
    public float[] Sample(ReadOnlySpan<double> lon, ReadOnlySpan<double> lat, int order = 1)
    {
        int n = lon.Length;
        var x = new double[n];
        var y = new double[n];
        Projection.ToMap(lon, lat, x, y);
        var gt = GeoTransform;
        var outp = new float[n];
        for (int i = 0; i < n; i++)
        {
            double col = (x[i] - gt[0]) / gt[1], row = (y[i] - gt[3]) / gt[5];
            if (_empty || col < 0 || row < 0 || col > Width || row > Height) { outp[i] = float.NaN; continue; }
            outp[i] = Interp.At(Data, Width, Height, row - 0.5, col - 0.5, order);
        }
        return outp;
    }

    /// <summary>Values on a lon/lat grid (rows north to south). Returns rows*cols values.</summary>
    public float[] SampleGrid(double[] lons, double[] lats, int order = 1)
    {
        var lo = new double[lons.Length * lats.Length];
        var la = new double[lo.Length];
        for (int r = 0, k = 0; r < lats.Length; r++)
            for (int c = 0; c < lons.Length; c++, k++) { lo[k] = lons[c]; la[k] = lats[r]; }
        return Sample(lo, la, order);
    }

    /// <summary>Reads the part of a raster source that covers a lon/lat box (plus a few pixels).</summary>
    public static Raster? FromSource(IRasterSource src, GeoBox box, int extraPx = 3, bool applyNoData = true)
    {
        var lon = new[] { box.West, box.East, box.West, box.East, box.CenterLon };
        var lat = new[] { box.South, box.South, box.North, box.North, box.CenterLat };
        var x = new double[5];
        var y = new double[5];
        src.Projection.ToMap(lon, lat, x, y);
        var gt = src.GeoTransform;
        double cMin = double.MaxValue, cMax = double.MinValue, rMin = double.MaxValue, rMax = double.MinValue;
        for (int i = 0; i < 5; i++)
        {
            double c = (x[i] - gt[0]) / gt[1], r = (y[i] - gt[3]) / gt[5];
            cMin = Math.Min(cMin, c); cMax = Math.Max(cMax, c); rMin = Math.Min(rMin, r); rMax = Math.Max(rMax, r);
        }
        int c0 = Math.Max(0, (int)Math.Floor(cMin) - extraPx), r0 = Math.Max(0, (int)Math.Floor(rMin) - extraPx);
        int c1 = Math.Min(src.Width, (int)Math.Ceiling(cMax) + extraPx + 1), r1 = Math.Min(src.Height, (int)Math.Ceiling(rMax) + extraPx + 1);
        if (c1 - c0 < 2 || r1 - r0 < 2) return null;
        var data = src.Read(c0, r0, c1 - c0, r1 - r0);
        float scale = (float)src.Scale, offset = (float)src.Offset;
        double? nod = applyNoData ? src.NoData : null;
        if (nod is not null)
            for (int i = 0; i < data.Length; i++) if (data[i] == nod.Value || data[i] == -32768f) data[i] = float.NaN;
        if (scale != 1f || offset != 0f)
            for (int i = 0; i < data.Length; i++) data[i] = data[i] * scale + offset;
        var wgt = new[] { gt[0] + c0 * gt[1], gt[1], 0, gt[3] + r0 * gt[5], 0, gt[5] };
        return new Raster(data, c1 - c0, r1 - r0, wgt, src.Projection);
    }
}

/// <summary>Interpolation in a 2-D array (row by row). Coordinates in array index units.</summary>
public static class Interp
{
    public static float At(float[] a, int w, int h, double r, double c, int order)
    {
        if (order == 0)
        {
            int ri = Math.Clamp((int)Math.Round(r, MidpointRounding.ToEven), 0, h - 1);
            int ci = Math.Clamp((int)Math.Round(c, MidpointRounding.ToEven), 0, w - 1);
            return a[ri * w + ci];
        }
        if (order == 1) return Linear(a, w, h, r, c);
        return Cubic(a, w, h, r, c);
    }

    public static float Linear(float[] a, int w, int h, double r, double c)
    {
        r = Math.Clamp(r, 0, h - 1);
        c = Math.Clamp(c, 0, w - 1);
        int r0 = Math.Min((int)r, h - 1), c0 = Math.Min((int)c, w - 1);
        int r1 = Math.Min(r0 + 1, h - 1), c1 = Math.Min(c0 + 1, w - 1);
        double fr = r - r0, fc = c - c0;
        double top = a[r0 * w + c0] * (1 - fc) + a[r0 * w + c1] * fc;
        double bot = a[r1 * w + c0] * (1 - fc) + a[r1 * w + c1] * fc;
        return (float)(top * (1 - fr) + bot * fr);
    }

    /// <summary>Cubic (Catmull-Rom) interpolation: passes exactly through every value, smooth in between.</summary>
    public static float Cubic(float[] a, int w, int h, double r, double c)
    {
        r = Math.Clamp(r, 0, h - 1);
        c = Math.Clamp(c, 0, w - 1);
        int ri = (int)Math.Floor(r), ci = (int)Math.Floor(c);
        double fr = r - ri, fc = c - ci;
        Span<double> wr = stackalloc double[4];
        Span<double> wc = stackalloc double[4];
        Weights(fr, wr);
        Weights(fc, wc);
        double s = 0;
        for (int m = 0; m < 4; m++)
        {
            int rr = Math.Clamp(ri - 1 + m, 0, h - 1);
            double row = 0;
            for (int k = 0; k < 4; k++)
            {
                int cc = Math.Clamp(ci - 1 + k, 0, w - 1);
                row += wc[k] * a[rr * w + cc];
            }
            s += wr[m] * row;
        }
        return (float)s;
    }

    static void Weights(double t, Span<double> wts)
    {
        double t2 = t * t, t3 = t2 * t;
        wts[0] = -0.5 * t3 + t2 - 0.5 * t;
        wts[1] = 1.5 * t3 - 2.5 * t2 + 1;
        wts[2] = -1.5 * t3 + 2 * t2 + 0.5 * t;
        wts[3] = 0.5 * t3 - 0.5 * t2;
    }
}
