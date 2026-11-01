namespace MarsForge.Core;

/// <summary>An AI model (ONNX in the app). Run returns the outputs in the model's order, flattened.</summary>
public interface IModel : IDisposable
{
    float[][] Run(float[] input, int[] shape);
}

/// <summary>A rock found in a HiRISE image. X, Y: image pixels (x right, y down; integer = pixel centre).</summary>
public readonly record struct FoundRock(double X, double Y, double DiameterM, double MajorM, double MinorM, double AngleDeg, double Score);

/// <summary>Finds real rocks in HiRISE images with RockBoxNet (same steps and settings as the original detector):
/// model peaks -> half-brightness outline -> remove doubles -> rule-based extra rocks for large ones.</summary>
public sealed class RockDetector(IModel model)
{
    public double Threshold { get; init; } = 0.35;
    public int Tile { get; init; } = 512;
    public int Overlap { get; init; } = 64;
    public double SupplementMinDiameterPx { get; init; } = 8.0;
    public double SupplementMaxAspect { get; init; } = 1.8;

    sealed class Geometry
    {
        public double CenterX, CenterY, XMin, XMax, YMin, YMax, AreaPx, EqDiameterPx, MajorPx, MinorPx, OrientationDeg;
        public bool FromBox;
    }

    sealed record Candidate(double Cx, double Cy, double W, double H, double Score);

    sealed class Rock(double score, Geometry g, Candidate m)
    {
        public double Score = score;
        public Geometry G = g;
        public Candidate M = m;
    }

    /// <summary>image: brightness values, 0 = no data.</summary>
    public List<FoundRock> Detect(float[] image, int w, int h, double pixelM, Action<double>? progress = null)
    {
        var (heat, extra) = Stitch(image, w, h, progress);
        var rocks = new List<Rock>();
        foreach (var d in Decode(heat, extra, w, h))
            rocks.Add(new Rock(d.Score, Measure(image, w, h, d.Cx, d.Cy, d.W, d.H) ?? Box(d.Cx, d.Cy, d.W, d.H), d));
        rocks = Deduplicate(rocks);
        rocks.AddRange(Supplement(image, w, h, rocks));
        var outp = new List<FoundRock>(rocks.Count);
        foreach (var r0 in rocks)
        {
            var r = AreaLimit(r0);
            var g = r.G;
            outp.Add(new FoundRock(g.CenterX, g.CenterY, g.EqDiameterPx * pixelM, g.MajorPx * pixelM, g.MinorPx * pixelM, g.OrientationDeg, r.Score));
        }
        return outp;
    }

    // ------------------------------------------------------------------ model on overlapping tiles
    static List<int> Origins(int length, int tile, int overlap)
    {
        if (length <= tile) return [0];
        var s = new SortedSet<int>();
        for (int i = 0; i < length - tile; i += tile - overlap) s.Add(i);
        s.Add(length - tile);
        return [.. s];
    }

    /// <summary>Heat map of the whole image, plus box size and offset at the pixels that may be rocks.</summary>
    (float[] heat, Dictionary<int, (float w, float h, float ox, float oy)> extra) Stitch(float[] image, int w, int h, Action<double>? progress)
    {
        var heat = new float[w * h];
        var valid = new bool[w * h];
        var extra = new Dictionary<int, (float, float, float, float)>();
        int half = Overlap / 2;
        var rows = Origins(h, Tile, Overlap);
        var cols = Origins(w, Tile, Overlap);
        int total = rows.Count * cols.Count, done = 0;
        foreach (int ty in rows)
            foreach (int tx in cols)
            {
                int th = Math.Min(Tile, h - ty), tw = Math.Min(Tile, w - tx);
                var data = new float[th * tw];
                var mask = new bool[th * tw];
                int nValid = 0;
                for (int r = 0; r < th; r++)
                    for (int c = 0; c < tw; c++)
                    {
                        float v = image[(ty + r) * w + tx + c];
                        data[r * tw + c] = v;
                        if (v > 0) { mask[r * tw + c] = true; nValid++; }
                    }
                float[][]? maps = nValid >= 64 ? Predict(data, mask, tw, th) : null;
                int top = ty == rows[0] ? 0 : half, left = tx == cols[0] ? 0 : half;
                int bottom = ty == rows[^1] ? th : th - half, right = tx == cols[^1] ? tw : tw - half;
                for (int r = top; r < bottom; r++)
                    for (int c = left; c < right; c++)
                    {
                        int k = r * tw + c, g = (ty + r) * w + tx + c;
                        valid[g] = mask[k];
                        float v = maps is null ? 0 : maps[0][k];
                        heat[g] = v;
                        if (maps is not null && v >= Threshold) extra[g] = (maps[1][k], maps[2][k], maps[3][k], maps[4][k]);
                        else extra.Remove(g);
                    }
                progress?.Invoke(++done / (double)total);
            }
        var eroded = Erode(valid, w, h, 3);
        for (int i = 0; i < heat.Length; i++) if (!eroded[i]) heat[i] = 0;
        return (heat, extra);
    }

