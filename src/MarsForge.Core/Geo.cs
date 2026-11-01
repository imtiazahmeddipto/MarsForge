namespace MarsForge.Core;

/// <summary>Mars sphere and simple ground coordinates.</summary>
public static class Mars
{
    public const double Radius = 3396190.0;
    public const double MaxSideM = 20000.0;
    public const double MinSideM = 50.0;

    public static double Rad(double deg) => deg * Math.PI / 180.0;
    public static double Deg(double rad) => rad * 180.0 / Math.PI;

    /// <summary>Ground east / north metres from (lon0, lat0) on the sphere (exact to mm over a few km).</summary>
    public static (double E, double N) LocalEN(double lon, double lat, double lon0, double lat0)
    {
        double dlon = Wrap180(lon - lon0);
        return (Radius * Math.Cos(Rad(lat0)) * Rad(dlon), Radius * Rad(lat - lat0));
    }

    public static (double Lon, double Lat) EnToLonLat(double e, double n, double lon0, double lat0)
        => (lon0 + Deg(e / (Radius * Math.Cos(Rad(lat0)))), lat0 + Deg(n / Radius));

    public static double Wrap180(double lon) => ((lon + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
}

/// <summary>A lon/lat box (degrees, east-positive longitudes -180..180).</summary>
public readonly record struct GeoBox(double West, double South, double East, double North)
{
    public double CenterLon => (West + East) / 2;
    public double CenterLat => (South + North) / 2;

    /// <summary>Width (east-west) and height (north-south) in metres at the box centre.</summary>
    public (double Width, double Height) SizeM =>
        (Mars.Radius * Math.Cos(Mars.Rad(CenterLat)) * Mars.Rad(East - West), Mars.Radius * Mars.Rad(North - South));

    public bool Contains(double lon, double lat) => lon >= West && lon <= East && lat >= South && lat <= North;

    public GeoBox Grow(double metres)
    {
        double dLon = Mars.Deg(metres / (Mars.Radius * Math.Cos(Mars.Rad(CenterLat))));
        double dLat = Mars.Deg(metres / Mars.Radius);
        return new GeoBox(West - dLon, South - dLat, East + dLon, North + dLat);
    }

    public static GeoBox Around(double lon, double lat, double halfM)
        => new GeoBox(lon, lat, lon, lat).Grow(halfM);

    /// <summary>A number that is the same for the same box (for repeatable random results).</summary>
    public int Seed
    {
        get
        {
            unchecked
            {
                long a = (long)Math.Round(West * 1e5), b = (long)Math.Round(South * 1e5);
                ulong h = 1469598103934665603UL;
                foreach (long v in new[] { a, b })
                {
                    h ^= (ulong)v;
                    h *= 1099511628211UL;
                }
                return (int)(h & 0x7FFFFFFF);
            }
        }
    }
}

/// <summary>Map projection of a raster: lon/lat (degrees) to map x/y and back.</summary>
public interface IProjection
{
    void ToMap(ReadOnlySpan<double> lon, ReadOnlySpan<double> lat, Span<double> x, Span<double> y);
    void ToLonLat(ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> lon, Span<double> lat);
}

/// <summary>Plain lon/lat degrees (Mars Trek tiles).</summary>
public sealed class LonLatProjection : IProjection
{
    public static readonly LonLatProjection Instance = new();

    public void ToMap(ReadOnlySpan<double> lon, ReadOnlySpan<double> lat, Span<double> x, Span<double> y)
    {
        for (int i = 0; i < lon.Length; i++) { x[i] = Mars.Wrap180(lon[i]); y[i] = lat[i]; }
    }

    public void ToLonLat(ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> lon, Span<double> lat)
    {
        x.CopyTo(lon);
        y.CopyTo(lat);
    }
}

/// <summary>Equirectangular (equidistant cylindrical) projection on the Mars sphere.</summary>
public sealed class EquirectangularProjection(double standardParallelDeg = 0, double centralMeridianDeg = 0, double radius = Mars.Radius) : IProjection
{
    readonly double _k = radius * Math.Cos(Mars.Rad(standardParallelDeg)), _r = radius, _lon0 = centralMeridianDeg;

    public void ToMap(ReadOnlySpan<double> lon, ReadOnlySpan<double> lat, Span<double> x, Span<double> y)
    {
        for (int i = 0; i < lon.Length; i++)
        {
            x[i] = _k * Mars.Rad(Mars.Wrap180(lon[i] - _lon0));
            y[i] = _r * Mars.Rad(lat[i]);
        }
    }

    public void ToLonLat(ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> lon, Span<double> lat)
    {
        for (int i = 0; i < x.Length; i++)
        {
            lon[i] = Mars.Wrap180(_lon0 + Mars.Deg(x[i] / _k));
            lat[i] = Mars.Deg(y[i] / _r);
        }
    }
}

/// <summary>A georeferenced raster file or web file (implemented with GDAL in the app).</summary>
public interface IRasterSource : IDisposable
{
    int Width { get; }
    int Height { get; }
    /// <summary>GDAL geotransform: x = gt0 + col * gt1, y = gt3 + row * gt5 (no rotation).</summary>
    double[] GeoTransform { get; }
    IProjection Projection { get; }
    /// <summary>True when map units are degrees.</summary>
    bool IsGeographic { get; }
    double? NoData { get; }
    double Scale { get; }
    double Offset { get; }
    /// <summary>Raw values of band 1 in a pixel window, row by row.</summary>
    float[] Read(int col, int row, int width, int height);
}

/// <summary>Opens rasters, downloads files and decodes pictures (supplied by the app).</summary>
public interface IGeoServices
{
    IRasterSource OpenRaster(string pathOrUrl);
    Task<byte[]?> DownloadAsync(string url, CancellationToken ct);
    /// <summary>JPEG / PNG bytes to grey pixels (0..255), resized to size x size.</summary>
    byte[] DecodeGray(byte[] image, int size);
    /// <summary>Loads an ONNX model.</summary>
    IModel LoadModel(string path);
}
