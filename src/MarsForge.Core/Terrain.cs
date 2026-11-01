namespace MarsForge.Core;

public enum SurfaceDetail { Smooth, Low, Medium, High }

/// <summary>The terrain mesh: a grid of vertices in metres (x east, y up, z north; origin at the box centre,
/// y = height above the lowest point). Rows run north to south.</summary>
public sealed class TerrainMesh
{
    public required GeoBox Box { get; init; }
    public required int Nx { get; init; }
    public required int Ny { get; init; }
    public required float[] Positions { get; init; }   // x, y, z per vertex
    public required float[] Normals { get; init; }
    public required float[] Uvs { get; init; }          // u, v per vertex (v = 1 at the north edge)
    public required int[] Indices { get; init; }        // triangles
    public double WidthM { get; init; }
    public double DepthM { get; init; }
    public double ElevationBaseM { get; init; }
    public double CenterLat => Box.CenterLat;
    public double CenterLon => Box.CenterLon;

    /// <summary>Mesh position (x, ground height y, z) of lon/lat points inside the box.</summary>
    public (float[] x, float[] y, float[] z) Ground(ReadOnlySpan<double> lon, ReadOnlySpan<double> lat)
    {
        int n = lon.Length;
        var x = new float[n];
        var y = new float[n];
        var z = new float[n];
        for (int i = 0; i < n; i++)
        {
            double c = Math.Clamp((lon[i] - Box.West) / (Box.East - Box.West) * (Nx - 1), 0, Nx - 1);   // columns are even in longitude
            double r = Math.Clamp((Box.North - lat[i]) / (Box.North - Box.South) * (Ny - 1), 0, Ny - 1); // rows are even in latitude
            int c0 = Math.Min((int)c, Nx - 2), r0 = Math.Min((int)r, Ny - 2);
            double fc = c - c0, fr = r - r0;
            for (int k = 0; k < 3; k++)
            {
                double v00 = Positions[3 * (r0 * Nx + c0) + k], v01 = Positions[3 * (r0 * Nx + c0 + 1) + k];
                double v10 = Positions[3 * ((r0 + 1) * Nx + c0) + k], v11 = Positions[3 * ((r0 + 1) * Nx + c0 + 1) + k];
                float v = (float)((v00 * (1 - fc) + v01 * fc) * (1 - fr) + (v10 * (1 - fc) + v11 * fc) * fr);
                if (k == 0) x[i] = v; else if (k == 1) y[i] = v; else z[i] = v;
            }
        }
        return (x, y, z);
    }
}

/// <summary>Builds the terrain from the USGS MOLA-HRSC blended elevation map (one height every 200 m).
/// The mesh passes exactly through every real height and is smooth (cubic) in between.</summary>
public static class TerrainBuilder
{
    public const string DemUrl = "https://planetarymaps.usgs.gov/mosaic/Mars/HRSC_MOLA_Blend/Mars_HRSC_MOLA_BlendDEM_Global_200mp_v2.tif";
    public const int MaxGrid = 1000;
    public const double MinSpacingM = 2.0;

    public static double DetailLevel(SurfaceDetail d) => d switch
    {
        SurfaceDetail.Smooth => 0,
        SurfaceDetail.Low => 0.5,
        SurfaceDetail.Medium => 1,
        _ => 2,
    };