    static bool[] Erode(bool[] m, int w, int h, int iterations)
    {
        var cur = (bool[])m.Clone();
        for (int it = 0; it < iterations; it++)
        {
            var nxt = new bool[cur.Length];
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                {
                    int i = r * w + c;
                    nxt[i] = cur[i] && r > 0 && cur[i - w] && r < h - 1 && cur[i + w] && c > 0 && cur[i - 1] && c < w - 1 && cur[i + 1];
                }
            cur = nxt;
        }
        return cur;
    }

    /// <summary>RockBoxNet on one tile: [heat 0..1, box width, box height, offset x, offset y].</summary>
    float[][] Predict(float[] data, bool[] valid, int w, int h)
    {
        const int multiple = 8;
        int pw = w + (-w % multiple + multiple) % multiple, ph = h + (-h % multiple + multiple) % multiple;
        double sum = 0, sum2 = 0;
        int n = 0;
        for (int i = 0; i < data.Length; i++) if (valid[i]) { sum += data[i]; sum2 += (double)data[i] * data[i]; n++; }
        double mean = n > 0 ? sum / n : 0, std = n > 0 ? Math.Sqrt(Math.Max(sum2 / n - mean * mean, 0)) : 0;
        var x = new float[2 * ph * pw];
        if (n >= 16)
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    if (valid[r * w + c]) x[r * pw + c] = (float)((data[r * w + c] - mean) / Math.Max(std, 1.0));
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++) x[ph * pw + r * pw + c] = valid[r * w + c] ? 1 : 0;
        var o = model.Run(x, [1, 2, ph, pw]);                      // heat, size, offset
        var maps = new float[5][];
        for (int k = 0; k < 5; k++) maps[k] = new float[w * h];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int i = r * w + c, p = r * pw + c, plane = ph * pw;
                maps[0][i] = valid[i] ? 1f / (1f + MathF.Exp(-o[0][p])) : 0f;
                maps[1][i] = MathF.Exp(o[1][p]);
                maps[2][i] = MathF.Exp(o[1][plane + p]);
                maps[3][i] = o[2][p];
                maps[4][i] = o[2][plane + p];
            }
        return maps;
    }

    // ------------------------------------------------------------------ peaks -> boxes
    List<Candidate> Decode(float[] heat, Dictionary<int, (float w, float h, float ox, float oy)> extra, int w, int h)
    {
        var peaks = new List<int>();
        foreach (int i in extra.Keys)
        {
            float v = heat[i];
            if (v < Threshold) continue;
            int r = i / w, c = i % w;
            bool max = true;
            for (int dr = -1; dr <= 1 && max; dr++)
                for (int dc = -1; dc <= 1; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    int rr = r + dr, cc = c + dc;
                    if (rr < 0 || cc < 0 || rr >= h || cc >= w) continue;
                    if (heat[rr * w + cc] > v) { max = false; break; }
                }
            if (max) peaks.Add(i);
        }
        peaks.Sort((a, b) => heat[b] != heat[a] ? heat[b].CompareTo(heat[a]) : a.CompareTo(b));
        var kept = new List<(Candidate c, double x0, double y0, double x1, double y1)>();
        var grid = new Dictionary<(int, int), List<int>>();
        const int cell = 32;
        foreach (int i in peaks)
        {
            int r = i / w, c = i % w;
            var (bw, bh, ox, oy) = extra[i];
            double cx = c + ox, cy = r + oy;
            var box = (cx - bw / 2.0, cy - bh / 2.0, cx + bw / 2.0, cy + bh / 2.0);
            var key = ((int)Math.Floor(cy / cell), (int)Math.Floor(cx / cell));
            bool clash = false;
            for (int dy = -1; dy <= 1 && !clash; dy++)
                for (int dx = -1; dx <= 1 && !clash; dx++)
                    if (grid.TryGetValue((key.Item1 + dy, key.Item2 + dx), out var list))
                        foreach (int j in list)
                        {
                            var k = kept[j];
                            if (Iou(box, (k.x0, k.y0, k.x1, k.y1)) > 0.3 ||
                                Math.Sqrt((cx - k.c.Cx) * (cx - k.c.Cx) + (cy - k.c.Cy) * (cy - k.c.Cy)) < 0.5 * Math.Min(Math.Max(bw, bh), Math.Max(k.c.W, k.c.H)))
                            { clash = true; break; }
                        }
            if (clash) continue;
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = [];
            l.Add(kept.Count);
            kept.Add((new Candidate(cx, cy, bw, bh, heat[i]), box.Item1, box.Item2, box.Item3, box.Item4));
        }
        return kept.Select(k => k.c).ToList();
    }

    static double Iou((double, double, double, double) a, (double, double, double, double) b)
    {
        double ix = Math.Max(0, Math.Min(a.Item3, b.Item3) - Math.Max(a.Item1, b.Item1));
        double iy = Math.Max(0, Math.Min(a.Item4, b.Item4) - Math.Max(a.Item2, b.Item2));
        double inter = ix * iy;
        double union = (a.Item3 - a.Item1) * (a.Item4 - a.Item2) + (b.Item3 - b.Item1) * (b.Item4 - b.Item2) - inter;
        return union > 0 ? inter / union : 0;
    }

    // ------------------------------------------------------------------ rock outline at half brightness
    static Geometry? Measure(float[] image, int iw, int ih, double cx, double cy, double w, double h, double minSnr = 4.0)
    {
        double size = Math.Max(Math.Max(w, h), 2.0);
        int pad = (int)Math.Ceiling(1.6 * size + 4);
        int r0 = (int)Math.Round(cy, MidpointRounding.ToEven) - pad, c0 = (int)Math.Round(cx, MidpointRounding.ToEven) - pad;
        int n = 2 * pad + 1;
        if (r0 < 0 || c0 < 0 || r0 + n > ih || c0 + n > iw) return null;
        var patch = new float[n * n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                float v = image[(r0 + r) * iw + c0 + c];
                if (v <= 0) return null;
                patch[r * n + c] = v;
            }
        double lx = cx - c0, ly = cy - r0;
        var dist = new double[n * n];
        var ringVals = new List<float>();
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                double d = Math.Sqrt((c - lx) * (c - lx) + (r - ly) * (r - ly));
                dist[r * n + c] = d;
                if (d >= 0.75 * size + 2 && d <= pad) ringVals.Add(patch[r * n + c]);
            }
        if (ringVals.Count < 12) return null;
        var ring = ringVals.ToArray();
        double background = Img.Median(ring);
        var dev = ring.Select(v => (float)Math.Abs(v - background)).ToArray();
        double noise = Math.Max(1.4826 * Img.Median(dev), 0.5);
        double coreR = Math.Max(1.5, 0.35 * size), peak = double.MinValue;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                if (dist[r * n + c] > coreR) continue;
                double s = 0;
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                        s += patch[Math.Clamp(r + dr, 0, n - 1) * n + Math.Clamp(c + dc, 0, n - 1)];
                peak = Math.Max(peak, s / 9.0);
            }
        if (peak - background < minSnr * noise) return null;
        double threshold = background + 0.5 * (peak - background);
        var above = new bool[n * n];
        for (int i = 0; i < above.Length; i++) above[i] = patch[i] > threshold;
        var (labels, count) = Img.Label(above, n, n, eight: false);
        var votes = new int[count + 1];
        bool anySeed = false;
        for (int i = 0; i < above.Length; i++)
            if (above[i] && dist[i] <= coreR) { votes[labels[i]]++; anySeed = true; }
        if (!anySeed) return null;
        int best = 1;
        for (int k = 2; k <= count; k++) if (votes[k] > votes[best]) best = k;
        var rows = new List<int>();
        var cols = new List<int>();
        for (int i = 0; i < labels.Length; i++) if (labels[i] == best) { rows.Add(i / n); cols.Add(i % n); }
        if (rows.Count < 2 || rows.Min() == 0 || cols.Min() == 0 || rows.Max() == n - 1 || cols.Max() == n - 1) return null;
        if (rows.Count > 2.0 * w * h + 9) return null;
        double sw = 0, sx = 0, sy = 0;
        for (int k = 0; k < rows.Count; k++)
        {
            double wt = patch[rows[k] * n + cols[k]] - background;
            sw += wt; sx += cols[k] * wt; sy += rows[k] * wt;
        }
        var (minor, major, ang) = Ellipse(rows, cols);
        double area = rows.Count;
        return new Geometry
        {
            CenterX = sx / sw + c0, CenterY = sy / sw + r0,
            XMin = cols.Min() - 0.5 + c0, XMax = cols.Max() + 0.5 + c0, YMin = rows.Min() - 0.5 + r0, YMax = rows.Max() + 0.5 + r0,
            AreaPx = area, EqDiameterPx = 2.0 * Math.Sqrt(area / Math.PI), MajorPx = major, MinorPx = minor, OrientationDeg = ang,
        };
    }

    /// <summary>(minor, major, orientation 0..180) of the ellipse with the same spread as the pixels.</summary>
    static (double minor, double major, double angle) Ellipse(List<int> rows, List<int> cols)
    {
        double mr = rows.Average(), mc = cols.Average(), a = 0, b = 0, d = 0;
        for (int k = 0; k < rows.Count; k++)
        {
            double dc = cols[k] - mc, dr = rows[k] - mr;
            a += dc * dc; b += dc * dr; d += dr * dr;
        }
        a = a / rows.Count + 1.0 / 12; b /= rows.Count; d = d / rows.Count + 1.0 / 12;
        var (small, large, vx, vy) = Img.Eigen2(a, b, d);
        double ang = Mars.Deg(Math.Atan2(vy, vx)) % 180.0;
        if (ang < 0) ang += 180.0;
        return (4.0 * Math.Sqrt(Math.Max(small, 1e-6)), 4.0 * Math.Sqrt(Math.Max(large, 1e-6)), ang);
    }

    static Geometry Box(double cx, double cy, double w, double h) => new()
    {
        CenterX = cx, CenterY = cy, XMin = cx - w / 2, XMax = cx + w / 2, YMin = cy - h / 2, YMax = cy + h / 2,
        AreaPx = Math.PI * w * h / 4.0, EqDiameterPx = Math.Sqrt(w * h), MajorPx = Math.Max(w, h), MinorPx = Math.Min(w, h),
        OrientationDeg = w >= h ? 0 : 90, FromBox = true,
    };

    static List<Rock> Deduplicate(List<Rock> rocks)
    {
        var kept = new List<Rock>();
        var grid = new Dictionary<(int, int), List<Rock>>();
        const int cell = 16;
        foreach (var rock in rocks.OrderByDescending(r => r.Score))
        {
            var g = rock.G;
            var key = ((int)Math.Floor(g.CenterY / cell), (int)Math.Floor(g.CenterX / cell));
            bool clash = false;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (grid.TryGetValue((key.Item1 + dy, key.Item2 + dx), out var list))
                        foreach (var o in list.Select(x => x.G))
                            if ((o.XMin <= g.CenterX && g.CenterX <= o.XMax && o.YMin <= g.CenterY && g.CenterY <= o.YMax) ||
                                Math.Sqrt((g.CenterX - o.CenterX) * (g.CenterX - o.CenterX) + (g.CenterY - o.CenterY) * (g.CenterY - o.CenterY)) < 1.0)
                                clash = true;
            if (clash) continue;
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = [];
            l.Add(rock);
            kept.Add(rock);
        }
        return kept;
    }

    static Rock AreaLimit(Rock r)
    {
        if (!r.G.FromBox && r.G.AreaPx > 2.0 * r.M.W * r.M.H + 9) return new Rock(r.Score, Box(r.M.Cx, r.M.Cy, r.M.W, r.M.H), r.M);
        return r;
    }

    // ------------------------------------------------------------------ rule-based extra rocks (large bright bodies)
    List<Rock> Supplement(float[] image, int w, int h, List<Rock> rocks)
    {
        var outp = new List<Rock>();
        foreach (var b in PseudoRocks(image, w, h))
        {
            double d = b.EqDiameterPx;
            if (d < SupplementMinDiameterPx || b.MajorPx > SupplementMaxAspect * Math.Max(b.MinorPx, 1e-6)) continue;
            bool clash = rocks.Any(r => Math.Sqrt((r.G.CenterX - b.CenterX) * (r.G.CenterX - b.CenterX) + (r.G.CenterY - b.CenterY) * (r.G.CenterY - b.CenterY)) <= Math.Max(2.0, 0.5 * d)
                                     || (r.G.XMin <= b.CenterX && b.CenterX <= r.G.XMax && r.G.YMin <= b.CenterY && b.CenterY <= r.G.YMax));
            if (clash) continue;
            var g = Measure(image, w, h, b.CenterX, b.CenterY, b.WidthPx, b.HeightPx) ?? Box(b.CenterX, b.CenterY, b.WidthPx, b.HeightPx);
            outp.Add(new Rock(b.Confidence, g, new Candidate(b.CenterX, b.CenterY, b.WidthPx, b.HeightPx, b.Confidence)));
        }
        return outp;
    }

    sealed record Body(double CenterX, double CenterY, double WidthPx, double HeightPx, double EqDiameterPx, double MajorPx, double MinorPx, double Confidence);

    /// <summary>Compact, isolated, bright bodies (local z-score) with size and shape limits.</summary>
    static List<Body> PseudoRocks(float[] image, int w, int h)
    {
        const int radius = 24;
        const double brightZ = 2.0, bodyMeanZ = 2.8, peakZ = 3.5, ringMaxZ = 0.8, minCompact = 0.5, maxAspect = 3.0, minD = 3.0, maxD = 48.0;
        const int minArea = 6, maxArea = 1600;
        var valid = new bool[w * h];
        for (int i = 0; i < valid.Length; i++) valid[i] = image[i] > 0;
        var z = LocalZ(image, valid, w, h, radius);
        var mask = new bool[w * h];
        for (int i = 0; i < mask.Length; i++) mask[i] = valid[i] && z[i] > brightZ;
        var (labels, count) = Img.Label(mask, w, h, eight: false);
        var found = new List<Body>();
        if (count == 0) return found;
        var area = new int[count + 1];
        foreach (int l in labels) area[l]++;
        var start = new int[count + 2];
        for (int l = 1; l <= count; l++) start[l + 1] = start[l] + area[l];
        var members = new int[start[count + 1]];
        var fill = (int[])start.Clone();
        for (int i = 0; i < labels.Length; i++) if (labels[i] > 0) members[fill[labels[i]]++] = i;
        for (int l = 1; l <= count; l++)
        {
            if (area[l] < minArea || area[l] > maxArea) continue;
            int r0 = int.MaxValue, r1 = -1, c0 = int.MaxValue, c1 = -1;
            double zs = 0, zmax = double.MinValue;
            var rows = new List<int>(area[l]);
            var cols = new List<int>(area[l]);
            for (int k = start[l]; k < start[l + 1]; k++)
            {
                int i = members[k], r = i / w, c = i % w;
                rows.Add(r); cols.Add(c);
                r0 = Math.Min(r0, r); r1 = Math.Max(r1, r); c0 = Math.Min(c0, c); c1 = Math.Max(c1, c);
                zs += z[i]; zmax = Math.Max(zmax, z[i]);
            }
            int bw = c1 - c0 + 1, bh = r1 - r0 + 1;
            if (Math.Max(bw, bh) / (double)Math.Max(Math.Min(bw, bh), 1) > maxAspect) continue;
            if (rows.Count / (Math.PI / 4 * bw * bh) < minCompact) continue;
            double bodyZ = zs / rows.Count;
            if (bodyZ < bodyMeanZ || zmax < peakZ) continue;
            int pad = (int)Math.Max(2, Math.Ceiling(0.5 * Math.Max(bw, bh)));
            int y0 = Math.Max(r0 - pad, 0), y1 = Math.Min(r1 + pad + 1, h), x0 = Math.Max(c0 - pad, 0), x1 = Math.Min(c1 + pad + 1, w);
            if (y0 == 0 || x0 == 0 || y1 == h || x1 == w) continue;
            bool allValid = true;
            double ringSum = 0;
            int ringN = 0;
            for (int r = y0; r < y1 && allValid; r++)
                for (int c = x0; c < x1; c++)
                {
                    int i = r * w + c;
                    if (!valid[i]) { allValid = false; break; }
                    if (labels[i] != l) { ringSum += z[i]; ringN++; }
                }
            if (!allValid) continue;
            double ringZ = ringN > 0 ? ringSum / ringN : 99.0;
            if (ringZ > ringMaxZ) continue;
            double diameter = Math.Sqrt(bw * bh);
            if (diameter < minD || diameter > maxD) continue;
            var (minor, major, _) = Ellipse(rows, cols);
            double conf = 0.6 * Math.Clamp((bodyZ - brightZ) / 4.0, 0, 1) + 0.4 * Math.Clamp((ringMaxZ - ringZ) / 1.5, 0, 1);
            found.Add(new Body((c0 - 0.5 + c1 + 0.5) / 2, (r0 - 0.5 + r1 + 0.5) / 2, bw, bh, diameter, major, minor, conf));
        }
        return found;
    }

    /// <summary>(value - local mean) / local spread over a (2r+1)^2 window (edges repeat), spread at least 2.</summary>
    static float[] LocalZ(float[] image, bool[] valid, int w, int h, int r)
    {
        var data = new double[w * h];
        var data2 = new double[w * h];
        var wt = new double[w * h];
        for (int i = 0; i < data.Length; i++)
            if (valid[i]) { data[i] = image[i]; data2[i] = (double)image[i] * image[i]; wt[i] = 1; }
        var s1 = BoxSum(data, w, h, r);
        var s2 = BoxSum(data2, w, h, r);
        var cnt = BoxSum(wt, w, h, r);
        var z = new float[w * h];
        Parallel.For(0, h, row =>
        {
            for (int c = 0; c < w; c++)
            {
                int i = row * w + c;
                if (!valid[i]) continue;
                double n = Math.Max(cnt[i], 1.0), mean = s1[i] / n;
                double var = Math.Max(s2[i] / n - mean * mean, 0.0);
                z[i] = (float)((image[i] - mean) / Math.Max(Math.Sqrt(var), 2.0));
            }
        });
        return z;
    }

    /// <summary>Sum over a (2r+1)^2 window around each pixel; outside the image the edge value repeats.</summary>
    static double[] BoxSum(double[] a, int w, int h, int r)
    {
        var tmp = new double[w * h];
        Parallel.For(0, h, row => Running(a, row * w, 1, w, r, tmp));
        var outp = new double[w * h];
        Parallel.For(0, w, col => Running(tmp, col, w, h, r, outp));
        return outp;
    }

    static void Running(double[] src, int offset, int stride, int n, int r, double[] dst)
    {
        var prefix = new double[n + 1];
        for (int k = 0; k < n; k++) prefix[k + 1] = prefix[k] + src[offset + k * stride];
        double first = src[offset], last = src[offset + (n - 1) * stride];
        for (int j = 0; j < n; j++)
        {
            int lo = j - r, hi = j + r;
            double s = prefix[Math.Min(hi, n - 1) + 1] - prefix[Math.Max(lo, 0)];
            if (lo < 0) s += -lo * first;
            if (hi > n - 1) s += (hi - (n - 1)) * last;
            dst[offset + j * stride] = s;
        }
    }
}
