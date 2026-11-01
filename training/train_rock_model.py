"""
Step 3: train the rock model (Mars Trek inputs -> rocks per 50 m square) and test it honestly.

    python train_rock_model.py --lessons data/lessons

Testing: 5-fold cross-validation grouped by HiRISE site, so every score comes from sites the model never saw.
Compared against "always predict the average", so you can see how much the model really knows.
Writes models/rock_density_v1.onnx and models/rock_density_v1.json (sizes, shapes, scores).
"""
import argparse
import glob
import json
import os
import time

import numpy as np
import torch
from torch import nn

HERE = os.path.dirname(os.path.abspath(__file__))
BINS = (1.0, 2.0, 4.0)
CELL_M = 50.0


# ------------------------------------------------------------------ data
def load(folder, max_pixel_m=0.3):
    """Lessons with a CTX image and a fine HiRISE image (coarser images miss the 1-2 m rocks)."""
    areas = []
    for f in sorted(glob.glob(os.path.join(folder, "*.npz"))):
        z = np.load(f, allow_pickle=False)
        if not bool(z["has_ctx"]) or float(z["pixel_m"]) > max_pixel_m:
            continue
        mask = (z["valid"] > 0.95).astype(np.float32)
        if mask.sum() < 50:
            continue
        areas.append({"name": os.path.basename(f)[:-4], "site": str(z["site"]), "x": z["x"].astype(np.float32),
                      "y": z["counts"].astype(np.float32), "m": mask, "lat": float(z["lat"]), "lon": float(z["lon"])})
    return areas


def rocks_for_sizes(folder, max_pixel_m=0.3):
    """All detected rocks (diameter, minor/major) from the fine HiRISE images (rocks.csv next to the lessons)."""
    d, r = [], []
    for f in glob.glob(os.path.join(folder, "..", "training", "*", "rocks.csv")):
        meta = os.path.join(os.path.dirname(f), "meta.json")
        if os.path.exists(meta) and json.load(open(meta)).get("hirise_pixel_m", 0) > max_pixel_m:
            continue
        a = np.loadtxt(f, delimiter=",", skiprows=1, ndmin=2)
        if len(a):
            d.append(a[:, 2])
            r.append(a[:, 4] / np.maximum(a[:, 3], 1e-6))
    return (np.concatenate(d), np.concatenate(r)) if d else (np.array([]), np.array([]))


# ------------------------------------------------------------------ model
class RockDensityNet(nn.Module):
    """5 x 200 x 200 inputs at 10 m  ->  log(rocks per 50 m square) for 3 size classes, 20 x 20 centre squares."""

    def __init__(self, cin=5, w=32):
        super().__init__()
        self.fine = nn.Sequential(nn.Conv2d(cin, w, 5, padding=2), nn.ReLU(), nn.Conv2d(w, w, 3, padding=1), nn.ReLU())
        self.pool = nn.AvgPool2d(5)                                         # 10 m -> 50 m
        self.coarse = nn.Sequential(
            nn.Conv2d(w, 2 * w, 3, padding=1), nn.ReLU(),
            nn.Conv2d(2 * w, 2 * w, 3, padding=2, dilation=2), nn.ReLU(),
            nn.Conv2d(2 * w, 2 * w, 3, padding=4, dilation=4), nn.ReLU(),
            nn.Conv2d(2 * w, len(BINS), 1))

    def forward(self, x):
        out = self.coarse(self.pool(self.fine(x)))                          # 40 x 40 squares of 50 m
        return out[:, :, 10:30, 10:30]


class ThermalRockModel(nn.Module):
    """Simple model: rocks per 50 m square from THEMIS night infrared only (rocky ground stays warm at night).
    log(rocks) = c + w1 * (THEMIS mean of the 2 km window - 0.5) + w2 * (THEMIS of the square - window mean)."""

    def __init__(self, base_rate):
        super().__init__()
        self.c = nn.Parameter(torch.log(torch.tensor(base_rate + 1e-3, dtype=torch.float32)))
        self.w = nn.Parameter(torch.zeros(len(BINS), 2))

    def forward(self, x):
        th = x[:, 2]
        tw = th.mean((1, 2))
        tc = nn.functional.avg_pool2d(th[:, None, 50:150, 50:150], 5)[:, 0]
        f1 = (tw - 0.5)[:, None, None, None]
        f2 = (tc - tw[:, None, None])[:, None]
        return self.c[None, :, None, None] + self.w[:, 0][None, :, None, None] * f1 + self.w[:, 1][None, :, None, None] * f2


