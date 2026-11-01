"""
Step 1 of the rock-prediction model: download training data.  Run on Windows (it needs NASA's servers):

    cd "C:\\Users\\imtia\\OneDrive\\Desktop\\Mars Surface\\mars-dem-tool\\ml"
    python collect_training_data.py                 # 150 HiRISE sites x 2 areas of 1 km (about 4-6 GB)
    python collect_training_data.py --sites 20      # quick first test

Safe to stop and re-run: finished areas are skipped.

For each chosen HiRISE site it saves, for one or more 1 km x 1 km areas (in data/training/<area>/):
  hirise.tif   the HiRISE image (about 25 cm per pixel)  -> the rock detector finds the real rocks here
  ctx.tif      Mars Trek CTX image of the same place (about 10 m per pixel, 2 km around the area)
  themis.tif   Mars Trek THEMIS night-time infrared (about 100 m per pixel; rocky ground stays warm at night)
  dem.tif      USGS MOLA-HRSC elevation (200 m per pixel)
  meta.json    where it is, and details of the HiRISE image
"""
import argparse
import json
import math
import os
import random
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor

try:
    import certifi
    os.environ.setdefault("CURL_CA_BUNDLE", certifi.where())
    os.environ.setdefault("GDAL_CURL_CA_BUNDLE", certifi.where())
    os.environ.setdefault("SSL_CERT_FILE", certifi.where())
except ImportError:
    pass
for k, v in {"GDAL_DISABLE_READDIR_ON_OPEN": "EMPTY_DIR", "CPL_VSIL_CURL_ALLOWED_EXTENSIONS": ".tif,.TIF,.tiff",
             "GDAL_HTTP_MAX_RETRY": "4", "GDAL_HTTP_RETRY_DELAY": "2", "GDAL_HTTP_CONNECTTIMEOUT": "30",
             "GDAL_HTTP_LOW_SPEED_TIME": "60", "GDAL_HTTP_LOW_SPEED_LIMIT": "50", "VSI_CACHE": "TRUE"}.items():
    os.environ.setdefault(k, v)

import warnings

import numpy as np
import rasterio
from affine import Affine
from pyproj import CRS, Geod, Transformer
from rasterio.io import MemoryFile
from rasterio.windows import Window

warnings.filterwarnings("ignore", category=rasterio.errors.NotGeoreferencedWarning)

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "data", "training")
MARS_R = 3396190.0
GEOD = Geod(a=MARS_R, b=MARS_R)
LONLAT = CRS.from_proj4(f"+proj=longlat +R={MARS_R:.0f} +no_defs")

STAC = os.environ.get("MTT_STAC", "https://stac.astrogeology.usgs.gov/api").rstrip("/")
HIRISE_COLLECTION = "mro_hirise_socet_dtms"
TREK = "https://trek.nasa.gov/tiles/Mars/EQ/{id}/1.0.0//default/default028mm/{z}/{y}/{x}.{ext}"
CTX_URL = os.environ.get("MTT_CTX_TILES", "https://astro.arcgis.com/arcgis/rest/services/OnMars/CTX/MapServer/tile/{z}/{y}/{x}")
THEMIS_URL = os.environ.get("MTT_THEMIS_TILES", TREK.replace("{id}", "Mars_MO_THEMIS-IR-Night_mosaic_60N60S_100m_v14_clon0_ly")
                            .replace("{ext}", "png"))
DEM_URL = os.environ.get("MARS_DEM_URL", "https://planetarymaps.usgs.gov/mosaic/Mars/HRSC_MOLA_Blend/"
                         "Mars_HRSC_MOLA_BlendDEM_Global_200mp_v2.tif")

AREA_M = 1024.0          # side of one training area
CONTEXT_M = 512.0        # extra Trek / elevation data around it, so the model sees the surroundings
MAX_LAT = 55.0           # THEMIS night mosaic covers 60S-60N


# ------------------------------------------------------------------ helpers
def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


def http(url, timeout=60, tries=4):
    last = None
    for i in range(tries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "MarsSurfaceML/1.0"})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            if e.code in (400, 404):
                return None
            last = e
        except Exception as e:
            last = e
        time.sleep(2 + 3 * i)
    raise IOError(f"{url}: {last}")


