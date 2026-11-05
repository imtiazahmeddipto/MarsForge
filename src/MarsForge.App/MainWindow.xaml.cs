using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HelixToolkit.Wpf.SharpDX;
using MarsForge.Core;
using Color4 = SharpDX.Color4;
using Matrix = SharpDX.Matrix;
using MediaColor = System.Windows.Media.Color;
using Point3D = System.Windows.Media.Media3D.Point3D;
using SolidBrush = System.Windows.Media.SolidColorBrush;
using Vector2 = SharpDX.Vector2;
using Vector3 = SharpDX.Vector3;
using Vector3D = System.Windows.Media.Media3D.Vector3D;

namespace MarsForge.App;

public partial class MainWindow : Window
{
    static readonly (double R, double G, double B) DefaultSoft = (0xE2 / 255.0, 0xB4 / 255.0, 0x69 / 255.0);
    static readonly (double R, double G, double B) DefaultDeep = (0xB5 / 255.0, 0x52 / 255.0, 0x2C / 255.0);
    const int PreviewRocks = 30000, PreviewGrid = 600;
    bool _pickingDeep, _eyedrop;
    const string DrawHelp = "Click Draw box, then drag on the map (up to 20 km).";
    readonly System.Windows.Threading.DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    readonly WindowsServices _services = new();
    readonly string _models = Path.Combine(AppFiles.Lib, "models");
    readonly string _localDem = Path.Combine(AppFiles.HomeFolder, "data", "Mars_HRSC_MOLA_BlendDEM_Global_200mp_v2.tif");

