"""Replay the visually reviewed development captures; not an independent release gate."""

import argparse, hashlib, json, subprocess, sys
from pathlib import Path
import numpy as np


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--dotnet", default="work/dotnet/dotnet.exe")
    p.add_argument("--out", type=Path, default=Path("work/development-regression"))
    p.add_argument("--skip-run", action="store_true")
    args = p.parse_args()
    root = Path(__file__).resolve().parents[1]
    manifest = json.loads(
        (root / "data/development-regression.json").read_text(encoding="utf8")
    )
    out = (root / args.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    sources = {str((root / r["file"]).resolve()) for r in manifest["records"]}
    actual = {str(p.resolve()) for p in (root / "poe2_ritual_data").rglob("*.png")}
    errors = []
    if sources != actual:
        errors.append(
            dict(kind="unreviewed_or_missing_sources", files=sorted(sources ^ actual))
        )
    if not args.skip_run:
        for directory in sorted(
            {str((root / r["file"]).parent) for r in manifest["records"]}
        ):
            subprocess.run(
                [
                    str((root / args.dotnet).resolve()),
                    str(
                        root
                        / "src/Ritual.Cli/bin/Release/net8.0-windows10.0.19041.0/Ritual.Cli.dll"
                    ),
                    "analyze",
                    directory,
                    "--data",
                    str(root / "data"),
                    "--out",
                    str(out),
                    "--ocr",
                ],
                cwd=root,
                check=True,
            )
    times = []
    instances = 0
    tooltip_count = 0
    for r in manifest["records"]:
        source = root / r["file"]
        result = out / (source.stem + ".json")
        if (
            not source.exists()
            or hashlib.sha256(source.read_bytes()).hexdigest() != r["sha256"]
        ):
            errors.append(dict(file=r["file"], kind="source_changed"))
            continue
        if not result.exists():
            errors.append(dict(file=r["file"], kind="missing_result"))
            continue
        a = json.loads(result.read_text(encoding="utf8"))
        times.append(a["elapsedMs"])
        instances += len(r["items"])
        if not a["grid"]:
            errors.append(dict(file=r["file"], kind="missing_grid"))
            continue
        g = a["grid"]["bounds"]
        eg = r["grid"]
        if any(abs(g[k] - eg[k]) > 5 for k in g):
            errors.append(
                dict(file=r["file"], kind="grid_geometry", actual=g, expected=eg)
            )
        observed = {i["instanceId"]: i for i in a["items"]}
        expected = {i["instanceId"]: i for i in r["items"]}
        if observed.keys() != expected.keys():
            errors.append(
                dict(
                    file=r["file"],
                    kind="item_regions",
                    extra=sorted(observed.keys() - expected.keys()),
                    missing=sorted(expected.keys() - observed.keys()),
                )
            )
        for key, e in expected.items():
            if key not in observed:
                continue
            for field in ["catalogId", "quantity", "column", "row", "columns", "rows"]:
                if e[field] != observed[key][field]:
                    errors.append(
                        dict(
                            file=r["file"],
                            item=key,
                            kind=field,
                            expected=e[field],
                            actual=observed[key][field],
                        )
                    )
        selected = sorted(i["instanceId"] for i in a["items"] if i["selected"])
        if selected != sorted(r["selected"]):
            errors.append(
                dict(
                    file=r["file"],
                    kind="selection",
                    expected=r["selected"],
                    actual=selected,
                )
            )
        if r.get("modal") and not any("확인 창" in w for w in a["warnings"]):
            errors.append(dict(file=r["file"], kind="modal_not_reported"))
        if r["tooltip"]:
            tooltip_count += 1
            tp = out / (source.stem + ".tooltip.json")
            tip = (
                json.loads(tp.read_text(encoding="utf8"))["tooltip"]
                if tp.exists()
                else None
            )
            if tip is None:
                errors.append(dict(file=r["file"], kind="missing_tooltip"))
                continue
            association = json.loads(tp.read_text(encoding="utf8")).get(
                "associatedInstance"
            )
            if association != r["associatedInstance"]:
                errors.append(
                    dict(
                        file=r["file"],
                        kind="tooltip_association",
                        expected=r["associatedInstance"],
                        actual=association,
                    )
                )
            for k, v in r["tooltip"].items():
                if tip.get(k) != v:
                    errors.append(
                        dict(
                            file=r["file"],
                            kind="tooltip_" + k,
                            expected=v,
                            actual=tip.get(k),
                        )
                    )
    report = dict(
        scope=manifest["scope"],
        independent=False,
        fullReleaseGatePassed=False,
        notEstablished=manifest["notEstablished"],
        frames=len(manifest["records"]),
        uniqueImages=len(set(r["sha256"] for r in manifest["records"])),
        instances=instances,
        tooltipFrames=tooltip_count,
        errors=errors,
        regressionPassed=not errors,
        visionOnlyP95Ms=float(np.percentile(times, 95)),
        timingExcludes=["capture", "OCR", "network", "first price render"],
    )
    (out / "regression-report.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf8"
    )
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