def vsi(href):
    if href.startswith("s3://"):
        b, _, k = href[5:].partition("/")
        return f"/vsicurl/https://{b}.s3.amazonaws.com/{k}"
    return "/vsicurl/" + href if href.startswith("http") else href


def outer_ring(geom):
    t, c = geom.get("type"), geom.get("coordinates") or []
    polys = [c] if t == "Polygon" else c if t == "MultiPolygon" else []
    best = max((p[0] for p in polys if p and len(p[0]) >= 3), key=len, default=None)
    if best is None:
        return None
    ring = np.array(best, float)[:, :2]
    for i in range(1, len(ring)):                         # continuous across 180 degrees
        while ring[i, 0] - ring[0, 0] > 180:
            ring[i, 0] -= 360
        while ring[i, 0] - ring[0, 0] < -180:
            ring[i, 0] += 360
    return ring


def inside(x, y, ring):
    x1, y1 = ring[:, 0], ring[:, 1]
    x2, y2 = np.roll(x1, -1), np.roll(y1, -1)
    with np.errstate(divide="ignore", invalid="ignore"):
        return bool((((y1 > y) != (y2 > y)) & (x < (x2 - x1) * (y - y1) / (y2 - y1) + x1)).sum() % 2)


def box_lonlat(lon, lat, half_m):
    dlat = math.degrees(half_m / MARS_R)
    dlon = math.degrees(half_m / (MARS_R * math.cos(math.radians(lat))))
    return lon - dlon, lat - dlat, lon + dlon, lat + dlat


# ------------------------------------------------------------------ 1. find HiRISE sites
def discover(cache):
    if os.path.exists(cache):
        return json.load(open(cache))
    log("Asking the USGS catalogue for all HiRISE terrain-model sites ...")
    url = f"{STAC}/search?" + urllib.parse.urlencode({"collections": HIRISE_COLLECTION, "limit": 500})
    sites, pages = [], 0
    while url and pages < 50:
        page = json.loads(http(url, timeout=120))
        pages += 1
        for f in page.get("features", []):
            ring = outer_ring(f.get("geometry") or {})
            imgs = {k: a.get("href") for k, a in (f.get("assets") or {}).items()
                    if str(a.get("href", "")).lower().endswith((".tif", ".tiff"))}
            if ring is None or not imgs:
                continue
            sites.append({"id": f["id"], "ring": ring.tolist(), "assets": imgs, "properties": f.get("properties") or {}})
        nxt = [l["href"] for l in page.get("links", []) if l.get("rel") == "next" and l.get("href")]
        url = nxt[0] if nxt else None
        log(f"  {len(sites)} sites so far")
    if not sites:
        raise SystemExit("The catalogue returned no HiRISE sites. Send this output to Claude.")
    os.makedirs(os.path.dirname(cache), exist_ok=True)
    json.dump(sites, open(cache, "w"))
    log(f"Found {len(sites)} HiRISE sites. Example image files of the first one: {list(sites[0]['assets'])[:8]}")
    return sites


def pick_image(site):
    """The HiRISE orthoimage with the finest pixels (prefers RED orthoimages, never the elevation file)."""
    cands = [(k, h) for k, h in site["assets"].items() if "ORTHO" in k.upper() or "ortho" in h.lower()]
    red = [(k, h) for k, h in cands if "RED" in (k + h).upper()]
    best = None
    for k, h in (red or cands):
        try:
            with rasterio.open(vsi(h)) as d:
                px = _ground_px(d)
                if d.count >= 1 and (best is None or px < best[2]):
                    best = (k, h, px)
        except Exception as e:
            log(f"    cannot open {k}: {str(e)[:120]}")
    return best


def _ground_px(ds):
    """Ground size of one pixel (m) at the image centre."""
    crs = CRS.from_wkt(ds.crs.to_wkt())
    tr = Transformer.from_crs(crs, crs.geodetic_crs, always_xy=True)
    c, r = ds.width / 2, ds.height / 2
    xs, ys = ds.transform * (np.array([c, c + 1, c]), np.array([r, r, r + 1]))
    lon, lat = tr.transform(xs, ys)
    e = GEOD.inv(lon[0], lat[0], lon[1], lat[1])[2]
    n = GEOD.inv(lon[0], lat[0], lon[2], lat[2])[2]
    return math.sqrt(e * n)


