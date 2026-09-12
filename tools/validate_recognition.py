"""Evaluate reviewed capture labels without treating the unreviewed pool as ground truth."""

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np


def validate(manifest, predictions, model=None, split="validation"):
    records = json.loads(manifest.read_text(encoding="utf-8"))["records"]
    errors, times, active_times = [], [], []
    reviewed = [
        r
        for r in records
        if r["split"] == split and r.get("verifiedBy") and r.get("groundTruth")
    ]
    if not reviewed:
        errors.append(dict(kind="no_reviewed_frames"))
    if model:
        trained = {
            p["sourceHash"].lower()
            for p in json.loads(model.read_text(encoding="utf-8"))["prototypes"]
        }
        train_groups = {
            r["splitGroup"] for r in records if r["sha256"].lower() in trained
        }
        if split != "train" and any(r["splitGroup"] in train_groups for r in reviewed):
            errors.append(dict(kind="training_session_leakage"))
    count = 0
    for r in reviewed:
        file = Path(r["file"])
        if hashlib.sha256(file.read_bytes()).hexdigest() != r["sha256"].lower():
            errors.append(dict(file=file.name, kind="source_hash"))
            continue
        output = predictions / (file.stem + ".json")
        if not output.exists():
            errors.append(dict(file=file.name, kind="missing_prediction"))
            continue
        p = json.loads(output.read_text(encoding="utf-8"))
        gt = r["groundTruth"]
        if gt.get("complete"):
            expected_grid = gt.get("grid")
            actual_grid = p.get("grid")
            if (expected_grid is None) != (actual_grid is None):
                errors.append(dict(file=file.name, kind="grid_presence"))
            elif expected_grid is not None and any(
                abs(expected_grid[k] - actual_grid["bounds"][k]) > 5
                for k in expected_grid
            ):
                errors.append(dict(file=file.name, kind="grid_bounds"))
        actual = {i["instanceId"]: i for i in p["items"]}
        expected = (
            {}
            if gt.get("analysisSuppressed")
            else {i["instanceId"]: i for i in gt["items"]}
        )
        if gt.get("analysisSuppressed") and actual:
            errors.append(dict(file=file.name, kind="items_on_confirmation_dialog"))
        if gt.get("complete") and actual.keys() != expected.keys():
            errors.append(
                dict(
                    file=file.name,
                    kind="regions",
                    missing=sorted(expected.keys() - actual.keys()),
                    extra=sorted(actual.keys() - expected.keys()),
                )
            )
        for key, item in expected.items():
            count += 1
            a = actual.get(key)
            if a is None:
                if not gt.get("complete"):
                    errors.append(
                        dict(file=file.name, kind="missing_item", instance=key)
                    )
                continue
            for field in ("catalogId", "quantity", "columns", "rows"):
                if a[field] != item[field]:
                    errors.append(
                        dict(
                            file=file.name,
                            instance=key,
                            kind=field,
                            expected=item[field],
                            actual=a[field],
                        )
                    )
            if any(abs(item["bounds"][k] - a["bounds"][k]) > 5 for k in item["bounds"]):
                errors.append(dict(file=file.name, instance=key, kind="item_bounds"))
        times.append(p["elapsedMs"])
        if gt["items"]:
            active_times.append(p["elapsedMs"])
    return dict(
        split=split,
        reviewedFrames=len(reviewed),
        reviewedItems=count,
        annotatedItems=sum(len(r["groundTruth"]["items"]) for r in reviewed),
        unreviewedFrames=sum(
            r["split"] == split and not r.get("groundTruth") for r in records
        ),
        errors=errors,
        passed=not errors,
        independentFinalTest=False,
        activeVisionP95Ms=float(np.percentile(active_times, 95))
        if active_times
        else None,
        allFramesVisionP95Ms=float(np.percentile(times, 95)) if times else None,
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--predictions", type=Path, required=True)
    parser.add_argument("--model", type=Path)
    parser.add_argument(
        "--split", choices=["train", "validation"], default="validation"
    )
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    result = validate(args.manifest, args.predictions, args.model, args.split)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(
        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(
        json.dumps(
            {k: v for k, v in result.items() if k != "errors"}, ensure_ascii=False
        ),
        f"errors={len(result['errors'])}",
    )
    raise SystemExit(0 if result["passed"] else 1)


if __name__ == "__main__":
    main()
