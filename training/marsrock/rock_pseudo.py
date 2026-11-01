import math
from dataclasses import asdict, dataclass

import numpy as np

PSEUDO_LABEL_STATUS = "PSEUDO_LABEL_AUTOMATIC_NOT_VERIFIED"
PSEUDO_METHOD = "compact isolated high-contrast sunlit body (local z-score), size/shape/compactness limits; box and ellipse from the body mask; shadows not required (mostly 1-2 px at 31-33 deg incidence)"


@dataclass(frozen=True)
class PseudoParams:
    background_radius: int = 24
    bright_z: float = 2.0
    body_mean_z: float = 2.8
    peak_z: float = 3.5
    ring_max_z: float = 0.8
    min_area_px: int = 6
    max_area_px: int = 1600
    min_compactness: float = 0.5
    max_aspect: float = 3.0
    min_diameter_px: float = 3.0
    max_diameter_px: float = 48.0


@dataclass
class RockShape:
    center_x: float
    center_y: float
    x_min: float
    y_min: float
    x_max: float
    y_max: float
    width_px: float
    height_px: float
    equivalent_diameter_px: float
    ellipse_major_px: float
    ellipse_minor_px: float
    ellipse_angle_deg: float
    body_area_px: int
    ring_contrast_z: float
    body_contrast_z: float
    confidence: float

    def as_dict(self) -> dict:
        return {k: (round(v, 3) if isinstance(v, float) else v) for k, v in asdict(self).items()}


def box_sum(values: np.ndarray, radius: int) -> np.ndarray:
    padded = np.pad(values, ((radius + 1, radius), (radius + 1, radius)), mode="edge")
    integral = padded.cumsum(0).cumsum(1)
    size = 2 * radius + 1
    return integral[size:, size:] - integral[:-size, size:] - integral[size:, :-size] + integral[:-size, :-size]


def local_zscore(image: np.ndarray, valid: np.ndarray, radius: int) -> np.ndarray:
    data = np.where(valid, image.astype(np.float64), 0.0)
    weight = valid.astype(np.float64)
    count = np.maximum(box_sum(weight, radius), 1.0)
    mean = box_sum(data, radius) / count
    variance = np.maximum(box_sum(data * data, radius) / count - mean * mean, 0.0)
    z = (image - mean) / np.maximum(np.sqrt(variance), 2.0)
    z[~valid] = 0.0
    return z.astype(np.float32)


def label_components(mask: np.ndarray, max_iterations: int = 400) -> np.ndarray:
    height, width = mask.shape
    labels = np.where(mask, np.arange(1, height * width + 1, dtype=np.int64).reshape(height, width), 0)
    for _ in range(max_iterations):
        padded = np.pad(labels, 1)
        neighbours = np.maximum.reduce([padded[1:-1, 1:-1], padded[:-2, 1:-1], padded[2:, 1:-1], padded[1:-1, :-2], padded[1:-1, 2:]])
        updated = np.where(mask, neighbours, 0)
        if np.array_equal(updated, labels):
            break
        labels = updated
    unique, inverse = np.unique(labels, return_inverse=True)
    relabelled = inverse.reshape(labels.shape)
    if unique[0] != 0:
        relabelled = relabelled + 1
    return relabelled


def ellipse_from_mask(rows: np.ndarray, cols: np.ndarray) -> tuple[float, float, float]:
    if rows.size < 2:
        return 1.0, 1.0, 0.0
    dr, dc = rows - rows.mean(), cols - cols.mean()
    cov = np.array([[np.mean(dc * dc), np.mean(dc * dr)], [np.mean(dc * dr), np.mean(dr * dr)]]) + np.eye(2) / 12.0
    values, vectors = np.linalg.eigh(cov)
    major, minor = 4 * math.sqrt(max(values[1], 1e-6)), 4 * math.sqrt(max(values[0], 1e-6))
    angle = math.degrees(math.atan2(vectors[1, 1], vectors[0, 1]))
    return major, minor, angle


def pseudo_rocks(image: np.ndarray, valid: np.ndarray | None = None, params: PseudoParams = PseudoParams()) -> list[RockShape]:
    valid = image > 0 if valid is None else valid
    z = local_zscore(image, valid, params.background_radius)
    labels = label_components((z > params.bright_z) & valid)
    count = int(labels.max())
    if count == 0:
        return []
    flat = labels.ravel()
    areas = np.bincount(flat, minlength=count + 1)
    rows_all, cols_all = np.indices(labels.shape)
    rows_all, cols_all = rows_all.ravel(), cols_all.ravel()
    order = np.argsort(flat, kind="stable")
    starts = np.searchsorted(flat[order], np.arange(count + 2))
    height, width = image.shape
    found = []
    for label in np.nonzero((areas >= params.min_area_px) & (areas <= params.max_area_px))[0]:
        if label == 0:
            continue
        members = order[starts[label] : starts[label + 1]]
        rr, cc = rows_all[members], cols_all[members]
        r0, r1, c0, c1 = int(rr.min()), int(rr.max()), int(cc.min()), int(cc.max())
        bw, bh = c1 - c0 + 1, r1 - r0 + 1
        if max(bw, bh) / max(min(bw, bh), 1) > params.max_aspect:
            continue
        if rr.size / (math.pi / 4 * bw * bh) < params.min_compactness:
            continue
        values = z[rr, cc]
        body_z = float(values.mean())
        if body_z < params.body_mean_z or float(values.max()) < params.peak_z:
            continue
        pad = int(max(2, math.ceil(0.5 * max(bw, bh))))
        y0, y1, x0, x1 = max(r0 - pad, 0), min(r1 + pad + 1, height), max(c0 - pad, 0), min(c1 + pad + 1, width)
        if y0 == 0 or x0 == 0 or y1 == height or x1 == width or not valid[y0:y1, x0:x1].all():
            continue
        window = labels[y0:y1, x0:x1] != label
        ring = z[y0:y1, x0:x1][window]
        ring_z = float(ring.mean()) if ring.size else 99.0
        if ring_z > params.ring_max_z:
            continue
        diameter = math.sqrt(bw * bh)
        if not (params.min_diameter_px <= diameter <= params.max_diameter_px):
            continue
        major, minor, angle = ellipse_from_mask(rr.astype(float), cc.astype(float))
        confidence = 0.6 * float(np.clip((body_z - params.bright_z) / 4.0, 0, 1)) + 0.4 * float(np.clip((params.ring_max_z - ring_z) / 1.5, 0, 1))
        x_min, x_max, y_min, y_max = c0 - 0.5, c1 + 0.5, r0 - 0.5, r1 + 0.5
        found.append(RockShape((x_min + x_max) / 2, (y_min + y_max) / 2, x_min, y_min, x_max, y_max, float(bw), float(bh), diameter, major, minor, angle, int(rr.size), ring_z, body_z, confidence))
    return found