# ------------------------------------------------------------------ 2. one training area
def read_hirise(href, lon, lat):
    """1024 m square of the HiRISE image around lon/lat, in the image's own pixels."""
    with rasterio.open(vsi(href)) as d:
        crs = CRS.from_wkt(d.crs.to_wkt())
        px = _ground_px(d)
        n = int(round(AREA_M / px))
        x, y = Transformer.from_crs(crs.geodetic_crs, crs, always_xy=True).transform(lon, lat)
        col, row = ~d.transform * (x, y)
        c0, r0 = int(round(col - n / 2)), int(round(row - n / 2))
        if c0 < 0 or r0 < 0 or c0 + n > d.width or r0 + n > d.height:
            return None
        win = Window(c0, r0, n, n)
        a = d.read(1, window=win, masked=True)
        data = np.where(np.ma.getmaskarray(a), 0, a.data)
        if d.nodata is not None:
            data = np.where(a.data == d.nodata, 0, data)
        return {"data": data, "transform": d.window_transform(win), "crs": d.crs, "pixel_m": px,
                "dtype": str(data.dtype), "tags": d.tags()}


def decode_tile(raw, tile_px):
    """Map tile (jpg / png, grey, grey+alpha, colour or palette) -> grey uint8 tile_px x tile_px."""
    with MemoryFile(raw) as mf, mf.open() as ds:
        a = ds.read(out_shape=(ds.count, tile_px, tile_px))
        if ds.count == 1 and ds.colorinterp[0].name == "palette":
            cmap = ds.colormap(1)
            lut = np.array([np.mean(cmap.get(i, (0, 0, 0, 0))[:3]) for i in range(256)], np.float32)
            return lut[a[0]].astype(np.uint8)
    if a.shape[0] >= 3:
        return a[:3].astype(np.float32).mean(0).astype(np.uint8)
    return a[0].astype(np.uint8)


def stitch_tiles(url_tpl, tile_px, z, w, s, e, n):
    """Mosaic of lon/lat map tiles (Trek grid: 2^(z+1) x 2^z tiles) covering the box -> (array, transform)."""
    tdeg = 180.0 / 2 ** z
    dpp = tdeg / tile_px
    x0, x1 = math.floor((w + 180) / tdeg), math.floor((e + 180) / tdeg)
    y0, y1 = math.floor((90 - n) / tdeg), math.floor((90 - s) / tdeg)
    jobs = [(x, y) for y in range(y0, y1 + 1) for x in range(x0, x1 + 1)]
    canvas = np.zeros(((y1 - y0 + 1) * tile_px, (x1 - x0 + 1) * tile_px), np.uint8)
    got = [0]

    def one(xy):
        x, y = xy
        raw = http(url_tpl.format(z=z, y=y, x=x))
        if not raw:
            return
        a = decode_tile(raw, tile_px)
        canvas[(y - y0) * tile_px:(y - y0 + 1) * tile_px, (x - x0) * tile_px:(x - x0 + 1) * tile_px] = a
        got[0] += 1

    with ThreadPoolExecutor(max_workers=6) as ex:
        list(ex.map(one, jobs))
    if got[0] == 0:
        return None
    T = Affine(dpp, 0, -180 + x0 * tdeg, 0, -dpp, 90 - y0 * tdeg)
    c0, r0 = int((w - T.c) / dpp), int((T.f - n) / dpp)
    c1, r1 = int(math.ceil((e - T.c) / dpp)), int(math.ceil((T.f - s) / dpp))
    return canvas[r0:r1, c0:c1], T * Affine.translation(c0, r0)


