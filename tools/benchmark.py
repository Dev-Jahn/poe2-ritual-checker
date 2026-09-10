"""Compare all implemented recognition methods on exactly the same screens."""

import argparse
import json
import subprocess
from pathlib import Path
import numpy as np


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--dotnet", default="dotnet")
    p.add_argument("--screens", type=Path, default=Path("poe2_ritual_data"))
    p.add_argument("--data", type=Path, default=Path("data"))
    p.add_argument("--out", type=Path, default=Path("work/benchmark"))
    a = p.parse_args()
    dll = Path("src/Ritual.Cli/bin/Release/net8.0-windows10.0.19041.0/Ritual.Cli.dll")
    report = []
    for method in ("template", "embedding", "hybrid"):
        folder = a.out / method
        subprocess.run(
            [
                a.dotnet,
                str(dll),
                "analyze",
                str(a.screens),
                "--data",
                str(a.data),
                "--out",
                str(folder),
                "--method",
                method,
            ],
            check=True,
            stdout=subprocess.DEVNULL,
        )
        rows = json.loads((folder / "summary.json").read_text(encoding="utf-8"))
        times = [r["elapsedMs"] for r in rows]
        report.append(
            dict(
                method=method,
                screens=len(rows),
                gridsFound=sum(r["grid"] is not None for r in rows),
                predictedItems=sum(r["items"] for r in rows),
                visionP50Ms=float(np.median(times)),
                visionP95Ms=float(np.percentile(times, 95)),
                accuracy=None,
                groundTruth="not supplied",
            )
        )
        print(method, report[-1]["visionP95Ms"], flush=True)
    (a.out / "comparison.json").write_text(
        json.dumps(
            dict(
                results=report,
                selected="template",
                selectionStatus="provisional baseline; cannot select by accuracy without labels",
                scope="development crops; excludes capture, OCR, market and overlay latency",
            ),
            indent=2,
        ),
        encoding="utf-8",
    )


if __name__ == "__main__":
    main()
