"""CPU-only PCA embedding baseline. Catalog art is training data, never holdout evidence."""

import argparse
import hashlib
import json
from pathlib import Path
import cv2
import numpy as np


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", type=Path, default=Path("data"))
    parser.add_argument("--dimensions", type=int, default=48)
    args = parser.parse_args()
    catalog = json.loads((args.data / "catalog.json").read_text(encoding="utf-8"))
    features = []
    overrides_path = args.data / "official-references.json"
    overrides = (
        {
            i["catalogId"]: i["image"]
            for i in json.loads(overrides_path.read_text())["items"]
        }
        if overrides_path.exists()
        else {}
    )
    for item in catalog["items"]:
        im = cv2.imread(
            str(args.data / overrides.get(item["id"], item["image"])),
            cv2.IMREAD_UNCHANGED,
        )
        if im is None:
            raise ValueError(item["image"])
        if im.shape[2] == 4:
            im = (im[:, :, :3].astype(np.float32) * (im[:, :, 3:4] / 255)).astype(
                np.uint8
            )
        g = (
            cv2.cvtColor(
                cv2.resize(im, (24, 48), interpolation=cv2.INTER_AREA),
                cv2.COLOR_BGR2GRAY,
            )
            .astype(np.float32)
            .ravel()
        )
        g -= g.mean()
        g /= max(0.001, np.linalg.norm(g))
        features.append(g)
    x = np.stack(features)
    mean = x.mean(axis=0)
    x -= mean
    # Deterministic randomized SVD limits memory and work on the development CPU.
    rng = np.random.default_rng(42)
    omega = rng.normal(size=(x.shape[1], args.dimensions + 12)).astype(np.float32)
    q, _ = np.linalg.qr(x @ omega)
    for _ in range(2):
        q, _ = np.linalg.qr(x @ (x.T @ q))
    _, singular, v = np.linalg.svd(q.T @ x, full_matrices=False)
    model = dict(
        officialArtHash=hashlib.sha256(overrides_path.read_bytes()).hexdigest().upper()
        if overrides_path.exists()
        else None,
        catalogVersion=catalog["version"],
        method="pca-gray-24x48",
        mean=mean.tolist(),
        components=v[: args.dimensions].tolist(),
    )
    (args.data / "embedding.json").write_text(
        json.dumps(model, separators=(",", ":")), encoding="utf-8"
    )
    print(
        json.dumps(
            dict(
                trainingReferences=len(features),
                dimensions=args.dimensions,
                heldOutRealScreens=0,
                explainedApprox=float(
                    (singular[: args.dimensions] ** 2).sum() / (x * x).sum()
                ),
            )
        )
    )


if __name__ == "__main__":
    main()