def read_dem(w, s, e, n):
    with rasterio.open(vsi(DEM_URL)) as d:
        crs = CRS.from_wkt(d.crs.to_wkt())
        tr = Transformer.from_crs(crs.geodetic_crs, crs, always_xy=True)
        xs, ys = tr.transform([w, e], [n, s])
        c0, r0 = ~d.transform * (xs[0], ys[0])
        c1, r1 = ~d.transform * (xs[1], ys[1])
        win = Window(int(c0) - 2, int(r0) - 2, int(math.ceil(c1 - c0)) + 5, int(math.ceil(r1 - r0)) + 5)
        a = d.read(1, window=win).astype(np.float32) * float((d.scales or (1,))[0] or 1) + float((d.offsets or (0,))[0] or 0)
        return a, d.window_transform(win), d.crs


def save(path, arr, transform, crs, nodata=None):
    with rasterio.open(path, "w", driver="GTiff", width=arr.shape[1], height=arr.shape[0], count=1, dtype=arr.dtype,
                       crs=crs, transform=transform, compress="deflate", tiled=arr.shape[0] >= 256 and arr.shape[1] >= 256,
                       nodata=nodata) as f:
        f.write(arr, 1)


def zoom_for(url, tile_px, want_m, z_max, lon, lat):
    """Finest zoom level (<= z_max) that has a tile at this place."""
    for z in range(z_max, -1, -1):
        if 180.0 / 2 ** z / tile_px * MARS_R * math.pi / 180 > 4 * want_m:
            break
        tdeg = 180.0 / 2 ** z
        if http(url.format(z=z, x=int((lon + 180) // tdeg), y=int((90 - lat) // tdeg))):
            return z
    return None


def collect_area(site, image, k, rng, zooms):
    name = f"{site['id']}_{k}"
    folder = os.path.join(OUT, name)
    if os.path.exists(os.path.join(folder, "meta.json")):
        return "skip"
    ring = np.array(site["ring"])
    lo0, la0, lo1, la1 = ring[:, 0].min(), ring[:, 1].min(), ring[:, 0].max(), ring[:, 1].max()
    for attempt in range(12):                                   # a 1 km square fully inside the site
        lon, lat = rng.uniform(lo0, lo1), rng.uniform(la0, la1)
        w, s, e, n = box_lonlat(lon, lat, AREA_M / 2 + 20)
        if not all(inside(x, y, ring) for x, y in ((w, s), (e, s), (e, n), (w, n), (lon, lat))):
            continue
        h = read_hirise(image[1], ((lon + 180) % 360) - 180, lat)
        if h is None or (h["data"] > 0).mean() < 0.9:
            continue
        break
    else:
        return "no-area"
    os.makedirs(folder, exist_ok=True)
    save(os.path.join(folder, "hirise.tif"), h["data"], h["transform"], h["crs"], nodata=0)
    lon = ((lon + 180) % 360) - 180
    w, s, e, n = box_lonlat(lon, lat, AREA_M / 2 + CONTEXT_M)
    got = {}
    for key, url, tpx in (("ctx", CTX_URL, 512), ("themis", THEMIS_URL, 256)):
        z = zooms.get(key)
        res = stitch_tiles(url, tpx, z, w, s, e, n) if z is not None else None
        if res is not None:
            save(os.path.join(folder, f"{key}.tif"), res[0], res[1], LONLAT)
            got[key] = z
    try:
        a, T, crs = read_dem(w, s, e, n)
        save(os.path.join(folder, "dem.tif"), a, T, crs)
        got["dem"] = True
    except Exception as ex:
        log(f"    elevation failed: {str(ex)[:150]}")
    meta = {"area": name, "site": site["id"], "center_lon": lon, "center_lat": lat, "area_m": AREA_M,
            "context_m": CONTEXT_M, "hirise_asset": image[0], "hirise_href": image[1], "hirise_pixel_m": h["pixel_m"],
            "hirise_dtype": h["dtype"], "site_properties": site["properties"], "inputs": got,
            "created": time.strftime("%Y-%m-%d %H:%M:%S")}
    json.dump(meta, open(os.path.join(folder, "meta.json"), "w"), indent=1, default=str)
    return "ok"


def add_themis():
    """Add themis.tif to downloaded areas that do not have it yet."""
    import glob
    folders = sorted(os.path.dirname(f) for f in glob.glob(os.path.join(OUT, "*", "meta.json")))
    todo = [f for f in folders if not os.path.exists(os.path.join(f, "themis.tif"))]
    log(f"{len(todo)} of {len(folders)} areas need THEMIS night infrared")
    z = None
    done = 0
    for i, f in enumerate(todo):
        meta = json.load(open(os.path.join(f, "meta.json")))
        lon, lat = meta["center_lon"], meta["center_lat"]
        if z is None:
            z = zoom_for(THEMIS_URL, 256, 100, 8, lon, lat)
            log(f"THEMIS zoom level: {z}")
            if z is None:
                raise SystemExit("THEMIS tiles could not be downloaded. Send this output to Claude.")
        w, s, e, n = box_lonlat(lon, lat, meta.get("area_m", AREA_M) / 2 + meta.get("context_m", CONTEXT_M))
        try:
            res = stitch_tiles(THEMIS_URL, 256, z, w, s, e, n)
        except Exception as ex:
            log(f"[{i + 1}/{len(todo)}] {os.path.basename(f)}: FAILED {str(ex)[:150]}")
            continue
        if res is None:
            log(f"[{i + 1}/{len(todo)}] {os.path.basename(f)}: no THEMIS here")
            continue
        save(os.path.join(f, "themis.tif"), res[0], res[1], LONLAT)
        meta.setdefault("inputs", {})["themis"] = z
        json.dump(meta, open(os.path.join(f, "meta.json"), "w"), indent=1, default=str)
        done += 1
        if done % 25 == 0:
            log(f"  {done} done")
    log(f"Finished: THEMIS added to {done} areas.")


# ------------------------------------------------------------------ main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sites", type=int, default=150, help="number of HiRISE sites (default 150)")
    ap.add_argument("--areas", type=int, default=2, help="1 km areas per site (default 2)")
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--add-themis", action="store_true", help="only add THEMIS night infrared to areas already downloaded")
    a = ap.parse_args()
    if a.add_themis:
        return add_themis()
    os.makedirs(OUT, exist_ok=True)
    sites = discover(os.path.join(HERE, "data", "hirise_sites.json"))

    def centre(s):
        r = np.array(s["ring"])
        return r[:, 0].mean(), r[:, 1].mean()
    usable = [s for s in sites if abs(centre(s)[1]) <= MAX_LAT]
    rng = random.Random(a.seed)
    rng.shuffle(usable)
    log(f"{len(usable)} sites between {MAX_LAT}S and {MAX_LAT}N; using up to {a.sites}")

    lon, lat = centre(usable[0])
    lon = ((lon + 180) % 360) - 180
    zooms = {"ctx": zoom_for(CTX_URL, 512, 10, 12, lon, lat), "themis": zoom_for(THEMIS_URL, 256, 100, 8, lon, lat)}
    log(f"Mars Trek zoom levels: CTX {zooms['ctx']}, THEMIS night {zooms['themis']}")
    if zooms["ctx"] is None:
        log("WARNING: CTX tiles could not be downloaded. Send this output to Claude.")

    done = fails = 0
    nrng = np.random.default_rng(a.seed)
    for i, site in enumerate(usable[:a.sites]):
        t = time.time()
        try:
            image = pick_image(site)
            if image is None or image[2] > 0.6:
                log(f"[{i + 1}/{a.sites}] {site['id']}: no usable HiRISE image" + (f" ({image[2]:.2f} m/px)" if image else ""))
                continue
            res = [collect_area(site, image, k, nrng, zooms) for k in range(a.areas)]
            done += res.count("ok")
            log(f"[{i + 1}/{a.sites}] {site['id']}: {', '.join(res)}  ({image[2]:.2f} m/px, {time.time() - t:.0f} s)")
        except KeyboardInterrupt:
            raise
        except Exception as ex:
            fails += 1
            log(f"[{i + 1}/{a.sites}] {site['id']}: FAILED {str(ex)[:200]}")
            if fails <= 3:
                traceback.print_exc()
    log(f"Finished: {done} new areas. Data is in {OUT}")


if __name__ == "__main__":
    main()
