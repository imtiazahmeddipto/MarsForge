namespace MarsForge.Core;

public enum EdgeMode { Reflect, Nearest, Wrap }

/// <summary>2-D image operations on float arrays stored row by row (same results as the scipy ones used before).</summary>
public static class Img
{
    /// <summary>Gaussian blur (sigma in pixels, kernel cut at 4 sigma). Reflect = mirror at the pixel edge.</summary>
    public static float[] Gaussian(float[] a, int w, int h, double sigma, EdgeMode border = EdgeMode.Reflect)
    {
        if (sigma <= 0) return (float[])a.Clone();
        int rad = (int)(4.0 * sigma + 0.5);
        var k = new double[2 * rad + 1];
        double sum = 0;
        for (int i = -rad; i <= rad; i++) { k[i + rad] = Math.Exp(-0.5 * i * i / (sigma * sigma)); sum += k[i + rad]; }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        var tmp = new float[a.Length];
        var outp = new float[a.Length];
        Parallel.For(0, h, r =>
        {
            int o = r * w;
            for (int c = 0; c < w; c++)
            {
                double s = 0;
                for (int i = -rad; i <= rad; i++) s += k[i + rad] * a[o + Index(c + i, w, border)];
                tmp[o + c] = (float)s;
            }
        });
        Parallel.For(0, w, c =>
        {
            for (int r = 0; r < h; r++)
            {
                double s = 0;
                for (int i = -rad; i <= rad; i++) s += k[i + rad] * tmp[Index(r + i, h, border) * w + c];
                outp[r * w + c] = (float)s;
            }
        });
        return outp;
    }

    public static int Index(int i, int n, EdgeMode border)
    {
        if (i >= 0 && i < n) return i;
        switch (border)
        {
            case EdgeMode.Nearest:
                return i < 0 ? 0 : n - 1;
            case EdgeMode.Wrap:
                return ((i % n) + n) % n;
            default:
                if (n == 1) return 0;
                int period = 2 * n;
                i = ((i % period) + period) % period;
                return i < n ? i : period - 1 - i;
        }
    }

    /// <summary>Slope parts like numpy.gradient: central differences inside, one-sided at the edges.</summary>
    public static (float[] dRow, float[] dCol) Gradient(float[] a, int w, int h, double spacing)
    {
        var gr = new float[a.Length];
        var gc = new float[a.Length];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int i = r * w + c;
                gc[i] = w < 2 ? 0 : (float)(c == 0 ? (a[i + 1] - a[i]) / spacing : c == w - 1 ? (a[i] - a[i - 1]) / spacing : (a[i + 1] - a[i - 1]) / (2 * spacing));
                gr[i] = h < 2 ? 0 : (float)(r == 0 ? (a[i + w] - a[i]) / spacing : r == h - 1 ? (a[i] - a[i - w]) / spacing : (a[i + w] - a[i - w]) / (2 * spacing));
            }
        return (gr, gc);
    }

    /// <summary>Resize with cubic interpolation; corners map to corners.</summary>
    public static float[] Resize(float[] a, int w, int h, int nw, int nh)
    {
        var outp = new float[nw * nh];
        double sr = nh > 1 ? (h - 1.0) / (nh - 1) : 0, sc = nw > 1 ? (w - 1.0) / (nw - 1) : 0;
        Parallel.For(0, nh, r =>
        {
            for (int c = 0; c < nw; c++) outp[r * nw + c] = Interp.Cubic(a, w, h, r * sr, c * sc);
        });
        return outp;
    }

    /// <summary>Connected areas of true pixels. eight = diagonal neighbours count too. Returns labels (0 = none) and count.</summary>
    public static (int[] labels, int count) Label(bool[] mask, int w, int h, bool eight)
    {
        var lab = new int[mask.Length];
        int n = 0;
        var stack = new Stack<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || lab[start] != 0) continue;
            n++;
            lab[start] = n;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int p = stack.Pop(), r = p / w, c = p % w;
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if ((dr == 0 && dc == 0) || (!eight && dr != 0 && dc != 0)) continue;
                        int rr = r + dr, cc = c + dc;
                        if (rr < 0 || cc < 0 || rr >= h || cc >= w) continue;
                        int q = rr * w + cc;
                        if (mask[q] && lab[q] == 0) { lab[q] = n; stack.Push(q); }
                    }
            }
        }
        return (lab, n);
    }

    public static double Mean(ReadOnlySpan<float> a)
    {
        double s = 0;
        foreach (float v in a) s += v;
        return a.Length > 0 ? s / a.Length : 0;
    }

    public static double Std(ReadOnlySpan<float> a)
    {
        double m = Mean(a), s = 0;
        foreach (float v in a) s += (v - m) * (v - m);
        return a.Length > 0 ? Math.Sqrt(s / a.Length) : 0;
    }

    /// <summary>Mean of the finite values (NaN if none).</summary>
    public static double NanMean(ReadOnlySpan<float> a)
    {
        double s = 0;
        int n = 0;
        foreach (float v in a) if (float.IsFinite(v)) { s += v; n++; }
        return n > 0 ? s / n : double.NaN;
    }

    public static double FiniteShare(ReadOnlySpan<float> a)
    {
        int n = 0;
        foreach (float v in a) if (float.IsFinite(v)) n++;
        return a.Length > 0 ? (double)n / a.Length : 0;
    }

    public static void FillNaN(Span<float> a, float value)
    {
        for (int i = 0; i < a.Length; i++) if (!float.IsFinite(a[i])) a[i] = value;
    }

    public static double Median(Span<float> a)
    {
        if (a.Length == 0) return double.NaN;
        var copy = a.ToArray();
        Array.Sort(copy);
        int m = copy.Length / 2;
        return copy.Length % 2 == 1 ? copy[m] : 0.5 * (copy[m - 1] + copy[m]);
    }

    /// <summary>Eigen-decomposition of a symmetric 2x2 matrix [[a, b], [b, d]]: (small, large, vector of large (x, y)).</summary>
    public static (double small, double large, double vx, double vy) Eigen2(double a, double b, double d)
    {
        double tr = a + d, det = a * d - b * b;
        double disc = Math.Sqrt(Math.Max(tr * tr / 4 - det, 0));
        double l1 = tr / 2 - disc, l2 = tr / 2 + disc;
        double vx, vy;
        if (Math.Abs(b) > 1e-12) { vx = l2 - d; vy = b; }
        else if (a >= d) { vx = 1; vy = 0; }
        else { vx = 0; vy = 1; }
        double len = Math.Sqrt(vx * vx + vy * vy);
        return (l1, l2, vx / len, vy / len);
    }
}
