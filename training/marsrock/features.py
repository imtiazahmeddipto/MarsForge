"""
The Mars Trek inputs the rock model sees, built the SAME way for training (build_dataset.py) and for
prediction in the tool (surface.py): a 2 km x 2 km window, 10 m grid (200 x 200), rows north to south.

  0 ctx         CTX image brightness (0..1)
  1 ctx_local   CTX brightness minus its 100 m surroundings, scaled (texture: boulders, ripples, crater rims)
  2 themis      THEMIS night-time infrared (0..1; rocky / hard ground stays warmer at night)
  3 elev_local  elevation minus the window mean, / 100 m
  4 slope       slope in degrees / 10
"""
import math

import numpy as np
import rasterio
from pyproj import CRS, Transformer
from scipy import ndimage

MARS_R = 3396190.0
GRID_M = 10.0
INPUT_HALF_M = 1000.0
INPUT_NAMES = ["ctx", "ctx_local", "themis", "elev_local", "slope_deg"]


def local_en(lon, lat, lon0, lat0):
    """Ground east / north metres from (lon0, lat0) (sphere; exact to mm over a few km)."""
    dlon = (np.asarray(lon) - lon0 + 180.0) % 360.0 - 180.0
    return (MARS_R * math.cos(math.radians(lat0)) * np.radians(dlon),
            MARS_R * np.radians(np.asarray(lat) - lat0))


def en_to_lonlat(e, n, lon0, lat0):
    return (lon0 + np.degrees(np.asarray(e) / (MARS_R * math.cos(math.radians(lat0)))),
            lat0 + np.degrees(np.asarray(n) / MARS_R))


class Raster:
    """A map layer in memory (array + georeference). sample(lon, lat) -> values, NaN outside / no data."""

    def __init__(self, array, transform, crs, nodata=None):
        a = np.asarray(array).astype(np.float32)
        crs = CRS.from_user_input(crs.to_wkt() if hasattr(crs, "to_wkt") else crs)
        bad = np.zeros(a.shape, bool) if nodata is None or (isinstance(nodata, float) and math.isnan(nodata)) else a == nodata
        bad |= ~np.isfinite(a)
        if bad.all():
            a = np.full(a.shape, np.nan, np.float32)
        elif bad.any():
            a = np.where(bad, float(np.nanmean(np.where(bad, np.nan, a))), a)
        self.a, self.T, self.crs = a, transform, crs
        self._tr = None if crs.is_geographic else Transformer.from_crs(crs.geodetic_crs, crs, always_xy=True)

    @classmethod
    def open(cls, path):
        with rasterio.open(path) as d:
            return cls(d.read(1), d.transform, d.crs, d.nodata)

    def sample(self, lon, lat, order=1):
        lon, lat = np.asarray(lon, float), np.asarray(lat, float)
        if self._tr is None:
            x, y = (lon + 180) % 360 - 180, lat
        else:
            x, y = self._tr.transform(lon, lat)
        col, row = ~self.T * (x, y)
        col, row = np.asarray(col), np.asarray(row)
        if not np.isfinite(self.a).any():
            return np.full(lon.shape, np.nan)
        out = ndimage.map_coordinates(self.a, [row - 0.5, col - 0.5], order=order, mode="nearest")
        out[(col < 0) | (row < 0) | (col > self.a.shape[1]) | (row > self.a.shape[0])] = np.nan
        return out


def window_grid(lon0, lat0, half_m=INPUT_HALF_M, step=GRID_M):
    n = int(round(2 * half_m / step))
    c = -half_m + step / 2 + step * np.arange(n)
    E, N = np.meshgrid(c, -c)
    return en_to_lonlat(E, N, lon0, lat0)


def window_inputs(ctx, themis, dem, lon0, lat0):
    """ctx / themis / dem: Raster or None. Returns (5 x 200 x 200 float32, {"ctx": ok, "themis": ok})."""
    lon, lat = window_grid(lon0, lat0)
    have = {}
    c = ctx.sample(lon, lat) / 255.0 if ctx is not None else np.full(lon.shape, np.nan)
    have["ctx"] = bool(np.isfinite(c).mean() > 0.9)
    c = np.nan_to_num(c, nan=np.nanmean(c) if np.isfinite(c).any() else 0.5)
    blur = ndimage.gaussian_filter(c, 10)                          # 100 m neighbourhood
    c_local = (c - blur) / max(float(np.std(c - blur)), 1e-3)
    th = themis.sample(lon, lat) / 255.0 if themis is not None else np.full(lon.shape, np.nan)
    have["themis"] = bool(np.isfinite(th).mean() > 0.9)
    th = np.nan_to_num(th, nan=0.5)
    z = dem.sample(lon, lat, order=3) if dem is not None else np.zeros(lon.shape)
    z = np.nan_to_num(z, nan=float(np.nanmean(z)) if np.isfinite(z).any() else 0.0)
    gy, gx = np.gradient(z, GRID_M)
    slope = np.degrees(np.arctan(np.hypot(gx, gy)))
    return np.stack([c, c_local, th, (z - z.mean()) / 100.0, slope / 10.0]).astype(np.float32), have
