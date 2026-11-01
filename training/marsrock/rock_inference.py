"""RockBoxNet on overlapping tiles, and turning the heat map into boxes."""
import math
from typing import Callable

import numpy as np


def tile_origins(length: int, tile: int, overlap: int) -> list[int]:
    if tile <= overlap:
        raise ValueError("tile must exceed overlap")
    if length <= tile:
        return [0]
    return sorted(set(list(range(0, length - tile, tile - overlap)) + [length - tile]))


def erode(mask: np.ndarray, iterations: int) -> np.ndarray:
    result = mask.copy()
    for _ in range(iterations):
        p = np.pad(result, 1)
        result = p[1:-1, 1:-1] & p[:-2, 1:-1] & p[2:, 1:-1] & p[1:-1, :-2] & p[1:-1, 2:]
    return result


def stitch(read: Callable[[int, int, int, int], np.ndarray], window: tuple[int, int, int, int], predictor, tile: int = 512, overlap: int = 64, edge_margin: int = 3, progress=None) -> tuple[np.ndarray, np.ndarray]:
    col0, row0, width, height = window
    maps = np.zeros((5, height, width), dtype=np.float32)
    valid = np.zeros((height, width), dtype=bool)
    half = overlap // 2
    rows, cols = tile_origins(height, tile, overlap), tile_origins(width, tile, overlap)
    total, done = len(rows) * len(cols), 0
    for ty in rows:
        for tx in cols:
            th, tw = min(tile, height - ty), min(tile, width - tx)
            data = read(col0 + tx, row0 + ty, tw, th)
            mask = data > 0
            out = predictor(data, mask) if mask.sum() >= 64 else np.zeros((5, th, tw), np.float32)
            top = 0 if ty == rows[0] else half
            left = 0 if tx == cols[0] else half
            bottom = th if ty == rows[-1] else th - half
            right = tw if tx == cols[-1] else tw - half
            maps[:, ty + top : ty + bottom, tx + left : tx + right] = out[:, top:bottom, left:right]
            valid[ty + top : ty + bottom, tx + left : tx + right] = mask[top:bottom, left:right]
            done += 1
            if progress:
                progress(done, total)
    maps[0][~erode(valid, edge_margin)] = 0.0
    return maps, valid


def local_maxima(values: np.ndarray) -> np.ndarray:
    p = np.pad(values, 1, constant_values=-np.inf)
    centre = p[1:-1, 1:-1]
    result = np.ones(values.shape, dtype=bool)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            if dy or dx:
                result &= centre >= p[1 + dy : 1 + dy + values.shape[0], 1 + dx : 1 + dx + values.shape[1]]
    return result


def box_iou(a: tuple, b: tuple) -> float:
    ix = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = ix * iy
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


def decode(maps: np.ndarray, threshold: float, iou_threshold: float = 0.3, max_detections: int = 200000) -> list[dict]:
    if not 0.0 <= threshold <= 1.0:
        raise ValueError("threshold must be in [0, 1]")
    heat = maps[0]
    rows, cols = np.nonzero(local_maxima(heat) & (heat >= threshold))
    order = np.argsort(-heat[rows, cols], kind="stable")[:max_detections]
    kept, grid, cell = [], {}, 32
    for index in order:
        r, c = int(rows[index]), int(cols[index])
        w, h = float(maps[1, r, c]), float(maps[2, r, c])
        cx, cy = c + float(maps[3, r, c]), r + float(maps[4, r, c])
        box = (cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2)
        key = (int(cy // cell), int(cx // cell))
        neighbours = [kept[i] for dy in (-1, 0, 1) for dx in (-1, 0, 1) for i in grid.get((key[0] + dy, key[1] + dx), [])]
        if any(box_iou(box, k["box"]) > iou_threshold or math.hypot(cx - k["cx"], cy - k["cy"]) < 0.5 * min(max(w, h), max(k["w"], k["h"])) for k in neighbours):
            continue
        grid.setdefault(key, []).append(len(kept))
        kept.append({"cx": cx, "cy": cy, "w": w, "h": h, "box": box, "score": float(heat[r, c])})
    return kept
