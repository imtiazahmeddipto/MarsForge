using System.Text.Json;

namespace MarsForge.Core;

/// <summary>Online map layers for one box: CTX image and THEMIS night infrared (Mars Trek tiles) and the
/// USGS catalogue of HiRISE images.</summary>
public sealed class Trek(IGeoServices services)
{
    public const string TrekTiles = "https://trek.nasa.gov/tiles/Mars/EQ/{id}/1.0.0//default/default028mm/{z}/{y}/{x}.{ext}";
    public const string Stac = "https://stac.astrogeology.usgs.gov/api";
    public const string HiriseCollection = "mro_hirise_socet_dtms";

    public sealed record Layer(string Url, int TilePx, double WantedM, int MaxZoom);

    public static readonly Layer Ctx = new("https://astro.arcgis.com/arcgis/rest/services/OnMars/CTX/MapServer/tile/{z}/{y}/{x}", 512, 10.0, 12);
    public static readonly Layer Themis = new(TrekTiles.Replace("{id}", "Mars_MO_THEMIS-IR-Night_mosaic_60N60S_100m_v14_clon0_ly").Replace("{ext}", "png"), 256, 100.0, 8);

    readonly Dictionary<Layer, int?> _zoom = [];

    static string Url(Layer l, int z, int x, int y) => l.Url.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());

    async Task<int?> ZoomFor(Layer layer, double lon, double lat, CancellationToken ct)
    {
        if (_zoom.TryGetValue(layer, out var known)) return known;
        int? found = null;
        for (int z = layer.MaxZoom; z >= 0; z--)
        {
            if (180.0 / (1 << z) / layer.TilePx * Mars.Radius * Math.PI / 180 > 4 * layer.WantedM) break;
            double tdeg = 180.0 / (1 << z);
            if (await services.DownloadAsync(Url(layer, z, (int)Math.Floor((lon + 180) / tdeg), (int)Math.Floor((90 - lat) / tdeg)), ct) is not null)
            {
                found = z;
                break;
            }
        }
        _zoom[layer] = found;
        return found;
    }

    /// <summary>The layer over the box as a raster (lon/lat degrees), or null if there is none here.</summary>
    public async Task<Raster?> Tiles(Layer layer, GeoBox box, CancellationToken ct)
    {
        var z = await ZoomFor(layer, box.CenterLon, box.CenterLat, ct);
        if (z is null) return null;
        int zz = z.Value, tp = layer.TilePx;
        double tdeg = 180.0 / (1 << zz), dpp = tdeg / tp;
        int x0 = (int)Math.Floor((box.West + 180) / tdeg), x1 = (int)Math.Floor((box.East + 180) / tdeg);
        int y0 = Math.Max(0, (int)Math.Floor((90 - box.North) / tdeg)), y1 = Math.Min((1 << zz) - 1, (int)Math.Floor((90 - box.South) / tdeg));
        int nx = x1 - x0 + 1, ny = y1 - y0 + 1;
        if (nx * ny > 400) throw new ArgumentException("The box is too big for the map layers.");
        var canvas = new float[nx * tp * ny * tp];
        int got = 0;
        using var gate = new SemaphoreSlim(6);
        var jobs = new List<Task>();
        for (int ty = y0; ty <= y1; ty++)
            for (int tx = x0; tx <= x1; tx++)
            {
                int cx = tx, cy = ty;
                jobs.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var raw = await services.DownloadAsync(Url(layer, zz, cx, cy), ct);
                        if (raw is null) return;
                        byte[] px;
                        try { px = services.DecodeGray(raw, tp); }
                        catch (Exception ex) { Log.Error("Tile picture", ex); return; }
                        int ox = (cx - x0) * tp, oy = (cy - y0) * tp, stride = nx * tp;
                        for (int r = 0; r < tp; r++)
                            for (int c = 0; c < tp; c++) canvas[(oy + r) * stride + ox + c] = px[r * tp + c];
                        Interlocked.Increment(ref got);
                    }
                    finally { gate.Release(); }
                }, ct));
            }
        await Task.WhenAll(jobs);
        if (got == 0) return null;
        var gt = new[] { -180 + x0 * tdeg, dpp, 0, 90 - y0 * tdeg, 0, -dpp };
        return new Raster(canvas, nx * tp, ny * tp, gt, LonLatProjection.Instance);
    }

    public sealed record HiriseSite(string Id, Dictionary<string, string> Assets);

    /// <summary>HiRISE terrain-model sites (with orthoimages) that touch the box.</summary>
    public async Task<List<HiriseSite>> HiriseSites(GeoBox b, CancellationToken ct)
    {
        var found = new Dictionary<string, HiriseSite>();
        var boxes = new List<(double, double, double, double)> { (b.West, b.South, b.East, b.North) };
        if (b.West < 0)                                         // footprints may use 0..360 longitudes
        {
            if (b.East <= 0) boxes.Add((b.West + 360, b.South, Math.Min(b.East + 360, 360), b.North));
            else { boxes.Add((b.West + 360, b.South, 360, b.North)); boxes.Add((0, b.South, b.East, b.North)); }
        }
        foreach (var (w, s, e, n) in boxes)
        {
            string url = FormattableString.Invariant($"{Stac}/search?collections={HiriseCollection}&limit=50&bbox={w},{s},{e},{n}");
            byte[]? raw;
            try { raw = await services.DownloadAsync(url, ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { continue; }
            if (raw is null) continue;
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("features", out var feats)) continue;
            foreach (var f in feats.EnumerateArray())
            {
                var assets = new Dictionary<string, string>();
                if (f.TryGetProperty("assets", out var a))
                    foreach (var p in a.EnumerateObject())
                        if (p.Value.TryGetProperty("href", out var href) && href.GetString() is string h &&
                            (h.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)))
                            assets[p.Name] = h;
                string id = f.GetProperty("id").GetString() ?? "";
                if (assets.Count > 0) found[id] = new HiriseSite(id, assets);
            }
        }
        return found.Values.ToList();
    }

    public static string Vsi(string href)
    {
        if (href.StartsWith("s3://"))
        {
            var rest = href[5..];
            int slash = rest.IndexOf('/');
            return $"/vsicurl/https://{rest[..slash]}.s3.amazonaws.com/{rest[(slash + 1)..]}";
        }
        return href.StartsWith("http") ? "/vsicurl/" + href : href;
    }

    public sealed record HiriseImage(float[] Data, int Width, int Height, double[] GeoTransform, IProjection Projection, double PixelM, string Site);

    /// <summary>The finest HiRISE orthoimage of a site over the box (null if none, too coarse or too big).</summary>
    public HiriseImage? HiriseWindow(HiriseSite site, GeoBox box, double maxMegapixels = 90)
    {
        var cands = site.Assets.Where(kv => kv.Key.Contains("ORTHO", StringComparison.OrdinalIgnoreCase) || kv.Value.Contains("ortho", StringComparison.OrdinalIgnoreCase)).ToList();
        var red = cands.Where(kv => (kv.Key + kv.Value).Contains("RED", StringComparison.OrdinalIgnoreCase)).ToList();
        (double px, string href, int c0, int r0, int w, int h)? best = null;
        foreach (var (_, href) in red.Count > 0 ? red : cands)
        {
            try
            {
                using var ds = services.OpenRaster(Vsi(href));
                var lon = new[] { box.West, box.East, box.West, box.East };
                var lat = new[] { box.South, box.South, box.North, box.North };
                var x = new double[4];
                var y = new double[4];
                ds.Projection.ToMap(lon, lat, x, y);
                var gt = ds.GeoTransform;
                var cols = x.Select(v => (v - gt[0]) / gt[1]).ToArray();
                var rows = y.Select(v => (v - gt[3]) / gt[5]).ToArray();
                int c0 = Math.Max(0, (int)Math.Floor(cols.Min())), c1 = Math.Min(ds.Width, (int)Math.Ceiling(cols.Max()));
                int r0 = Math.Max(0, (int)Math.Floor(rows.Min())), r1 = Math.Min(ds.Height, (int)Math.Ceiling(rows.Max()));
                if (c1 - c0 < 64 || r1 - r0 < 64) continue;
                double px = ds.IsGeographic ? Math.Abs(gt[1]) * Math.PI / 180 * Mars.Radius : Math.Abs(gt[1]);
                if (best is null || px < best.Value.px) best = (px, href, c0, r0, c1 - c0, r1 - r0);
            }
            catch (Exception) { }
        }
        if (best is null || best.Value.px > 0.6 || (double)best.Value.w * best.Value.h > maxMegapixels * 1e6) return null;
        var bb = best.Value;
        using var src = services.OpenRaster(Vsi(bb.href));
        var data = src.Read(bb.c0, bb.r0, bb.w, bb.h);
        double? nod = src.NoData;
        for (int i = 0; i < data.Length; i++)
            if (!float.IsFinite(data[i]) || (nod is not null && data[i] == nod.Value) || data[i] < 0) data[i] = 0;
        var g = src.GeoTransform;
        var wgt = new[] { g[0] + bb.c0 * g[1], g[1], 0, g[3] + bb.r0 * g[5], 0, g[5] };
        return new HiriseImage(data, bb.w, bb.h, wgt, src.Projection, bb.px, site.Id);
    }
}
