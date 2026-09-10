"""Extract the development-only empty-grid reference from the supplied screenshot."""

import argparse
from pathlib import Path
import json
import cv2
import numpy as np

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument(
    "source", type=Path, help="Development frame with the documented 1293x1168 layout"
)
source = parser.parse_args().source
im = cv2.imdecode(np.fromfile(source, dtype=np.uint8), cv2.IMREAD_COLOR)
if im is None or im.shape[:2] != (1168, 1293):
    raise SystemExit("Expected original development screenshot, 1293x1168")
out = root / "data" / "ui"
out.mkdir(parents=True, exist_ok=True)
cv2.imwrite(str(out / "empty-cell.png"), im[275:338, 575:635])
cv2.imwrite(str(out / "ritual-title.png"), im[103:144, 588:691])
cv2.imwrite(str(out / "tribute-icon.png"), im[185:247, 506:570])
hsv = cv2.cvtColor(im[277:306, 222:238], cv2.COLOR_BGR2HSV)
digit = cv2.inRange(hsv, (0, 0, 170), (180, 85, 255))
points = cv2.findNonZero(digit)
if points is not None:
    x, y, w, h = cv2.boundingRect(points)
    cv2.imwrite(
        str(out / "quantity-one.png"),
        cv2.resize(
            digit[y : y + h, x : x + w], (24, 40), interpolation=cv2.INTER_NEAREST
        ),
    )
(out / "provenance.json").write_text(
    json.dumps(
        dict(
            source=source.name,
            split="development",
            box=[575, 275, 60, 63],
            cellSize=70,
        ),
        indent=2,
    ),
    encoding="utf-8",
)