    /// <summary>The folder of MarsForge.exe.</summary>
    public static string AppFolder => AppFiles.AppFolder;
    (double R, double G, double B) _soft = DefaultSoft, _deep = DefaultDeep;
    TerrainResult? _result;
    bool _busy;
    int _paintVersion;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar();
        View.EffectsManager = new DefaultEffectsManager();
        Map.BoxChanged += OnBoxChanged;
        Map.LoadingChanged += loading => MapLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        Map.Hint += (text, bad) =>
        {
            if (text.Length == 0) { DrawButton.Content = "Draw box"; HideHint(); }
            else ShowHint(text, bad, autoHide: bad);
        };
        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); if (!Map.Drawing && !DrawButton.IsMouseOver) HideHint(); };
        Deactivated += (_, _) => SearchPopup.IsOpen = false;
        LocationChanged += (_, _) => SearchPopup.IsOpen = false;
        UpdateColorButtons();
        Picker.ColorChanged += c => SetColor((c.R / 255.0, c.G / 255.0, c.B / 255.0));
        Picker.EyedropperRequested += StartEyedropper;
        PreviewArea.PreviewMouseLeftButtonDown += Eyedropper_Click;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && _eyedrop) StopEyedropper(); };
        View.MouseEnter += (_, _) => View.Focus();
    }

    /// <summary>Dark window title bar (Windows 10 / 11), in the panel colour where Windows allows it.</summary>
    void DarkTitleBar()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int on = 1, caption = 0x0018191C, text = 0x00E0E7EE;              // colours are 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));              // dark mode
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));         // caption colour (Windows 11)
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));            // caption text colour (Windows 11)
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ------------------------------------------------------------------ map
    void Draw_Click(object sender, RoutedEventArgs e)
    {
        Map.Drawing = !Map.Drawing;
        DrawButton.Content = Map.Drawing ? "Drawing…" : "Draw box";
        HideHint();                                                  // the box shows its own size while drawing
        Map.Focus();
    }

    void Draw_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!Map.Drawing && !_hintTimer.IsEnabled) ShowHint(DrawHelp, false, autoHide: false);
    }

    void Draw_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!Map.Drawing && !_hintTimer.IsEnabled) HideHint();
    }

    void ShowHint(string text, bool bad, bool autoHide)
    {
        HintText.Text = text;
        HintText.Foreground = (System.Windows.Media.Brush)FindResource(bad ? "Error" : "Text");
        HintBox.Visibility = Visibility.Visible;
        _hintTimer.Stop();
        if (autoHide) _hintTimer.Start();
    }

    void HideHint()
    {
        _hintTimer.Stop();
        HintBox.Visibility = Visibility.Collapsed;
    }

    void ZoomIn_Click(object sender, RoutedEventArgs e) => Map.ZoomBy(1);
    void ZoomOut_Click(object sender, RoutedEventArgs e) => Map.ZoomBy(-1);

    // ------------------------------------------------------------------ search
    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool empty = SearchBox.Text.Length == 0;
        SearchHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SearchClear.Visibility = empty && Map.Marker is null ? Visibility.Collapsed : Visibility.Visible;
        ShowResults();
    }

    // the list opens on the first click / focus (after the click has finished, so nothing closes it again)
    void Search_Focus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        Dispatcher.InvokeAsync(() => ShowResults(), System.Windows.Threading.DispatcherPriority.Input);

    void Search_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!SearchPopup.IsOpen) ShowResults();
    }

    void Search_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject d && SearchPopup.Child is { } list && list.IsAncestorOf(d)) return;
        SearchPopup.IsOpen = false;
    }

    void Search_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            if (Places.ParseLatLon(SearchBox.Text) is { } p) GoTo(p.lat, p.lon, null);
            else if (Places.Find(SearchBox.Text).FirstOrDefault() is { } place) GoTo(place.Lat, place.Lon, place.Name);
            else ShowResults(notFound: true);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            SearchPopup.IsOpen = false;
            Map.Focus();
        }
    }

    void ShowResults(bool notFound = false)
    {
        SearchResults.Children.Clear();
        if (Places.ParseLatLon(SearchBox.Text) is { } p)
            SearchResults.Children.Add(ResultRow("Go to " + Places.Format(p.lat, p.lon), "latitude, longitude", () => GoTo(p.lat, p.lon, null)));
        else
        {
            foreach (var place in Places.Find(SearchBox.Text))
                SearchResults.Children.Add(ResultRow(place.Name, $"{place.Note} · {Places.Format(place.Lat, place.Lon)}", () => GoTo(place.Lat, place.Lon, place.Name)));
            if (SearchResults.Children.Count == 0)
                SearchResults.Children.Add(new TextBlock
                {
                    Text = notFound || SearchBox.Text.Length > 0 ? "Not found. Try a place name or 18.4, 77.5 (or 18.4N 77.5E)." : "",
                    Foreground = (System.Windows.Media.Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(8, 6, 8, 6), TextWrapping = TextWrapping.Wrap,
                });
        }
        SearchPopup.IsOpen = SearchBox.IsKeyboardFocused || notFound;
    }

    UIElement ResultRow(string title, string detail, Action go)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 13 });
        text.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
        var row = new Border { Child = text, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6), Background = System.Windows.Media.Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        var hover = new SolidBrush(MediaColor.FromRgb(0x3A, 0x30, 0x2C));
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => go();
        return row;
    }

    void GoTo(double lat, double lon, string? name)
    {
        Map.Marker = (lon, lat);
        Map.GoTo(lon, lat);
        Map.Focus();                                                 // first, so the new text does not open the list again
        SearchBox.Text = name ?? Places.Format(lat, lon);
        SearchPopup.IsOpen = false;
        SearchClear.Visibility = Visibility.Visible;
    }

    void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        Map.Marker = null;
        SearchBox.Text = "";
        SearchClear.Visibility = Visibility.Collapsed;
        SearchPopup.IsOpen = false;
    }

    void OnBoxChanged(GeoBox? box)
    {
        DrawButton.Content = "Draw box";
        HideHint();
        if (box is not { } b) return;
        var (w, h) = b.SizeM;
        AreaText.Text = $"Area: {MapView.Km(w)} × {MapView.Km(h)} at {Math.Abs(b.CenterLat):0.000}°{(b.CenterLat >= 0 ? "N" : "S")}, " +
                        $"{Math.Abs(b.CenterLon):0.000}°{(b.CenterLon >= 0 ? "E" : "W")}";
        GenerateButton.IsEnabled = !_busy;
        if (_result is not null) Status("Click Generate to make the new area.");
        else Status("");
    }

    // ------------------------------------------------------------------ generate
    async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (Map.Box is not { } box || _busy) return;
        var opt = new TerrainOptions((SurfaceDetail)DetailBox.SelectedIndex, (RockSize)RockSizeBox.SelectedIndex,
                                     RocksCheck.IsChecked == true, SoilCheck.IsChecked == true);
        SetBusy(true);
        Status("Starting…", busy: true);
        try
        {
            var progress = new Progress<string>(m => { if (_busy) Status(m, busy: true); });
            var job = new TerrainJob(_services, _models, _localDem);
            var result = await Task.Run(() => job.RunAsync(box, opt, progress, CancellationToken.None));
            Status("Loading preview…", busy: true);
            await ShowResult(result);
            _result = result;
            var bits = new System.Collections.Generic.List<string>();
            if (opt.Rocks) bits.Add($"{result.Rocks?.Count ?? 0:N0} rocks");
            if (opt.SoftGround) bits.Add(result.Soil is { Zones: > 0 } s ? $"{s.Zones} soft ground areas" : "no soft ground found");
            Status("Done" + (bits.Count > 0 ? " — " + string.Join(", ", bits) : "") + ".");
            DownloadButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log.Error("Generate", ex);
            Status(Friendly(ex), error: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    static string Friendly(Exception ex)
    {
        var e = ex is AggregateException ae ? ae.Flatten().InnerException ?? ex : ex;
        return e is ArgumentException or IOException ? e.Message : "Something went wrong (" + e.Message + "). Try again.";
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        GenerateButton.IsEnabled = !busy && Map.Box is not null;
        DownloadButton.IsEnabled = !busy && _result is not null;
        Spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    void Status(string text, bool busy = false, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "Error" : "Muted");
        Spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    void Setting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && _result is not null && !_busy) Status("Click Generate to use the new setting.");
    }

    void Tick_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        RocksModel.Visibility = RocksCheck.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
        ColorsPanel.Visibility = SoilCheck.IsChecked == true && _result.RiskTexture is not null ? Visibility.Visible : Visibility.Collapsed;
        _ = Paint();
        bool missing = (RocksCheck.IsChecked == true && !_result.Options.Rocks) || (SoilCheck.IsChecked == true && !_result.Options.SoftGround);
        if (missing && !_busy) Status("Click Generate to add it.");
    }

    // ------------------------------------------------------------------ 3D preview
    async Task ShowResult(TerrainResult r)
    {
        var m = r.Mesh;
        var geo = await Task.Run(() => TerrainGeometry(m));
        TerrainModel.Geometry = geo;
        TerrainModel.Material = new PhongMaterial
        {
            DiffuseColor = new Color4(1, 1, 1, 1), AmbientColor = new Color4(0.35f, 0.32f, 0.3f, 1),
            SpecularColor = new Color4(0, 0, 0, 1), SpecularShininess = 1,
        };
        var (rockGeo, instances) = await Task.Run(() => Rocks(r));
        RocksModel.Geometry = rockGeo;
        RocksModel.Instances = instances;
        RocksModel.Material = new PhongMaterial { DiffuseColor = new Color4(0.35f, 0.27f, 0.23f, 1), AmbientColor = new Color4(0.3f, 0.27f, 0.25f, 1), SpecularColor = new Color4(0, 0, 0, 1) };
        RocksModel.Visibility = RocksCheck.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
        ColorsPanel.Visibility = SoilCheck.IsChecked == true && r.RiskTexture is not null ? Visibility.Visible : Visibility.Collapsed;

        // camera: look at the whole terrain from the south, a little above
        float span = (float)Math.Max(m.WidthM, m.DepthM), top = m.Positions.Where((_, i) => i % 3 == 1).Max();
        var c = new Point3D(0, top / 2, 0);
        var pos = new Point3D(c.X + span * 0.2, c.Y + span * 0.5, c.Z + span * 0.9);
        Camera.Position = pos;
        Camera.LookDirection = new Vector3D(c.X - pos.X, c.Y - pos.Y, c.Z - pos.Z);
        Camera.UpDirection = new Vector3D(0, 1, 0);
        Camera.NearPlaneDistance = Math.Max(0.5, span / 5000);
        Camera.FarPlaneDistance = span * 20;
        Sun.Direction = new Vector3D(1, -1, -0.5);
        View.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        _result = r;
        await Paint();
    }

    /// <summary>Terrain for the preview (three.js-like axes: x east, y up, z south), at most PreviewGrid vertices
    /// per side so turning and zooming stay smooth. The download always uses the full mesh.</summary>
    static MeshGeometry3D TerrainGeometry(TerrainMesh m)
    {
        int step = Math.Max(1, (int)Math.Ceiling(Math.Max(m.Nx, m.Ny) / (double)PreviewGrid));
        int[] cols = Pick(m.Nx, step), rows = Pick(m.Ny, step);
        int nx = cols.Length, ny = rows.Length;
        var pos = new Vector3Collection(nx * ny);
        var nrm = new Vector3Collection(nx * ny);
        var uv = new Vector2Collection(nx * ny);
        foreach (int r in rows)
            foreach (int c in cols)
            {
                int i = r * m.Nx + c;
                pos.Add(new Vector3(m.Positions[3 * i], m.Positions[3 * i + 1], -m.Positions[3 * i + 2]));
                nrm.Add(new Vector3(m.Normals[3 * i], m.Normals[3 * i + 1], -m.Normals[3 * i + 2]));
                uv.Add(new Vector2(m.Uvs[2 * i], 1 - m.Uvs[2 * i + 1]));
            }
        var idx = new IntCollection((nx - 1) * (ny - 1) * 6);
        for (int r = 0; r < ny - 1; r++)
            for (int c = 0; c < nx - 1; c++)
            {
                int a = r * nx + c, b = a + 1, cc = a + nx, d = cc + 1;
                idx.Add(a); idx.Add(cc); idx.Add(d);
                idx.Add(a); idx.Add(d); idx.Add(b);
            }
        return new MeshGeometry3D { Positions = pos, Normals = nrm, TextureCoordinates = uv, Indices = idx };
    }

    static int[] Pick(int n, int step)
    {
        var list = new System.Collections.Generic.List<int>();
        for (int i = 0; i < n - 1; i += step) list.Add(i);
        list.Add(n - 1);
        return [.. list];
    }

    /// <summary>One lumpy rock shape, placed many times (biggest rocks first).</summary>
    static (MeshGeometry3D?, System.Collections.Generic.List<Matrix>) Rocks(TerrainResult r)
    {
        var list = new System.Collections.Generic.List<Matrix>();
        if (r.Rocks is not { Count: > 0 } rocks) return (null, list);
        var idx = Enumerable.Range(0, rocks.Count).OrderByDescending(i => rocks.Diameter[i]).Take(PreviewRocks).ToArray();
        var (x, y, z) = r.Mesh.Ground(idx.Select(i => rocks.Lon[i]).ToArray(), idx.Select(i => rocks.Lat[i]).ToArray());
        for (int k = 0; k < idx.Length; k++)
        {
            int i = idx[k];
            float h = rocks.Height[i];
            list.Add(Matrix.Scaling(rocks.Major[i], h * 1.2f, rocks.Minor[i]) * Matrix.RotationY(-rocks.Yaw[i] * MathF.PI / 180)
                     * Matrix.Translation(x[k], y[k] + 0.1f * h, -z[k]));
        }
        var (p, nn, t) = RockMesh.Build([0f], [0f], [0f], [1f], [1f], [1f], [0f], 3);
        var pos = new Vector3Collection();
        var nrm = new Vector3Collection();
        for (int v = 0; v < p.Length / 3; v++)
        {
            pos.Add(new Vector3(p[3 * v], p[3 * v + 1], -p[3 * v + 2]));
            nrm.Add(new Vector3(nn[3 * v], nn[3 * v + 1], -nn[3 * v + 2]));
        }
        return (new MeshGeometry3D { Positions = pos, Normals = nrm, Indices = new IntCollection(t) }, list);
    }

    /// <summary>Puts the texture (with the chosen soft-ground colours) on the terrain.</summary>
    async Task Paint()
    {
        if (_result is null || TerrainModel.Material is not PhongMaterial mat) return;
        int version = ++_paintVersion;
        await Task.Delay(60);                                        // wait while colours are still changing
        if (version != _paintVersion) return;
        var result = _result;
        bool soil = SoilCheck.IsChecked == true;                     // read the tick here: controls belong to the window's thread
        try
        {
            var png = await Task.Run(() => TexturePng(result, soil));
            if (version != _paintVersion) return;
            mat.DiffuseMap = TextureModel.Create(new MemoryStream(png));
        }
        catch (Exception ex)
        {
            Log.Error("Texture", ex);
            Status("The texture could not be shown (" + ex.Message + ").", error: true);
        }
    }

    byte[] TexturePng(TerrainResult r, bool soil)
    {
        var g = r.GroundTexture;
        var rgba = Textures.Compose(g, soil ? r.RiskTexture : null, _soft, _deep);
        return Png.Rgba(rgba, g.Width, g.Height);
    }

    // ------------------------------------------------------------------ colours
    void SoftColor_Click(object sender, RoutedEventArgs e) => OpenPalette(false, SoftColorButton);
    void DeepColor_Click(object sender, RoutedEventArgs e) => OpenPalette(true, DeepColorButton);

    void OpenPalette(bool deep, UIElement target)
    {
        _pickingDeep = deep;
        ColorPopupTitle.Text = deep ? "Deep soft ground colour" : "Soft ground colour";
        var c = deep ? _deep : _soft;
        Picker.SetColor(MediaColor.FromRgb((byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255)));
        ColorPopup.PlacementTarget = target;
        ColorPopup.IsOpen = true;
    }

    // eyedropper: click anywhere on the 3D preview to take that colour
    void StartEyedropper()
    {
        ColorPopup.IsOpen = false;
        _eyedrop = true;
        PreviewArea.Cursor = View.Cursor = System.Windows.Input.Cursors.Cross;
        Status("Click on the 3D preview to take a colour (Esc to cancel).");
    }

    void StopEyedropper()
    {
        _eyedrop = false;
        PreviewArea.Cursor = View.Cursor = null;
        if (!_busy) Status("");
    }

    void Eyedropper_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_eyedrop) return;
        e.Handled = true;                                            // do not turn the view
        var p = PreviewArea.PointToScreen(e.GetPosition(PreviewArea));
        using var bmp = new System.Drawing.Bitmap(1, 1);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen((int)p.X, (int)p.Y, 0, 0, new System.Drawing.Size(1, 1));
        var c = bmp.GetPixel(0, 0);
        StopEyedropper();
        SetColor((c.R / 255.0, c.G / 255.0, c.B / 255.0));
        OpenPalette(_pickingDeep, _pickingDeep ? DeepColorButton : SoftColorButton);
    }

    void SetColor((double R, double G, double B) color)
    {
        if (_pickingDeep) _deep = color; else _soft = color;
        UpdateColorButtons();
        _ = Paint();
    }

    void ResetColors_Click(object sender, RoutedEventArgs e)
    {
        _soft = DefaultSoft;
        _deep = DefaultDeep;
        UpdateColorButtons();
        _ = Paint();
    }

    void UpdateColorButtons()
    {
        static SolidBrush B((double R, double G, double B) c) => new(MediaColor.FromRgb((byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255)));
        SoftColorButton.Background = B(_soft);
        DeepColorButton.Background = B(_deep);
    }

    // ------------------------------------------------------------------ download
    bool UnityFormat => FormatBox?.SelectedIndex == 1;

    void Format_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DownloadButton is null) return;
        DownloadButton.Content = UnityFormat ? "Download for Unity 6+" : "Download .fbx";
    }

    async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_result is not { } r || _busy) return;
        bool unity = UnityFormat;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = BlenderExport.BaseName(r.Mesh) + ".zip",
            Filter = unity ? "Zip file with the Unity 6+ terrain (*.zip)|*.zip" : "Zip file with the .fbx and its texture (*.zip)|*.zip",
            Title = unity ? "Save the Unity 6+ terrain (.asset + terrain layer, zipped)" : "Save the terrain (.fbx + texture, zipped)",
        };
        if (dlg.ShowDialog(this) != true) return;
        SetBusy(true);
        try
        {
            bool soil = SoilCheck.IsChecked == true, rocks = RocksCheck.IsChecked == true;
            string path = dlg.FileName;
            if (unity)
            {
                Status("Creating the Unity terrain…", busy: true);
                var (soft, deep) = (_soft, _deep);
                await Task.Run(() =>
                {
                    var g = r.GroundTexture;
                    var rgba = Textures.Compose(g, soil ? r.RiskTexture : null, soft, deep);   // same texture as the FBX
                    UnityExport.SaveZip(path, r, rgba, g.Width, g.Height, rocks ? r.Rocks : null);
                });
                Status($"Saved {Path.GetFileName(path)}. Put its files (with the .meta files) anywhere in your Unity 6 project's Assets folder.");
            }
            else
            {
                Status("Making the texture…", busy: true);
                var png = await Task.Run(() => TexturePng(r, soil));
                Status("Creating the .fbx file…", busy: true);
                await Task.Run(() => BlenderExport.SaveZip(path, r, png, rocks));
                Status($"Saved {Path.GetFileName(path)}. Unzip it, then in Blender: File › Import › FBX.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(unity ? "Unity export" : "FBX export", ex);
            Status("Saving failed: " + ex.Message, error: true);
        }
        finally
        {
            SetBusy(false);
        }
    }
}
