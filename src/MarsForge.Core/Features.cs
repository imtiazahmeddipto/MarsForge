namespace MarsForge.Core;

/// <summary>The Mars Trek inputs the rock model sees, built exactly as for training: a 2 km x 2 km window on a
/// 10 m grid (200 x 200), rows north to south. Channels: CTX brightness, CTX local contrast, THEMIS night
/// infrared, elevation minus the window mean (/100 m), slope (degrees /10).</summary>
public static class Features
{
    public const int Size = 200;
    public const double Step = 10.0;
    public const double HalfM = 1000.0;

    /// <summary>The lon/lat of the grid points (rows north to south).</summary>
    public static (double[] lon, double[] lat) WindowGrid(double lon0, double lat0)
    {
        var lon = new double[Size * Size];
        var lat = new double[Size * Size];
        for (int r = 0; r < Size; r++)
            for (int c = 0; c < Size; c++)
            {
                double e = -HalfM + Step / 2 + Step * c, n = -(-HalfM + Step / 2 + Step * r);
                var (lo, la) = Mars.EnToLonLat(e, n, lon0, lat0);
                lon[r * Size + c] = lo;
                lat[r * Size + c] = la;
            }
        return (lon, lat);
    }

    /// <summary>5 x 200 x 200 values for the window centred on (lon0, lat0).</summary>
    public static float[] Window(Raster? ctx, Raster? themis, Raster? dem, double lon0, double lat0)
    {
        const int n = Size * Size;
        var (lon, lat) = WindowGrid(lon0, lat0);
        var x = new float[5 * n];

        var c = ctx is not null ? ctx.Sample(lon, lat, 1) : Nan(n);
        for (int i = 0; i < n; i++) c[i] /= 255f;
        double cm = Img.NanMean(c);
        Img.FillNaN(c, double.IsNaN(cm) ? 0.5f : (float)cm);
        var blur = Img.Gaussian(c, Size, Size, 10);                    // 100 m neighbourhood
        var local = new float[n];
        for (int i = 0; i < n; i++) local[i] = c[i] - blur[i];
        double sd = Math.Max(Img.Std(local), 1e-3);

        var th = themis is not null ? themis.Sample(lon, lat, 1) : Nan(n);
        for (int i = 0; i < n; i++) th[i] /= 255f;
        Img.FillNaN(th, 0.5f);

        var z = dem is not null ? dem.Sample(lon, lat, 3) : new float[n];
        double zm = Img.NanMean(z);
        Img.FillNaN(z, double.IsNaN(zm) ? 0f : (float)zm);
        var (gy, gx) = Img.Gradient(z, Size, Size, Step);
        double mean = Img.Mean(z);
        for (int i = 0; i < n; i++)
        {
            x[i] = c[i];
            x[n + i] = (float)(local[i] / sd);
            x[2 * n + i] = th[i];
            x[3 * n + i] = (float)((z[i] - mean) / 100.0);
            x[4 * n + i] = (float)(Mars.Deg(Math.Atan(Math.Sqrt(gx[i] * gx[i] + gy[i] * gy[i]))) / 10.0);
        }
        return x;
    }

    static float[] Nan(int n)
    {
        var a = new float[n];
        Array.Fill(a, float.NaN);
        return a;
    }
}