    public static TerrainMesh Build(IRasterSource dem, GeoBox box, SurfaceDetail detail)
    {
        if (!(box.West >= -180 && box.West < box.East && box.East <= 180)) throw new ArgumentException("The box must not cross the 180° line.");
        if (Math.Max(Math.Abs(box.South), Math.Abs(box.North)) > 85) throw new ArgumentException("Boxes near the poles are not supported.");
        var (W, H) = box.SizeM;
        if (Math.Max(W, H) > Mars.MaxSideM * 1.002) throw new ArgumentException("The box is too big. Draw one up to 20 km.");
        if (Math.Min(W, H) < Mars.MinSideM) throw new ArgumentException("The box is too small.");

        // real heights around the box (+4 samples for the curves)
        var lonC = new[] { box.West, box.East, box.West, box.East };
        var latC = new[] { box.South, box.South, box.North, box.North };
        var X = new double[4];
        var Y = new double[4];
        dem.Projection.ToMap(lonC, latC, X, Y);
        var gt = dem.GeoTransform;
        double cMin = Math.Min((X[0] - gt[0]) / gt[1], (X[1] - gt[0]) / gt[1]), cMax = Math.Max((X[0] - gt[0]) / gt[1], (X[1] - gt[0]) / gt[1]);
        double rMin = Math.Min((Y[0] - gt[3]) / gt[5], (Y[2] - gt[3]) / gt[5]), rMax = Math.Max((Y[0] - gt[3]) / gt[5], (Y[2] - gt[3]) / gt[5]);
        int c0 = Math.Max(0, (int)Math.Floor(cMin) - 4), c1 = Math.Min(dem.Width, (int)Math.Ceiling(cMax) + 4);
        int r0 = Math.Max(0, (int)Math.Floor(rMin) - 4), r1 = Math.Min(dem.Height, (int)Math.Ceiling(rMax) + 4);
        int ww = c1 - c0, wh = r1 - r0;
        if (ww < 2 || wh < 2) throw new ArgumentException("No height data here.");
        float[] z;
        try { z = dem.Read(c0, r0, ww, wh); }
        catch (Exception ex) { throw new IOException("Downloading the heights failed. Check your internet connection and try again.", ex); }
        FillGaps(z, ww, wh, dem.NoData, (float)dem.Scale, (float)dem.Offset);

        // vertex grid: true-metre spacing, at most MaxGrid per side
        double spacing = Math.Max(MinSpacingM, Math.Max(W, H) / (MaxGrid - 1));
        int nx = (int)Math.Round(W / spacing) + 1, ny = (int)Math.Round(H / spacing) + 1;
        var hgt = new float[nx * ny];
        var lons = new double[nx];
        var lats = new double[ny];
        var gxs = new double[nx];
        var gys = new double[ny];
        for (int c = 0; c < nx; c++) gxs[c] = X[0] + (X[1] - X[0]) * c / (nx - 1);
        for (int r = 0; r < ny; r++) gys[r] = Y[2] + (Y[0] - Y[2]) * r / (ny - 1);   // north to south
        {
            // the elevation map is equirectangular: longitude depends on x only, latitude on y only
            var tmpY = Enumerable.Repeat(Y[2], nx).ToArray();
            var tmpLat = new double[nx];
            dem.Projection.ToLonLat(gxs, tmpY, lons, tmpLat);
            var tmpX = Enumerable.Repeat(X[0], ny).ToArray();
            var tmpLon = new double[ny];
            dem.Projection.ToLonLat(tmpX, gys, tmpLon, lats);
        }
        Parallel.For(0, ny, r =>
        {
            double ri = (gys[r] - gt[3]) / gt[5] - r0 - 0.5;
            for (int c = 0; c < nx; c++)
            {
                double ci = (gxs[c] - gt[0]) / gt[1] - c0 - 0.5;
                hgt[r * nx + c] = Interp.Cubic(z, ww, wh, ri, ci);
            }
        });

        // true positions in metres (local east / north plane at the box centre)
        var E = new float[nx * ny];
        var N = new float[nx * ny];
        double lat0 = box.CenterLat, lon0 = box.CenterLon;
        Parallel.For(0, ny, r =>
        {
            for (int c = 0; c < nx; c++)
            {
                var (e, n) = EastNorth(lons[c], lats[r], lon0, lat0);
                E[r * nx + c] = (float)e;
                N[r * nx + c] = (float)n;
            }
        });
        double level = DetailLevel(detail);
        if (level > 0)
        {
            var add = SurfaceDetailField(E, N, nx, ny, spacing, level, new Random(box.Seed));
            for (int i = 0; i < hgt.Length; i++) hgt[i] += add[i];
        }
        float zbase = hgt.Min();
        var pos = new float[nx * ny * 3];
        var uv = new float[nx * ny * 2];
        for (int r = 0, i = 0; r < ny; r++)
            for (int c = 0; c < nx; c++, i++)
            {
                pos[3 * i] = E[i];
                pos[3 * i + 1] = hgt[i] - zbase;
                pos[3 * i + 2] = N[i];
                uv[2 * i] = (float)c / (nx - 1);
                uv[2 * i + 1] = 1f - (float)r / (ny - 1);
            }
        var idx = new int[(nx - 1) * (ny - 1) * 6];
        int q = 0;
        // triangles counter-clockwise seen from above: (A, C, D) and (A, D, B)
        int cells = (nx - 1) * (ny - 1);
        for (int r = 0; r < ny - 1; r++)
            for (int c = 0; c < nx - 1; c++, q++)
            {
                int a = r * nx + c, b = a + 1, cc = a + nx, d = cc + 1;
                idx[3 * q] = a; idx[3 * q + 1] = cc; idx[3 * q + 2] = d;
                idx[3 * (cells + q)] = a; idx[3 * (cells + q) + 1] = d; idx[3 * (cells + q) + 2] = b;
            }
        return new TerrainMesh
        {
            Box = box, Nx = nx, Ny = ny, Positions = pos, Normals = Normals(pos, nx, ny), Uvs = uv, Indices = idx,
            WidthM = E.Max() - E.Min(), DepthM = N.Max() - N.Min(), ElevationBaseM = zbase,
        };
    }

