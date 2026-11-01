using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MarsForge.Core;
using MaxRev.Gdal.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace MarsForge.App;

/// <summary>Where the app's own files are. The published app is MarsForge.exe plus a "lib" folder with the big
/// parts (map tools, AI runtime, AI models), so they are used straight from disk instead of being unpacked first.</summary>
public static class AppFiles
{
    /// <summary>The folder of MarsForge.exe.</summary>
    public static string AppFolder { get; } = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>The folder of the MarsForge.exe the user started (the launcher passes it; the app itself runs from
    /// its unpacked copy in %LOCALAPPDATA%\MarsForge). An optional "data" folder is looked for there.</summary>
    public static string HomeFolder { get; } =
        Environment.GetEnvironmentVariable("MARSFORGE_HOME") is { Length: > 0 } h && Directory.Exists(h) ? h : AppFolder;

    /// <summary>The "lib" folder next to the app.</summary>
    public static string Lib { get; } = Path.Combine(AppFolder, "lib");

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectoryW(string path);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr AddDllDirectory(string path);

    /// <summary>Lets Windows find the libraries in "lib" (called once at start, before anything uses them).</summary>
    public static void Setup()
    {
        if (!Directory.Exists(Lib)) return;
        SetDllDirectoryW(Lib);
        AddDllDirectory(Lib);
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.StartsWith(Lib, StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", Lib + ";" + path);
    }

    /// <summary>Loads native libraries from "lib" by full path, so later loads by name find them already loaded.</summary>
    public static void LoadNative(params string[] names)
    {
        foreach (var name in names)
        {
            string file = Path.Combine(Lib, name + ".dll");
            if (!File.Exists(file)) continue;                                // plain build output: they sit next to the app
            try { NativeLibrary.Load(file); }
            catch (Exception e) { Log.Error("Loading " + name, e); }
        }
    }
}

/// <summary>Windows side of the app: GDAL for map files (also online), web downloads, picture decoding, ONNX models.</summary>
public sealed class WindowsServices : IGeoServices
{
    static readonly HttpClient Http = CreateHttp();
    static bool _gdalReady;
    static readonly object GdalLock = new();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, MaxConnectionsPerServer = 8 })
        { Timeout = TimeSpan.FromSeconds(60) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("MarsForge/1.0");
        return h;
    }

    public static void InitGdal()
    {
        lock (GdalLock)
        {
            if (_gdalReady) return;
            AppFiles.LoadNative("gdal_wrap", "gdalconst_wrap", "osr_wrap", "ogr_wrap");
            string gdalData = Path.Combine(AppFiles.Lib, "gdal-data");
            if (Directory.Exists(gdalData))
            {
                GdalBase.ConfigureGdalDrivers(gdalData);
                Proj.Configure(gdalData);                                    // proj.db lives there too
            }
            else GdalBase.ConfigureAll();                                    // plain build output (not published)
            // web certificates: Windows' own list (also works behind antivirus / VPN), plus the standard list
            Gdal.SetConfigOption("GDAL_HTTP_USE_CAPI_STORE", "YES");
            string ca = Path.Combine(AppFiles.Lib, "cacert.pem");
            if (File.Exists(ca))
            {
                Gdal.SetConfigOption("GDAL_CURL_CA_BUNDLE", ca);
                Gdal.SetConfigOption("CURL_CA_BUNDLE", ca);
                Environment.SetEnvironmentVariable("CURL_CA_BUNDLE", ca);
            }
            foreach (var (k, v) in new[]
                     {
                         ("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR"), ("CPL_VSIL_CURL_ALLOWED_EXTENSIONS", ".tif,.TIF,.tiff"),
                         ("GDAL_HTTP_MAX_RETRY", "4"), ("GDAL_HTTP_RETRY_DELAY", "2"), ("GDAL_HTTP_CONNECTTIMEOUT", "30"),
                         ("GDAL_HTTP_LOW_SPEED_TIME", "60"), ("GDAL_HTTP_LOW_SPEED_LIMIT", "50"), ("VSI_CACHE", "TRUE"),
                         ("GTIFF_DIRECT_IO", "YES"),
                     })
                Gdal.SetConfigOption(k, v);
            _gdalReady = true;
        }
    }

    public IRasterSource OpenRaster(string pathOrUrl)
    {
        InitGdal();
        var ds = Gdal.Open(pathOrUrl, Access.GA_ReadOnly)
                 ?? throw new IOException("Could not open the map data. Check your internet connection.");
        return new GdalRaster(ds);
    }

