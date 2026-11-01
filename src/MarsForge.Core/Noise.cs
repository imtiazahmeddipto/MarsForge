namespace MarsForge.Core;

/// <summary>Random surfaces made in the frequency domain (no tiles, no seams, no grid pattern).</summary>
public static class Noise
{
    /// <summary>One band of a noise recipe: weight, shortest and longest wavelength (pixels), spectral slope.</summary>
    public readonly record struct Band(double Weight, double MinWavelength, double MaxWavelength, double Slope = 1.0);

    /// <summary>Noise that repeats perfectly across its edges (left = right, top = bottom). Width and height must be
    /// powers of two. Mean 0, standard deviation 1.</summary>
    public static float[] Seamless(int w, int h, Random rng, params Band[] bands)
    {
        if (!IsPow2(w) || !IsPow2(h)) throw new ArgumentException("Size must be a power of two.");
        var re = new float[w * h];
        var im = new float[w * h];
        for (int i = 0; i < re.Length; i++) re[i] = (float)Gauss(rng);
        Fft.Transform2D(re, im, w, h, false);
        var amp = new float[w * h];
        foreach (var b in bands)
        {
            var a = new float[w * h];
            double energy = 0;
            for (int r = 0; r < h; r++)
            {
                double ky = (r <= h / 2 ? r : r - h) / (double)h;
                for (int c = 0; c < w; c++)
                {
                    double kx = (c <= w / 2 ? c : c - w) / (double)w;
                    double k = Math.Sqrt(kx * kx + ky * ky);
                    if (k >= 1.0 / b.MaxWavelength && k <= 1.0 / b.MinWavelength)
                    {
                        double v = Math.Pow(Math.Max(k, 1e-6), -b.Slope);
                        a[r * w + c] = (float)v;
                        energy += v * v;
                    }
                }
            }
            if (energy <= 0) continue;
            float f = (float)(b.Weight / Math.Sqrt(energy));
            for (int i = 0; i < a.Length; i++) amp[i] += f * a[i];
        }
        for (int i = 0; i < re.Length; i++) { re[i] *= amp[i]; im[i] *= amp[i]; }
        Fft.Transform2D(re, im, w, h, true);
        Normalise(re);
        return re;
    }

    /// <summary>Fractal bumps for a terrain grid (not repeating): wavelengths lamMin..lamMax metres, RMS 1.</summary>
    public static float[] Fractal(int nx, int ny, double spacing, Random rng, double lamMin, double lamMax, double hurst = 0.8)
    {
        var outp = new float[nx * ny];
        if (lamMin >= lamMax) return outp;
        int pad = (int)(lamMax / spacing) + 2;                 // pad, so the edges don't wrap around
        int w = NextPow2(nx + 2 * pad), h = NextPow2(ny + 2 * pad);
        var re = new float[w * h];
        var im = new float[w * h];
        for (int i = 0; i < re.Length; i++) re[i] = (float)Gauss(rng);
        Fft.Transform2D(re, im, w, h, false);
        for (int r = 0; r < h; r++)
        {
            double ky = (r <= h / 2 ? r : r - h) / (h * spacing);
            for (int c = 0; c < w; c++)
            {
                double kx = (c <= w / 2 ? c : c - w) / (w * spacing);
                double k = Math.Sqrt(kx * kx + ky * ky);
                float a = k >= 1.0 / lamMax && k <= 1.0 / lamMin ? (float)Math.Pow(k * lamMax, -(hurst + 1.0)) : 0f;
                re[r * w + c] *= a;
                im[r * w + c] *= a;
            }
        }
        Fft.Transform2D(re, im, w, h, true);
        for (int r = 0; r < ny; r++)
            for (int c = 0; c < nx; c++) outp[r * nx + c] = re[(r + pad) * w + c + pad];
        double std = Img.Std(outp);
        if (std > 0) for (int i = 0; i < outp.Length; i++) outp[i] = (float)(outp[i] / std);
        return outp;
    }

    public static double Gauss(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    public static bool IsPow2(int n) => n > 0 && (n & (n - 1)) == 0;

    public static int NextPow2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    static void Normalise(float[] a)
    {
        double m = Img.Mean(a), s = Img.Std(a);
        if (s < 1e-12) s = 1;
        for (int i = 0; i < a.Length; i++) a[i] = (float)((a[i] - m) / s);
    }
}

/// <summary>Fast Fourier transform (radix 2) of complex 2-D arrays.</summary>
public static class Fft
{
    public static void Transform2D(float[] re, float[] im, int w, int h, bool inverse)
    {
        Parallel.For(0, h, () => (new double[w], new double[w]), (r, _, buf) =>
        {
            var (br, bi) = buf;
            for (int c = 0; c < w; c++) { br[c] = re[r * w + c]; bi[c] = im[r * w + c]; }
            Transform(br, bi, inverse);
            for (int c = 0; c < w; c++) { re[r * w + c] = (float)br[c]; im[r * w + c] = (float)bi[c]; }
            return buf;
        }, _ => { });
        Parallel.For(0, w, () => (new double[h], new double[h]), (c, _, buf) =>
        {
            var (br, bi) = buf;
            for (int r = 0; r < h; r++) { br[r] = re[r * w + c]; bi[r] = im[r * w + c]; }
            Transform(br, bi, inverse);
            for (int r = 0; r < h; r++) { re[r * w + c] = (float)br[r]; im[r * w + c] = (float)bi[r]; }
            return buf;
        }, _ => { });
    }

    /// <summary>In-place 1-D FFT; the inverse is scaled by 1/n.</summary>
    public static void Transform(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
        if (inverse)
            for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }
}
