"""Index real captures, group near-duplicate Ritual views and keep labels separate."""

import argparse
import hashlib
import json
import os
import shutil
from pathlib import Path

import cv2
import numpy as np


def load_image(path):
    return cv2.imdecode(np.fromfile(path, np.uint8), cv2.IMREAD_COLOR)


def view_features(image, prediction):
    grid = prediction.get("grid")
    if not grid:
        return None
    b = grid["bounds"]
    x, y, w, h = (b[k] for k in ("x", "y", "width", "height"))
    if (
        min(x, y) < 0
        or min(w, h) <= 0
        or x + w > image.shape[1]
        or y + h > image.shape[0]
    ):
        raise ValueError("grid outside image")
    small = cv2.resize(
        image[y : y + h, x : x + w], (144, 120), interpolation=cv2.INTER_AREA
    )
    gray = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY).astype(np.float32)
    dct = cv2.dct(cv2.resize(gray, (32, 32)))[:16, :16].ravel()[1:]
    bits = dct > np.median(dct)
    return small, bits


def near_duplicate(a, b):
    if np.count_nonzero(a[1] != b[1]) > 14:
        return False
    difference = np.abs(a[0].astype(np.int16) - b[0].astype(np.int16))
    return float(difference.mean()) < 2.5 and float(np.percentile(difference, 99)) < 24


def assign_splits(records):
    # A duplicate view shared by two sessions joins their split, preventing leakage.
    parent = {r["session"]: r["session"] for r in records}

    def find(s):
        while parent[s] != s:
            parent[s] = parent[parent[s]]
            s = parent[s]
        return s

    for r in records:
        for member in r["members"]:
            s = member["session"]
            parent.setdefault(s, s)
            a, b = find(r["session"]), find(s)
            parent[max(a, b)] = min(a, b)
    for r in records:
        group = find(r["session"])
        r["splitGroup"] = group
        bucket = int(hashlib.sha256(group.encode()).hexdigest()[:8], 16) % 4
        r["split"] = "validation" if bucket == 0 else "train"


def materialize(records, out):
    for r in records:
        source = Path(r["file"])
        target = out.resolve() / "frames" / r["session"].replace("/", "-") / source.name
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists():
            try:
                os.link(source, target)
            except OSError:
                shutil.copyfile(source, target)
        if hashlib.sha256(target.read_bytes()).hexdigest() != r["sha256"]:
            raise ValueError("materialized frame hash mismatch")
        r["file"] = str(target)


def build(roots, out, copy_frames=False):
    out.mkdir(parents=True, exist_ok=True)
    existing = out / "manifest.json"
    reviewed = {}
    if existing.exists():
        reviewed = {
            r["sha256"]: r
            for r in json.loads(existing.read_text(encoding="utf-8"))["records"]
            if r.get("verifiedBy")
        }
    records, features, rejected = [], [], []
    hashes = {}
    for root_index, root in enumerate(roots):
        for metadata in sorted(root.rglob("*.json")):
            image_path = metadata.with_suffix(".png")
            if not image_path.exists():
                continue
            try:
                d = json.loads(metadata.read_text(encoding="utf-8"))
                prediction = d.get("prediction")
                if not isinstance(prediction, dict):
                    continue
                raw = image_path.read_bytes()
                digest = hashlib.sha256(raw).hexdigest()
                if digest != d.get("sha256", "").lower():
                    raise ValueError("source hash mismatch")
                image = cv2.imdecode(np.frombuffer(raw, np.uint8), cv2.IMREAD_COLOR)
                if image is None:
                    raise ValueError("image decode failed")
                session = f"{root_index}/{d.get('session', metadata.parent.name)}"
                member = dict(
                    file=str(image_path.resolve()),
                    sha256=digest,
                    session=session,
                    prediction=prediction,
                    tooltip=d.get("tooltip"),
                )
                f = view_features(image, prediction)
                match = hashes.get(digest)
                if match is None and f is not None:
                    match = next(
                        (
                            i
                            for i, old in enumerate(features)
                            if old is not None and near_duplicate(f, old)
                        ),
                        None,
                    )
                if match is not None:
                    records[match]["members"].append(member)
                    hashes[digest] = match
                    continue
                hashes[digest] = len(records)
                records.append(
                    dict(
                        id=digest[:16],
                        file=member["file"],
                        sha256=digest,
                        session=session,
                        members=[member],
                        resolution=[image.shape[1], image.shape[0]],
                        hdr=d.get("hdr"),
                        grid=prediction.get("grid"),
                        prediction=prediction,
                        groundTruth=None,
                        verifiedBy=None,
                    )
                )
                features.append(f)
            except (ValueError, OSError, cv2.error) as error:
                rejected.append(dict(file=str(metadata), reason=str(error)))
    assign_splits(records)
    for r in records:
        if r["sha256"] in reviewed:
            old = reviewed[r["sha256"]]
            if r["split"] != old["split"]:
                raise ValueError(
                    "new duplicate links changed a reviewed split; review leakage before rebuilding"
                )
            r["groundTruth"], r["verifiedBy"] = old["groundTruth"], old["verifiedBy"]
    if copy_frames:
        materialize(records, out)
    result = dict(
        schemaVersion=1, independent=False, records=records, rejected=rejected
    )
    (out / "manifest.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    summary = dict(
        sourceCaptures=sum(len(r["members"]) for r in records),
        distinctViews=len(records),
        duplicates=sum(len(r["members"]) - 1 for r in records),
        train=sum(r["split"] == "train" for r in records),
        validation=sum(r["split"] == "validation" for r in records),
        rejected=len(rejected),
        reviewed=sum(bool(r.get("verifiedBy")) for r in records),
    )
    (out / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--captures", type=Path, nargs="+", required=True)
    parser.add_argument("--out", type=Path, default=Path("data/capture-dataset"))
    parser.add_argument(
        "--materialize",
        action="store_true",
        help="Keep hash-verified frames beside the manifest (hard link, or copy across volumes)",
    )
    args = parser.parse_args()
    build(args.captures, args.out, args.materialize)


if __name__ == "__main__":
    main()
