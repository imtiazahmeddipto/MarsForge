"""
Step 2: turn the downloaded areas into training lessons.

For every area in data/training/:
  * finds the real rocks in hirise.tif with RockBoxNet  -> rocks.csv (position in metres, diameter)
  * counts them in 50 m x 50 m squares (the "answer" the new model must learn)
  * resamples the Mars Trek inputs (CTX, THEMIS night, elevation) to one 10 m grid around the area
  -> data/lessons/<area>.npz

    python build_dataset.py            (re-run safely: finished areas are skipped)
"""
import argparse
import csv
import glob
import json
import os
import sys
import time
import warnings

import numpy as np
import rasterio
from pyproj import CRS, Transformer

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
warnings.filterwarnings("ignore", category=rasterio.errors.NotGeoreferencedWarning)
from marsrock.features import Raster, en_to_lonlat, local_en, window_inputs  # noqa: E402

CELL_M = 50.0                    # size of one counting square
LABEL_HALF_M = 500.0             # labels cover the central 1000 m x 1000 m (20 x 20 squares)
BINS = (1.0, 2.0, 4.0)           # rock diameter classes: 1-2 m, 2-4 m, 4 m and larger


def detect_rocks(folder, meta, detector):
    """Real rocks in the HiRISE image -> list of (east_m, north_m, diameter_m, major_m, minor_m, score)."""
    path = os.path.join(folder, "rocks.csv")
    if os.path.exists(path):
        return np.loadtxt(path, delimiter=",", skiprows=1, ndmin=2), _valid_cells(folder, meta)
    with rasterio.open(os.path.join(folder, "hirise.tif")) as d:
        img, T = d.read(1), d.transform
        crs = CRS.from_wkt(d.crs.to_wkt())
    rocks = detector.detect(img, meta["hirise_pixel_m"])
    out = np.zeros((len(rocks), 6))
    if rocks:
        xs = np.array([r["x"] for r in rocks]) + 0.5           # detector: integer = pixel centre
        ys = np.array([r["y"] for r in rocks]) + 0.5
        mx, my = T * (xs, ys)
        lon, lat = Transformer.from_crs(crs, crs.geodetic_crs, always_xy=True).transform(mx, my)
        e, n = local_en(lon, lat, meta["center_lon"], meta["center_lat"])
        out = np.column_stack([e, n, [r["diameter_m"] for r in rocks], [r["major_m"] for r in rocks],
                               [r["minor_m"] for r in rocks], [r["score"] for r in rocks]])
    with open(path, "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["east_m", "north_m", "diameter_m", "major_m", "minor_m", "score"])
        w.writerows(np.round(out, 3).tolist())
    return out, _valid_cells(folder, meta)


def _valid_cells(folder, meta):
    """Share of each 50 m square that the HiRISE image really covers (5 m sample points)."""
    k = int(2 * LABEL_HALF_M / CELL_M)
    step = 5.0
    c = -LABEL_HALF_M + step / 2 + step * np.arange(int(2 * LABEL_HALF_M / step))
    E, N = np.meshgrid(c, -c)
    lon, lat = en_to_lonlat(E, N, meta["center_lon"], meta["center_lat"])
    with rasterio.open(os.path.join(folder, "hirise.tif")) as d:
        v = Raster(d.read(1), d.transform, d.crs, nodata=None).sample(lon, lat, order=0)   # 0 = no image
    ok = np.isfinite(v) & (v > 0)
    f = int(CELL_M / step)
    return ok.reshape(k, f, k, f).mean(axis=(1, 3))


def labels(rocks, valid):
    """Rock counts per 50 m square and diameter class. Rows: north to south; columns: west to east."""
    k = valid.shape[0]
    counts = np.zeros((len(BINS), k, k), np.float32)
    area = np.zeros((k, k), np.float32)
    if len(rocks):
        col = np.floor((rocks[:, 0] + LABEL_HALF_M) / CELL_M).astype(int)
        row = np.floor((LABEL_HALF_M - rocks[:, 1]) / CELL_M).astype(int)
        inside = (col >= 0) & (col < k) & (row >= 0) & (row < k)
        d = rocks[:, 2]
        for b, lo in enumerate(BINS):
            hi = BINS[b + 1] if b + 1 < len(BINS) else np.inf
            m = inside & (d >= lo) & (d < hi)
            np.add.at(counts[b], (row[m], col[m]), 1)
        m = inside & (d >= BINS[0])
        np.add.at(area, (row[m], col[m]), np.pi * d[m] ** 2 / 4)
    return counts, area / CELL_M ** 2


def inputs(folder, meta):
    """Mars Trek inputs on a 200 x 200 grid (10 m), rows north to south (same code as the tool uses)."""
    layers = [Raster.open(os.path.join(folder, f)) if os.path.exists(os.path.join(folder, f)) else None
              for f in ("ctx.tif", "themis.tif", "dem.tif")]
    return window_inputs(*layers, meta["center_lon"], meta["center_lat"])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default=os.path.join(HERE, "data"))
    a = ap.parse_args()
    from marsrock.detect import RockDetector
    detector = RockDetector()
    print(f"Rock detector running on: {detector.device}", flush=True)
    out_dir = os.path.join(a.data, "lessons")
    os.makedirs(out_dir, exist_ok=True)
    areas = sorted(d for d in glob.glob(os.path.join(a.data, "training", "*")) if os.path.exists(os.path.join(d, "meta.json")))
    print(f"{len(areas)} downloaded areas", flush=True)
    for i, folder in enumerate(areas):
        name = os.path.basename(folder)
        target = os.path.join(out_dir, name + ".npz")
        if os.path.exists(target):
            continue
        t = time.time()
        try:
            meta = json.load(open(os.path.join(folder, "meta.json")))
            rocks, valid = detect_rocks(folder, meta, detector)
            counts, cover = labels(rocks, valid)
            x, have = inputs(folder, meta)
            props = meta.get("site_properties") or {}
            np.savez_compressed(target, x=x, counts=counts, cover=cover, valid=valid.astype(np.float32),
                                lat=meta["center_lat"], lon=meta["center_lon"], site=meta["site"],
                                pixel_m=meta["hirise_pixel_m"], has_ctx=have["ctx"], has_themis=have["themis"],
                                incidence=float(props.get("incidence_angle", props.get("view:sun_elevation", np.nan)) or np.nan))
            ok = valid > 0.95
            dens = counts.sum(0)[ok].sum() / max(ok.sum() * CELL_M ** 2 / 1e6, 1e-9)
            print(f"[{i + 1}/{len(areas)}] {name}: {len(rocks)} rocks, {dens:,.0f} rocks >=1 m per km2, "
                  f"inputs ctx={have['ctx']} themis={have['themis']} ({time.time() - t:.0f} s)", flush=True)
        except Exception as e:
            print(f"[{i + 1}/{len(areas)}] {name}: FAILED {e}", flush=True)
    print("Done.", flush=True)


if __name__ == "__main__":
    main()
