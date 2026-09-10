"""Strict real-screen evaluation and release gate. Predictions never become labels.
python tools/evaluate.py init --screens poe2_ritual_data --out data/evaluation-manifest.json
python tools/evaluate.py check --manifest data/evaluation-manifest.json --predictions work/current --out work/evaluation.json
"""

import argparse
import hashlib
import json
from pathlib import Path
from itertools import product
import numpy as np
from PIL import Image


def iou(a, b):
    intersection = max(
        0, min(a["x"] + a["width"], b["x"] + b["width"]) - max(a["x"], b["x"])
    ) * max(0, min(a["y"] + a["height"], b["y"] + b["height"]) - max(a["y"], b["y"]))
    return intersection / max(
        1, a["width"] * a["height"] + b["width"] * b["height"] - intersection
    )


def evaluate(manifest, predictions):
    records = manifest["screens"]
    labeled = 0
    instances = 0
    errors = []
    unlabeled = []
    conditions = set()
    sessions = {}
    hashes = {}
    duplicates = []
    times = []
    evaluated = []
    required = {"catalogId", "quantity", "deferred", "dimmed", "selected", "bounds"}
    for record in records:
        session = record["session"]
        split = record["split"]
        sessions.setdefault(session, set()).add(split)
        if record["sha256"] in hashes and (
            "test" in hashes[record["sha256"]] or split == "test"
        ):
            duplicates.append(record["file"])
        hashes.setdefault(record["sha256"], set()).add(split)
        if split != "test":
            continue
        gt = record.get("groundTruth")
        path = predictions / (Path(record["file"]).stem + ".json")
        if not gt or not gt.get("complete") or not record.get("verifiedBy"):
            unlabeled.append(record["file"])
            continue
        source = Path(record["file"])
        if (
            not source.exists()
            or hashlib.sha256(source.read_bytes()).hexdigest() != record["sha256"]
        ):
            errors.append(dict(file=record["file"], kind="source_hash_mismatch"))
            continue
        if not path.exists():
            errors.append(dict(file=record["file"], kind="missing_prediction"))
            continue
        pred = json.loads(path.read_text(encoding="utf-8"))
        labeled += 1
        instances += len(gt["items"])
        times.append(pred["elapsedMs"])
        evaluated.append(record)
        conditions.add(
            (record.get("resolutionClass"), record.get("hdr"), record.get("windowMode"))
        )
        if (gt.get("grid") is None) != (pred.get("grid") is None):
            errors.append(dict(file=record["file"], kind="grid_presence"))
        elif (
            gt.get("grid") is not None and iou(gt["grid"], pred["grid"]["bounds"]) < 0.9
        ):
            errors.append(dict(file=record["file"], kind="grid_bounds"))
        unused = set(range(len(pred["items"])))
        for expected in gt["items"]:
            if not required.issubset(expected):
                errors.append(dict(file=record["file"], kind="incomplete_item_label"))
                continue
            index = max(
                unused,
                key=lambda i: iou(expected["bounds"], pred["items"][i]["bounds"]),
                default=None,
            )
            if (
                index is None
                or iou(expected["bounds"], pred["items"][index]["bounds"]) < 0.9
            ):
                errors.append(
                    dict(file=record["file"], kind="missing_item", expected=expected)
                )
                continue
            unused.remove(index)
            actual = pred["items"][index]
            for key in required - {"bounds"}:
                if expected[key] != actual.get(key):
                    errors.append(
                        dict(
                            file=record["file"],
                            kind=key,
                            expected=expected[key],
                            actual=actual.get(key),
                        )
                    )
        errors.extend(
            dict(file=record["file"], kind="extra_item", actual=pred["items"][i])
            for i in unused
        )
        if "tooltip" not in gt:
            errors.append(dict(file=record["file"], kind="tooltip_label_missing"))
        elif gt["tooltip"] is not None:
            tooltip_path = predictions / (Path(record["file"]).stem + ".tooltip.json")
            actual = (
                json.loads(tooltip_path.read_text(encoding="utf-8")).get("tooltip")
                if tooltip_path.exists()
                else None
            )
            if actual is None:
                errors.append(dict(file=record["file"], kind="tooltip_missing"))
            else:
                for key in (
                    "catalogId",
                    "purchaseTribute",
                    "deferTribute",
                    "mods",
                    "corrupted",
                ):
                    if gt["tooltip"].get(key) != actual.get(key):
                        errors.append(dict(file=record["file"], kind="tooltip_" + key))
    leakage = [
        s for s, values in sessions.items() if "test" in values and len(values) > 1
    ]
    missing_conditions = [
        list(x)
        for x in product(
            ["1080p", "1440p", "4k"], [False, True], ["windowed", "borderless"]
        )
        if x not in conditions
    ]
    reasons = []
    if labeled < 1000:
        reasons.append(f"Only {labeled}/1000 labeled test screens")
    if instances < 10000:
        reasons.append(f"Only {instances}/10000 labeled test items")
    if errors:
        reasons.append(f"{len(errors)} observed errors")
    if unlabeled:
        reasons.append(f"{len(unlabeled)} unverified test screens")
    if leakage:
        reasons.append("Session leakage")
    if duplicates:
        reasons.append("Duplicate screenshots")
    if missing_conditions:
        reasons.append("Resolution/HDR/window-mode coverage incomplete")
    if any(
        r.get("source") != "real" or not r.get("independent") or not r.get("fullFrame")
        for r in evaluated
    ):
        reasons.append("Test evidence is not independent full real captures")
    return dict(
        releaseReady=not reasons,
        reasons=reasons,
        labeledTestScreens=labeled,
        labeledTestItems=instances,
        observedErrors=len(errors) if labeled else None,
        errors=errors,
        unlabeled=unlabeled,
        sessionLeakage=leakage,
        duplicates=duplicates,
        missingConditions=missing_conditions,
        visionP95Ms=float(np.percentile(times, 95)) if times else None,
        accuracyClaim=None,
    )


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    init = sub.add_parser("init")
    init.add_argument("--screens", type=Path, required=True)
    init.add_argument("--out", type=Path, required=True)
    check = sub.add_parser("check")
    check.add_argument("--manifest", type=Path, required=True)
    check.add_argument("--predictions", type=Path, required=True)
    check.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "init":
        if args.out.exists():
            raise SystemExit("Refusing to overwrite an existing label manifest")
        records = []
        for file in sorted(args.screens.glob("*.png")):
            with Image.open(file) as image:
                size = list(image.size)
            records.append(
                dict(
                    file=str(file),
                    sha256=hashlib.sha256(file.read_bytes()).hexdigest(),
                    session="provided-development",
                    split="development",
                    source="real",
                    independent=False,
                    fullFrame=False,
                    pixels=size,
                    resolutionClass=None,
                    hdr=None,
                    windowMode=None,
                    ui="controller-ko",
                    verifiedBy=None,
                    groundTruth=None,
                )
            )
        result = dict(version=1, screens=records)
    else:
        result = evaluate(
            json.loads(args.manifest.read_text(encoding="utf-8")), args.predictions
        )
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(
        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(
        json.dumps(
            {
                k: v
                for k, v in result.items()
                if k not in ("screens", "errors", "unlabeled")
            },
            ensure_ascii=False,
        )
    )
    if args.command == "check" and not result["releaseReady"]:
        raise SystemExit(2)


if __name__ == "__main__":
    main()
