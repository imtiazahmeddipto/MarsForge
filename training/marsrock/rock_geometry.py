import math

import numpy as np

from marsrock.rock_pseudo import label_components

HALF_MAX = "half_max_segmentation"
MODEL_BOX = "model_box_fallback"


def smooth3(patch: np.ndarray) -> np.ndarray:
    p = np.pad(patch, 1, mode="edge")
    return sum(p[1 + dy : 1 + dy + patch.shape[0], 1 + dx : 1 + dx + patch.shape[1]] for dy in (-1, 0, 1) for dx in (-1, 0, 1)) / 9.0


def star_polygon(mask: np.ndarray, cx: float, cy: float, points: int = 32) -> list[tuple[float, float]]:
    out = []
    limit = float(max(mask.shape))
    for angle in np.linspace(0, 2 * math.pi, points, endpoint=False):
        ca, sa = math.cos(angle), math.sin(angle)
        radius = 0.0
        step = 0.25
        while radius < limit:
            x, y = int(round(cx + (radius + step) * ca)), int(round(cy + (radius + step) * sa))
            if not (0 <= y < mask.shape[0] and 0 <= x < mask.shape[1]) or not mask[y, x]:
                break
            radius += step
        radius += 0.5
        out.append((cx + radius * ca, cy + radius * sa))
    return out


def measure_rock(image: np.ndarray, cx: float, cy: float, w: float, h: float, min_snr: float = 4.0) -> dict | None:
    size = max(w, h, 2.0)
    pad = int(math.ceil(1.6 * size + 4))
    r0, c0 = int(round(cy)) - pad, int(round(cx)) - pad
    if r0 < 0 or c0 < 0 or r0 + 2 * pad + 1 > image.shape[0] or c0 + 2 * pad + 1 > image.shape[1]:
        return None
    patch = image[r0 : r0 + 2 * pad + 1, c0 : c0 + 2 * pad + 1].astype(np.float32)
    if (patch <= 0).any():
        return None
    yy, xx = np.mgrid[0 : patch.shape[0], 0 : patch.shape[1]]
    lx, ly = cx - c0, cy - r0
    dist = np.hypot(xx - lx, yy - ly)
    ring = (dist >= 0.75 * size + 2) & (dist <= pad)
    if ring.sum() < 12:
        return None
    background = float(np.median(patch[ring]))
    noise = max(1.4826 * float(np.median(np.abs(patch[ring] - background))), 0.5)
    smooth = smooth3(patch)
    core = dist <= max(1.5, 0.35 * size)
    peak = float(smooth[core].max())
    if peak - background < min_snr * noise:
        return None
    threshold = background + 0.5 * (peak - background)
    above = patch > threshold
    labels = label_components(above)
    seeds = labels[core & above]
    if seeds.size == 0:
        return None
    component = labels == np.bincount(seeds[seeds > 0]).argmax()
    rows, cols = np.nonzero(component)
    if rows.size < 2 or rows.min() == 0 or cols.min() == 0 or rows.max() == patch.shape[0] - 1 or cols.max() == patch.shape[1] - 1:
        return None
    if rows.size > 2.0 * w * h + 9:
        return None
    weight = patch[rows, cols] - background
    mx, my = float((cols * weight).sum() / weight.sum()), float((rows * weight).sum() / weight.sum())
    dc, dr = cols - cols.mean(), rows - rows.mean()
    cov = np.array([[np.mean(dc * dc), np.mean(dc * dr)], [np.mean(dc * dr), np.mean(dr * dr)]]) + np.eye(2) / 12.0
    values, vectors = np.linalg.eigh(cov)
    area = float(rows.size)
    polygon = star_polygon(component, float(cols.mean()), float(rows.mean()))
    return {
        "center_x": mx + c0,
        "center_y": my + r0,
        "x_min": float(cols.min()) - 0.5 + c0,
        "x_max": float(cols.max()) + 0.5 + c0,
        "y_min": float(rows.min()) - 0.5 + r0,
        "y_max": float(rows.max()) + 0.5 + r0,
        "area_px": area,
        "equivalent_diameter_px": 2.0 * math.sqrt(area / math.pi),
        "major_axis_px": 4.0 * math.sqrt(max(values[1], 1e-6)),
        "minor_axis_px": 4.0 * math.sqrt(max(values[0], 1e-6)),
        "orientation_deg": math.degrees(math.atan2(vectors[1, 1], vectors[0, 1])) % 180.0,
        "polygon": [(x + c0, y + r0) for x, y in polygon],
        "background_dn": background,
        "peak_dn": peak,
        "contrast_snr": (peak - background) / noise,
        "method": HALF_MAX,
    }


def box_geometry(cx: float, cy: float, w: float, h: float) -> dict:
    angles = np.linspace(0, 2 * math.pi, 32, endpoint=False)
    return {
        "center_x": cx,
        "center_y": cy,
        "x_min": cx - w / 2,
        "x_max": cx + w / 2,
        "y_min": cy - h / 2,
        "y_max": cy + h / 2,
        "area_px": math.pi * w * h / 4.0,
        "equivalent_diameter_px": math.sqrt(w * h),
        "major_axis_px": max(w, h),
        "minor_axis_px": min(w, h),
        "orientation_deg": 0.0 if w >= h else 90.0,
        "polygon": [(cx + w / 2 * math.cos(a), cy + h / 2 * math.sin(a)) for a in angles],
        "background_dn": None,
        "peak_dn": None,
        "contrast_snr": None,
        "method": MODEL_BOX,
    }


def deduplicate(rocks: list[dict]) -> list[dict]:
    kept, grid, cell = [], {}, 16
    for rock in sorted(rocks, key=lambda r: -r["score"]):
        g = rock["geometry"]
        key = (int(g["center_y"] // cell), int(g["center_x"] // cell))
        clash = False
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                for other in grid.get((key[0] + dy, key[1] + dx), []):
                    o = other["geometry"]
                    inside = o["x_min"] <= g["center_x"] <= o["x_max"] and o["y_min"] <= g["center_y"] <= o["y_max"]
                    if inside or math.hypot(g["center_x"] - o["center_x"], g["center_y"] - o["center_y"]) < 1.0:
                        clash = True
        if not clash:
            grid.setdefault(key, []).append(rock)
            kept.append(rock)
    return kept
