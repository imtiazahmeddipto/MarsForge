"""
Rock detection with RockBoxNet (same settings and steps as the desktop app):
model peaks -> half-brightness outline -> de-duplicate -> rule-based supplement for large rocks.

    rocks = RockDetector().detect(image, pixel_scale_m=0.25)

`image`: HiRISE brightness (any number type), 0 = no data. Returns rocks in image pixels and metres.
"""
import math
import os
from dataclasses import dataclass

import numpy as np

from marsrock.rock_geometry import box_geometry, deduplicate, measure_rock
from marsrock.rock_inference import decode, stitch
from marsrock.rock_pseudo import pseudo_rocks

HERE = os.path.dirname(os.path.abspath(__file__))
MODEL_FILE = os.path.join(os.path.dirname(os.path.dirname(HERE)), "models", "rockboxnet_v1.onnx")


@dataclass
class Settings:
    threshold: float = 0.35
    tile: int = 512
    overlap: int = 64
    supplement_min_diameter_px: float = 8.0
    supplement_max_aspect: float = 1.8


def _supplement(image, rocks, s):
    out = []
    for body in pseudo_rocks(image.astype(np.float32)):
        d = body.equivalent_diameter_px
        if d < s.supplement_min_diameter_px or body.ellipse_major_px > s.supplement_max_aspect * max(body.ellipse_minor_px, 1e-6):
            continue
        if any(math.hypot(r["geometry"]["center_x"] - body.center_x, r["geometry"]["center_y"] - body.center_y) <= max(2.0, 0.5 * d)
               or (r["geometry"]["x_min"] <= body.center_x <= r["geometry"]["x_max"]
                   and r["geometry"]["y_min"] <= body.center_y <= r["geometry"]["y_max"]) for r in rocks):
            continue
        w, h = body.width_px, body.height_px
        g = measure_rock(image, body.center_x, body.center_y, w, h) or box_geometry(body.center_x, body.center_y, w, h)
        out.append({"score": float(body.confidence), "geometry": g,
                    "model": {"cx": body.center_x, "cy": body.center_y, "w": w, "h": h}})
    return out


def _area_limit(rock):
    g, m = rock["geometry"], rock["model"]
    if g["method"] != "model_box_fallback" and g["area_px"] > 2.0 * m["w"] * m["h"] + 9:
        return dict(rock, geometry=box_geometry(m["cx"], m["cy"], m["w"], m["h"]))
    return rock


class OnnxPredictor:
    """RockBoxNet (ONNX) on one tile: [heat 0..1, box width, box height, offset x, offset y]."""

    def __init__(self, path):
        import onnxruntime as ort
        self.session = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
        self.multiple = 8

    def __call__(self, data, valid):
        x = _model_input(data, valid)
        h, w = data.shape
        x = np.pad(x, ((0, 0), (0, (-h) % self.multiple), (0, (-w) % self.multiple)))
        heat, size, offset = self.session.run(None, {"x": x[None].astype(np.float32)})
        maps = np.concatenate([1 / (1 + np.exp(-heat)), np.exp(size), offset], axis=1)[0, :, :h, :w]
        maps[0][~valid] = 0.0
        return maps.astype(np.float32)


def _model_input(data, valid):
    """2 channels: brightness scaled by its own mean / spread, and the 'has data' mask (as in training)."""
    values = data[valid].astype(np.float32)
    if values.size < 16:
        norm = np.zeros(data.shape, np.float32)
    else:
        norm = (data.astype(np.float32) - float(values.mean())) / max(float(values.std()), 1.0)
        norm[~valid] = 0.0
    return np.stack([norm, valid.astype(np.float32)])


class RockDetector:
    def __init__(self, model_file=MODEL_FILE, settings=Settings()):
        self.predictor, self.device, self.settings = OnnxPredictor(model_file), "cpu (onnx)", settings

    def detect(self, image, pixel_scale_m, supplement=True):
        image = np.asarray(image)
        maps, _ = stitch(lambda c, r, w, h: image[r:r + h, c:c + w], (0, 0, image.shape[1], image.shape[0]),
                         self.predictor, self.settings.tile, self.settings.overlap)
        rocks = []
        for det in decode(maps, self.settings.threshold):
            g = measure_rock(image, det["cx"], det["cy"], det["w"], det["h"]) or box_geometry(det["cx"], det["cy"], det["w"], det["h"])
            rocks.append({"score": det["score"], "geometry": g, "model": det})
        rocks = deduplicate(rocks)
        if supplement:
            rocks += _supplement(image, rocks, self.settings)
        out = []
        for r in map(_area_limit, rocks):
            g = r["geometry"]
            out.append({"x": g["center_x"], "y": g["center_y"],                       # image pixels (x right, y down)
                        "diameter_m": g["equivalent_diameter_px"] * pixel_scale_m,
                        "major_m": g["major_axis_px"] * pixel_scale_m, "minor_m": g["minor_axis_px"] * pixel_scale_m,
                        "angle_deg": g["orientation_deg"], "score": float(r["score"])})
        return out