    public async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        Exception? last = null;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct);
                if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                await Task.Delay(1000 + 2000 * i, ct);
            }
        }
        throw new IOException("Download failed. Check your internet connection.", last);
    }

    /// <summary>Map tile (JPEG / PNG, any colour type) -> grey pixels (mean of red, green, blue), size x size.</summary>
    public byte[] DecodeGray(byte[] image, int size)
    {
        using var src = new System.Drawing.Bitmap(new MemoryStream(image));
        using var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(src, 0, 0, size, size);
        }
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var raw = new byte[data.Stride * size];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, raw, 0, raw.Length);
        bmp.UnlockBits(data);
        var px = new byte[size * size];
        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
            {
                int o = r * data.Stride + 4 * c;                       // B, G, R, A
                px[r * size + c] = (byte)((raw[o] + raw[o + 1] + raw[o + 2]) / 3);
            }
        return px;
    }

    public IModel LoadModel(string path) => new OnnxModel(path);
}

/// <summary>A raster opened with GDAL (local file or /vsicurl/ web file; only the needed parts are downloaded).</summary>
sealed class GdalRaster : IRasterSource
{
    readonly Dataset _ds;
    readonly Band _band;

    public int Width => _ds.RasterXSize;
    public int Height => _ds.RasterYSize;
    public double[] GeoTransform { get; }
    public IProjection Projection { get; }
    public bool IsGeographic { get; }
    public double? NoData { get; }
    public double Scale { get; }
    public double Offset { get; }

    public GdalRaster(Dataset ds)
    {
        _ds = ds;
        _band = ds.GetRasterBand(1);
        GeoTransform = new double[6];
        ds.GetGeoTransform(GeoTransform);
        _band.GetNoDataValue(out double nd, out int hasNd);
        NoData = hasNd != 0 ? nd : null;
        _band.GetScale(out double scale, out int hasScale);
        _band.GetOffset(out double offset, out int hasOffset);
        Scale = hasScale != 0 && scale != 0 ? scale : 1.0;
        Offset = hasOffset != 0 ? offset : 0.0;
        var srs = new SpatialReference(ds.GetProjectionRef());
        srs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        IsGeographic = srs.IsGeographic() == 1;
        Projection = new GdalProjection(srs);
    }

    public float[] Read(int col, int row, int width, int height)
    {
        var buf = new float[width * height];
        lock (this)
        {
            var err = _band.ReadRaster(col, row, width, height, buf, width, height, 0, 0);
            if (err != CPLErr.CE_None) throw new IOException("Reading the map data failed: " + Gdal.GetLastErrorMsg());
        }
        return buf;
    }

    public void Dispose()
    {
        _band.Dispose();
        _ds.Dispose();
    }
}

/// <summary>Coordinate conversion with GDAL / PROJ between a raster's map units and its lon/lat.</summary>
sealed class GdalProjection : IProjection
{
    readonly CoordinateTransformation _toMap, _toGeo;
    readonly bool _identity;

    public GdalProjection(SpatialReference srs)
    {
        var geo = srs.CloneGeogCS();
        geo.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        _identity = srs.IsGeographic() == 1;
        _toMap = new CoordinateTransformation(geo, srs);
        _toGeo = new CoordinateTransformation(srs, geo);
    }

    public void ToMap(ReadOnlySpan<double> lon, ReadOnlySpan<double> lat, Span<double> x, Span<double> y)
    {
        if (_identity)
        {
            for (int i = 0; i < lon.Length; i++) { x[i] = Mars.Wrap180(lon[i]); y[i] = lat[i]; }
            return;
        }
        Transform(_toMap, lon, lat, x, y);
    }

    public void ToLonLat(ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> lon, Span<double> lat)
    {
        if (_identity)
        {
            x.CopyTo(lon);
            y.CopyTo(lat);
            return;
        }
        Transform(_toGeo, x, y, lon, lat);
    }

    void Transform(CoordinateTransformation ct, ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> oa, Span<double> ob)
    {
        int n = a.Length;
        var xa = a.ToArray();
        var ya = b.ToArray();
        var z = new double[n];
        lock (this) ct.TransformPoints(n, xa, ya, z);
        xa.CopyTo(oa);
        ya.CopyTo(ob);
    }
}

/// <summary>An ONNX model (rock detector or rock model).</summary>
sealed class OnnxModel : IModel
{
    readonly InferenceSession _session;
    readonly string _input;

    public OnnxModel(string path)
    {
        AppFiles.LoadNative("onnxruntime");
        _session = new InferenceSession(path);
        _input = _session.InputMetadata.Keys.First();
    }

    public float[][] Run(float[] input, int[] shape)
    {
        var tensor = new DenseTensor<float>(input, shape);
        using var results = _session.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_input, tensor) });
        return results.Select(r => r.AsTensor<float>().ToArray()).ToArray();
    }

    public void Dispose() => _session.Dispose();
}
