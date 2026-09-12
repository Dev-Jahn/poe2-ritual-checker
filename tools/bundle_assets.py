"""Create a reproducible, allowlisted runtime asset bundle (never captures or caches)."""

from pathlib import Path
import zipfile

root = Path(__file__).resolve().parents[1]
paths = [
    root / "data" / n
    for n in (
        "catalog.json",
        "embedding.json",
        "official-references.json",
        "recognition-model.json",
        "ritual-pool.json",
    )
]
for folder in ("images", "official-images", "ui"):
    paths += sorted((root / "data" / folder).glob("*.png"))
for stem in (
    "user-confirmed-chaotic-rarity-20260910",
    "user-current-chaotic-rarity-20260910",
):
    paths += [
        root / "data" / "user-examples" / (stem + ext) for ext in (".png", ".json")
    ]
paths += [root / "THIRD_PARTY_NOTICES.md", root / "LICENSE"]
paths += sorted(p for p in (root / "third-party").rglob("*") if p.is_file())
assert all(p.is_file() for p in paths)
out = root / "work/release-assets.zip"
out.parent.mkdir(exist_ok=True)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for p in sorted(paths):
        info = zipfile.ZipInfo(
            p.relative_to(root).as_posix(), date_time=(2026, 1, 1, 0, 0, 0)
        )
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, p.read_bytes())
print(f"Bundled {len(paths)} reviewed runtime assets")