def train_thermal(areas):
    base = sum(a["y"].sum((1, 2)) for a in areas) / sum(a["m"].sum() for a in areas)
    net = ThermalRockModel(base)
    x = torch.from_numpy(np.stack([a["x"] for a in areas]))
    y = torch.from_numpy(np.stack([a["y"] for a in areas]))
    m = torch.from_numpy(np.stack([a["m"] for a in areas]))[:, None]
    opt = torch.optim.LBFGS(net.parameters(), max_iter=200, line_search_fn="strong_wolfe")

    def closure():
        opt.zero_grad()
        logr = net(x)
        loss = ((torch.exp(logr) - y * logr) * m).sum() / m.sum()
        loss.backward()
        return loss
    opt.step(closure)
    return net.eval()


def augment(x, y, m, rng):
    k = int(rng.integers(4))
    x, y, m = np.rot90(x, k, (1, 2)), np.rot90(y, k, (1, 2)), np.rot90(m, k)
    if rng.random() < 0.5:
        x, y, m = x[:, :, ::-1], y[:, :, ::-1], m[:, ::-1]
    return np.ascontiguousarray(x), np.ascontiguousarray(y), np.ascontiguousarray(m)


def train_net(areas, epochs, seed=0, log=None):
    torch.manual_seed(seed)
    rng = np.random.default_rng(seed)
    net = RockDensityNet()
    mean_rate = sum(a["y"].sum((1, 2)) for a in areas) / sum(a["m"].sum() for a in areas)
    with torch.no_grad():
        net.coarse[-1].bias.copy_(torch.log(torch.tensor(mean_rate + 1e-3, dtype=torch.float32)))
    opt = torch.optim.AdamW(net.parameters(), lr=2e-3, weight_decay=1e-3)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, epochs)
    for ep in range(epochs):
        net.train()
        order = rng.permutation(len(areas))
        tot = 0.0
        for i in range(0, len(order), 8):
            batch = [augment(areas[j]["x"], areas[j]["y"], areas[j]["m"], rng) for j in order[i:i + 8]]
            x = torch.from_numpy(np.stack([b[0] for b in batch]))
            y = torch.from_numpy(np.stack([b[1] for b in batch]))
            m = torch.from_numpy(np.stack([b[2] for b in batch]))[:, None]
            logr = net(x)
            loss = ((torch.exp(logr) - y * logr) * m).sum() / m.sum()
            opt.zero_grad()
            loss.backward()
            opt.step()
            tot += loss.item() * len(batch)
        sched.step()
        if log and (ep % 10 == 0 or ep == epochs - 1):
            log(f"    epoch {ep + 1}/{epochs}  loss {tot / len(areas):.4f}")
    return net.eval()


def predict(net, areas):
    with torch.no_grad():
        return [torch.exp(net(torch.from_numpy(a["x"][None])))[0].numpy() for a in areas]


# ------------------------------------------------------------------ scores
def deviance(y, mu):
    y, mu = np.asarray(y, float), np.maximum(np.asarray(mu, float), 1e-9)
    return 2 * np.sum(np.where(y > 0, y * np.log(np.maximum(y, 1e-12) / mu), 0) - (y - mu))


def spearman(a, b):
    ra, rb = np.argsort(np.argsort(a)), np.argsort(np.argsort(b))
    return float(np.corrcoef(ra, rb)[0, 1]) if len(a) > 2 else float("nan")


def scores(areas, preds, base_rate):
    ys = np.concatenate([a["y"][:, a["m"] > 0].ravel() for a in areas])
    ps = np.concatenate([p[:, a["m"] > 0].ravel() for a, p in zip(areas, preds)])
    bs = np.concatenate([np.repeat(base_rate[:, None], int(a["m"].sum()), 1).ravel() for a in areas])
    tot_y = np.array([a["y"][:, a["m"] > 0].sum() / a["m"].sum() for a in areas])
    tot_p = np.array([p[:, a["m"] > 0].sum() / a["m"].sum() for a, p in zip(areas, preds)])
    cell_y = np.concatenate([a["y"].sum(0)[a["m"] > 0] for a in areas])
    cell_p = np.concatenate([p.sum(0)[a["m"] > 0] for a, p in zip(areas, preds)])
    ratio = tot_p / np.maximum(tot_y, 1e-6)
    within = [spearman(a["y"].sum(0)[a["m"] > 0], p.sum(0)[a["m"] > 0]) for a, p in zip(areas, preds)
              if a["y"].sum() > 30 and np.std(p.sum(0)[a["m"] > 0]) > 0]
    return {
        "within_area_square_spearman": float(np.median(within)) if within else 0.0,
        "deviance_explained_vs_average": float(1 - deviance(ys, ps) / deviance(ys, bs)),
        "area_density_spearman": spearman(tot_y, tot_p),
        "square_count_spearman": spearman(cell_y, cell_p),
        "area_density_within_2x": float(np.mean((ratio > 0.5) & (ratio < 2.0))),
        "areas": len(areas),
    }