    static void FillGaps(float[] z, int w, int h, double? noData, float scale, float offset)
    {
        var bad = new bool[z.Length];
        int nBad = 0;
        for (int i = 0; i < z.Length; i++)
        {
            float v = z[i];
            if (!float.IsFinite(v) || v == -32768f || (noData is not null && v == noData.Value)) { bad[i] = true; nBad++; }
            else z[i] = v * scale + offset;
        }
        if (nBad == z.Length) throw new ArgumentException("There is no height data here.");
        if (nBad == 0) return;
        // rare gaps: use the nearest real height (grow the known area step by step)
        var queue = new Queue<int>();
        for (int i = 0; i < z.Length; i++) if (!bad[i]) queue.Enqueue(i);
        while (queue.Count > 0)
        {
            int p = queue.Dequeue(), r = p / w, c = p % w;
            foreach (var (dr, dc) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                int rr = r + dr, cc = c + dc;
                if (rr < 0 || cc < 0 || rr >= h || cc >= w) continue;
                int k = rr * w + cc;
                if (!bad[k]) continue;
                bad[k] = false;
                z[k] = z[p];
                queue.Enqueue(k);
            }
        }
    }

    /// <summary>Tangent-plane east / north metres relative to (lon0, lat0) on the Mars sphere.</summary>
    public static (double E, double N) EastNorth(double lon, double lat, double lon0, double lat0)
    {
        double R = Mars.Radius;
        double lam = Mars.Rad(lon), phi = Mars.Rad(lat), l0 = Mars.Rad(lon0), p0 = Mars.Rad(lat0);
        double x = R * Math.Cos(phi) * Math.Cos(lam), y = R * Math.Cos(phi) * Math.Sin(lam), zz = R * Math.Sin(phi);
        double x0 = R * Math.Cos(p0) * Math.Cos(l0), y0 = R * Math.Cos(p0) * Math.Sin(l0), z0 = R * Math.Sin(p0);
        double dx = x - x0, dy = y - y0, dz = zz - z0;
        double east = -Math.Sin(l0) * dx + Math.Cos(l0) * dy;
        double north = -Math.Sin(p0) * Math.Cos(l0) * dx - Math.Sin(p0) * Math.Sin(l0) * dy + Math.Cos(p0) * dz;
        return (east, north);
    }

