"""Build small local appearance prototypes from reviewed training captures only."""

import argparse
import hashlib
import json
from pathlib import Path

import cv2
import numpy as np

from capture_dataset import load_image


def descriptor(crop):
    small = cv2.resize(crop, (24, 48), interpolation=cv2.INTER_AREA)
    gray = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY)
    gx = cv2.Sobel(gray, cv2.CV_32F, 1, 0)
    gy = cv2.Sobel(gray, cv2.CV_32F, 0, 1)
    edge = np.abs(gx) + np.abs(gy)
    color = small.astype(np.float32)
    sums = color.sum(axis=2, keepdims=True)
    color = np.where(sums >= 100, color / np.maximum(sums, 1), 0)
    return gray.ravel(), edge.ravel(), color.ravel()


def train(manifest, catalog, output):
    records = json.loads(manifest.read_text(encoding="utf-8"))["records"]
    known = {i["id"]: i for i in catalog["items"]}
    prototypes, evidence, groups = [], [], {}
    for record in records:
        if (
            record["split"] != "train"
            or not record.get("verifiedBy")
            or not record.get("groundTruth")
        ):
            continue
        source = Path(record["file"])
        if hashlib.sha256(source.read_bytes()).hexdigest() != record["sha256"].lower():
            raise ValueError("reviewed capture changed")
        image = load_image(source)
        used = False
        for item in record["groundTruth"]["items"]:
            if item["catalogId"] not in known:
                raise ValueError("unknown reviewed catalog ID")
            b = item["bounds"]
            x, y, w, h = (b[k] for k in ("x", "y", "width", "height"))
            if (
                min(x, y) < 0
                or min(w, h) <= 0
                or x + w > image.shape[1]
                or y + h > image.shape[0]
            ):
                raise ValueError("reviewed crop outside image")
            gray, edge, color = descriptor(image[y : y + h, x : x + w])
            key = (item["catalogId"], item["columns"], item["rows"])
            previous = groups.setdefault(key, [])
            # Keep independent appearances, not repeated screenshots of one icon.
            mask = np.zeros((48, 24), bool)
            mask[3:40, 2:22] = True
            if key[1:] == (1, 1):
                mask[:15, :8] = False
                mask[31:, 15:] = False
            values = gray[mask.ravel()].astype(float)
            values -= values.mean()
            values /= max(1e-6, np.linalg.norm(values))
            if (
                any(np.dot(values, old) > 0.985 for old in previous)
                or len(previous) >= 6
            ):
                continue
            previous.append(values)
            prototypes.append(
                dict(
                    catalogId=key[0],
                    columns=key[1],
                    rows=key[2],
                    gray=gray.tolist(),
                    edge=edge.astype(int).tolist(),
                    color=[round(float(v), 4) for v in color],
                    sourceHash=record["sha256"],
                )
            )
            used = True
        if used:
            evidence.append(
                dict(
                    sha256=record["sha256"],
                    splitGroup=hashlib.sha256(
                        record["splitGroup"].encode()
                    ).hexdigest()[:16],
                )
            )
    if not prototypes:
        raise ValueError(
            "No reviewed training items; predictions cannot train the model"
        )
    model = dict(
        schemaVersion=1,
        catalogVersion=catalog["version"],
        method="real-appearance-24x48",
        trainingEvidence=evidence,
        prototypes=prototypes,
    )
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(model, separators=(",", ":")), encoding="utf-8")
    print(
        json.dumps(
            dict(
                prototypes=len(prototypes),
                items=len({p["catalogId"] for p in prototypes}),
                trainingCaptures=len(evidence),
            )
        )
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--data", type=Path, default=Path("data"))
    parser.add_argument("--out", type=Path, default=Path("data/recognition-model.json"))
    args = parser.parse_args()
    train(
        args.manifest,
        json.loads((args.data / "catalog.json").read_text(encoding="utf-8")),
        args.out,
    )


if __name__ == "__main__":
    main()