def folds_by_site(areas, k=5, seed=0):
    sites = sorted({a["site"] for a in areas})
    rng = np.random.default_rng(seed)
    rng.shuffle(sites)
    return [{s for i, s in enumerate(sites) if i % k == f} for f in range(k)]


# ------------------------------------------------------------------ size law
def size_law(d, ratio):
    """Cumulative size law N(>D) ~ D^-b above 1 m (maximum likelihood), and the shape (minor/major) spread."""
    d = d[d >= BINS[0]]
    b = float(len(d) / np.sum(np.log(d / BINS[0]))) if len(d) > 10 else 2.5
    q = np.quantile(ratio, [0.1, 0.25, 0.5, 0.75, 0.9]).tolist() if len(ratio) > 10 else [0.6, 0.7, 0.8, 0.9, 0.95]
    return {"power_b": b, "minor_major_quantiles": q, "rocks_measured": int(len(d)), "max_diameter_m": float(d.max()) if len(d) else 8.0}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lessons", default=os.path.join(HERE, "data", "lessons"))
    ap.add_argument("--model", choices=["thermal", "cnn"], default="thermal",
                    help="thermal: simple THEMIS model (default, tested best); cnn: deep network on all layers")
    ap.add_argument("--epochs", type=int, default=60)
    ap.add_argument("--out", default=os.path.join(os.path.dirname(HERE), "models"))
    ap.add_argument("--max-pixel-m", type=float, default=0.3, help="skip HiRISE images coarser than this")
    ap.add_argument("--folds", type=int, default=5, help="0 = skip the honest test (quick)")
    a = ap.parse_args()
    t0 = time.time()
    log = lambda *s: print(f"{time.time() - t0:7.0f}s", *s, flush=True)
    areas = load(a.lessons, a.max_pixel_m)
    sites = sorted({x["site"] for x in areas})
    log(f"{len(areas)} areas from {len(sites)} HiRISE sites")
    if len(sites) < max(a.folds, 2):
        raise SystemExit("Need lessons from more HiRISE sites.")

    base = sum(x["y"].sum((1, 2)) for x in areas) / sum(x["m"].sum() for x in areas)
    sc = None
    if a.folds:
        oof = [None] * len(areas)
        for f, test in enumerate(folds_by_site(areas, a.folds)):
            tr = [x for x in areas if x["site"] not in test]
            te_idx = [i for i, x in enumerate(areas) if x["site"] in test]
            log(f"fold {f + 1}/{a.folds}: train {len(tr)} areas, test {len(te_idx)} areas from {len(test)} unseen sites")
            net = train_thermal(tr) if a.model == "thermal" else train_net(tr, a.epochs, seed=f)
            for i, p in zip(te_idx, predict(net, [areas[i] for i in te_idx])):
                oof[i] = p
        sc = scores(areas, oof, base)
        log("Honest test on unseen sites: " + json.dumps({k: round(v, 3) if isinstance(v, float) else v for k, v in sc.items()}))

    log("Training the final model on all sites ...")
    net = train_thermal(areas) if a.model == "thermal" else train_net(areas, a.epochs, seed=99, log=log)
    os.makedirs(a.out, exist_ok=True)
    onnx_path = os.path.join(a.out, "rock_density_v1.onnx")
    torch.onnx.export(net, torch.zeros(1, 5, 200, 200), onnx_path, input_names=["x"], output_names=["log_rate"],
                      dynamic_axes={"x": {0: "batch"}, "log_rate": {0: "batch"}}, opset_version=17, dynamo=False)
    d, r = rocks_for_sizes(a.lessons, a.max_pixel_m)
    info = {"model": "ThermalRockModel v1" if a.model == "thermal" else "RockDensityNet v1", "inputs": ["ctx", "ctx_local", "themis", "elev_local", "slope_deg/10"],
            "input_grid_m": 10.0, "input_size": 200, "output_cell_m": CELL_M, "output_size": 20,
            "size_classes_m": list(BINS), "average_rocks_per_square": base.tolist(),
            "test_on_unseen_sites": sc, "size_law": size_law(d, r), "areas": len(areas), "sites": len(sites),
            "labels": "RockBoxNet v1 detections on HiRISE (automatic, not hand-checked)",
            "created": time.strftime("%Y-%m-%d %H:%M")}
    if a.model == "thermal":
        info["weights"] = {"c": net.c.tolist(), "w": net.w.tolist()}
    json.dump(info, open(os.path.join(a.out, "rock_density_v1.json"), "w"), indent=1)
    log(f"Saved {onnx_path}")


if __name__ == "__main__":
    main()
