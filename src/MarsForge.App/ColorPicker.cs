using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MarsForge.App;

/// <summary>Pick any colour: a square (colour strength left to right, brightness top to bottom) with a hue bar
/// beside it, a hex box, and an eyedropper button to take a colour from the 3D preview.
/// ColorChanged fires while dragging.</summary>
public sealed class ColorPicker : UserControl
{
    const double S = 200, BarW = 18;
    readonly Rectangle _hueFill = new() { Width = S, Height = S };
    readonly Ellipse _thumb = new() { Width = 13, Height = 13, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    readonly Polygon _hueMark = new() { Points = new PointCollection(new[] { new Point(0, 0), new Point(7, 5), new Point(0, 10) }), Fill = Brushes.White, IsHitTestVisible = false };
    readonly Border _preview = new() { Width = 30, Height = 28, CornerRadius = new CornerRadius(4), BorderBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x5D, 0x55)), BorderThickness = new Thickness(1) };
    readonly TextBox _hex = new();
    readonly Canvas _square = new() { Width = S, Height = S, ClipToBounds = true, Cursor = Cursors.Cross };
    readonly Canvas _bar = new() { Width = BarW + 8, Height = S, Cursor = Cursors.Hand, Background = Brushes.Transparent };
    double _h, _s, _v;
    bool _quiet;

    public event Action<Color>? ColorChanged;
    /// <summary>The eyedropper button was clicked.</summary>
    public event Action? EyedropperRequested;

    public ColorPicker()
    {
        // square: the hue colour, white from the left, black from the bottom
        _square.Children.Add(_hueFill);
        _square.Children.Add(new Rectangle { Width = S, Height = S, Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) });
        _square.Children.Add(new Rectangle { Width = S, Height = S, Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) });
        _square.Children.Add(_thumb);

        // hue bar (top to bottom: red, magenta, blue, cyan, green, yellow, red)
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(FromHsv((360 - i * 60) % 360, 1, 1), i / 6.0));
        var bar = new Rectangle { Width = BarW, Height = S, Fill = rainbow, Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x2C, 0x29)) };
        Canvas.SetLeft(bar, 8);
        _bar.Children.Add(bar);
        _bar.Children.Add(_hueMark);

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x2C, 0x29)), BorderThickness = new Thickness(1), Child = _square });
        top.Children.Add(new Border { Width = 6 });
        top.Children.Add(_bar);

        _hex.Width = 100;
        _hex.Height = 28;
        _hex.Margin = new Thickness(8, 0, 8, 0);
        _hex.VerticalContentAlignment = VerticalAlignment.Center;
        _hex.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x21, 0x20));
        _hex.Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xE7, 0xE0));
        _hex.BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x2C, 0x29));
        _hex.CaretBrush = Brushes.White;
        _hex.FontFamily = new FontFamily("Consolas");
        _hex.FontSize = 13;
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };
        _hex.LostFocus += (_, _) => ApplyHex();

        var dropper = new Button
        {
            Width = 34, Height = 28, Cursor = Cursors.Hand, ToolTip = "Pick a colour from the 3D preview",
            Style = (Style)Application.Current.FindResource("SecondaryButton"), Padding = new Thickness(0),
            Content = new Path
            {
                Data = Geometry.Parse("M 13,1 L 16,4 L 13.5,6.5 L 14.5,7.5 L 13.5,8.5 L 7,15 L 3.5,15.5 L 2,17 L 1,16 L 2.5,14.5 L 3,11 L 9.5,4.5 L 10.5,3.5 L 11.5,4.5 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(0xEE, 0xE7, 0xE0)), Width = 16, Height = 17, Stretch = Stretch.Uniform,
            },
        };
        dropper.Click += (_, _) => EyedropperRequested?.Invoke();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(_preview);
        row.Children.Add(_hex);
        row.Children.Add(dropper);
        var root = new StackPanel();
        root.Children.Add(top);
        root.Children.Add(row);
        Content = root;

        _square.MouseLeftButtonDown += (_, e) => { _square.CaptureMouse(); PickSquare(e.GetPosition(_square)); };
        _square.MouseMove += (_, e) => { if (_square.IsMouseCaptured) PickSquare(e.GetPosition(_square)); };
        _square.MouseLeftButtonUp += (_, _) => _square.ReleaseMouseCapture();
        _bar.MouseLeftButtonDown += (_, e) => { _bar.CaptureMouse(); PickHue(e.GetPosition(_bar)); };
        _bar.MouseMove += (_, e) => { if (_bar.IsMouseCaptured) PickHue(e.GetPosition(_bar)); };
        _bar.MouseLeftButtonUp += (_, _) => _bar.ReleaseMouseCapture();
    }

    /// <summary>Shows a colour without firing ColorChanged.</summary>
    public void SetColor(Color c)
    {
        _quiet = true;
        (_h, _s, _v) = ToHsv(c);
        Update();
        _quiet = false;
    }

    void PickSquare(Point p)
    {
        _s = Math.Clamp(p.X / S, 0, 1);
        _v = 1 - Math.Clamp(p.Y / S, 0, 1);
        Update();
    }

    void PickHue(Point p)
    {
        _h = (1 - Math.Clamp(p.Y / S, 0, 1)) * 360 % 360;
        Update();
    }

    void ApplyHex()
    {
        var t = _hex.Text.Trim().TrimStart('#');
        if (t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
        {
            (_h, _s, _v) = ToHsv(Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v));
            Update();
        }
    }

    void Update()
    {
        var c = FromHsv(_h, _s, _v);
        _hueFill.Fill = new SolidColorBrush(FromHsv(_h, 1, 1));
        Canvas.SetLeft(_thumb, _s * S - 6.5);
        Canvas.SetTop(_thumb, (1 - _v) * S - 6.5);
        _thumb.Stroke = _v > 0.6 && _s < 0.35 ? Brushes.Black : Brushes.White;
        Canvas.SetLeft(_hueMark, 0);
        Canvas.SetTop(_hueMark, (1 - _h / 360) * S - 5);
        _preview.Background = new SolidColorBrush(c);
        if (!_hex.IsKeyboardFocused) _hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        if (!_quiet) ColorChanged?.Invoke(c);
    }

    static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (h / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }
}
