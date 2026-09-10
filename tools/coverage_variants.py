"""Development-only resize/brightness fixtures; these are not independent HDR tests."""

from pathlib import Path
import json
import cv2
import numpy as np

root = Path(__file__).resolve().parents[1]
source = root / "poe2_ritual_data" / "스크린샷 2026-09-06 182900.png"
image = cv2.imdecode(np.fromfile(source, dtype=np.uint8), cv2.IMREAD_COLOR)
out = root / "work" / "coverage-variants"
out.mkdir(parents=True, exist_ok=True)
rows = []
for name, width, height in [
    ("1080p", 1920, 1080),
    ("1440p", 2560, 1440),
    ("4k", 3840, 2160),
    ("ultrawide", 5120, 1440),
]:
    scale = height * 0.88 / image.shape[0]
    resized = cv2.resize(image, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    canvas = np.full((height, width, 3), 12, np.uint8)
    x = int(width * 0.08)
    y = int(height * 0.05)
    canvas[y : y + resized.shape[0], x : x + resized.shape[1]] = resized
    cv2.imwrite(str(out / f"{name}.png"), canvas)
    rows.append(
        dict(
            file=f"{name}.png",
            source=str(source),
            split="development",
            independent=False,
            hdr=False,
            synthetic=True,
            expectedGrid=dict(
                x=round(x + 212 * scale),
                y=round(y + 253 * scale),
                width=round(842 * scale),
                height=round(702 * scale),
            ),
        )
    )
(out / "manifest.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
