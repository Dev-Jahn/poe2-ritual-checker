"""Resolve current currency art from GGG's trade2 static item catalogue.

PoE2DB remains the name/mod catalogue. The separate override manifest survives
PoE2DB updates and records both URLs. Only exact English item names are joined.
"""

import argparse
import hashlib
import io
import json
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urljoin, urlparse

import requests
from PIL import Image

API = "https://www.pathofexile.com/api/trade2/data/static"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", type=Path, default=Path("data"))
    args = parser.parse_args()
    catalog = json.loads((args.data / "catalog.json").read_text(encoding="utf-8"))
    session = requests.Session()
    session.headers["User-Agent"] = "RitualChecker/0.2 (current item art updater)"
    response = session.get(API, timeout=30)
    response.raise_for_status()
    groups = response.json()["result"]
    names = {}
    for group in groups:
        for entry in group["entries"]:
            if entry.get("image"):
                names.setdefault(entry["text"], set()).add(
                    urljoin("https://web.poecdn.com", entry["image"])
                )
    target = args.data / "official-images"
    target.mkdir(exist_ok=True)
    records, errors = [], []
    for item in catalog["items"]:
        urls = names.get(item["nameEn"], set())
        if item["kind"] != "currency" or len(urls) != 1:
            continue
        url = next(iter(urls))
        if urlparse(url).hostname != "web.poecdn.com":
            continue
        asset = hashlib.sha256(url.encode()).hexdigest()[:24] + ".png"
        path = target / asset
        try:
            if not path.exists():
                r = session.get(url, timeout=25)
                if r.status_code == 429:
                    time.sleep(
                        min(120, max(1, int(r.headers.get("Retry-After", "10"))))
                    )
                    r = session.get(url, timeout=25)
                r.raise_for_status()
                im = Image.open(io.BytesIO(r.content)).convert("RGBA")
                im.save(path)
                time.sleep(0.2)
            with Image.open(path) as im:
                width, height = im.size
            if not (32 <= width <= 512 and 32 <= height <= 512):
                raise ValueError("Unexpected icon dimensions")
            records.append(
                dict(
                    catalogId=item["id"],
                    image="official-images/" + asset,
                    imageSource=url,
                    previousImageSource=item["imageSource"],
                    width=width,
                    height=height,
                    sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                )
            )
            if len(records) % 25 == 0:
                print(f"{len(records)} official references ready", flush=True)
        except (requests.RequestException, OSError, ValueError) as exc:
            errors.append(dict(catalogId=item["id"], error=str(exc)))
    report = dict(
        source=API,
        retrievedAt=datetime.now(timezone.utc).isoformat(),
        catalogVersion=catalog["version"],
        items=records,
        errors=errors,
    )
    # Never publish a partial update as a complete new reference set.
    (args.data / "official-art-report.json").write_text(
        json.dumps(report, indent=2), encoding="utf-8"
    )
    if errors:
        raise SystemExit(f"{len(errors)} images failed; previous manifest retained")
    if not records:
        raise SystemExit("No exact item joins; previous manifest retained")
    temp = args.data / "official-references.json.tmp"
    temp.write_text(json.dumps(report, indent=2), encoding="utf-8")
    temp.replace(args.data / "official-references.json")
    print(f"Published {len(records)} current official references", flush=True)


if __name__ == "__main__":
    main()