    /// <summary>Generated (not measured) roughness the 200 m data cannot contain: small craters (Mars-like
    /// sizes, never overlapping, biggest first) and fine fractal bumps (wavelengths up to 200 m only).</summary>
    public static float[] SurfaceDetailField(float[] E, float[] N, int nx, int ny, double spacing, double level, Random rng)
    {
        var outp = Noise.Fractal(nx, ny, spacing, rng, 4 * spacing, 200.0);
        for (int i = 0; i < outp.Length; i++) outp[i] *= (float)level;
        float eMin = E.Min(), eMax = E.Max(), nMin = N.Min(), nMax = N.Max();
        double areaKm2 = (eMax - eMin) * (nMax - nMin) / 1e6;
        double dMin = Math.Max(4 * spacing, 10.0), dMax = 400.0;
        if (dMin >= dMax) return outp;
        double k = 4.0 * level;                                   // craters wider than 100 m per km^2
        int count = (int)Math.Min(20000, Poisson(rng, k * areaKm2 * (Math.Pow(100.0 / dMin, 2) - Math.Pow(100.0 / dMax, 2))));
        var D = new double[count];
        for (int i = 0; i < count; i++)
        {
            double u = rng.NextDouble();
            D[i] = 1.0 / Math.Sqrt(u / (dMin * dMin) + (1 - u) / (dMax * dMax));
        }
        Array.Sort(D);
        Array.Reverse(D);                                         // biggest first
        double e0 = E[0], n0 = N[0];
        double de = (E[nx - 1] - e0) / Math.Max(1, nx - 1), dn = (N[(ny - 1) * nx] - n0) / Math.Max(1, ny - 1);
        double cell = dMax * 0.8;
        var placed = new Dictionary<(int, int), List<(double x, double y, double r)>>();
        foreach (double d in D)
        {
            double rad = d / 2, x = 0, y = 0;
            bool ok = false;
            for (int t = 0; t < 6 && !ok; t++)
            {
                x = eMin + rng.NextDouble() * (eMax - eMin);
                y = nMin + rng.NextDouble() * (nMax - nMin);
                int gx = (int)Math.Floor(x / cell), gy = (int)Math.Floor(y / cell);
                ok = true;
                for (int ix = gx - 1; ix <= gx + 1 && ok; ix++)
                    for (int iy = gy - 1; iy <= gy + 1 && ok; iy++)
                        if (placed.TryGetValue((ix, iy), out var list))
                            foreach (var p in list)
                                if (Math.Sqrt((x - p.x) * (x - p.x) + (y - p.y) * (y - p.y)) < 1.25 * (rad + p.r)) { ok = false; break; }
                if (ok)
                {
                    if (!placed.TryGetValue((gx, gy), out var l)) placed[(gx, gy)] = l = new();
                    l.Add((x, y, rad));
                }
            }
            if (!ok) continue;
            double f = 0.25 + 0.75 * rng.NextDouble();               // worn craters are shallower
            int j0 = (int)((x - 1.6 * rad - e0) / de), j1 = (int)((x + 1.6 * rad - e0) / de) + 2;
            int ia = (int)((y - 1.6 * rad - n0) / dn), ib = (int)((y + 1.6 * rad - n0) / dn);
            int i0 = Math.Min(ia, ib), i1 = Math.Max(ia, ib) + 2;
            j0 = Math.Max(0, j0); j1 = Math.Min(nx, j1); i0 = Math.Max(0, i0); i1 = Math.Min(ny, i1);
            double depth = 0.14 * d * f, rim = 0.035 * d * f;        // walls up to ~30 degrees, like loose soil
            for (int i = i0; i < i1; i++)
                for (int j = j0; j < j1; j++)
                {
                    int v = i * nx + j;
                    double qd = Math.Sqrt((E[v] - x) * (E[v] - x) + (N[v] - y) * (N[v] - y)) / rad;
                    if (qd >= 1.6) continue;
                    double bowl = qd < 1 ? -depth * (1 - qd * qd) + rim : rim * Math.Exp(-Math.Pow((qd - 1) / 0.25, 2));
                    outp[v] += (float)bowl;
                }
        }
        return outp;
    }

    /// <summary>Area-weighted vertex normals of the grid triangles (x east, y up, z north).</summary>
    static float[] Normals(float[] pos, int nx, int ny)
    {
        var nrm = new double[pos.Length];
        void Add(int a, int b, int c)
        {
            // positions as right-handed (east, north, up)
            double ax = pos[3 * a], ay = pos[3 * a + 2], az = pos[3 * a + 1];
            double ux = pos[3 * b] - ax, uy = pos[3 * b + 2] - ay, uz = pos[3 * b + 1] - az;
            double wx = pos[3 * c] - ax, wy = pos[3 * c + 2] - ay, wz = pos[3 * c + 1] - az;
            double ne = uy * wz - uz * wy, nn = uz * wx - ux * wz, nu = ux * wy - uy * wx;
            foreach (int v in new[] { a, b, c }) { nrm[3 * v] += ne; nrm[3 * v + 1] += nu; nrm[3 * v + 2] += nn; }
        }
        for (int r = 0; r < ny - 1; r++)
            for (int c = 0; c < nx - 1; c++)
            {
                int a = r * nx + c, b = a + 1, cc = a + nx, d = cc + 1;
                Add(a, cc, d);
                Add(a, d, b);
            }
        var outp = new float[pos.Length];
        for (int v = 0; v < pos.Length / 3; v++)
        {
            double l = Math.Sqrt(nrm[3 * v] * nrm[3 * v] + nrm[3 * v + 1] * nrm[3 * v + 1] + nrm[3 * v + 2] * nrm[3 * v + 2]);
            if (l < 1e-12) { outp[3 * v + 1] = 1; continue; }
            for (int k = 0; k < 3; k++) outp[3 * v + k] = (float)(nrm[3 * v + k] / l);
        }
        return outp;
    }

    public static int Poisson(Random rng, double lambda)
    {
        if (lambda <= 0) return 0;
        if (lambda > 30)
            return Math.Max(0, (int)Math.Round(lambda + Math.Sqrt(lambda) * Noise.Gauss(rng)));
        double L = Math.Exp(-lambda), p = 1;
        int k = 0;
        do { k++; p *= rng.NextDouble(); } while (p > L);
        return k - 1;
    }
}
