namespace MarsForge.Core;

/// <summary>A grey picture (0..255), row by row, north at the top.</summary>
public sealed record GrayImage(byte[] Pixels, int Width, int Height)
{
    public float At(double u, double v)          // u, v in 0..1 (bilinear)
    {
        double c = u * (Width - 1), r = v * (Height - 1);
        int c0 = Math.Clamp((int)c, 0, Width - 1), r0 = Math.Clamp((int)r, 0, Height - 1);
        int c1 = Math.Min(c0 + 1, Width - 1), r1 = Math.Min(r0 + 1, Height - 1);
        double fc = c - c0, fr = r - r0;
        double top = Pixels[r0 * Width + c0] * (1 - fc) + Pixels[r0 * Width + c1] * fc;
        double bot = Pixels[r1 * Width + c0] * (1 - fc) + Pixels[r1 * Width + c1] * fc;
        return (float)(top * (1 - fr) + bot * fr);
    }
}

/// <summary>The ground texture: plain seamless Mars ground with soft ground painted on top.</summary>
public static class Textures
{
    public const int Size = 2048;

    /// <summary>Picture size for a box: powers of two, long side = size, shape close to the box.</summary>
    public static (int W, int H) Shape(double widthM, double heightM, int size)
    {
        double ratio = Math.Min(widthM, heightM) / Math.Max(widthM, heightM);
        int shortSide = Math.Max(size / 8, (int)Math.Pow(2, Math.Round(Math.Log2(size * ratio))));
        shortSide = Math.Min(shortSide, size);
        return widthM >= heightM ? (size, shortSide) : (shortSide, size);
    }

    /// <summary>Plain, seamless Mars ground (grey, mean 128): fine sandy grain and very gentle colour drift.
    /// No photo details, no shading, no repeating tiles; its edges also match, so it can be repeated.</summary>
    public static GrayImage Ground(double widthM, double heightM, int seed, int size = Size)
    {
        var (w, h) = Shape(widthM, heightM, size);
        var z = Noise.Seamless(w, h, new Random(seed),
            new Noise.Band(1.0, 1.5, 6),                          // sand grain
            new Noise.Band(0.5, 6, 40),                           // small soft spots
            new Noise.Band(0.6, size / 8.0, size / 2.0, 0.5));    // gentle colour drift
        var px = new byte[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)Math.Clamp(128 + 9 * z[i], 0, 255);
        return new GrayImage(px, w, h);
    }

    /// <summary>Soft-ground risk (0..1 grid, north up) as a smooth grey picture with natural, uneven edges.</summary>
    public static GrayImage Risk(float[] risk, int rw, int rh, double widthM, double heightM, int seed, int size = Size / 2)
    {
        var (w, h) = Shape(widthM, heightM, size);
        var big = Img.Resize(risk, rw, rh, w, h);
        double px = Math.Max(widthM / w, heightM / h);
        big = Img.Gaussian(big, w, h, Math.Max(2.0, 60 / px), EdgeMode.Nearest);
        var n = Noise.Seamless(w, h, new Random(seed + 1), new Noise.Band(1.0, Math.Max(4.0, 80 / px), Math.Max(8.0, 400 / px)));
        var outp = new byte[w * h];
        for (int i = 0; i < outp.Length; i++) outp[i] = (byte)(Math.Clamp(big[i] + 0.04 * n[i], 0, 1) * 255);
        return new GrayImage(outp, w, h);
    }

    /// <summary>The final colour texture (RGBA): Mars ground, with soft ground (light colour) and deep soft ground
    /// (dark colour) blended in smoothly. Colours are 0..1 RGB.</summary>
    public static byte[] Compose(GrayImage ground, GrayImage? risk, (double R, double G, double B) soft, (double R, double G, double B) deep)
    {
        int w = ground.Width, h = ground.Height;
        var outp = new byte[w * h * 4];
        double[] dark = [0.40, 0.25, 0.17], light = [0.86, 0.62, 0.45];
        double[] s = [soft.R, soft.G, soft.B], d = [deep.R, deep.G, deep.B];
        Parallel.For(0, h, r =>
        {
            for (int c = 0; c < w; c++)
            {
                int i = r * w + c;
                double t = ground.Pixels[i] / 255.0;
                double a1 = 0, a2 = 0, shade = 0.78 + 0.44 * t;
                if (risk is not null)
                {
                    double v = risk.At(c / (double)Math.Max(1, w - 1), r / (double)Math.Max(1, h - 1)) / 255.0;
                    a1 = 0.65 * Smooth(0.36, 0.70, v);
                    a2 = 0.75 * Smooth(0.60, 0.92, v);
                }
                for (int k = 0; k < 3; k++)
                {
                    double col = dark[k] + (light[k] - dark[k]) * t;
                    col += (s[k] * shade - col) * a1;
                    col += (d[k] * shade - col) * a2;
                    outp[4 * i + k] = (byte)Math.Clamp(col * 255, 0, 255);
                }
                outp[4 * i + 3] = 255;
            }
        });
        return outp;
    }

    static double Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
