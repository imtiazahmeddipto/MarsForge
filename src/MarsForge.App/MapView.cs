using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MarsForge.Core;

namespace MarsForge.App;

/// <summary>The NASA Mars Trek map (Viking colour mosaic, lon/lat tiles). Drag to move, scroll to zoom;
/// in drawing mode, drag to draw the box.</summary>
public sealed class MapView : FrameworkElement
{
    const string TileUrl = "https://trek.nasa.gov/tiles/Mars/EQ/Mars_Viking_MDIM21_ClrMosaic_global_232m/1.0.0//default/default028mm/{z}/{y}/{x}.jpg";
    const int TilePx = 256, MaxNativeZoom = 7;
    const double MinZoom = 0.5, MaxZoom = 15;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly Dictionary<(int z, int x, int y), ImageSource?> _tiles = [];
    readonly SemaphoreSlim _gate = new(8);
    double _lon, _lat, _zoom = 1;
    bool _fitted, _drawing, _panning;
    Point _start, _last;
    (double lon, double lat)? _dragA, _dragB;          // box corners while drawing (map position, so zooming keeps them)
    bool _clamped;
    GeoBox? _box;

    static readonly Brush BoxFill = new SolidColorBrush(Color.FromArgb(30, 241, 154, 99));
    static readonly Pen BoxPen = new(new SolidColorBrush(Color.FromRgb(241, 154, 99)), 2);
    static readonly Pen LimitPen = new(new SolidColorBrush(Color.FromRgb(255, 190, 120)), 2.5);
    static readonly Brush LabelBg = new SolidColorBrush(Color.FromArgb(235, 0x1C, 0x19, 0x18));
    static readonly Brush LabelText = new SolidColorBrush(Color.FromRgb(0xEE, 0xE7, 0xE0));
    static readonly Brush LabelLimit = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47));

    /// <summary>The finished box changed (null = removed).</summary>
    public event Action<GeoBox?>? BoxChanged;
    /// <summary>Size text while drawing, or a problem.</summary>
    public event Action<string, bool>? Hint;

    public GeoBox? Box => _box;

    /// <summary>True while map pictures are still downloading.</summary>
    public event Action<bool>? LoadingChanged;
    int _loading;
    (double lon, double lat)? _marker;

    /// <summary>A pin on the map (null = none).</summary>
    public (double lon, double lat)? Marker
    {
        get => _marker;
        set { _marker = value; InvalidateVisual(); }
    }

    /// <summary>Moves the map to a place (zoom 11: about 16 km across).</summary>
    public void GoTo(double lon, double lat, double zoom = 11)
    {
        _lon = lon;
        _lat = lat;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Clamp();
        InvalidateVisual();
    }

    public bool Drawing
    {
        get => _drawing;
        set
        {
            _drawing = value;
            Cursor = value ? Cursors.Cross : Cursors.Arrow;
        }
    }

    static MapView()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("MarsForge/1.0");
        BoxPen.Freeze();
        LimitPen.Freeze();
        LabelBg.Freeze();
        LabelText.Freeze();
        LabelLimit.Freeze();
        BoxFill.Freeze();
    }

    public MapView()
    {
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, _) =>
        {
            if (!_fitted && ActualWidth > 0)
            {
                _zoom = Math.Max(MinZoom, Math.Log2(ActualWidth / 360.0 * 180 / TilePx));
                _fitted = true;
            }
            InvalidateVisual();
        };
    }

    double PxPerDeg => TilePx * Math.Pow(2, _zoom) / 180.0;

    Point ToScreen(double lon, double lat) => new((lon - _lon) * PxPerDeg + ActualWidth / 2, (_lat - lat) * PxPerDeg + ActualHeight / 2);

    (double lon, double lat) ToGeo(Point p) => (_lon + (p.X - ActualWidth / 2) / PxPerDeg, _lat - (p.Y - ActualHeight / 2) / PxPerDeg);

    public void ZoomBy(double steps, Point? at = null)
    {
        var p = at ?? new Point(ActualWidth / 2, ActualHeight / 2);
        var (lon, lat) = ToGeo(p);
        _zoom = Math.Clamp(_zoom + steps, MinZoom, MaxZoom);
        _lon = lon - (p.X - ActualWidth / 2) / PxPerDeg;
        _lat = lat + (p.Y - ActualHeight / 2) / PxPerDeg;
        Clamp();
        InvalidateVisual();
    }

    void Clamp()
    {
        _lon = Math.Clamp(_lon, -180, 180);
        _lat = Math.Clamp(_lat, -90, 90);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        int level = Math.Clamp((int)Math.Ceiling(_zoom - 0.2), 0, MaxNativeZoom);
        for (int z = Math.Max(0, level - 4); z < level; z++)           // coarser pictures underneath: never black while loading
            DrawLevel(dc, z, load: true);
        DrawLevel(dc, level, load: true);
        if (_marker is { } mk) DrawPin(dc, ToScreen(mk.lon, mk.lat));
        if (_dragA is { } a && _dragB is { } b)
        {
            var box = Corners(a, b);
            var r = new Rect(ToScreen(box.West, box.North), ToScreen(box.East, box.South));
            dc.DrawRectangle(BoxFill, _clamped ? LimitPen : BoxPen, r);
            DrawSize(dc, box, r, _clamped);
        }
        else if (_box is { } box)
        {
            var r = new Rect(ToScreen(box.West, box.North), ToScreen(box.East, box.South));
            dc.DrawRectangle(BoxFill, BoxPen, r);
            DrawSize(dc, box, r, false);
        }
        var credit = new FormattedText("NASA Mars Trek · USGS", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, Brushes.LightGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null,
            new Rect(ActualWidth - credit.Width - 12, ActualHeight - 18, credit.Width + 12, 18));
        dc.DrawText(credit, new Point(ActualWidth - credit.Width - 6, ActualHeight - 16));
    }

    void DrawLevel(DrawingContext dc, int z, bool load)
    {
        double tdeg = 180.0 / (1 << z);
        var (lon0, lat1) = ToGeo(new Point(0, 0));
        var (lon1, lat0) = ToGeo(new Point(ActualWidth, ActualHeight));
        int x0 = Math.Max(0, (int)Math.Floor((lon0 + 180) / tdeg)), x1 = Math.Min((2 << z) - 1, (int)Math.Floor((lon1 + 180) / tdeg));
        int y0 = Math.Max(0, (int)Math.Floor((90 - lat1) / tdeg)), y1 = Math.Min((1 << z) - 1, (int)Math.Floor((90 - lat0) / tdeg));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var key = (z, x, y);
                if (_tiles.TryGetValue(key, out var img))
                {
                    if (img is null) continue;
                    var p0 = ToScreen(-180 + x * tdeg, 90 - y * tdeg);
                    var p1 = ToScreen(-180 + (x + 1) * tdeg, 90 - (y + 1) * tdeg);
                    dc.DrawImage(img, new Rect(Math.Floor(p0.X), Math.Floor(p0.Y), Math.Ceiling(p1.X - p0.X) + 1, Math.Ceiling(p1.Y - p0.Y) + 1));
                }
                else if (load)
                {
                    _tiles[key] = null;
                    _ = LoadTile(key);
                }
            }
    }

    static readonly Brush PinFill = Frozen(new SolidColorBrush(Color.FromRgb(0xE2, 0x73, 0x3A)));
    static readonly Pen PinPen = FrozenPen(new Pen(Brushes.White, 2));
    static readonly Geometry PinShape = Geometry.Parse("M 0,0 C -3,-9 -11,-13 -11,-21 A 11,11 0 1 1 11,-21 C 11,-13 3,-9 0,0 Z");

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
    static Pen FrozenPen(Pen p) { p.Freeze(); return p; }

    static void DrawPin(DrawingContext dc, Point p)
    {
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, new Point(p.X, p.Y), 6, 2.5);
        dc.PushTransform(new TranslateTransform(p.X, p.Y));
        dc.DrawGeometry(PinFill, PinPen, PinShape);
        dc.DrawEllipse(Brushes.White, null, new Point(0, -21), 4, 4);
        dc.Pop();
    }

    void SetLoading(int delta)
    {
        bool before = _loading > 0;
        _loading += delta;
        if (before != _loading > 0) LoadingChanged?.Invoke(_loading > 0);
    }

    async Task LoadTile((int z, int x, int y) key)
    {
        SetLoading(+1);
        await _gate.WaitAsync();
        try
        {
            var url = TileUrl.Replace("{z}", key.z.ToString()).Replace("{x}", key.x.ToString()).Replace("{y}", key.y.ToString());
            var bytes = await Http.GetByteArrayAsync(url);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            _tiles[key] = bmp;
            if (_tiles.Count > 1500) Trim(key.z);
            InvalidateVisual();
        }
        catch (Exception)
        {
            _ = Task.Delay(5000).ContinueWith(_ => Dispatcher.Invoke(() => _tiles.Remove(key)));   // try again later
        }
        finally
        {
            _gate.Release();
            SetLoading(-1);
        }
    }

    void Trim(int keepLevel)
    {
        var drop = new List<(int, int, int)>();
        foreach (var k in _tiles.Keys) if (k.z > 3 && Math.Abs(k.z - keepLevel) > 4) drop.Add(k);
        foreach (var k in drop) _tiles.Remove(k);
    }

    static GeoBox Corners((double lon, double lat) a, (double lon, double lat) b) =>
        new(Math.Max(-180, Math.Min(a.lon, b.lon)), Math.Max(-89.9, Math.Min(a.lat, b.lat)),
            Math.Min(180, Math.Max(a.lon, b.lon)), Math.Min(89.9, Math.Max(a.lat, b.lat)));

    /// <summary>The dragged corner, kept so that no side is longer than 20 km (each side on its own).</summary>
    (double lon, double lat) Limit((double lon, double lat) a, (double lon, double lat) p)
    {
        const double max = Mars.MaxSideM * 0.9995;
        double maxDLat = Mars.Deg(max / Mars.Radius), dLat = p.lat - a.lat;
        bool clamped = false;
        if (Math.Abs(dLat) > maxDLat) { dLat = Math.Sign(dLat) * maxDLat; clamped = true; }
        double lat = a.lat + dLat;
        double maxDLon = Mars.Deg(max / (Mars.Radius * Math.Cos(Mars.Rad((a.lat + lat) / 2)))), dLon = p.lon - a.lon;
        if (Math.Abs(dLon) > maxDLon) { dLon = Math.Sign(dLon) * maxDLon; clamped = true; }
        _clamped = clamped;
        return (a.lon + dLon, lat);
    }

    /// <summary>Size label centred under the box ("8.2 × 6.1 km", amber "20 km max" at the limit).</summary>
    void DrawSize(DrawingContext dc, GeoBox box, Rect r, bool limit)
    {
        var (w, h) = box.SizeM;
        string text = $"{Km(w)} × {Km(h)}" + (limit ? "  ·  20 km max" : "");
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12,
                                   limit ? LabelLimit : LabelText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double bw = ft.Width + 16, bh = 22;
        double x = Math.Clamp(r.Left + r.Width / 2 - bw / 2, 4, Math.Max(4, ActualWidth - bw - 4));   // centred under the box
        double y = r.Bottom + 6;
        if (y + bh > ActualHeight - 22) y = Math.Max(4, r.Top - bh - 6);   // no room below: above the box
        dc.DrawRoundedRectangle(LabelBg, null, new Rect(x, y, bw, bh), 6, 6);
        dc.DrawText(ft, new Point(x + 8, y + (bh - ft.Height) / 2));
    }

    public static string Km(double m) => m >= 1000 ? $"{m / 1000:0.0} km" : $"{m:0} m";

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        _start = _last = e.GetPosition(this);
        if (_drawing)
        {
            _dragA = _dragB = ToGeo(_start);
            _clamped = false;
        }
        else _panning = true;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragA is { } a && e.LeftButton == MouseButtonState.Pressed)
        {
            _dragB = Limit(a, ToGeo(p));
            InvalidateVisual();
        }
        else if (_panning && e.LeftButton == MouseButtonState.Pressed)
        {
            _lon -= (p.X - _last.X) / PxPerDeg;
            _lat += (p.Y - _last.Y) / PxPerDeg;
            Clamp();
            _last = p;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        _panning = false;
        if (_dragA is { } a && _dragB is { } b)
        {
            _dragA = _dragB = null;
            Drawing = false;
            var box = Corners(a, b);
            var (w, h) = box.SizeM;
            if (Math.Min(w, h) < Mars.MinSideM) Hint?.Invoke("The box is too small. Drag a bigger box.", true);
            else
            {
                _box = box;
                BoxChanged?.Invoke(box);
            }
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomBy(e.Delta / 120.0 * 0.5, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_drawing || _dragA is not null))
        {
            _dragA = _dragB = null;
            Drawing = false;
            Hint?.Invoke("", false);
            InvalidateVisual();
        }
    }
}
